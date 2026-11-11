using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;

namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    private readonly Dictionary<MongoId, KeyWear> _rareWorldKeys = [];
    private readonly HashSet<(string Map, string Point, MongoId Key)> _fixedWorldKeySources = [];
    private const string MechanicalKey = "5c99f98d86f7745c314214b3";
    private const string Keycard = "5c164d2286f774194c5e69fa";

    private sealed class KeyWear
    {
        internal readonly int MaxUses;
        internal readonly double WeightMultiplier;
        private readonly int[] _weights;
        private readonly int _totalWeight;

        internal KeyWear(int maxUses, int[] remainingUseWeights, double weightMultiplier)
        {
            MaxUses = maxUses;
            _weights = remainingUseWeights;
            _totalWeight = _weights.Sum();
            WeightMultiplier = weightMultiplier;
        }

        internal int RollRemainingUses(Random random)
        {
            var draw = random.Next(_totalWeight);
            for (var index = 0; index < _weights.Length - 1; index++)
            {
                draw -= _weights[index];
                if (draw < 0) return index + 1;
            }
            return _weights.Length;
        }
    }

    private void ConfigureRareKeyLoot()
    {
        var rules = Read("data/rare-keys.json");
        if ((int?)rules["schema"] != 3) throw new InvalidDataException("Unsupported rare key loot schema.");
        var weights = rules["remainingUseWeights"]!.AsArray().Select(value => (int)value!).ToArray();
        if (weights.Length != 10 || weights[^1] <= 0 ||
            weights.Zip(weights.Skip(1)).Any(pair => pair.First <= pair.Second))
            throw new InvalidDataException("World keys require ten positive, descending remaining-use weights.");
        var minimumPrice = (int)rules["ordinaryMinimumPrice"]!;
        var ordinaryMultiplier = (double)rules["ordinaryWeightMultiplier"]!;
        if (!double.IsFinite(ordinaryMultiplier) || ordinaryMultiplier < 1)
            throw new InvalidDataException("Invalid ordinary world key weight multiplier.");
        var averageRemaining = weights.Select((weight, index) => weight * (index + 1d)).Sum() / weights.Sum();
        var ordinary = new KeyWear(40, weights, ordinaryMultiplier);
        var tenUse = new KeyWear(10, weights, 10 / averageRemaining);
        foreach (var (tpl, item) in _items)
        {
            if (item["_type"]?.ToString() != "Item" || item["_parent"]?.ToString() is not (MechanicalKey or Keycard) ||
                item["_props"] is not { } props || (bool?)props["QuestItem"] == true ||
                (int?)props["MaximumNumberOfUsage"] is not ((10 or 40) and var capacity) || IsClosedMapKey(tpl)) continue;
            var id = (MongoId)tpl;
            if (capacity == 40 && handbookHelper.GetTemplatePrice(id) < minimumPrice) continue;
            _rareWorldKeys.Add(id, capacity == 10 ? tenUse : ordinary);
        }

        foreach (var (key, location) in db.GetLocations().GetDictionary())
        {
            var map = location.Base?.Id?.ToLowerInvariant() ?? key.ToLowerInvariant();
            if (Maps[map] is not JsonObject) continue;
            RememberFixedKeySources(map, location.LooseLoot?.Value);
            if (location.StaticContainers?.Value?.StaticForced is { } fixedContainerItems)
                foreach (var item in fixedContainerItems)
                    if (_rareWorldKeys.ContainsKey(item.ItemTpl))
                        _fixedWorldKeySources.Add((map, item.ContainerId, item.ItemTpl));
            location.StaticLoot?.AddTransformer(tables =>
            {
                if (tables is null) return tables;
                foreach (var table in tables.Values)
                {
                    if (table.ItemDistribution is { } options) ReweightKeyContainer(options);
                }
                return tables;
            });
            location.LooseLoot?.AddTransformer(loot =>
            {
                if (loot?.Spawnpoints is { } points)
                    foreach (var point in points) ReweightLooseKeyPoint(point);
                return loot;
            });
        }
        log.Info($"[SPT-Z] World key loot: {_rareWorldKeys.Count} repeatable key templates with partial charges.", null);
    }

    private void RememberFixedKeySources(string map, LooseLoot? loot)
    {
        if (loot is null) return;
        var fixedPoints = (loot.SpawnpointsForced ?? []).Concat((loot.Spawnpoints ?? [])
            .Where(point => point.Probability == 1 || point.Template?.IsAlwaysSpawn == true));
        foreach (var point in fixedPoints)
        {
            if (point.Template is not { Id: { } id, Items: { } items }) continue;
            foreach (var item in items)
                if (_rareWorldKeys.ContainsKey(item.Template)) _fixedWorldKeySources.Add((map, id, item.Template));
        }
    }

    private void ReweightKeyContainer(IEnumerable<ItemDistribution> options)
    {
        var keys = new List<(ItemDistribution Option, double Multiplier)>();
        var other = new List<ItemDistribution>();
        double extra = 0, otherWeight = 0;
        foreach (var option in options)
        {
            if (option.RelativeProbability is not > 0) continue;
            var weight = (double)option.RelativeProbability.Value;
            if (_rareWorldKeys.TryGetValue(option.Tpl, out var wear))
            {
                keys.Add((option, wear.WeightMultiplier));
                extra += weight * (wear.WeightMultiplier - 1);
            }
            else
            {
                other.Add(option);
                otherWeight += weight;
            }
        }
        if (keys.Count == 0 || extra <= 0) return;
        // Keep the total weight fixed so each key gets its intended per-draw odds.
        // Reserve half the other pool's weight even if a custom pool is already key-heavy.
        var boost = Math.Min(1, otherWeight * .5 / extra);
        var otherScale = otherWeight == 0 ? 1 : 1 - extra * boost / otherWeight;
        foreach (var (option, multiplier) in keys)
            option.RelativeProbability *= (float)(1 + (multiplier - 1) * boost);
        foreach (var option in other)
            option.RelativeProbability *= (float)otherScale;
    }

    private void ReweightLooseKeyPoint(Spawnpoint point)
    {
        if (point.Template is not { IsAlwaysSpawn: not true, Items: { } items } ||
            point.Probability is not (> 0 and < 1) || point.ItemDistribution is not { } options ||
            !items.Any(item => _rareWorldKeys.ContainsKey(item.Template))) return;
        var variants = items.Where(item => _rareWorldKeys.ContainsKey(item.Template) && !string.IsNullOrEmpty(item.ComposedKey))
            .ToLookup(item => item.ComposedKey);
        if (variants.Count == 0) return;

        var total = options.Sum(option => Math.Max(0, option.RelativeProbability.GetValueOrDefault()));
        if (total <= 0) return;
        var newTotal = total;
        foreach (var option in options)
        {
            if (option.RelativeProbability is not > 0 || option.ComposedKey?.Key is not { } key ||
                variants[key].FirstOrDefault() is not { } item) continue;
            var old = option.RelativeProbability.Value;
            option.RelativeProbability = old * _rareWorldKeys[item.Template].WeightMultiplier;
            newTotal += option.RelativeProbability.Value - old;
        }
        // Native loose loot first draws a point, then a variant within that point.
        // Give the point the variant pool's added weight, including single-item keycard spots.
        point.Probability = Math.Min(.999999, point.Probability.Value * newTotal / total);
    }

    private void WearRareWorldKeys(JsonArray items, string map, string point)
    {
        foreach (var item in items.OfType<JsonObject>())
        {
            if (item["_tpl"]?.ToString() is not { } tpl) continue;
            var id = (MongoId)tpl;
            if (!_rareWorldKeys.TryGetValue(id, out var wear) || _fixedWorldKeySources.Contains((map, point, id))) continue;
            var remaining = wear.RollRemainingUses(Random.Shared);
            var update = item["upd"] as JsonObject ?? new JsonObject();
            var key = update["Key"] as JsonObject ?? new JsonObject();
            key["NumberOfUsages"] = Math.Max((int?)key["NumberOfUsages"] ?? 0, wear.MaxUses - remaining);
            if (key.Parent is null) update["Key"] = key;
            if (update.Parent is null) item["upd"] = update;
        }
    }
}
