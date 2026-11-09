using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SPTarkov.Server.Core.Generators;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils.Collections;

namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    [ThreadStatic]
    private static LootScope? _abundanceLootScope;

    private readonly Dictionary<MongoId, double> _abundanceEligible = [];
    private bool _abundanceContainerDuplicates;

    private sealed class LootScope(MongoId session, string map, double increase)
    {
        internal readonly MongoId Session = session;
        internal readonly string Map = map;
        internal readonly double Increase = increase;
        internal int LooseExtras;
        internal int ContainerExtras;
    }

    private void InitializeAbundanceLoot()
    {
        _abundanceContainerDuplicates = configs.GetConfig<LocationConfig>().AllowDuplicateItemsInStaticContainers;
        var materials = new HashSet<string>(StringComparer.Ordinal)
        {
            "57864a66245977548f04a81f", // Electronics
            "57864ada245977548638de91", // Building materials
            "57864bb7245977548b3b66c2", // Tools
            "57864c322459775490116fbf", // Household goods
            "57864c8c245977548867e7f1", // Barter medical supplies
            "57864e4c24597754843f8723", // Lubricants
            "57864ee62459775490116fc1"  // Batteries
        };
        var basicCalibres = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Caliber9x18PM", "Caliber9x19PARA", "Caliber762x25TT", "Caliber9x21", "Caliber1143x23ACP",
            "Caliber12g", "Caliber20g", "Caliber545x39", "Caliber556x45NATO", "Caliber762x39", "Caliber366TKM"
        };
        foreach (var (tpl, item) in _items)
        {
            if (item["_type"]?.ToString() != "Item" || item["_props"] is not { } props ||
                (bool?)props["QuestItem"] == true) continue;
            var id = (MongoId)tpl;
            var category = Category(tpl);
            if (category == "ammo")
            {
                var ammo = AmmoPackPolicy.Describe(_items, tpl) is { } pack ? _items[pack.Ammunition]["_props"]! : props;
                if ((double?)ammo["PenetrationPower"] is >= 0 and <= 22 && basicCalibres.Contains(ammo["Caliber"]?.ToString() ?? ""))
                    _abundanceEligible[id] = .5;
                continue;
            }
            if (category is "medical" or "food" or "drink" or "drug")
            {
                if (handbookHelper.GetTemplatePrice(id) is > 0 and <= 12000) _abundanceEligible[id] = .5;
            }
            else if (category == "other" && materials.Contains(item["_parent"]?.ToString() ?? "") &&
                handbookHelper.GetTemplatePrice(id) is > 0 and <= 24000)
            {
                _abundanceEligible[id] = 1;
            }
        }
    }

    private void InstallAbundanceLoot(Harmony harmony)
    {
        var hooks = typeof(AbundanceLootPatches);
        harmony.Patch(AccessTools.DeclaredMethod(typeof(LocationLifecycleService), "GenerateLocationAndLoot"),
            prefix: new HarmonyMethod(hooks, nameof(AbundanceLootPatches.Begin)),
            finalizer: new HarmonyMethod(hooks, nameof(AbundanceLootPatches.End)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(LocationLootGenerator), "AddLootToContainer"),
            transpiler: new HarmonyMethod(hooks, nameof(AbundanceLootPatches.StaticLoot)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(LocationLootGenerator), "GenerateDynamicLoot"),
            transpiler: new HarmonyMethod(hooks, nameof(AbundanceLootPatches.DynamicLoot)));
    }

    private static class AbundanceLootPatches
    {
        public static void Begin(MongoId __0, string __1, bool __2, out LootScope? __state)
        {
            __state = _abundanceLootScope;
            _abundanceLootScope = null;
            var map = AbundanceMap(__1);
            if (!__2 || !AbundanceMapNames.ContainsKey(map)) return;
            var progress = Current.AbundanceFor(__0)[map];
            _abundanceLootScope = new LootScope(__0, map, (double)(progress.Total - 1m));
        }

        public static void End(LootScope? __state, Exception? __exception)
        {
            var scope = _abundanceLootScope;
            _abundanceLootScope = __state;
            if (scope is null || __exception is not null) return;
            Current.CaptureDiagnostic(() => Current.Trace("abundance.loot", scope.Session, new
            {
                map = scope.Map, multiplier = 1 + scope.Increase, looseExtras = scope.LooseExtras,
                containerExtraDraws = scope.ContainerExtras
            }));
        }

        // Keep native generation/placement. Extend the list of draws after the base
        // container picks, using the already-filtered native probability table.
        public static IEnumerable<CodeInstruction> StaticLoot(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            CheckLocal<ProbabilityObjectArray<MongoId, float?>>(original, 5);
            var codes = instructions.ToList();
            var call = codes.Single(code => IsEnumerableCall<MongoId>(code, nameof(Enumerable.Concat)));
            var index = codes.IndexOf(call);
            var options = new CodeInstruction(OpCodes.Ldloc_S, (byte)5);
            options.labels.AddRange(call.labels);
            call.labels.Clear();
            codes.Insert(index, options);
            call.operand = AccessTools.DeclaredMethod(typeof(AbundanceLootPatches), nameof(ExtendStatic));
            return codes;
        }

        // Append after native grouping and before item generation, seasonal filtering
        // and the native per-template spawn limits. Pass its forced and weighted points.
        public static IEnumerable<CodeInstruction> DynamicLoot(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            CheckLocal<List<Spawnpoint>>(original, 2);
            CheckLocal<ProbabilityObjectArray<string, Spawnpoint>>(original, 5);
            var codes = instructions.ToList();
            var anchor = codes.Single(code => IsEnumerableCall<Spawnpoint>(code, nameof(Enumerable.ToList)));
            codes.InsertRange(codes.IndexOf(anchor) + 1,
            [
                new CodeInstruction(OpCodes.Ldloc_2), new CodeInstruction(OpCodes.Ldloc_S, (byte)5),
                new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(AbundanceLootPatches), nameof(ExtendLoose)))
            ]);
            return codes;
        }

        private static bool IsEnumerableCall<T>(CodeInstruction code, string name) =>
            code.opcode == OpCodes.Call && code.operand is MethodInfo method && method.DeclaringType == typeof(Enumerable) &&
            method.Name == name && method.IsGenericMethod && method.GetGenericArguments() is [var type] && type == typeof(T);

        private static void CheckLocal<T>(MethodBase method, int index)
        {
            var locals = method.GetMethodBody()?.LocalVariables;
            if (locals is null || index >= locals.Count || locals[index].LocalType != typeof(T))
                throw new InvalidOperationException($"Map abundance native binding changed: {method.Name} local {index}.");
        }

        public static IEnumerable<MongoId> ExtendStatic(IEnumerable<MongoId> forced, IEnumerable<MongoId> drawn,
            ProbabilityObjectArray<MongoId, float?> options)
        {
            if (_abundanceLootScope is not { Increase: > 0 } scope) return forced.Concat(drawn);
            var eligible = Current._abundanceEligible;
            var original = drawn.ToArray();
        var bands = original.Where(eligible.ContainsKey).GroupBy(id => eligible[id])
            .Select(group => (Weight: group.Key, Count: Rounded(group.Count() * group.Key * scope.Increase)))
            .Where(band => band.Count > 0).ToArray();
        if (bands.Length == 0) return forced.Concat(original);

            var noDuplicates = !Current._abundanceContainerDuplicates;
            var excluded = forced.ToHashSet();
            if (noDuplicates) excluded.UnionWith(original);
            var candidates = options.Filter(row => row.RelativeProbability > 0 && eligible.ContainsKey(row.Key) && !excluded.Contains(row.Key));
            // Sample material and consumable bonuses separately to preserve their different rates.
            var extraTemplates = new List<MongoId>();
        foreach (var (weight, count) in bands)
            {
            var pool = candidates.Filter(row => eligible[row.Key] == weight);
                if (pool.Count == 0) continue;
                extraTemplates.AddRange(noDuplicates ? pool.DrawAndRemove(Math.Min(count, pool.Count), null) : pool.Draw(count));
            }
            scope.ContainerExtras += extraTemplates.Count;
            return forced.Concat(original).Concat(extraTemplates);
        }

        public static List<Spawnpoint> ExtendLoose(List<Spawnpoint> selected, List<Spawnpoint> forced,
            ProbabilityObjectArray<string, Spawnpoint> options)
        {
            if (_abundanceLootScope is not { Increase: > 0 } scope) return selected;
            var budget = Rounded(selected.Count * scope.Increase);
            if (budget == 0) return selected;
            var occupied = selected.Concat(forced).Select(point => point.LocationId).ToHashSet();
            var pool = options.Filter(row => row.RelativeProbability > 0 && row.Data is { } point && !occupied.Contains(point.LocationId));
            var eligible = Current._abundanceEligible;
            foreach (var key in pool.DrawAndRemove(Math.Min(budget, pool.Count), null))
            {
                var point = pool.Data(key);
                if (point is null || !occupied.Add(point.LocationId) || point.Template is not { IsContainer: not true, Items: { } items } template ||
                    point.ItemDistribution is not { } distribution) continue;
                var byKey = items.Where(i => !string.IsNullOrEmpty(i.ComposedKey)).ToLookup(i => i.ComposedKey);
                var weights = distribution.Where(row => row.RelativeProbability > 0 && row.ComposedKey?.Key is { } key && byKey.Contains(key))
                    .Select(row => (Row: row, Key: row.ComposedKey!.Key!, Weight: row.RelativeProbability!.Value)).ToArray();
                var draw = Random.Shared.NextDouble() * weights.Sum(row => row.Weight);
                foreach (var (row, composedKey, weight) in weights)
                {
                    draw -= weight;
                    if (draw > 0) continue;
                    var item = byKey[composedKey].First();
                    if (eligible.TryGetValue(item.Template, out var chance) && Random.Shared.NextDouble() < chance)
                    {
                        // The bonus uses this real location's variant and item tree.
                        selected.Add(point with { Template = template with { Items = items.Select(i => i with { }).ToList() }, ItemDistribution = [row] });
                        scope.LooseExtras++;
                    }
                    break;
                }
            }
            return selected;
        }

        private static int Rounded(double count) => (int)count + (Random.Shared.NextDouble() < count % 1 ? 1 : 0);
    }
}
