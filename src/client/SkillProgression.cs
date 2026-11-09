using System;
using Comfort.Common;
using EFT;
using HarmonyLib;

namespace Codex.ZombieSurvival.Client;

internal static class SkillProgression
{
    private const float SprintGainScale = 1.5f;

    internal static void Install(Harmony harmony)
    {
        var constructor = AccessTools.Constructor(typeof(Profile), new[] { typeof(CompleteProfileDescriptorClass) });
        harmony.Patch(constructor, postfix: new HarmonyMethod(typeof(SkillProgression), nameof(ApplyToPmc)));
        var sprintSpeed = AccessTools.PropertyGetter(typeof(MovementContext), nameof(MovementContext.SprintingSpeed));
        harmony.Patch(sprintSpeed, postfix: new HarmonyMethod(typeof(SkillProgression), nameof(ApplySprintGain)));
    }

    private static void ApplyToPmc(Profile __instance)
    {
        if (__instance.Side is not (EPlayerSide.Usec or EPlayerSide.Bear) ||
            __instance.Info.Settings?.Role is not (null or WildSpawnType.assault))
            return;

        var skills = __instance.Skills;
        ApplyMaximum(skills.Strength, skills.StrengthBuffSprintSpeedInc, 0.20f * SprintGainScale);
        ApplyMaximum(skills.Endurance, skills.EnduranceBuffEnduranceInc, 0.65f);
        ApplyMaximum(skills.Endurance, skills.EnduranceBuffRestoration, 0.68f);
        ApplyMaximum(skills.MagDrills, skills.MagDrillsLoadSpeed, 45f);
        ApplyMaximum(skills.MagDrills, skills.MagDrillsUnloadSpeed, 45f);
    }

    private static void ApplySprintGain(MovementContext __instance, Player ____player, ref float __result)
    {
        if (!____player.IsYourPlayer || ____player.Profile.Side is not (EPlayerSide.Usec or EPlayerSide.Bear))
            return;

        // Native sprint movement uses the globals curve instead of the displayed buff.
        // Preserve its level-zero value and leave the native movement penalties in place.
        var curve = Singleton<BackendConfigSettingsClass>.Instance.SprintSpeed;
        var level = Math.Min(__instance.SkillManager.Strength.SummaryLevel, 50);
        var gain = (curve.y - curve.x) * level / 60f;
        __result += gain * (SprintGainScale - 1f);
    }

    private static void ApplyMaximum(SkillClass skill, SkillManager.SkillBuffClass buff, float maximum)
    {
        // Profile construction has already loaded levels. Refresh this buff now
        // and keep the native Elite rule for both this refresh and later level changes.
        var level = skill.SummaryLevel;
        buff.Max(maximum).BaseRuleFunc(Math.Min(level, 50));
        if (level > 50)
            buff.EliteRuleFunc?.Invoke();
    }
}
