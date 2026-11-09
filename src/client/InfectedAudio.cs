using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Codex.ZombieSurvival.Client;

internal static class InfectedAudio
{
    private const float DefaultVoiceVolume = 0.45f;
    private static float _voiceVolume = DefaultVoiceVolume;

    internal static void Install(Harmony harmony, BepInEx.Configuration.ConfigFile config)
    {
        var volume = config.Bind("Audio", "InfectedVoiceVolume", DefaultVoiceVolume,
            "Volume multiplier for infected voices and crowd screams (0 to 1).").Value;
        _voiceVolume = float.IsNaN(volume) ? DefaultVoiceVolume : Mathf.Clamp01(volume);

        var speech = AccessTools.Method(typeof(Player), nameof(Player.PlaySpeechFromTime));
        harmony.Patch(speech, transpiler: new HarmonyMethod(typeof(InfectedAudio), nameof(ScaleSpeech)));
        var crowd = AccessTools.Method(typeof(GClass1196), nameof(GClass1196.method_4));
        harmony.Patch(crowd, transpiler: new HarmonyMethod(typeof(InfectedAudio), nameof(ScaleCrowd)));
    }

    private static IEnumerable<CodeInstruction> ScaleSpeech(IEnumerable<CodeInstruction> instructions)
    {
        var volume = typeof(TaggedClip).GetField(nameof(TaggedClip.Volume));
        var scale = ((Func<float, Player, float>)ScaleSpeechVolume).Method;
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (instruction.opcode != OpCodes.Ldfld || !Equals(instruction.operand, volume)) continue;
            // Change the gain passed to the audio source without editing the shared clip.
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Call, scale);
        }
    }

    private static IEnumerable<CodeInstruction> ScaleCrowd(IEnumerable<CodeInstruction> instructions)
    {
        var scale = ((Func<float, float>)ScaleCrowdVolume).Method;
        foreach (var instruction in instructions)
        {
            yield return instruction;
            // The dedicated crowd sound method passes its only float constant as gain.
            if (instruction.opcode == OpCodes.Ldc_R4 && instruction.operand is 1f)
                yield return new CodeInstruction(OpCodes.Call, scale);
        }
    }

    private static float ScaleSpeechVolume(float volume, Player player) =>
        player.UsedSimplifiedSkeleton || Plugin.Infected(player) ? ScaleCrowdVolume(volume) : volume;

    private static float ScaleCrowdVolume(float volume) => volume * _voiceVolume;
}
