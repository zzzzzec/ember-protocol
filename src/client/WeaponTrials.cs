using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using EFT.Quests;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace Codex.ZombieSurvival.Client;

internal static class WeaponTrials
{
    private static readonly Dictionary<string, string[]> Builds = new(StringComparer.Ordinal);

    internal static void Install(Harmony harmony)
    {
        var path = Path.GetFullPath(Path.Combine(BepInEx.Paths.PluginPath, "..", "..", "SPT", "user", "mods", "Codex-ZombieSurvival", "data", "field-quests.json"));
        if (!File.Exists(path)) return;
        if (JObject.Parse(File.ReadAllText(path))["loans"] is not JObject loans) return;
        foreach (var entry in loans.Properties())
        {
            var kill = (string?)entry.Value["killCondition"];
            if (kill is not null && entry.Value["exactParts"] is JArray parts)
                Builds.Add(kill, parts.Values<string>().Select(part => part!).OrderBy(part => part, StringComparer.Ordinal).ToArray());
        }
        if (Builds.Count == 0) return;
        harmony.Patch(AccessTools.DeclaredMethod(typeof(GClass4042), nameof(GClass4042.Test), new[] { typeof(object) }),
            postfix: new HarmonyMethod(typeof(WeaponTrials), nameof(CheckBuild)));
        Plugin.Log.LogInfo($"SPT-Z weapon trials: {Builds.Count} exact loadouts registered.");
    }

    private static void CheckBuild(GClass4042 __instance, object __0, ref bool __result)
    {
        if (!__result || __0 is not GClass4015 hit || __instance.Condition is not ConditionKills condition ||
            !Builds.TryGetValue(condition.id.ToString(), out var expected)) return;
        __result = hit.WeaponMods is { } mods && mods.Length == expected.Length &&
            mods.OrderBy(part => part, StringComparer.Ordinal).SequenceEqual(expected);
    }
}
