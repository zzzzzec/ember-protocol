using System.Text.Json.Nodes;

namespace Codex.ZombieSurvival;

public static class AmmoPackPolicy
{
    public sealed record Pack(string Ammunition, int Capacity, int StackMaximum);
    public static Pack? Describe(IReadOnlyDictionary<string, JsonObject> templates, string tpl)
    {
        if (!templates.TryGetValue(tpl, out var box) || box["_props"]?["StackSlots"] is not JsonArray { Count: 1 } slots) return null;
        var parent = tpl; var seen = new HashSet<string>(); var isBox = false;
        while (templates.TryGetValue(parent, out var item) && seen.Add(parent))
        {
            if (parent == "543be5cb4bdc2deb348b4568") { isBox = true; break; }
            parent = item["_parent"]?.ToString() ?? "";
        }
        if (!isBox) return null;
        var slot = slots[0]!;
        if (slot["_name"]?.ToString() != "cartridges" || slot["_props"]?["filters"] is not JsonArray { Count: 1 } filters
            || filters[0]?["Filter"] is not JsonArray { Count: 1 } allowed) return null;
        var ammo = allowed[0]!.ToString(); var capacity = (int?)slot["_max_count"] ?? 0;
        if (capacity <= 0 || !templates.TryGetValue(ammo, out var cartridge) || cartridge["_props"]?["ammoType"] is null) return null;
        var maximum = (int?)cartridge["_props"]?["StackMaxSize"] ?? 0;
        return maximum > 0 ? new Pack(ammo, capacity, maximum) : null;
    }

    // Only call for newly generated raid loot, never an owned inventory or a saved profile.
    // Keep native stack maxima: a 120-round box may require two 60-round children.
    public static int Fill(JsonArray loot, IReadOnlyDictionary<string, JsonObject> templates, Func<string>? newId = null)
    {
        newId ??= () => Guid.NewGuid().ToString("N")[..24];
        var rows = loot.OfType<JsonObject>().ToArray();
        var children = rows.Where(i => i["parentId"] is not null).GroupBy(i => i["parentId"]!.ToString()).ToDictionary(g => g.Key, g => g.ToList());
        var count = 0;
        foreach (var box in rows)
        {
            if (Describe(templates, box["_tpl"]!.ToString()) is not { } pack) continue;
            var id = box["_id"]!.ToString();
            var contents = children.GetValueOrDefault(id, []).Where(i => i["slotId"]?.ToString() == "cartridges").ToArray();
            // Never silently convert an unexpected mixed/custom load into another cartridge.
            if (contents.Any(i => i["_tpl"]!.ToString() != pack.Ammunition)) continue;
            var needed = (pack.Capacity + pack.StackMaximum - 1) / pack.StackMaximum;
            var remaining = pack.Capacity;
            for (var index = 0; index < needed; index++)
            {
                var child = index < contents.Length ? contents[index] : new JsonObject {
                    ["_id"] = newId(), ["_tpl"] = pack.Ammunition, ["parentId"] = id, ["slotId"] = "cartridges",
                    ["upd"] = new JsonObject { ["SpawnedInSession"] = (bool?)box["upd"]?["SpawnedInSession"] ?? true }
                };
                child["location"] = index;
                child["upd"] ??= new JsonObject();
                var amount = Math.Min(remaining, pack.StackMaximum);
                child["upd"]!["StackObjectsCount"] = amount; remaining -= amount;
                if (index >= contents.Length) loot.Add(child);
            }
            foreach (var surplus in contents.Skip(needed)) loot.Remove(surplus);
            count++;
        }
        return count;
    }
}
