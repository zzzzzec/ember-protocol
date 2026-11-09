using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;
using UnityEngine;
using Codex.ZombieSurvival.Rules;
namespace Codex.ZombieSurvival.Client;

internal static class InfectedAwareness
{
    private static readonly FieldInfo MemoryOwner = AccessTools.Field(typeof(BotMemoryClass), "BotOwner_0");
    internal static void Install(Harmony h)
    {
        h.Patch(AccessTools.DeclaredMethod(typeof(BotRun), nameof(BotRun.smethod_0)),
            prefix: new HarmonyMethod(typeof(InfectedAwareness), nameof(KeepNavigableCorners)));
        h.Patch(AccessTools.PropertySetter(typeof(BotMemoryClass), "GoalEnemy"), prefix: new HarmonyMethod(typeof(InfectedAwareness), nameof(GoalAssigned)));
        h.Patch(AccessTools.Method(typeof(BotMemoryClass), "method_0"), prefix: new HarmonyMethod(typeof(InfectedAwareness), nameof(NoGlobalReport)));
        h.Patch(AccessTools.Method(typeof(BotHalloweenWithZombies), "method_8"), prefix: new HarmonyMethod(typeof(InfectedAwareness), nameof(LocalPursuit)));
    }
    // Use the same unmodified NavMesh path as native NO_ZIG_ZAG. Keep the
    // slow/fast chase nodes and their melee transitions, doors and stuck recovery.
    private static bool KeepNavigableCorners(Vector3[] __0, BotOwner __1)
    {
        var raid = SurvivalRaid.Current;
        if (raid == null || !Plugin.Infected(__1.GetPlayer)) return true;
        __1.GoToByWay(__0);
        raid.StraightInfectedPath();
        return false;
    }

    internal static bool DirectSight(BotOwner owner, Player target, bool nativeVisible)
    {
        var player = owner.GetPlayer;
        if (target == null || target.HealthController?.IsAlive != true) return false;
        var delta = target.Transform.position - player.Transform.position;
        var distance = delta.magnitude;
        if (!nativeVisible && distance > 2.6f) return ThreatActors.Sight(player,target,distance,0,false);
        var dot = distance < .01f ? 1 : Vector3.Dot(player.LookDirection.normalized, delta / distance);
        if (distance > 2.6f && dot < .25f) return ThreatActors.Sight(player,target,distance,dot,false);
        return ThreatActors.Sight(player,target,distance,dot,AwarenessPolicy.SightConfirmed(nativeVisible, SurvivalRaid.Occluded(player.Transform.position + Vector3.up * 1.4f, target.Transform.position + Vector3.up * 1.1f), distance, dot));
    }
    private static bool GoalAssigned(BotMemoryClass __instance, EnemyInfo __0)
    {
        var raid = SurvivalRaid.Current;
        // Native setter itself does nothing for an unchanged reference; the 5Hz raid tick rechecks sight.
        if (__0 == null || ReferenceEquals(__instance.GoalEnemy, __0) || __0.Person is not Player target || target.IsAI || MemoryOwner.GetValue(__instance) is not BotOwner owner || !Plugin.Infected(owner.GetPlayer)) return true;
        if (DirectSight(owner, target, __0.IsVisible)) { raid?.RememberSighting(owner.GetPlayer, target.Transform.position); return true; }
        raid?.UnknownGoalRejected(); return false;
    }
    private static bool NoGlobalReport(BotMemoryClass __instance) => MemoryOwner.GetValue(__instance) is not BotOwner owner || !Plugin.Infected(owner.GetPlayer);
    private static bool LocalPursuit(BotOwner __0, IPlayer __1)
    {
        var raid = SurvivalRaid.Current;
        if (!Plugin.Infected(__0.GetPlayer)) return true;
        if (raid == null) return false;
        raid.GlobalPursuitRejected();
        if (__1 is Player target && !target.IsAI && DirectSight(__0, target, __0.Memory.GoalEnemy?.IsVisible == true)) raid.ShareSighting(__0.GetPlayer, target.Transform.position);
        // Native pursuits retain a live IPlayer reference and recruit the whole infected population.
        // Local listeners get only a copied position. They cannot relay another listener's report.
        return false;
    }
}

public sealed partial class SurvivalRaid
{
    private readonly Dictionary<Player, float> _nextSightingReport = new();
    private int _straightInfectedPaths, _unknownGoalBlocks, _globalPursuitsBlocked, _localReportAlerts;
    private int _near25, _near50, _near100, _sameFloorNear25;
    internal void StraightInfectedPath() => _straightInfectedPaths++;
    internal void UnknownGoalRejected() => _unknownGoalBlocks++;
    internal void GlobalPursuitRejected() => _globalPursuitsBlocked++;
    internal object AwarenessDiagnostic() => new { straightInfectedPaths = _straightInfectedPaths, unknownGoalBlocks = _unknownGoalBlocks, globalPursuitsBlocked = _globalPursuitsBlocked, localReportAlerts = _localReportAlerts, near25 = _near25, near50 = _near50, near100 = _near100, sameFloorNear25 = _sameFloorNear25, near25Per1000SquareMetres = AwarenessPolicy.LocalDensity(_near25, 25) };
    internal bool InvestigateAt(Player listener, Vector3 point, float seconds, string reason="native_call")
    {
        var now = Time.time;
        if (Customs && _attention.TryGetValue(listener, out var old) && old.Until > now
            && AwarenessPolicy.CuePriority(old.Reason) > AwarenessPolicy.CuePriority(reason)) return false;
        _attention[listener] = new Attention { Point = point, Until = now + seconds, Reason = reason };
        WakeIdleActor(listener, reason);
        return true;
    }
    internal void RememberSighting(Player listener, Vector3 point) => InvestigateAt(listener, point, 12, "sighting");
    internal void ShareSighting(Player caller, Vector3 point)
    {
        RememberSighting(caller, point);
        if (_nextSightingReport.TryGetValue(caller, out var next) && Time.time < next) return;
        _nextSightingReport[caller] = Time.time + 10;
        var radius = CallRadius;
        var count = 0;
        foreach (var listener in _infected)
        {
            if (listener == null || listener == caller || listener.HealthController?.IsAlive != true) continue;
            var pos = listener.Transform.position;
            // A shout originates at the caller, not at the possibly distant player.
            var distance = Vector3.Distance(pos, caller.Transform.position);
            var audible=ThreatActors.Hearing(listener,radius);
            if (distance > audible || distance > BoundedHearing.Effective(pos, caller.Transform.position, audible)) continue;
            var alreadyAlerted = _attention.TryGetValue(listener, out var old) && old.Until > Time.time;
            if (InvestigateAt(listener, point, 8, "local_sighting") && !alreadyAlerted) count++;
        }
        _localReportAlerts += count;
        ClientDiagnostics.Record("awareness.local_report", new { radius, alerted = count, point = new { point.x, point.y, point.z }, targetReferenceShared = false });
    }
}