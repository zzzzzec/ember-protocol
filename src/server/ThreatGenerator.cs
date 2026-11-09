using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codex.ZombieSurvival.Rules;
using SPTarkov.Server.Core.Models.Common;

namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    private ThreatConfiguration _threat = new();
    private readonly ConcurrentDictionary<string, ThreatRaid> _threatRaids = new();
    private readonly Dictionary<string, JsonArray> _armorPresets = new();
    private sealed class ThreatRaid
    {
        public ThreatPlan Plan = new();
        public readonly Queue<ThreatArchetype> Melee = new(), Armed = new();
        public readonly Dictionary<string, ThreatArchetype> Assigned = new();
        public int Surplus;
    }
    private void InitializeThreatGenerator()
    {
        _threat = JsonSerializer.Deserialize<ThreatConfiguration>(Read("threat-generator.json").ToJsonString(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        ThreatPolicy.Validate(_threat);
        if (!_threat.Enabled) return;
        foreach (var (map,spec) in Maps) ThreatPolicy.Generate(_threat,map,(int)spec!["rank"]!,(int)spec["total"]!,(int)spec["shooters"]!,1);
        var presets = Node(db.GetGlobals())["ItemPresets"]!.AsObject();
        foreach (var tpl in _threat.Archetypes.SelectMany(a => new[] { a.ArmorTpl, a.HelmetTpl }).Where(t => t.Length > 0).Distinct())
        {
            var preset = presets.Select(p => p.Value!).FirstOrDefault(p => p["_encyclopedia"]?.ToString() == tpl);
            if (preset?["_items"] is not JsonArray items || !items.Any(i => i?["_tpl"]?.ToString() == tpl))
                throw new InvalidDataException("Missing native armor preset: " + tpl);
            _armorPresets[tpl] = items.DeepClone().AsArray();
        }
        log.Success($"[SPT-Z] Threat generator: {_threat.Archetypes.Count} archetypes; bounded seeded plans; native armor presets validated.", null);
    }
    private void StartThreatPlan(MongoId session, string map, JsonObject spec)
    {
        if (!_threat.Enabled) return;
        var total = (int)spec["total"]!;
        var scale = total / (double)(int)Maps[map]!["total"]!;
        if (spec["crisis"] is not null) scale *= 1.08;
        var plan = ThreatPolicy.Generate(_threat, map, (int)spec["rank"]!, total, (int)spec["shooters"]!, RandomNumberGenerator.GetInt32(1, int.MaxValue), scale);
        var raid = new ThreatRaid { Plan = plan };
        foreach (var unit in plan.Units) (unit.Armed ? raid.Armed : raid.Melee).Enqueue(unit);
        _threatRaids[session.ToString()] = raid;
        spec["threatSeed"] = plan.Seed; spec["threatBudget"] = plan.Budget; spec["threatSpent"] = plan.Spent;
        CaptureDiagnostic(() => Trace("threat.plan", session, new {
            map, seed = plan.Seed, revision = _threat.Revision, budget = plan.Budget, spent = plan.Spent,
            renderCost = plan.RenderCost, total, shooters = (int)spec["shooters"]!,
            composition = plan.Units.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => new { count = g.Count(), score = g.Sum(a=>a.Score) }),
            units = plan.Units.Select((a, i) => new { index = i, type = a.Id, score = a.Score,wear = a.WearCode }), performanceCapUnchanged = true
        }));
    }
    private bool ApplyThreatBot(JsonNode bot, string role, MongoId session)
    {
        if (!_threat.Enabled) return false;
        if (!_threatRaids.TryGetValue(session.ToString(), out var raid))
        {
            var map = MapFor(session); var spec = MapSpecFor(session, map)!;
            StartThreatPlan(session, map, spec); raid = _threatRaids[session.ToString()];
        }
        var id = bot["_id"]!.ToString();
        var armed = role.Equals("infectedPmc", StringComparison.OrdinalIgnoreCase) && bot["Info"]?["Settings"]?["BotDifficulty"]?.ToString() == "hard";
        ThreatArchetype unit; bool surplus = false;
        lock (raid)
        {
            if (!raid.Assigned.TryGetValue(id, out unit!))
            {
                var queue = armed ? raid.Armed : raid.Melee;
                if (queue.Count > 0) unit = queue.Dequeue();
                else { unit = _threat.Archetypes.Where(a => a.Armed == armed && a.MinRank == 1).OrderBy(a => a.Score).First(); surplus = true; raid.Surplus++; }
                raid.Assigned[id] = unit;
            }
        }
        bot["_id"] = ThreatPolicy.Encode(id, _threat.Revision, unit.Key,unit.WearCode);
        if (bot["Health"]?["BodyParts"] is JsonObject parts)
            foreach (var (part, body) in parts)
            {
                var hp = part == "Head" ? unit.Head : part == "Chest" ? unit.Chest : part == "Stomach" ? (int)(unit.Chest * .7) : 60;
                if (body?["Health"] is JsonObject health) { health["Current"] = hp; health["Maximum"] = hp; }
            }
        EquipThreatArmor(bot, unit.ArmorTpl, "ArmorVest",unit);
        EquipThreatArmor(bot, unit.HelmetTpl, "Headwear",unit);
        CaptureDiagnostic(() => Trace("threat.profile", session, new { type = unit.Id, score = unit.Score, head = unit.Head, chest = unit.Chest, speed = unit.Speed, sightRange = unit.SightRange, hearing = unit.Hearing, reaction = unit.ReactionSeconds, armor = unit.ArmorClass, helmet = unit.HelmetClass, wear = unit.WearCode, plateFraction = unit.PlateFraction, armed, surplusCacheProfile = surplus }));
        return true;
    }
    private void EquipThreatArmor(JsonNode bot, string tpl, string slot,ThreatArchetype unit)
    {
        var inventory = bot["Inventory"]!; var items = inventory["items"]!.AsArray();
        var removed = items.Where(i => i?["slotId"]?.ToString() == slot).Select(i => i!["_id"]!.ToString()).ToHashSet();
        bool changed; do { changed = false; foreach (var i in items) if (removed.Contains(i?["parentId"]?.ToString() ?? "")) changed |= removed.Add(i!["_id"]!.ToString()); } while (changed);
        for (int i = items.Count - 1; i >= 0; i--) if (removed.Contains(items[i]!["_id"]!.ToString())) items.RemoveAt(i);
        if (tpl.Length == 0) return;
        var preset = _armorPresets[tpl].DeepClone().AsArray();
        var ids = preset.ToDictionary(i => i!["_id"]!.ToString(), _ => Guid.NewGuid().ToString("N")[..24]);
        var root = preset.First(i => i?["_tpl"]?.ToString() == tpl)!;
        var rootId = root["_id"]!.ToString();
        foreach (var item in preset)
        {
            var old = item!["_id"]!.ToString(); item["_id"] = ids[old];
            if (old == rootId) { item["parentId"] = inventory["equipment"]!.ToString(); item["slotId"] = slot; }
            else item["parentId"] = ids[item["parentId"]!.ToString()];
            var max = (float?)_items[item["_tpl"]!.ToString()]["_props"]?["MaxDurability"] ?? 0;
            if (max > 0) {
                var plate=_items[item["_tpl"]!.ToString()]["_props"]?["armorPlateColliders"] is JsonArray masks&&masks.Count>0;
                var fraction=plate?unit.PlateFraction:unit.SoftFraction;var ceiling=plate?.55f+.0035f*unit.WearCode:.80f+.0015f*unit.WearCode;
                var current=Math.Max(1,Math.Round(max*fraction,1));var maximum=Math.Max(current,Math.Round(max*ceiling,1));
                item["upd"] ??= new JsonObject(); item["upd"]!["Repairable"] = new JsonObject { ["Durability"] = current, ["MaxDurability"] = maximum };
            }
            items.Add(item.DeepClone());
        }
    }
}
