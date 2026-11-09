using System.Text.Json.Nodes;
using System.Globalization;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;

namespace Codex.ZombieSurvival.Rules;

public sealed record AddedQuestLoot(string Container, string Template, float Weight);

public static class QuestLootPolicy
{
    // Ratios are relative to the pre-existing pool, not a guaranteed per-box drop rate.
    // Used by native lazy world-loot table transforms; owned inventories are never changed.
    public static IReadOnlyList<AddedQuestLoot> Apply(Dictionary<MongoId, StaticLootDetails>? tables, JsonObject rules, int rank)
    {
        if (tables is null) return [];
        if ((int?)rules["schema"] != 1) throw new InvalidDataException("Unsupported quest-loot schema.");
        var containers = rules["containers"]!.AsArray().Select(n => (MongoId)n!.ToString()).ToArray();
        var candidates = rules["items"]!.AsArray().Select(n => new {
            Template = (MongoId)n!["tpl"]!.ToString(),
            MinimumRank = (int)n["minimumRank"]!,
            Fraction = double.TryParse(n["weightFraction"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN
        }).ToArray();
        if (containers.Distinct().Count() != containers.Length || candidates.Select(x => x.Template).Distinct().Count() != candidates.Length
            || candidates.Any(x => x.MinimumRank is < 1 or > 6 || !double.IsFinite(x.Fraction) || x.Fraction <= 0)
            || candidates.Sum(x => x.Fraction) > .01)
            throw new InvalidDataException("Invalid quest-loot source specification.");
        var authored = candidates.Select(x => x.Template).ToHashSet();
        var added = new List<AddedQuestLoot>();
        foreach (var container in containers)
        {
            if (!tables.TryGetValue(container, out var pool)
                || pool.ItemCountDistribution?.Any(x => x.Count > 0 && x.RelativeProbability > 0) != true) continue;
            var distribution = pool.ItemDistribution?.ToList() ?? [];
            var baseline = distribution.Where(x => !authored.Contains(x.Tpl) && x.RelativeProbability > 0)
                .Sum(x => (double)x.RelativeProbability!.Value);
            if (!double.IsFinite(baseline) || baseline <= 0) continue;
            var changed = false;
            foreach (var candidate in candidates.Where(x => x.MinimumRank <= rank))
            {
                if (distribution.Any(x => x.Tpl == candidate.Template && x.RelativeProbability > 0)) continue;
                var weight = (float)Math.Max(1, Math.Round(baseline * candidate.Fraction));
                distribution.RemoveAll(x => x.Tpl == candidate.Template);
                distribution.Add(new ItemDistribution { Tpl = candidate.Template, RelativeProbability = weight });
                added.Add(new(container.ToString(), candidate.Template.ToString(), weight));
                changed = true;
            }
            if (changed) pool.ItemDistribution = distribution;
        }
        return added;
    }
}
