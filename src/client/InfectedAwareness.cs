using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
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

internal static class InfectedTurning
{
    private sealed class TurnLimits
    {
        internal readonly InfectedTurnBudget Look = new();
        internal readonly InfectedTurnBudget Body = new();
    }

    private static ConditionalWeakTable<MovementContext, TurnLimits> _limits = new();

    internal static void Install(Harmony harmony)
    {
        var steering = new HarmonyMethod(typeof(InfectedTurning), nameof(LimitSteering));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(BotSteering), nameof(BotSteering.SetXAngle)), prefix: steering);
        harmony.Patch(AccessTools.DeclaredMethod(typeof(GClass498), nameof(BotSteering.SetXAngle)), prefix: steering);
        harmony.Patch(AccessTools.DeclaredMethod(typeof(Player), nameof(Player.Rotate), new[] { typeof(Vector2), typeof(bool) }),
            prefix: new HarmonyMethod(typeof(InfectedTurning), nameof(LimitLook)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(MovementContext), nameof(MovementContext.ApplyRotation), new[] { typeof(Quaternion) }),
            prefix: new HarmonyMethod(typeof(InfectedTurning), nameof(LimitBody)));
    }

    internal static void Register(MovementContext context) => _limits.GetValue(context, _ => new TurnLimits());
    internal static void Reset() => _limits = new();

    private static bool MovingOrFighting(MovementContext context, Player player) =>
        context.MovementDirection.sqrMagnitude > .0004f || player.AIData?.BotOwner?.Memory?.GoalEnemy?.IsVisible == true;

    private static void LimitSteering(BotSteering __instance, ref float __0)
    {
        var player = __instance.Player;
        var context = player?.MovementContext;
        var seconds = Time.deltaTime;
        if (seconds <= 0 || context == null || !_limits.TryGetValue(context, out var limits)) return;
        // The steering code rotates weapon aim before it calls Player.Rotate.
        // Limit both to the same budget, which Player.Rotate consumes.
        __0 = limits.Look.Limit(__0 * seconds, Time.frameCount, seconds, MovingOrFighting(context, player!), consume: false) / seconds;
    }

    private static void LimitLook(Player __instance, ref Vector2 __0)
    {
        var context = __instance.MovementContext;
        if (__0.x == 0 || context == null || !_limits.TryGetValue(context, out var limits) || __instance.HealthController?.IsAlive != true) return;
        __0.x = limits.Look.Limit(__0.x, Time.frameCount, Time.deltaTime, MovingOrFighting(context, __instance));
    }

    private static void LimitBody(MovementContext __instance, Player ____player, ref Quaternion __0)
    {
        if (!_limits.TryGetValue(__instance, out var limits) || ____player.HealthController?.IsAlive != true) return;
        var requested = Mathf.DeltaAngle(__instance.TransformRotation.eulerAngles.y, __0.eulerAngles.y);
        var allowed = limits.Body.Limit(requested, Time.frameCount, Time.deltaTime, MovingOrFighting(__instance, ____player));
        if (requested != allowed)
            __0 = Quaternion.AngleAxis(allowed - requested, Vector3.up) * __0;
    }
}

public sealed partial class SurvivalRaid
{
    private readonly Dictionary<Player, float> _nextSightingReport = new();
    private int _straightInfectedPaths, _unknownGoalBlocks, _globalPursuitsBlocked, _localReportAlerts, _lostSight;
    private int _near25, _near50, _near100, _sameFloorNear25;
    internal void StraightInfectedPath() => _straightInfectedPaths++;
    internal void UnknownGoalRejected() => _unknownGoalBlocks++;
    internal void GlobalPursuitRejected() => _globalPursuitsBlocked++;
    internal object AwarenessDiagnostic() => new { straightInfectedPaths = _straightInfectedPaths, unknownGoalBlocks = _unknownGoalBlocks, lostSight = _lostSight, globalPursuitsBlocked = _globalPursuitsBlocked, localReportAlerts = _localReportAlerts, near25 = _near25, near50 = _near50, near100 = _near100, sameFloorNear25 = _sameFloorNear25, near25Per1000SquareMetres = AwarenessPolicy.LocalDensity(_near25, 25) };
    internal bool InvestigateAt(Player listener, Vector3 point, float seconds, string reason="native_call")
    {
        var now = Time.time;
        if (_attention.TryGetValue(listener, out var old) && AwarenessPolicy.InformationAlive(now,old.Until)
            && !AwarenessPolicy.AcceptCue(old.Reason, reason, now - old.ObservedAt, Customs)) return false;
        _attention[listener] = new Attention { Point = point, Until = now + seconds, ObservedAt = now, Reason = reason };
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
