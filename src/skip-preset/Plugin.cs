using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using EFT.UI;
using HarmonyLib;

namespace Codex.SkipPresetExitConfirmation;

[BepInPlugin(PluginGuid, "Skip Preset Exit Confirmation / 预设直接返回", "1.0.0")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.codex.spt.skippresetexitconfirmation";
    private static ConfigEntry<bool>? _enabled;
    private Harmony? _harmony;

    private void Awake()
    {
        _enabled = Config.Bind("General", "Enabled", true,
            "离开武器预设编辑器时直接丢弃未保存的修改，不弹出确认框。关闭可恢复原版提示。手动保存仍然可用。");
        try
        {
            // In SPT 4.0.13 this is the editor's discard-confirmation task.
            // Returning true lets the original controller finish its normal close flow.
            var target = typeof(EditBuildScreen).GetMethod("method_35",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            if (target == null || target.IsStatic || target.ReturnType != typeof(Task<bool>))
            {
                Logger.LogError("Unsupported game version: preset exit confirmation task not found. No patch applied.");
                return;
            }
            _harmony = new Harmony(PluginGuid);
            _harmony.Patch(target, prefix: new HarmonyMethod(typeof(Plugin), nameof(ExitConfirmationPrefix)));
            Logger.LogInfo("Loaded for SPT 4.0.13: weapon preset exit discards unsaved edits without a prompt. Manual save and normal screen cleanup are preserved.");
        }
        catch (Exception exception)
        {
            _harmony?.UnpatchSelf();
            Logger.LogError($"Could not apply preset exit confirmation patch: {exception}");
        }
    }

    private static bool ExitConfirmationPrefix(ref Task<bool> __result)
    {
        if (!(_enabled?.Value ?? true))
            return true;
        __result = Task.FromResult(true);
        return false;
    }

    private void OnDestroy() => _harmony?.UnpatchSelf();
}
