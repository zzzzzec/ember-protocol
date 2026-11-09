using System.Text.Json.Nodes;
using Codex.ZombieSurvival.Rules;

namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    private void ConfigureAk50Handguard(JsonObject chain)
    {
        var map = chain["sourceMap"]!.GetValue<string>();
        var sourceId = chain["sourceSpot"]!.GetValue<string>();
        var newId = chain["relocatedSpot"]!.GetValue<string>();
        var loose = db.GetLocation(map)?.LooseLoot
            ?? throw new InvalidDataException("Missing AK-50 handguard map loot.");
        loose.AddTransformer(loot =>
        {
            var points = loot.SpawnpointsForced?.ToList() ?? [];
            if (points.Any(point => point.Template?.Id == newId)) return loot;
            var source = points.Single(point => point.Template?.Id == sourceId);
            points.Add(Edit(source, node =>
            {
                var template = node["template"]!;
                var item = template["Items"]![0]!;
                var position = template["Position"]!;
                template["Id"] = newId;
                template["Root"] = chain["relocatedItemId"]!.DeepClone();
                item["_id"] = chain["relocatedItemId"]!.DeepClone();
                item["_tpl"] = chain["handguard"]!.DeepClone();
                // Leave the original manifest's pickup clear on the same desk.
                position["x"] = (double)position["x"]! + .27;
                position["y"] = (double)position["y"]! + .015;
                template["IsAlwaysSpawn"] = true;
                node["probability"] = 1;
            }));
            loot.SpawnpointsForced = points;
            return loot;
        });
    }

    private readonly HashSet<string> _questLootTransforms = new(StringComparer.OrdinalIgnoreCase);
    private void ConfigureQuestLootSources()
    {
        var rules = Read("data/quest-loot.json").AsObject();
        var registered = 0;
        foreach (var (key, location) in db.GetLocations().GetDictionary())
        {
            var map = Node(location.Base)["Id"]?.ToString().ToLowerInvariant() ?? key.ToLowerInvariant();
            if (Maps[map] is not JsonObject spec || location.StaticLoot is null) continue;
            var rank = (int)spec["rank"]!;
            if (!rules["items"]!.AsArray().Any(x => (int)x!["minimumRank"]! <= rank)) continue;
            if (!_questLootTransforms.Add(map)) continue;
            var reported = 0;
            // SPT re-deserializes Value on every read. An in-place mutation of
            // one Value disappears; register the transformation with the loader.
            location.StaticLoot.AddTransformer(tables => {
                var added = QuestLootPolicy.Apply(tables, rules, rank);
                if (Interlocked.Exchange(ref reported, 1) == 0)
                    CaptureDiagnostic(() => _journal?.Record("quest_loot.sources", new { map, rank, added, newSpawnPoints = false }));
                return tables;
            });
            registered++;
        }
        log.Info($"[SPT-Z] Quest collection sources: {registered} high-danger map transforms registered; existing weights and spawn points retained.", null);
    }
}
