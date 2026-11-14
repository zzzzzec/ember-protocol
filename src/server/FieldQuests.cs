using System.Text.Json;
using System.Text.Json.Nodes;
using HarmonyLib;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Eft.Quests;
using SPTarkov.Server.Core.Models.Enums;

namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    private readonly object _fieldIssueGate = new();
    private JsonObject _fieldQuests = new();
    private const string FieldIssueKey = "emberFieldIssue";

    private void SetQuestMapLabels(IEnumerable<Quest> quests)
    {
        var mapIds = db.GetLocations().GetDictionary().Values.Select(l => l?.Base)
            .Where(l => l?.Id is { } id && Maps.ContainsKey(id.ToLowerInvariant()))
            .OrderBy(l => l!.Id, StringComparer.OrdinalIgnoreCase)
            .GroupBy(l => AbundanceMap(l!.Id)).ToDictionary(g => g.Key, g => g.First()!.IdField.ToString());
        var changed = 0;
        foreach (var quest in quests)
        {
            if (!string.Equals(quest.Location, "any", StringComparison.OrdinalIgnoreCase)) continue;
            var locations = (quest.Conditions.AvailableForFinish ?? [])
                .Where(c => c.ConditionType == "CounterCreator")
                .SelectMany(c => c.Counter?.Conditions ?? [])
                .Where(c => c.ConditionType == "Location")
                .SelectMany(c => c.Target?.List ?? (c.Target?.Item is { } id ? [id] : []))
                .Select(AbundanceMap).Distinct().ToArray();
            if (locations is not [var map] || !mapIds.TryGetValue(map, out var mapId)) continue;
            quest.Location = mapId;
            changed++;
        }
        log.Info($"[SPT-Z] Resolved {changed} authored quest labels from explicit raid locations.", null);
    }

    private bool FieldGift(MongoId id) => _fieldQuests["issuedOnce"]?[id.ToString()] is not null;
    private JsonObject? Loan(MongoId id) => _fieldQuests["loans"]?[id.ToString()] as JsonObject;
    private JsonObject IssueLedger(SptProfile profile)
    {
        profile.SptData ??= new Spt(); profile.SptData.ExtensionData ??= new();
        if (profile.SptData.ExtensionData.TryGetValue(FieldIssueKey, out var raw))
        {
            var node = raw switch { JsonObject o => o, JsonElement e => JsonNode.Parse(e.GetRawText()), _ => JsonSerializer.SerializeToNode(raw) };
            if (node is JsonObject obj) { profile.SptData.ExtensionData[FieldIssueKey] = obj; return obj; }
        }
        var ledger = new JsonObject(); profile.SptData.ExtensionData[FieldIssueKey] = ledger; return ledger;
    }
    private ItemEventRouterResponse FieldDenied(MongoId session, string reason) => responses.AppendErrorToOutput(outputs.GetOutput(session), reason);
    private void InstallFieldQuestPatches(Harmony h)
    {
        _fieldQuests = Read("data/field-quests.json").AsObject();
        h.Patch(AccessTools.Method(typeof(QuestController), "AcceptQuest"), prefix: new HarmonyMethod(typeof(FieldPatches), nameof(FieldPatches.BeforeAccept)), postfix: new HarmonyMethod(typeof(FieldPatches), nameof(FieldPatches.AfterAccept)), finalizer: new HarmonyMethod(typeof(FieldPatches), nameof(FieldPatches.AcceptFinished)));
        h.Patch(AccessTools.Method(typeof(QuestRewardHelper), "ApplyQuestReward"), prefix: new HarmonyMethod(typeof(FieldPatches), nameof(FieldPatches.StartedRewards)));
        h.Patch(AccessTools.Method(typeof(QuestController), "HandoverQuest"), prefix: new HarmonyMethod(typeof(FieldPatches), nameof(FieldPatches.BeforeHandover)), postfix: new HarmonyMethod(typeof(FieldPatches), nameof(FieldPatches.AfterHandover)));
        h.Patch(AccessTools.Method(typeof(QuestController), "CompleteQuest"), prefix: new HarmonyMethod(typeof(FieldPatches), nameof(FieldPatches.BeforeComplete)));
    }
    public sealed class FieldAcceptState { public bool Native; public object Gate = null!; public MongoId Quest; public MongoId Session; }
    public bool BeforeFieldAccept(PmcData pmc, AcceptQuestRequestData request, MongoId session, ref ItemEventRouterResponse result, out FieldAcceptState? state)
    {
        state = null;
        var weaponTrial = Loan(request.QuestId)?["killCondition"] is not null;
        if (!FieldGift(request.QuestId) && !weaponTrial) return true;
        Monitor.Enter(_fieldIssueGate);
        state = new FieldAcceptState { Gate = _fieldIssueGate, Quest = request.QuestId, Session = session };
        var active = pmc.Quests?.FirstOrDefault(q => q.QId == request.QuestId);
        // Native AcceptQuest resets an already-started quest and repeats its start rewards.
        if (active is not null && (int)active.Status is 2 or 3 or 4)
        { result = outputs.GetOutput(session); Trace("field.accept_replayed", session, new { quest = request.QuestId.ToString() }); return false; }
        var quest = db.GetQuests()[request.QuestId];
        if (weaponTrial)
        {
            var trader = quest.TraderId;
            if (!TraderAvailable(pmc, trader.ToString(), session))
            { result = FieldDenied(session, "先联系这份试驾委托的商人。"); return false; }
            var reason = StoryQuestPolicy.PrerequisiteError(Node(pmc).AsObject(), Node(quest).AsObject());
            if (reason is not null)
            { result = FieldDenied(session, reason); return false; }
            var loyalty = pmc.TradersInfo?.GetValueOrDefault(trader)?.LoyaltyLevel ?? 0;
            if ((quest.Conditions.AvailableForStart ?? []).Any(c => c.ConditionType == "TraderLoyalty" && loyalty < c.Value))
            { result = FieldDenied(session, "先提升试驾商人的信任等级。"); return false; }
        }
        if ((quest.Conditions.AvailableForStart ?? []).Any(c => c.ConditionType == "Quest" && pmc.Quests?.Any(q => c.Target?.Item == q.QId.ToString() && (int)q.Status == 4) != true))
        { result = FieldDenied(session, "先完成这份借条前面的委托。"); return false; }
        state.Native = true; return true;
    }
    public bool AllowFieldStartedRewards(MongoId quest, QuestStatusEnum status, MongoId session)
        => status != QuestStatusEnum.Started || !FieldGift(quest) || !IssueLedger(saves.GetProfile(session)).ContainsKey(quest.ToString());
    public void AfterFieldAccept(PmcData pmc, FieldAcceptState? state)
    {
        if (state?.Native != true || !FieldGift(state.Quest) ||
            pmc.Quests?.Any(q => q.QId == state.Quest && q.Status == QuestStatusEnum.Started) != true) return;
        var profile = saves.GetProfile(state.Session); var ledger = IssueLedger(profile); var id = state.Quest.ToString();
        if (ledger.ContainsKey(id)) return;
        // The native controller has synchronously appended the reward mail. Persist both in the same profile save.
        ledger[id] = new JsonObject { ["issuedUtc"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["version"] = "1.3.0" };
        try { saves.SaveProfileAsync(state.Session).GetAwaiter().GetResult(); }
        catch (Exception e) { log.Error("[SPT-Z] Field issue save deferred: " + Redact(e.ToString()), null); }
        Trace("field.issued_once", state.Session, new { quest = id, replacementIssue = false });
    }
    private bool LoanObjectivesReady(PmcData pmc, MongoId questId, bool includeReturn)
    {
        var quest = Node(db.GetQuests()[questId]).AsObject(); var player = Node(pmc).AsObject();
        return quest["conditions"]!["AvailableForFinish"]!.AsArray().OfType<JsonObject>().Where(c => includeReturn || c["conditionType"]?.ToString() != "WeaponAssembly").All(c => FieldQuestPolicy.ObjectiveReady(player, c, questId.ToString()));
    }
    public bool BeforeFieldHandover(PmcData pmc, HandoverQuestRequestData request, MongoId session, ref ItemEventRouterResponse result)
    {
        if (Loan(request.QuestId) is not { } spec) return true;
        string? reason = null;
        if (request.ConditionId.ToString() != spec["returnCondition"]!.ToString()) reason = "这份借条只接收完整枪械。";
        else if (pmc.Quests?.Any(q => q.QId == request.QuestId && (int)q.Status is 2 or 3) != true) reason = "这份借条当前不能交付。";
        else if (pmc.TaskConditionCounters?.TryGetValue(request.ConditionId, out var done) == true && done.Value >= 1) { result = outputs.GetOutput(session); return false; }
        else if (!LoanObjectivesReady(pmc, request.QuestId, false)) reason = "先完成作战目标，再把枪交回来。";
        else if (request.Items?.Count != 1 || request.Items[0].Count != 1) reason = "每份借条只交还一支完整枪械。";
        else reason = FieldQuestPolicy.ValidateReturn(spec, Node(pmc.Inventory!.Items!).AsArray(), request.Items[0].Id.ToString(), _items);
        if (reason is null) return true;
        result = FieldDenied(session, reason); Trace("field.return_rejected", session, new { quest = request.QuestId.ToString(), reason }); return false;
    }
    public void AfterFieldHandover(PmcData pmc, HandoverQuestRequestData request, MongoId session)
    {
        if (Loan(request.QuestId) is not null && pmc.TaskConditionCounters?.TryGetValue(request.ConditionId, out var value) == true && value.Value >= 1)
            Trace("field.loan_returned", session, new { quest = request.QuestId.ToString(), acceptsReplacement = true });
    }
    public bool BeforeFieldComplete(PmcData pmc, CompleteQuestRequestData request, MongoId session, ref ItemEventRouterResponse result)
    {
        if (Loan(request.QuestId) is null) return true;
        if (pmc.Quests?.Any(q => q.QId == request.QuestId && (int)q.Status == 4) == true) { result = outputs.GetOutput(session); return false; }
        if (LoanObjectivesReady(pmc, request.QuestId, true)) return true;
        result = FieldDenied(session, "完成作战目标并归还枪械后，才能结算这份借条。"); return false;
    }
}

public static class FieldPatches
{
    private static SurvivalService S => SurvivalService.Current;
    public static bool BeforeAccept(PmcData __0, AcceptQuestRequestData __1, MongoId __2, ref ItemEventRouterResponse __result, out SurvivalService.FieldAcceptState? __state) => S.BeforeFieldAccept(__0, __1, __2, ref __result, out __state);
    public static void AfterAccept(PmcData __0, SurvivalService.FieldAcceptState? __state) => S.AfterFieldAccept(__0, __state);
    public static Exception? AcceptFinished(Exception? __exception, SurvivalService.FieldAcceptState? __state) { if (__state is not null) Monitor.Exit(__state.Gate); return __exception; }
    public static bool StartedRewards(MongoId __1, QuestStatusEnum __2, MongoId __3, ref IEnumerable<Item> __result) { if (S.AllowFieldStartedRewards(__1, __2, __3)) return true; __result = []; return false; }
    public static bool BeforeHandover(PmcData __0, HandoverQuestRequestData __1, MongoId __2, ref ItemEventRouterResponse __result) => S.BeforeFieldHandover(__0, __1, __2, ref __result);
    public static void AfterHandover(PmcData __0, HandoverQuestRequestData __1, MongoId __2) => S.AfterFieldHandover(__0, __1, __2);
    public static bool BeforeComplete(PmcData __0, CompleteQuestRequestData __1, MongoId __2, ref ItemEventRouterResponse __result) => S.BeforeFieldComplete(__0, __1, __2, ref __result);
}
