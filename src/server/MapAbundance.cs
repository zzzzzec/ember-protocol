using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Utils;

namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    private const string AbundanceLedger = "emberMapAbundance";
    private const int AbundanceScale = 10000;
    private const int AbundanceQuestCap = 4320;
    private const int AbundanceKillValue = 5;
    private const int AbundanceKillCap = (2 * AbundanceScale - AbundanceQuestCap) / AbundanceKillValue;
    private const string AbundanceLabel = "ember map abundance";
    private readonly object _abundanceGate = new();
    private readonly Dictionary<MongoId, AbundanceRaid> _abundanceRaids = [];
    private readonly Dictionary<MongoId, Dictionary<string, int>> _abundanceAwards = [];
    private static readonly Dictionary<string, string> AbundanceMapNames = new()
    {
        ["sandbox"] = "中心区", ["bigmap"] = "海关", ["interchange"] = "立交桥",
        ["factory"] = "工厂", ["rezervbase"] = "储备站", ["laboratory"] = "实验室"
    };

    private sealed class AbundanceRaid(string id, string map, HashSet<MongoId> credited)
    {
        public readonly string Id = id;
        public readonly string Map = map;
        public readonly HashSet<MongoId> Credited = credited;
        public bool Settled;
        public bool Transit;
    }

    private sealed record AbundanceProgress(decimal Total, decimal Quests, decimal Kills);

    private static string AbundanceMap(string? id) => id?.ToLowerInvariant() switch
    {
        "sandbox_high" => "sandbox",
        "factory4" or "factory4_day" or "factory4_night" => "factory",
        { } map => map,
        _ => ""
    };

    private void InitializeAbundanceAwards(JsonObject locales)
    {
        var data = Read("data/map-abundance.json");
        foreach (var (map, quests) in data["maps"]!.AsObject())
        {
            foreach (var quest in quests!["main"]!.AsArray()) _abundanceAwards.Add((MongoId)quest!.ToString(), new() { [map] = 720 });
            foreach (var quest in quests["side"]!.AsArray()) _abundanceAwards.Add((MongoId)quest!.ToString(), new() { [map] = 360 });
        }
        foreach (var (quest, maps) in data["shared"]!.AsObject())
            _abundanceAwards.Add((MongoId)quest, maps!.AsArray().ToDictionary(map => map!.ToString(), _ => 180));

        var englishMaps = new Dictionary<string, string>
        {
            ["sandbox"] = "Ground Zero", ["bigmap"] = "Customs", ["interchange"] = "Interchange",
            ["factory"] = "Factory", ["rezervbase"] = "Reserve", ["laboratory"] = "The Lab"
        };
        foreach (var (language, entries) in locales)
        {
            var chinese = language is "ch" or "zh" or "zh-cn";
            entries![AbundanceLabel] = chinese ? "物资丰度 ×{0}\n任务 +{1}　击杀 +{2}" : "Loot abundance ×{0}\nQuests +{1}   Infected +{2}";
            var names = chinese ? AbundanceMapNames : englishMaps;
            foreach (var (quest, awards) in _abundanceAwards)
            {
                var key = quest + " description";
                if (entries[key] is not JsonValue description) continue;
                var bonuses = string.Join(" / ", awards.Select(x => $"{names[x.Key]} +{(x.Value / (decimal)AbundanceScale).ToString("0.00#", CultureInfo.InvariantCulture)}"));
                entries[key] = description.GetValue<string>() + "\n\n" + (chinese ? "物资丰度奖励　" : "Loot abundance reward   ") + bonuses;
            }
        }
    }

    private static string AbundanceCharacter(PmcData pmc) => $"{pmc.Id}/{pmc.Info?.RegistrationDate}";

    private static JsonObject ReadAbundanceLedger(SptProfile profile, PmcData pmc)
    {
        if (profile.SptData?.ExtensionData?.TryGetValue(AbundanceLedger, out var value) == true)
        {
            var ledger = value switch
            {
                JsonObject node => node,
                JsonElement { ValueKind: JsonValueKind.Object } element => JsonNode.Parse(element.GetRawText()) as JsonObject,
                _ => null
            };
            if (ledger?["character"]?.ToString() == AbundanceCharacter(pmc) && ledger["kills"] is JsonObject) return ledger;
        }
        return new JsonObject { ["character"] = AbundanceCharacter(pmc), ["kills"] = new JsonObject() };
    }

    private static int AbundanceKills(JsonObject ledger, string map) =>
        ledger["kills"]?[map] is JsonValue node && node.TryGetValue<int>(out var kills) ? Math.Clamp(kills, 0, AbundanceKillCap) : 0;

    private Dictionary<string, AbundanceProgress> AbundanceFor(MongoId session)
    {
        var quests = new Dictionary<string, int>();
        JsonObject ledger = new();
        lock (_abundanceGate)
        {
            if (saves.GetProfiles().TryGetValue(session, out var profile) && profile.CharacterData?.PmcData is { } pmc)
            {
                ledger = ReadAbundanceLedger(profile, pmc);
                foreach (var id in (pmc.Quests ?? []).Where(q => q.Status == QuestStatusEnum.Success).Select(q => q.QId).Distinct())
                    if (_abundanceAwards.TryGetValue(id, out var awards))
                        foreach (var (map, value) in awards) quests[map] = quests.GetValueOrDefault(map) + value;
            }
        }
        return AbundanceMapNames.Keys.ToDictionary(map => map, map =>
        {
            var task = Math.Min(AbundanceQuestCap, quests.GetValueOrDefault(map)) / (decimal)AbundanceScale;
            var combat = AbundanceKills(ledger, map) * AbundanceKillValue / (decimal)AbundanceScale;
            return new AbundanceProgress(1m + task + combat, task, combat);
        });
    }

    public string MapAbundanceState(MongoId session) => JsonSerializer.Serialize(AbundanceFor(session), JsonSerializerOptions.Web);

    public void SnapshotAbundanceRaid(MongoId session, string map, string? id, bool transit)
    {
        if (string.IsNullOrEmpty(id) || !AbundanceMapNames.ContainsKey(AbundanceMap(map))) return;
        lock (_abundanceGate)
        {
            if (_abundanceRaids.TryGetValue(session, out var previous) && previous.Id == id && previous.Map == map) return;
            var credited = transit && previous?.Transit == true ? previous.Credited : [];
            _abundanceRaids[session] = new AbundanceRaid(id, map, credited);
        }
    }

    public void CreditAbundanceKills(MongoId session, EndLocalRaidRequestData request)
    {
        lock (_abundanceGate)
        {
            if (!_abundanceRaids.TryGetValue(session, out var raid) || raid.Id != request.ServerId || raid.Settled ||
                request.Results is not { Result: not null, Profile: { } ending } ||
                !saves.GetProfiles().TryGetValue(session, out var profile) || profile.CharacterData?.PmcData is not { } pmc ||
                pmc.Id is null || ending.Id != pmc.Id) return;

            var map = AbundanceMap(raid.Map);
            var victims = (ending.Stats?.Eft?.Victims ?? [])
                .Where(v => v.ProfileId.HasValue && AbundanceMap(v.Location) == map &&
                    Enum.TryParse<WildSpawnType>(v.Role, true, out var role) && role is WildSpawnType.infectedAssault or
                    WildSpawnType.infectedCivil or WildSpawnType.infectedPmc or WildSpawnType.infectedLaborant or WildSpawnType.infectedTagilla)
                .Select(v => v.ProfileId!.Value).Where(id => !raid.Credited.Contains(id)).Distinct().ToArray();

            var old = ReadAbundanceLedger(profile, pmc);
            var before = AbundanceKills(old, map);
            var next = Math.Min(AbundanceKillCap, before + victims.Length);
            if (next > before)
            {
                var updated = old.DeepClone().AsObject();
                updated["kills"]![map] = next;
                profile.SptData ??= new Spt();
                profile.SptData.ExtensionData ??= [];
                var extension = profile.SptData.ExtensionData;
                var hadValue = extension.TryGetValue(AbundanceLedger, out var previousValue);
                extension[AbundanceLedger] = updated;
                try { saves.SaveProfileAsync(session).GetAwaiter().GetResult(); }
                catch (Exception error)
                {
                    if (hadValue) extension[AbundanceLedger] = previousValue!;
                    else extension.Remove(AbundanceLedger);
                    log.Error("[SPT-Z] Map abundance could not be saved: " + Redact(error.ToString()), null);
                    return;
                }
                CaptureDiagnostic(() => Trace("abundance.kills", session, new { map, credited = next - before, totalKills = next, outcome = request.Results.Result.ToString() }));
            }

            raid.Settled = true;
            raid.Transit = request.Results.Result == ExitStatus.TRANSIT;
            if (raid.Transit) raid.Credited.UnionWith(victims);
        }
    }
}

[Injectable]
public sealed class MapAbundanceRouter(JsonUtil json, SurvivalService service) : DynamicRouter(json,
[
    new RouteAction("/ember/map/abundance", (_, _, session, _) => new ValueTask<object>(service.MapAbundanceState(session)))
]);
