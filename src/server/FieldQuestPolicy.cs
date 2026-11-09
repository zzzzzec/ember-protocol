using System.Globalization;
using System.Text.Json.Nodes;
namespace Codex.ZombieSurvival;

public static class FieldQuestPolicy
{
    private static double Number(JsonNode? n, double fallback = 0) => double.TryParse(n?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : fallback;
    public static bool ObjectiveReady(JsonObject pmc, JsonObject condition, string questId)
    {
        var id = condition["id"]!.ToString();
        if (Number(pmc["TaskConditionCounters"]?[id]?["value"]) >= Number(condition["value"], 1)) return true;
        return pmc["Quests"]?.AsArray().Any(q => q?["qid"]?.ToString() == questId && (q["completedConditions"]?.AsArray().Any(c => c?.ToString() == id) ?? false)) == true;
    }
    public static string? ValidateReturn(JsonObject spec, JsonArray inventory, string rootId, IReadOnlyDictionary<string, JsonObject> templates)
    {
        var all = inventory.OfType<JsonObject>().ToDictionary(i => i["_id"]!.ToString());
        if (!all.TryGetValue(rootId, out var root) || root["_tpl"]?.ToString() != spec["weapon"]!.ToString()) return "这不是借条上的枪械型号。";
        var tree = new List<JsonObject> { root }; var seen = new HashSet<string> { rootId };
        for (var index = 0; index < tree.Count; index++)
            foreach (var item in all.Values.Where(i => i["parentId"]?.ToString() == tree[index]["_id"]!.ToString()))
                if (seen.Add(item["_id"]!.ToString())) tree.Add(item);
        if (tree.Any(i => !templates.ContainsKey(i["_tpl"]!.ToString()))) return "枪械包含未知配件。";
        var props = templates[root["_tpl"]!.ToString()]["_props"]!;
        var durability = Number(root["upd"]?["Repairable"]?["Durability"], Number(props["MaxDurability"], 100));
        if (durability < Number(spec["minimumDurability"], 50)) return "枪械当前耐久不足50，请修复或换一支同型号枪。";
        var tpls = tree.Select(i => i["_tpl"]!.ToString()).ToHashSet();
        if (spec["requiredParts"]!.AsArray().Any(p => !tpls.Contains(p!.ToString()))) return "借条上要求的配件还没装齐。";
        if (tree.Skip(1).Any(i => templates[i["_tpl"]!.ToString()]["_props"]?["ammoType"] is not null && Number(i["upd"]?["StackObjectsCount"], 1) > 0)) return "请先退出弹匣和膛内的弹药，再交还枪械。";
        if (spec["exactParts"] is JsonArray exactParts)
        {
            var attached = tree.Skip(1).Select(i => i["_tpl"]!.ToString()).Order(StringComparer.Ordinal);
            if (!attached.SequenceEqual(exactParts.Select(part => part!.ToString()).Order(StringComparer.Ordinal)))
                return "请把枪恢复成试驾清单上的完整配置再交还。";
        }
        bool IsA(string tpl, string category)
        {
            var visited = new HashSet<string>();
            while (visited.Add(tpl) && templates.TryGetValue(tpl, out var item))
            { if (tpl == category) return true; tpl = item["_parent"]?.ToString() ?? ""; }
            return false;
        }
        foreach (var item in tree)
        {
            var itemProps = templates[item["_tpl"]!.ToString()]["_props"]!;
            var children = tree.Where(i => i["parentId"]?.ToString() == item["_id"]!.ToString()).ToArray();
            var slots = itemProps["Slots"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
            foreach (var slot in slots)
            {
                var matches = children.Where(c => c["slotId"]?.ToString() == slot["_name"]?.ToString()).ToArray();
                if (matches.Length > 1) return "枪械配件槽位重复，请检查装配。";
                if ((bool?)slot["_required"] == true && matches.Length == 0) return "枪械缺少必要零件，无法使用。";
                foreach (var child in matches)
                {
                    var filters = slot["_props"]?["filters"]?.AsArray();
                    if (filters is null || filters.Count == 0) continue;
                    var tpl = child["_tpl"]!.ToString();
                    var allowed = filters.Any(f => f?["Filter"]?.AsArray().Any(t => IsA(tpl, t!.ToString())) == true);
                    var excluded = filters.Any(f => f?["ExcludedFilter"]?.AsArray().Any(t => IsA(tpl, t!.ToString())) == true);
                    if (!allowed || excluded) return "枪械配件不兼容，请检查装配。";
                }
            }
            foreach (var conflict in itemProps["ConflictingItems"]?.AsArray() ?? [])
                if (tpls.Contains(conflict!.ToString())) return "枪械配件互相冲突，请检查装配。";
            foreach (var child in children)
                if (!slots.Any(s => s["_name"]?.ToString() == child["slotId"]?.ToString())) return "枪械配件装在了不正确的槽位。";
        }
        var magazine = tree.FirstOrDefault(i => i["slotId"]?.ToString() == "mod_magazine");
        var capacity = magazine is null ? 0 : templates[magazine["_tpl"]!.ToString()]["_props"]?["Cartridges"]?.AsArray().Sum(c => Number(c?["_max_count"])) ?? 0;
        if (capacity < Number(spec["minimumMagazineCapacity"], 1)) return "请装回借条要求容量的兼容弹匣。";
        return null;
    }
}
