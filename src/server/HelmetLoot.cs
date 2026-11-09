using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;

namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    private const string Headwear = "5a341c4086f77401f2541505";
    private sealed record HelmetInsert(string Slot, string Template);
    private readonly Dictionary<string, List<HelmetInsert>> _helmetInserts = [];

    private void InitializeHelmetLoot()
    {
        foreach (var (tpl, item) in _items)
        {
            if (item["_parent"]?.ToString() != Headwear || item["_props"]?["Slots"] is not JsonArray slots) continue;
            foreach (var slot in slots.OfType<JsonObject>())
            {
                if ((bool?)slot["_required"] != true || slot["_name"]?.ToString() is not { Length: > 0 } name ||
                    slot["_props"]?["filters"] is not JsonArray { Count: 1 } filters ||
                    (bool?)filters[0]?["locked"] != true || filters[0]?["Plate"]?.ToString() is not { Length: > 0 } plate ||
                    filters[0]?["Filter"] is not JsonArray { Count: 1 } allowed || allowed[0]?.ToString() != plate ||
                    !_items.ContainsKey(plate)) continue;
                if (!_helmetInserts.TryGetValue(tpl, out var inserts)) _helmetInserts[tpl] = inserts = [];
                inserts.Add(new(name, plate));
            }
        }
    }

    // Native slots enforce the lock. Restore only absent fixed inserts on freshly
    // generated loot, leaving fitted parts, optional accessories and wear intact.
    private void CompleteHelmetLoot(JsonArray loot)
    {
        var helmets = loot.OfType<JsonObject>().Where(i => _helmetInserts.ContainsKey(i["_tpl"]!.ToString())).ToArray();
        if (helmets.Length == 0) return;
        var slots = loot.OfType<JsonObject>().Where(i => i["parentId"] is not null && i["slotId"] is not null)
            .ToLookup(i => (i["parentId"]!.ToString(), i["slotId"]!.ToString()));
        foreach (var helmet in helmets)
        {
            var id = helmet["_id"]!.ToString();
            foreach (var insert in _helmetInserts[helmet["_tpl"]!.ToString()])
            {
                if (slots.Contains((id, insert.Slot))) continue;
                var child = new JsonObject { ["_id"] = new MongoId().ToString(), ["_tpl"] = insert.Template,
                    ["parentId"] = id, ["slotId"] = insert.Slot };
                if (helmet["upd"]?["SpawnedInSession"] is { } fir)
                    child["upd"] = new JsonObject { ["SpawnedInSession"] = fir.DeepClone() };
                loot.Add(child);
            }
        }
    }
}
