using System;
using EFT;
using EFT.CameraControl;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Codex.ZombieSurvival.Client;

internal static class InfectedWeaponActivation
{
    private sealed class Attempt(Player player)
    {
        internal readonly Player Player = player;
        internal readonly Attempt? Previous = Current;
        internal Weapon? FailedWeapon;
        internal Player.Class1311? HandsOperation;
    }

    [ThreadStatic]
    private static Attempt? Current;

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.DeclaredMethod(typeof(BotWeaponManager), nameof(BotWeaponManager.PreActivate)),
            prefix: new HarmonyMethod(typeof(InfectedWeaponActivation), nameof(Begin)),
            finalizer: new HarmonyMethod(typeof(InfectedWeaponActivation), nameof(End)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(PoolManagerClass), nameof(PoolManagerClass.CreateItem),
            new[] { typeof(Item), typeof(ECameraType), typeof(IPlayer), typeof(bool) }),
            postfix: new HarmonyMethod(typeof(InfectedWeaponActivation), nameof(ObservePoolResult)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(Player), "method_137", new[] { typeof(Item) }),
            postfix: new HarmonyMethod(typeof(InfectedWeaponActivation), nameof(ObserveHandsOperation)));
    }

    private static void Begin(BotWeaponManager __instance, out Attempt? __state)
    {
        var player = __instance.BotOwner_0.GetPlayer;
        __state = Plugin.Infected(player) ? new Attempt(player) : null;
        if (__state is not null)
            Current = __state;
    }

    private static void ObservePoolResult(Item __0, IPlayer? __2, GameObject? __result)
    {
        if (Current is { } attempt && ReferenceEquals(attempt.Player, __2) && __result == null && __0 is Weapon weapon)
            attempt.FailedWeapon = weapon;
    }

    private static void ObserveHandsOperation(Player __instance, Item __0, Player.Class1311? __result)
    {
        if (Current is { } attempt && ReferenceEquals(attempt.Player, __instance) && __0 is Weapon)
            attempt.HandsOperation = __result;
    }

    private static Exception? End(Exception? __exception, Attempt? __state)
    {
        if (__state is null)
            return __exception;
        Current = __state.Previous;
        return RecoverPoolFailure(__exception, __state);
    }

    internal static void Retry(Player player, BotWeaponSelector selector)
    {
        var attempt = new Attempt(player);
        Current = attempt;
        try
        {
            // The selector reads HandsController.Item even during an initial equip.
            if (player.HandsController is null)
            {
                selector.IsChanging = true;
                player.SetSlotItem(selector.MainWeapon, selector.OnWeaponTaken);
            }
            else
            {
                selector.TakeMainWeapon();
            }
        }
        catch (NullReferenceException ex) when (attempt.FailedWeapon is not null)
        {
            RecoverPoolFailure(ex, attempt);
        }
        finally
        {
            Current = attempt.Previous;
        }
    }

    private static Exception? RecoverPoolFailure(Exception? error, Attempt attempt)
    {
        if (error is not NullReferenceException || attempt.FailedWeapon is not { } weapon)
            return error;

        // Controller construction failed before the inventory change could finish.
        if (attempt.HandsOperation is { } operation)
        {
            operation.Confirm(false);
            operation.Dispose();
        }

        // The failed initial equip never reaches the native process completion.
        // With no controller and no queued hand action, release that process slot.
        var player = attempt.Player;
        var status = player.ProcessStatus;
        if (player.HandsController == null && player.AbstractProcess_0 is null &&
            status == Player.EProcessStatus.Scheduled)
            player.ProcessStatus = Player.EProcessStatus.None;

        // Let BotOwner.PreActivate finish registering the bot. Native activation
        // will still wait for a successful equip, which the raid retries later.
        ClientDiagnostics.Record("bot.activation_pool_failure", new
        {
            id = ClientDiagnostics.Key(attempt.Player.Profile.Id),
            weapon = weapon.StringTemplateId,
            handsProcess = status.ToString(),
            handsProcessAfter = player.ProcessStatus.ToString(),
            error = error.GetType().Name
        });
        return null;
    }
}
