using System;
using System.Reflection;
using EFT;
using EFT.UI;
using HarmonyLib;
using UnityEngine;
using Codex.ZombieSurvival.Rules;
namespace Codex.ZombieSurvival.Client;

internal static class BoundedHearing
{
    private static readonly FieldInfo SensorOwner = AccessTools.Field(typeof(BotHearingSensor), "BotOwner");
    private static readonly FieldInfo CallerOwner = AccessTools.Field(typeof(BotHalloweenWithZombiesGameEvent), "BotOwner_0");
    internal static void Install(Harmony harmony)
    {
        var sensor = AccessTools.Method(typeof(BotHearingSensor), "method_0", new[] { typeof(IPlayer), typeof(Vector3), typeof(float), typeof(AISoundType) });
        harmony.Patch(sensor, prefix: new HarmonyMethod(typeof(BoundedHearing), nameof(NativeSound)));
        harmony.Patch(AccessTools.Method(typeof(BotHalloweenWithZombiesGameEvent), "Call"), prefix: new HarmonyMethod(typeof(BoundedHearing), nameof(NativeCall)));
    }
    internal static bool InfectedSource(IPlayer? player) => player is Player p && Plugin.Infected(p);
    internal static string Kind(AISoundType type) => type == AISoundType.gun ? "gun" : type == AISoundType.silencedGun ? "silencedGun" : type == AISoundType.step ? "step" : "";
    internal static float Effective(Vector3 listener, Vector3 source, float radius, float minimumWallScale = 0)
    {
        var blocked = SurvivalRaid.Occluded(listener + Vector3.up * 1.4f, source + Vector3.up * .8f);
        return HearingPolicy.EffectiveRadius(Plugin.Hearing, radius, blocked, listener.y - source.y, minimumWallScale);
    }
    private static bool NativeSound(BotHearingSensor __instance, IPlayer __0, Vector3 __1, ref float __2, AISoundType __3)
    {
        var raid = SurvivalRaid.Current;
        if (raid == null || SensorOwner.GetValue(__instance) is not BotOwner owner || !Plugin.Infected(owner.GetPlayer)) return true;
        var kind = Kind(__3);
        if (kind.Length == 0) return false;
        if (kind == "step" && (__0 == null || __0.IsAI)) return false;
        var radius = raid.SoundRadius(kind, __2, InfectedSource(__0), __0 is { IsAI: false });
        var pos = owner.GetPlayer.Transform.position;
        var distance = Vector3.Distance(pos, __1);
        if (radius <= 0 || distance > radius || distance > Effective(pos, __1, radius, raid.Customs && __0 is { IsAI: false } && kind == "gun" ? .45f : 0))
        { raid.NativeHearingRejected(); return false; }
        // The shared sound event already queues this copied position. Native hearing
        // keeps a live player identity, which would turn investigation into tracking.
        __2 = radius;
        return false;
    }
    private static bool NativeCall(BotHalloweenWithZombiesGameEvent __instance, Vector3 __0)
    {
        var raid = SurvivalRaid.Current;
        if (raid == null || CallerOwner.GetValue(__instance) is not BotOwner owner || !Plugin.Infected(owner.GetPlayer)) return true;
        var radius = raid.CallRadius;
        var pos = owner.GetPlayer.Transform.position;
        var distance = Vector3.Distance(pos, __0);
        if (distance > radius || distance > Effective(pos, __0, radius)) { raid.NativeCallRejected(); return false; }
        raid.InvestigateAt(owner.GetPlayer, __0, 8);
        return false;
    }
}
