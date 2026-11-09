using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Utils;

namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    private const string LabTriggered = "emberLabOutage";
    private const string LabScouted = "emberLabScouted";
    private const string LabVerified = "emberLabRepairExtracted";
    private readonly object _labGate = new();
    private readonly Dictionary<MongoId, LabRaid> _labRaids = [];
    private MongoId _labTrigger, _labScout, _labParts, _labRepair, _scoutCondition, _labCondition, _labPlant;
    private string _labPlantZone = "";
    private sealed class LabRaid(string id, bool blackout, string operation)
    {
        public string Id { get; } = id;
        public bool Blackout { get; } = blackout;
        public string Operation { get; } = operation;
        public bool Operated { get; set; }
    }

    private void InitializeLabBlackout()
    {
        var data = Read("data/lab-blackout.json");
        _labTrigger = (MongoId)data["trigger"]!.ToString();
        _labScout = (MongoId)data["scout"]!.ToString();
        _scoutCondition = (MongoId)data["scoutCondition"]!.ToString();
        _labPlant = (MongoId)data["plantCondition"]!.ToString();
        _labPlantZone = data["plantZone"]!.ToString();
        _labParts = (MongoId)data["parts"]!.ToString();
        _labRepair = (MongoId)data["repair"]!.ToString();
        _labCondition = (MongoId)data["repairCondition"]!.ToString();
    }

    private static int LabQuestStatus(PmcData? pmc, MongoId id) =>
        (int?)pmc?.Quests?.FirstOrDefault(q => q.QId == id)?.Status ?? 0;

    private static bool LabFlag(SptProfile profile, string key) =>
        profile.SptData?.ExtensionData?.ContainsKey(key) == true;

    private static void SetLabFlag(SptProfile profile, string key)
    {
        profile.SptData ??= new Spt();
        profile.SptData.ExtensionData ??= [];
        profile.SptData.ExtensionData[key] = true;
    }

    private void OnLabStoryCompleted(MongoId session, MongoId quest)
    {
        if (quest != _labTrigger || !saves.GetProfiles().TryGetValue(session, out var profile)) return;
        if (LabQuestStatus(profile.CharacterData?.PmcData, quest) == 4) SetLabFlag(profile, LabTriggered);
    }

    private bool LabObjectiveVerified(MongoId session, MongoId quest) =>
        saves.GetProfiles().TryGetValue(session, out var profile) && LabFlag(profile, quest == _labScout ? LabScouted : LabVerified);

    public void SnapshotLabRaid(MongoId session, string map, string? id)
    {
        lock (_labGate)
        {
            _labRaids.Remove(session);
            if (map != "laboratory" || string.IsNullOrEmpty(id) || !saves.GetProfiles().TryGetValue(session, out var profile)) return;
            var pmc = profile.CharacterData?.PmcData;
            var scout = LabQuestStatus(pmc, _labScout);
            var parts = LabQuestStatus(pmc, _labParts);
            var repair = LabQuestStatus(pmc, _labRepair);
            var active = LabQuestStatus(pmc, _labTrigger) == 4 && repair != 4
                && (LabFlag(profile, LabTriggered) || scout is 2 or 3 or 4);
            var operation = "";
            if (active && scout == 2 && !LabFlag(profile, LabScouted)) operation = "scout";
            if (active && parts == 4 && repair == 2 && !LabFlag(profile, LabVerified)) operation = "repair";
            _labRaids[session] = new LabRaid(id, active, operation);
            CaptureDiagnostic(() => Trace("lab.power_state", session, new { blackout = active, operation }));
        }
    }

    public string LabRaidState(MongoId session)
    {
        lock (_labGate)
            return _labRaids.TryGetValue(session, out var raid)
                ? JsonSerializer.Serialize(new { blackout = raid.Blackout, operation = raid.Operation, raid = raid.Id, plantZone = _labPlantZone })
                : "{\"blackout\":false,\"repair\":false}";
    }

    public string OperateLabPanel(MongoId session, string id)
    {
        lock (_labGate)
        {
            if (!_labRaids.TryGetValue(session, out var raid) || raid.Id != id || raid.Operation.Length == 0)
                return "{\"ok\":false}";
            if (!raid.Operated)
            {
                raid.Operated = true;
                CaptureDiagnostic(() => Trace("lab.panel_operated", session, new { operation = raid.Operation, confirmed = true }));
            }
            return "{\"ok\":true}";
        }
    }

    public void SaveLabRaid(MongoId session, EndLocalRaidRequestData request)
    {
        LabRaid raid;
        lock (_labGate)
        {
            if (!_labRaids.TryGetValue(session, out var active) || active.Id != request.ServerId) return;
            raid = active;
            _labRaids.Remove(session);
        }
        var survived = request.Results?.Result == ExitStatus.SURVIVED;
        if (!raid.Operated || !survived || !saves.GetProfiles().TryGetValue(session, out var profile)) return;
        var pmc = profile.CharacterData?.PmcData;
        var scouting = raid.Operation == "scout";
        var questId = scouting ? _labScout : _labRepair;
        var conditionId = scouting ? _scoutCondition : _labCondition;
        var receipt = scouting ? LabScouted : LabVerified;
        var quest = pmc?.Quests?.FirstOrDefault(q => q.QId == questId);
        if (quest is null || (int)quest.Status is not (2 or 3) || LabFlag(profile, receipt)) return;
        if (!scouting)
        {
            var placement = StoryDefinition(questId)["conditions"]!["AvailableForFinish"]!.AsArray()
                .First(c => c?["id"]?.ToString() == _labPlant.ToString())!.AsObject();
            if (!FieldQuestPolicy.ObjectiveReady(Node(pmc).AsObject(), placement, questId.ToString())) return;
        }
        pmc!.TaskConditionCounters ??= [];
        pmc.TaskConditionCounters[conditionId] = new TaskConditionCounter
        {
            Id = conditionId, SourceId = questId, Type = "CounterCreator", Value = 1
        };
        quest.CompletedConditions ??= [];
        if (!quest.CompletedConditions.Contains(conditionId)) quest.CompletedConditions.Add(conditionId);
        quest.Status = QuestStatusEnum.AvailableForFinish;
        SetLabFlag(profile, receipt);
        saves.SaveProfileAsync(session).GetAwaiter().GetResult();
        CaptureDiagnostic(() => Trace("lab.operation_extracted", session, new { operation = raid.Operation, survived = true }));
    }
}

[Injectable]
public sealed class LabBlackoutRouter(JsonUtil json, SurvivalService service) : DynamicRouter(json,
[
    new RouteAction("/ember/lab/state", (_, _, session, _) => new ValueTask<object>(service.LabRaidState(session))),
    new RouteAction("/ember/lab/panel/", (url, _, session, _) =>
        new ValueTask<object>(service.OperateLabPanel(session, Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..]))))
]);
