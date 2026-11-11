using HarmonyLib;
using SPTarkov.Server.Core.Generators;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils.Collections;

namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    [ThreadStatic]
    private static LootScope? _abundanceLootScope;

    private readonly Dictionary<MongoId, double> _abundancePrices = [];

    private sealed class LootScope(MongoId session, string map, double multiplier)
    {
        internal readonly MongoId Session = session;
        internal readonly string Map = map;
        internal readonly double Multiplier = multiplier;
        // Native placement and guaranteed points damp the requested increase differently by map.
        internal readonly double LooseQuantity = Quantity(multiplier, map switch
        {
            "interchange" => .15,
            "laboratory" => .45,
            _ => 0
        });
        internal readonly double StaticQuantity = Quantity(multiplier, map switch
        {
            "sandbox" => .28,
            "bigmap" => .35,
            "interchange" => .47,
            "rezervbase" => .17,
            _ => .33
        });
        internal readonly double Quality = Math.Pow(multiplier, .22);
        internal int StaticExtraDraws;
        internal int LoosePoolsReweighted;

        private static double Quantity(double target, double placementCorrection) =>
            Math.Pow(target, .78) * (1 + placementCorrection * (target - 1) / 2);
    }

    private void InitializeAbundanceLoot()
    {
        foreach (var (tpl, item) in _items)
        {
            if (item["_type"]?.ToString() != "Item" || item["_props"] is not { } props ||
                (bool?)props["QuestItem"] == true || IsClosedMapKey(tpl) ||
                item["_parent"]?.ToString() is MechanicalKey or Keycard) continue;
            var id = (MongoId)tpl;
            var price = handbookHelper.GetTemplatePrice(id);
            if (AmmoPackPolicy.Describe(_items, tpl) is { } pack)
                price += handbookHelper.GetTemplatePrice((MongoId)pack.Ammunition) * pack.Capacity;
            if (price > 0) _abundancePrices[id] = price;
        }
    }

    private void InstallAbundanceLoot(Harmony harmony)
    {
        var hooks = typeof(AbundanceLootPatches);
        harmony.Patch(AccessTools.DeclaredMethod(typeof(LocationLifecycleService), "GenerateLocationAndLoot"),
            prefix: new HarmonyMethod(hooks, nameof(AbundanceLootPatches.Begin)),
            finalizer: new HarmonyMethod(hooks, nameof(AbundanceLootPatches.End)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(LocationLootGenerator), "GetWeightedCountOfContainerItems"),
            postfix: new HarmonyMethod(hooks, nameof(AbundanceLootPatches.StaticCount)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(LocationLootGenerator), "GetPossibleLootItemsForContainer"),
            postfix: new HarmonyMethod(hooks, nameof(AbundanceLootPatches.StaticQuality)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(LocationLootGenerator), "GetLooseLootMultiplierForLocation"),
            postfix: new HarmonyMethod(hooks, nameof(AbundanceLootPatches.LooseCount)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(LocationLootGenerator), "GenerateDynamicLoot"),
            prefix: new HarmonyMethod(hooks, nameof(AbundanceLootPatches.LooseQuality)));
    }

    private readonly record struct PriceTilt(double Mean, double MeanScore, double Strength)
    {
        private static double Score(double price, double mean) => price / (price + mean);

        internal static PriceTilt Create(IEnumerable<(double Weight, double Price)> entries, double factor)
        {
            double weight = 0, value = 0;
            foreach (var row in entries)
            {
                weight += row.Weight;
                value += row.Weight * row.Price;
            }
            if (weight <= 0 || value <= 0) return default;
            var mean = value / weight;
            double scoreSum = 0, slope = 0;
            foreach (var row in entries)
            {
                var score = Score(row.Price, mean);
                scoreSum += row.Weight * score;
                slope += row.Weight * (row.Price - mean) * score;
            }
            if (slope <= 0) return default;
            var meanScore = scoreSum / weight;
            // A centred score conserves pool weight. The bounded, saturating
            // score stops rare valuables from taking over the normal distribution.
            var strength = Math.Min((factor - 1) * value / slope, Math.Min(.35 / meanScore, .65 / (1 - meanScore)));
            return new(mean, meanScore, strength);
        }

        internal double WeightFactor(double price) => 1 + Strength * (Score(price, Mean) - MeanScore);
    }

    private static class AbundanceLootPatches
    {
        private sealed record LoosePricePool(Spawnpoint Point, double TotalWeight,
            List<(LooseLootItemDistribution Option, double Price)> Options);

        public static void Begin(MongoId __0, string __1, bool __2, out LootScope? __state)
        {
            __state = _abundanceLootScope;
            _abundanceLootScope = null;
            var map = AbundanceMap(__1);
            if (!__2 || !AbundanceMapNames.ContainsKey(map)) return;
            var progress = Current.AbundanceFor(__0)[map];
            _abundanceLootScope = new LootScope(__0, map, (double)progress.Total);
        }

        public static void End(LootScope? __state, Exception? __exception)
        {
            var scope = _abundanceLootScope;
            _abundanceLootScope = __state;
            if (scope is null || __exception is not null) return;
            Current.CaptureDiagnostic(() => Current.Trace("abundance.loot", scope.Session, new
            {
                map = scope.Map, multiplier = scope.Multiplier, containerCountTarget = scope.StaticQuantity,
                looseCountTarget = scope.LooseQuantity,
                priceTarget = scope.Quality, containerExtraDraws = scope.StaticExtraDraws,
                loosePoolsReweighted = scope.LoosePoolsReweighted
            }));
        }

        public static void StaticCount(ref int __result)
        {
            if (_abundanceLootScope is not { StaticQuantity: > 1 } scope || __result <= 0) return;
            var extra = __result * (scope.StaticQuantity - 1);
            var rounded = (int)extra + (Random.Shared.NextDouble() < extra % 1 ? 1 : 0);
            __result += rounded;
            scope.StaticExtraDraws += rounded;
        }

        public static void StaticQuality(ProbabilityObjectArray<MongoId, float?> __result)
        {
            if (_abundanceLootScope is not { Quality: > 1 } scope || __result.Count < 2) return;
            var prices = Current._abundancePrices;
            var entries = __result.Where(row => row.RelativeProbability is > 0 && prices.ContainsKey(row.Key))
                .Select(row => (row.RelativeProbability!.Value, prices[row.Key]));
            var tilt = PriceTilt.Create(entries, scope.Quality);
            if (tilt.Strength <= 0) return;
            foreach (var row in __result)
                if (row.RelativeProbability is > 0 && prices.TryGetValue(row.Key, out var price))
                    row.RelativeProbability *= tilt.WeightFactor(price);
        }

        public static void LooseCount(ref double __result)
        {
            if (_abundanceLootScope is { LooseQuantity: > 1 } scope) __result *= scope.LooseQuantity;
        }

        public static void LooseQuality(LooseLoot __0)
        {
            if (_abundanceLootScope is not { Quality: > 1 } scope || __0.Spawnpoints is not { } points) return;
            var prices = Current._abundancePrices;
            var pools = new List<LoosePricePool>();
            foreach (var point in points)
            {
                if (point.Probability is not (> 0 and < 1) || point.Template is not { IsAlwaysSpawn: not true, Items: { } items } ||
                    point.ItemDistribution is not { } options) continue;
                var templates = items.Where(item => !string.IsNullOrEmpty(item.ComposedKey)).ToLookup(item => item.ComposedKey);
                var total = 0d;
                var priced = new List<(LooseLootItemDistribution, double)>();
                foreach (var option in options)
                {
                    if (option.RelativeProbability is not > 0 || option.ComposedKey?.Key is not { } key ||
                        templates[key].FirstOrDefault() is not { } item) continue;
                    total += option.RelativeProbability.Value;
                    if (prices.TryGetValue(item.Template, out var price)) priced.Add((option, price));
                }
                if (priced.Count == 0) continue;
                pools.Add(new LoosePricePool(point, total, priced));
            }
            var entries = pools.SelectMany(pool => pool.Options.Select(row =>
                (pool.Point.Probability!.Value / pool.TotalWeight * row.Option.RelativeProbability!.Value, row.Price)));
            var tilt = PriceTilt.Create(entries, scope.Quality);
            if (tilt.Strength <= 0) return;
            foreach (var pool in pools)
            {
                var total = pool.TotalWeight;
                foreach (var (option, price) in pool.Options)
                {
                    var old = option.RelativeProbability!.Value;
                    option.RelativeProbability = old * tilt.WeightFactor(price);
                    total += option.RelativeProbability.Value - old;
                }
                // Keep the native two-stage draw equivalent to reweighting the
                // point/variant pair. Exactly 1 means guaranteed to the native generator.
                var pointWeight = pool.Point.Probability!.Value * total / pool.TotalWeight;
                pool.Point.Probability = pointWeight == 1 ? Math.BitDecrement(1d) : pointWeight;
            }
            scope.LoosePoolsReweighted += pools.Count;
        }
    }
}
