using System.Collections.Generic;
using EFT;
using HarmonyLib;
using UnityEngine;
using Codex.ZombieSurvival.Rules;

namespace Codex.ZombieSurvival.Client;

internal static class InfectedIdleBudget
{
    internal static void Install(Harmony harmony)
    {
        var ownerTick = new HarmonyMethod(typeof(InfectedIdleBudget), nameof(OwnerTick));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(BotOwner), nameof(BotOwner.UpdateManual)), prefix: ownerTick);
        harmony.Patch(AccessTools.DeclaredMethod(typeof(BotOwner), nameof(BotOwner.FixedUpdate)), prefix: ownerTick);
        harmony.Patch(AccessTools.DeclaredMethod(typeof(AICoreAgentClass<BotLogicDecision>), nameof(AICoreAgentClass<BotLogicDecision>.Update)),
            prefix: new HarmonyMethod(typeof(InfectedIdleBudget), nameof(BrainTick)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(BotOwner), nameof(BotOwner.method_9)),
            prefix: new HarmonyMethod(typeof(InfectedIdleBudget), nameof(Hit)));
    }

    private static bool OwnerTick(BotOwner __instance) => SurvivalRaid.Current?.IdleActorPaused(__instance.GetPlayer) != true;
    private static bool BrainTick(AICoreAgentClass<BotLogicDecision> __instance) =>
        __instance.Gclass309_0 is not BaseBrain brain || OwnerTick(brain.Owner);
    private static void Hit(BotOwner __instance) => SurvivalRaid.Current?.WakeIdleActor(__instance.GetPlayer, "hit");
}

public sealed partial class SurvivalRaid
{
    private const float IdleSleepDistance = 128;
    private const float IdleWakeDistance = 105;
    private readonly HashSet<Player> _pausedIdle = new();
    private readonly Dictionary<Player, float> _nextIdleCheck = new();
    private int _idlePauses, _idleWakes;

    internal bool IdleActorPaused(Player player) =>
        Customs && Plugin.CustomsIdleBudget && player is not null && _pausedIdle.Contains(player);

    private object IdleBudgetDiagnostic() => new
    {
        enabled = Customs && Plugin.CustomsIdleBudget, paused = _pausedIdle.Count, pauses = _idlePauses, wakes = _idleWakes
    };

    private void ForgetIdleActor(Player player)
    {
        if (player is null) return;
        _pausedIdle.Remove(player);
        _nextIdleCheck.Remove(player);
    }

    internal void WakeIdleActor(Player player, string reason)
    {
        if (!Customs || player is null || (!Plugin.CustomsIdleBudget && _pausedIdle.Count == 0)) return;
        if (reason == "hit") _nextIdleCheck[player] = Time.time + 15;
        if (!_pausedIdle.Remove(player)) return;
        _nextIdleCheck[player] = Mathf.Max(Time.time + 4, _nextIdleCheck.TryGetValue(player, out var next) ? next : 0);
        player.AIData?.BotOwner?.Brain?.BaseBrain?.CalcActionNextFrame();
        _idleWakes++;
        ClientDiagnostics.Record("idle.wake", new { id = ClientDiagnostics.Key(player.Profile.Id), reason });
    }

    private bool PauseIdleActor(Player player, BotOwner owner, Player[] humans, float distance)
    {
        var paused = _pausedIdle.Contains(player);
        if (!Customs || !Plugin.CustomsIdleBudget)
        {
            if (paused) WakeIdleActor(player, "disabled");
            return false;
        }

        if (distance <= (paused ? IdleWakeDistance : IdleSleepDistance)
            || (_attention.TryGetValue(player, out var cue) && AwarenessPolicy.InformationAlive(Time.time, cue.Until))
            || owner.Memory?.GoalEnemy?.IsVisible == true)
        {
            if (paused) WakeIdleActor(player, "engagement");
            return false;
        }

        if (!paused)
        {
            if (owner.BotState != EBotState.Active || (_nextIdleCheck.TryGetValue(player, out var next) && Time.time < next))
                return false;
            _nextIdleCheck[player] = Time.time + 1.5f;
        }

        var position = player.Transform.position;
        foreach (var human in humans)
        {
            var eye = human.Transform.position + Vector3.up * 1.55f;
            if (Occluded(eye, position + Vector3.up * .65f) && Occluded(eye, position + Vector3.up * 1.6f)) continue;
            if (paused) WakeIdleActor(player, "exposed");
            return false;
        }

        if (paused) return true;
        // Keep the same actor, pose, colliders, health, inventory and native
        // registration. Only heavy owner/brain ticks wait behind solid cover.
        owner.StopMove();
        _pausedIdle.Add(player);
        _idlePauses++;
        ClientDiagnostics.Record("idle.pause", new { id = ClientDiagnostics.Key(player.Profile.Id), distance });
        return true;
    }
}
