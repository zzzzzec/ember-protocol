using System.Text.Json.Nodes;
using Codex.ZombieSurvival;

internal static class AmmoChecks
{
    public static void Run(Dictionary<string, JsonObject> templates, Action<bool, string> check)
    {
        var boxes = templates.Keys.Select(tpl => (Tpl: tpl, Pack: AmmoPackPolicy.Describe(templates, tpl))).Where(x => x.Pack is not null).ToArray();
        check(boxes.Length > 100, "native ammunition pack coverage is not a synthetic single-box example");
        foreach (var (tpl, definition) in boxes)
        {
            var pack = definition!;
            var root = new JsonObject { ["_id"] = "aaaaaaaaaaaaaaaaaaaaaaaa", ["_tpl"] = tpl, ["upd"] = new JsonObject { ["SpawnedInSession"] = false } };
            var partial = new JsonObject { ["_id"] = "bbbbbbbbbbbbbbbbbbbbbbbb", ["_tpl"] = pack.Ammunition, ["parentId"] = root["_id"]!.ToString(), ["slotId"] = "cartridges", ["location"] = 0, ["upd"] = new JsonObject { ["StackObjectsCount"] = 1, ["SpawnedInSession"] = false } };
            var list = new JsonArray(root, partial);
            AmmoPackPolicy.Fill(list, templates);
            var rounds = list.OfType<JsonObject>().Where(x => x["parentId"]?.ToString() == root["_id"]!.ToString()).ToArray();
            check(rounds.Sum(x => (int)x["upd"]!["StackObjectsCount"]!) == pack.Capacity, "printed native pack capacity filled: " + tpl);
            check(rounds.All(x => (int)x["upd"]!["StackObjectsCount"]! <= pack.StackMaximum), "native ammo stack maximum respected: " + tpl);
            check(list[1]!["_id"]!.ToString() == "bbbbbbbbbbbbbbbbbbbbbbbb" && rounds.All(x => (bool?)x["upd"]?["SpawnedInSession"] == false), "existing identity and FIR flags preserved: " + tpl);
            var once = list.ToJsonString(); AmmoPackPolicy.Fill(list, templates);
            check(list.ToJsonString() == once, "refilling a full pack does not duplicate rounds or IDs: " + tpl);
            var empty = new JsonArray(root.DeepClone()); AmmoPackPolicy.Fill(empty, templates);
            check(empty.Skip(1).Sum(x => (int)x!["upd"]!["StackObjectsCount"]!) == pack.Capacity, "retained empty generated pack receives its declared contents: " + tpl);
            var absent = new JsonArray(); AmmoPackPolicy.Fill(absent, templates);
            check(absent.Count == 0, "discarded pack cannot be recreated by filling: " + tpl);
        }
        var loose = new JsonArray(new JsonObject { ["_id"] = "cccccccccccccccccccccccc", ["_tpl"] = "573719762459775a626ccbc1", ["upd"] = new JsonObject { ["StackObjectsCount"] = 3 } });
        var before = loose.ToJsonString(); AmmoPackPolicy.Fill(loose, templates);
        check(before == loose.ToJsonString(), "loose rounds are not expanded into a full stack");
        foreach (var (tpl, item) in templates.Where(x => x.Value["_parent"]?.ToString() == "5448bc234bdc2d3c308b4569"))
            check(AmmoPackPolicy.Describe(templates, tpl) is null, "magazines are not treated as sealed ammo packages: " + tpl);
        var first = boxes[0];
        var mixed = new JsonArray(new JsonObject { ["_id"] = "dddddddddddddddddddddddd", ["_tpl"] = first.Tpl }, new JsonObject { ["_id"] = "eeeeeeeeeeeeeeeeeeeeeeee", ["_tpl"] = "unrecognized-cartridge", ["parentId"] = "dddddddddddddddddddddddd", ["slotId"] = "cartridges", ["upd"] = new JsonObject { ["StackObjectsCount"] = 2 } });
        before = mixed.ToJsonString(); AmmoPackPolicy.Fill(mixed, templates);
        check(mixed.ToJsonString() == before, "unexpected custom contents are not converted to a different cartridge");
    }
}
