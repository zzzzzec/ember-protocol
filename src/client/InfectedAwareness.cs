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
    [ThreadStatic] private static EnemyInfo? _confirmedEnemy;
    private static readonly FieldInfo MemoryOwner = AccessTools.Field(typeof(BotMemoryClass), "BotOwner_0");
    internal static void Install(Harmony h)
    {
        h.Patch(AccessTools.DeclaredMethod(typeof(BotRun), nameof(BotRun.smethod_0)),
            prefix: new HarmonyMethod(typeof(InfectedAwareness), nameof(KeepNavigableCorners)));
        h.Patch(AccessTools.DeclaredMethod(typeof(GClass224), nameof(GClass224.NotMovingCheck)),
            prefix: new HarmonyMethod(typeof(InfectedAwareness), nameof(RefreshVisibleChasePath)));
        h.Patch(AccessTools.PropertySetter(typeof(BotMemoryClass), "GoalEnemy"), prefix: new HarmonyMethod(typeof(InfectedAwareness), nameof(GoalAssigned)));
        h.Patch(AccessTools.Method(typeof(EnemyInfo), nameof(EnemyInfo.SetVisible)), prefix: new HarmonyMethod(typeof(InfectedAwareness), nameof(NativeVisibility)));
        h.Patch(AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.ReportAboutEnemy)), prefix: new HarmonyMethod(typeof(InfectedAwareness), nameof(NoGroupSightReport)));
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

    private static void RefreshVisibleChasePath(GClass224 __instance)
    {
        var owner = __instance.BotOwner_0;
        if (Time.time < __instance.Float_2 || SurvivalRaid.Current == null || !Plugin.Infected(owner.GetPlayer)) return;
        if (owner.Memory.GoalEnemy is not { IsVisible: true, Person: Player target } enemy || target.IsAI) return;
        if (!owner.HasPathAndNotComplete || owner.Mover.TargetPoint is not { } destination) return;

        // Native NotMovingCheck overwrites its previous point before comparing
        // it. Compare against the actual route destination instead, on the same
        // one-second cadence, using the last clear sample. Native recovery still runs.
        if (AwarenessPolicy.ChaseDestinationMoved((destination - enemy.PersonalLastPos).sqrMagnitude))
        {
            __instance.Int_0 = -1;
            SurvivalRaid.Current?.ChaseRetargeted();
        }
    }

    internal static bool HasClearView(BotOwner owner, Player target, float distance, out bool checkedLine)
    {
        checkedLine = false;
        if (target.HealthController?.IsAlive != true || owner.FlashGrenade?.IsFlashed == true) return false;
        var delta = target.Transform.position - owner.GetPlayer.Transform.position;
        var direction = owner.LookDirection;
        direction.y = 0;
        delta.y = 0;
        var dot = delta.sqrMagnitude < .0001f ? 1 : Vector3.Dot(direction.normalized, delta.normalized);
        if (!ThreatActors.InSightCone(owner.GetPlayer, distance, dot)) return false;

        checkedLine = true;
        var origin = owner.MyHead.position;
        return CanSeePoint(owner, origin, target.PlayerBones.Head.position)
            || CanSeePoint(owner, origin, target.MainParts[BodyPartType.body].Position);
    }

    private static bool CanSeePoint(BotOwner owner, Vector3 from, Vector3 to) =>
        !SurvivalRaid.Occluded(from, to)
        && owner.BotsController.BotSmokesVisionSystem?.IsRayIntersectAnySmoke(from, to, out _, out _, out _) != true;

    internal static EnemyInfo? Engage(BotOwner owner, Player target)
    {
        var memory = owner.Memory;
        if (memory.DangerData.HaveCloseDanger) return null;
        var enemies = owner.EnemiesController.EnemyInfos;
        if (!enemies.TryGetValue(target, out var enemy))
        {
            // Use an existing native hostile record. Never add the target to the whole group.
            if (!owner.BotsGroup.Enemies.TryGetValue(target, out var settings)) return null;
            memory.AddEnemy(target, settings, onActivation: true);
            if (!enemies.TryGetValue(target, out enemy)) return null;
        }

        var previous = _confirmedEnemy;
        _confirmedEnemy = enemy;
        try
        {
            var newlyEngaged = !ReferenceEquals(memory.GoalEnemy, enemy);
            if (!enemy.IsVisible) enemy.SetVisible(true);
            enemy.PersonalLastPos = target.Transform.position;
            enemy.PersonalLastSeenTime = Time.time;
            memory.SetLastTimeSeeEnemy();
            if (newlyEngaged)
            {
                memory.GoalEnemy = enemy;
                owner.Brain?.BaseBrain?.CalcActionNextFrame();
            }
        }
        finally { _confirmedEnemy = previous; }
        return enemy;
    }

    internal static void EndEngagement(BotMemoryClass memory, EnemyInfo enemy)
    {
        // Native SetVisible(false) records the player's live position in group
        // memory even though sight was just lost. Keep only our copied sighting.
        enemy.IsVisible = false;
        enemy.SetCanShoot(false);
        memory.LoseVisionCurrentEnemy();
        memory.GoalEnemy = null;
        memory.LastEnemy = null;
        enemy.Owner.Brain?.BaseBrain?.CalcActionNextFrame();
    }

    private static bool GoalAssigned(BotMemoryClass __instance, EnemyInfo? __0)
    {
        var raid = SurvivalRaid.Current;
        var current = __instance.GoalEnemy;
        if (raid == null || ReferenceEquals(current, __0) ||
            (__0 != null && ReferenceEquals(_confirmedEnemy, __0))) return true;
        if (MemoryOwner.GetValue(__instance) is not BotOwner owner || !Plugin.Infected(owner.GetPlayer)) return true;
        if (current?.Person is Player currentTarget && !currentTarget.IsAI && current.IsVisible &&
            currentTarget.HealthController.IsAlive && !owner.IsDead && !__instance.DangerData.HaveCloseDanger) return false;
        if (__0?.Person is not Player target || target.IsAI) return true;
        raid.UnknownGoalRejected();
        return false;
    }

    private static bool NativeVisibility(EnemyInfo __instance, bool __0)
    {
        if (SurvivalRaid.Current == null || __instance.IsVisible == __0 || ReferenceEquals(_confirmedEnemy, __instance) ||
            __instance.Person is not Player target || target.IsAI || !Plugin.Infected(__instance.Owner.GetPlayer)) return true;
        // Native body-part checks still control CanShoot. The raid's visual samples
        // own visibility so the two sensors cannot alternate between chase and patrol.
        return !__0 && (!target.HealthController.IsAlive || __instance.Owner.IsDead);
    }

    private static bool NoGroupSightReport(IPlayer __0, BotOwner __2) =>
        SurvivalRaid.Current == null || __0.IsAI || !Plugin.Infected(__2.GetPlayer);

    private static bool NoGlobalReport(BotMemoryClass __instance) => MemoryOwner.GetValue(__instance) is not BotOwner owner || !Plugin.Infected(owner.GetPlayer);

    private static bool LocalPursuit(BotOwner __0)
    {
        if (!Plugin.Infected(__0.GetPlayer)) return true;
        SurvivalRaid.Current?.GlobalPursuitRejected();
        // Only the infected that saw the player can report a copied position to nearby listeners.
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
    private int _visionChecks, _visionObstructed, _visionAcquired, _visionWithoutNativeEnemy, _chaseRetargets;
    private int _localResponders, _localChasers, _peakLocalResponders;
    internal void ChaseRetargeted() => _chaseRetargets++;
    internal void StraightInfectedPath() => _straightInfectedPaths++;
    internal void UnknownGoalRejected() => _unknownGoalBlocks++;
    internal void GlobalPursuitRejected() => _globalPursuitsBlocked++;
    internal object AwarenessDiagnostic() => new { visionChecks = _visionChecks, visionObstructed = _visionObstructed,
        visionAcquired = _visionAcquired, visionWithoutNativeEnemy = _visionWithoutNativeEnemy, chaseRetargets = _chaseRetargets,
        localResponders = _localResponders, localChasers = _localChasers, peakLocalResponders = _peakLocalResponders, straightInfectedPaths = _straightInfectedPaths, unknownGoalBlocks = _unknownGoalBlocks, lostSight = _lostSight, globalPursuitsBlocked = _globalPursuitsBlocked, localReportAlerts = _localReportAlerts, near25 = _near25, near50 = _near50, near100 = _near100, sameFloorNear25 = _sameFloorNear25, near25Per1000SquareMetres = AwarenessPolicy.LocalDensity(_near25, 25) };
    private bool UpdateVisualGoal(Player infected, BotOwner owner, Player nearest, float nearestDistance)
    {
        var memory = owner.Memory;
        var enemy = memory.GoalEnemy;
        var previous = enemy?.Person as Player;
        var target = previous != null && !previous.IsAI && previous.HealthController?.IsAlive == true ? previous : nearest;
        var distance = target == nearest ? nearestDistance : Vector3.Distance(infected.Transform.position, target.Transform.position);
        var clearView = InfectedAwareness.HasClearView(owner, target, distance, out var checkedLine);
        if (checkedLine)
        {
            _visionChecks++;
            if (!clearView) _visionObstructed++;
        }

        var contact = target == nearest && clearView && distance <= AwarenessPolicy.ContactDistance;
        if (ThreatActors.ObserveSight(infected, target, distance, clearView))
        {
            _visibleEnemies++;
            ShareSighting(infected, target.Transform.position);
            var engaged = InfectedAwareness.Engage(owner, target);
            if (engaged != null && !ReferenceEquals(enemy, engaged)) _visionAcquired++;
            else if (engaged == null && !memory.DangerData.HaveCloseDanger) _visionWithoutNativeEnemy++;
        }
        else if (enemy != null && previous != null && !previous.IsAI)
        {
            InfectedAwareness.EndEngagement(memory, enemy);
            _lostSight++;
        }
        return contact;
    }

    internal bool InvestigateAt(Player listener, Vector3 point, float seconds, string reason="native_call", bool playerGunfire=false)
    {
        var now = Time.time;
        if (_attention.TryGetValue(listener, out var old) && AwarenessPolicy.InformationAlive(now,old.Until)
            && !AwarenessPolicy.AcceptCue(old.Reason, reason, now - old.ObservedAt, Customs, old.PlayerGunfire, playerGunfire)) return false;
        _attention[listener] = new Attention { Point = point, Until = now + seconds, ObservedAt = now, Reason = reason, PlayerGunfire = playerGunfire };
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
