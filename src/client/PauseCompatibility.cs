using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using EFT;
using HarmonyLib;

namespace Codex.ZombieSurvival.Client;

internal static class PauseCompatibility
{
    internal static void Install()
    {
        var controller = AccessTools.TypeByName("Pause.PauseController");
        if (controller is null) return;

        var harmony = new Harmony("codex.spt-z.pause-compat");
        try
        {
            harmony.Patch(AccessTools.DeclaredMethod(controller, "Unpause"),
                transpiler: new HarmonyMethod(typeof(PauseCompatibility), nameof(ResumeClockFirst)));
            harmony.Patch(AccessTools.DeclaredMethod(controller, "SetPlayerState"),
                transpiler: new HarmonyMethod(typeof(PauseCompatibility), nameof(KeepInfectedGoals)));
            harmony.Patch(AccessTools.DeclaredMethod(controller, "GetPlayers"),
                postfix: new HarmonyMethod(typeof(PauseCompatibility), nameof(SnapshotPlayers)));
            Plugin.Log.LogInfo("PAUSE compatibility installed: restore clocks before actors and preserve infected goals.");
        }
        catch (Exception exception)
        {
            harmony.UnpatchSelf();
            Plugin.Log.LogError($"Could not install PAUSE compatibility: {exception}");
        }
    }

    private static void SnapshotPlayers(ref IEnumerable<Player> __result)
    {
        // Unity activation callbacks can change the world list during pause or resume.
        __result = __result.ToArray();
    }

    private static IEnumerable<CodeInstruction> ResumeClockFirst(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var code = instructions.ToList();
        var controller = __originalMethod.DeclaringType!;
        var pausedDuration = AccessTools.DeclaredMethod(controller, "GetTimePaused");
        var updateTimers = AccessTools.DeclaredMethod(controller, "UpdateTimers");
        var resumeDate = AccessTools.DeclaredField(controller, "_unpausedDate");
        int start = code.FindIndex(i => i.Calls(pausedDuration)) - 2;
        int destination = code.FindIndex(i => i.StoresField(resumeDate));
        if (start < 0 || start + 3 >= code.Count || destination < 0 || destination >= start
            || code[start].opcode != OpCodes.Ldarg_0 || code[start + 1].opcode != OpCodes.Ldarg_0
            || !code[start + 3].Calls(updateTimers))
            throw new InvalidOperationException("Unexpected PAUSE clock update.");

        // Keep the original branch targets at the end of the method.
        var clockUpdate = code.GetRange(start, 4);
        var moved = clockUpdate.Select(i => new CodeInstruction(i.opcode, i.operand)).ToArray();
        foreach (var instruction in clockUpdate)
        {
            instruction.opcode = OpCodes.Nop;
            instruction.operand = null;
        }
        code.InsertRange(destination + 1, moved);
        return code;
    }

    private static IEnumerable<CodeInstruction> KeepInfectedGoals(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var goal = AccessTools.Method(typeof(BotOwner), nameof(BotOwner.CalcGoal));
        int index = code.FindIndex(i => i.Calls(goal));
        if (index < 2 || code[index - 2].operand is not MethodInfo ai || ai.Name != "get_AIData"
            || code[index - 1].operand is not MethodInfo owner || owner.Name != "get_BotOwner")
            throw new InvalidOperationException("Unexpected PAUSE bot goal update.");

        // The Player is already on the stack. Infected use their own pursuit and patrol logic.
        code[index - 2].opcode = OpCodes.Nop;
        code[index - 2].operand = null;
        code[index - 1].opcode = OpCodes.Nop;
        code[index - 1].operand = null;
        code[index].opcode = OpCodes.Call;
        code[index].operand = AccessTools.DeclaredMethod(typeof(PauseCompatibility), nameof(RestoreGoal));
        return code;
    }

    private static void RestoreGoal(Player player)
    {
        if (player != null && !Plugin.Infected(player))
            player.AIData?.BotOwner?.CalcGoal();
    }
}
