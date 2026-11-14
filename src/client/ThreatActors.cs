using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using Codex.ZombieSurvival.Rules;

namespace Codex.ZombieSurvival.Client;

internal static class ThreatActors
{
    private sealed class Actor
    {
        public ThreatArchetype Unit = null!;
        public bool Simplified;
        public readonly ThreatSight Acquisition = new();
    }
    private static ThreatConfiguration Settings = new();
    private static Dictionary<int, ThreatArchetype> Types = new();
    private static ConditionalWeakTable<Player, Actor> Players = new();
    private static ConditionalWeakTable<MovementContext, Actor> Movers = new();
    private static readonly FieldInfo Plates = AccessTools.Field(typeof(Player), "_armorPlateColliders");
    internal static void Install(Harmony h)
    {
        var path = Path.Combine(BepInEx.Paths.PluginPath, "Codex-ZombieSurvival", "threat-settings.json");
        Settings = JsonConvert.DeserializeObject<ThreatConfiguration>(File.ReadAllText(path))!;
        ThreatPolicy.Validate(Settings); Types = Settings.Archetypes.ToDictionary(a => a.Key);
        ThreatArmor.Install(h);
        h.Patch(AccessTools.PropertyGetter(typeof(MovementContext), "PlayerAnimatorDeltaPosition"), postfix: new HarmonyMethod(typeof(ThreatActors), nameof(RootMotion)));
    }
    internal static void Reset() { Players = new(); Movers = new(); }
    internal static void Register(Player player)
    {
        if (!Settings.Enabled || Players.TryGetValue(player, out _)) return;
        if (!ThreatPolicy.TryDecode(player.Profile.Id.ToString(), Settings.Revision, out var key,out var wear) || !Types.TryGetValue(key, out var unit))
        { Plugin.Log.LogError("[SPT-Z] Infected profile has no matching threat identity; check matching client/server package."); return; }
        unit=unit.WithWear(wear);
        var actor = new Actor { Unit = unit }; Players.Add(player, actor);
        if (player.MovementContext != null) Movers.Add(player.MovementContext, actor);
        var armors = new List<ArmorComponent>(); player.Inventory.GetPutOnArmorsNonAlloc(armors);
        var plateCount = (Plates.GetValue(player) as Array)?.Length ?? 0;
        actor.Simplified=plateCount==0&&(unit.ArmorClass>0||unit.HelmetClass>0);
        var maxArmor = armors.Count == 0 ? 0 : armors.Max(a => a.ArmorClass);
        ClientDiagnostics.Record("threat.actor", new { actor = ClientDiagnostics.Key(player.Profile.Id), type = unit.Id, score = unit.Score, head = unit.Head, chest = unit.Chest,
            wear=unit.WearCode,plateFraction=unit.PlateFraction,speed = unit.Speed, sight = unit.SightRange, hearing = unit.Hearing, reaction = unit.ReactionSeconds, armor = unit.ArmorClass, helmet = unit.HelmetClass,
            armorComponents = armors.Count, nativeMaxArmorClass = maxArmor, nativePlateColliders = plateCount, simplifiedArmorCompatibility = actor.Simplified });
        if (maxArmor < Math.Max(unit.ArmorClass, unit.HelmetClass))
            Plugin.Log.LogError("[SPT-Z] Native armor verification failed for " + unit.Id + "; inspect threat.actor diagnostics.");
    }
    // Native zombie locomotion ignores ChangeSpeed and uses animator root motion.
    // Scale only its existing horizontal movement; retain gravity, collisions and native pathfinding.
    private static void RootMotion(MovementContext __instance, ref Vector3 __result)
    {
        if (!Movers.TryGetValue(__instance, out var actor)) return;
        __result.x *= actor.Unit.Speed; __result.z *= actor.Unit.Speed;
    }
    internal static bool SimplifiedArmor(Player player)=>Players.TryGetValue(player,out var actor)&&actor.Simplified;
    internal static float Hearing(Player player, float radius) => Players.TryGetValue(player, out var a) ? radius * a.Unit.Hearing : radius;
    internal static bool InSightCone(Player player, float distance, float dot)
    {
        var unit = Players.TryGetValue(player, out var actor) ? actor.Unit : null;
        return AwarenessPolicy.InSightCone(distance, dot, unit?.SightRange ?? 25, unit?.SightAngle ?? 100);
    }

    internal static bool ObserveSight(Player player, Player target, float distance, bool clearView)
    {
        if (!Players.TryGetValue(player, out var actor)) return clearView;
        var recognised = actor.Acquisition.Observe(clearView ? target.ProfileId : "", clearView,
            Time.time, actor.Unit.ReactionSeconds);
        return clearView && (distance <= AwarenessPolicy.ContactDistance || recognised);
    }
}
