using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using EFT;
using EFT.UI;
using EFT.UI.Screens;
using EFT.UI.SessionEnd;
using HarmonyLib;

namespace Codex.SkipPostRaidHealing;

[BepInPlugin(PluginGuid, "战后伤情与投保 / Post-Raid Health & Insurance", "1.2.0")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.codex.spt.skippostraidhealing";
    internal static ConfigEntry<bool>? Enabled;
    private static ConfigEntry<bool>? _skipHealing;
    internal static ConfigEntry<bool>? ViewOnlyHealing;
    internal static ConfigEntry<bool>? HideInsuranceMenu;
    internal static ConfigEntry<bool>? SkipInsuranceScreen;
    internal static Plugin Instance = null!;
    private Harmony? _harmony;

    private void Awake()
    {
        Instance = this;
        Enabled = Config.Bind("General", "Enabled", true,
            "总开关。启用下面勾选的功能；关闭后恢复原版治疗与投保界面。");
        _skipHealing = Config.Bind("功能", "跳过战后付费治疗", false,
            "跳过战后付费治疗页；保留伤势、仓库药品治疗和自然回血。");
        ViewOnlyHealing = Config.Bind("功能", "战后治疗页仅查看伤情", true,
            "保留战后原生伤情页面，默认取消治疗选择，禁用治疗按钮并阻止治疗提交。优先于跳过战后付费治疗；不影响仓库用药、自然回血和商人服务页。");
        HideInsuranceMenu = Config.Bind("功能", "隐藏右键投保", true,
            "隐藏物品右键菜单中的投保选项；保留其他右键功能。");
        SkipInsuranceScreen = Config.Bind("功能", "跳过部署投保页", true,
            "部署时直接进入原版下一步，返回时也不会经过投保页；仍需手动确认开始战局。");

        try
        {
            var target = typeof(HealthTreatmentScreen).GetMethod("IsAvailable",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(Profile), typeof(HealthControllerClass), typeof(Profile.TraderInfo) }, null);
            if (target == null || target.ReturnType != typeof(bool))
            {
                Logger.LogError("Unsupported game version: post-raid healing availability method was not found. No patch applied.");
                return;
            }

            _harmony = new Harmony(PluginGuid);
            _harmony.Patch(target, prefix: new HarmonyMethod(typeof(Plugin), nameof(AvailabilityPrefix)));
            HealingViewIntegration.Install(_harmony);
            InsuranceIntegration.Install(_harmony);
            Logger.LogInfo("Loaded for SPT 4.0.13: native post-raid injury screen with view-only treatment, context-menu insurance hiding and deployment insurance bypass. Native damage history, inventory flush and deployment checks preserved.");
        }
        catch (Exception exception)
        {
            _harmony?.UnpatchSelf();
            Logger.LogError($"Could not apply the healing and insurance patches: {exception}");
        }
    }

    private static bool AvailabilityPrefix(ref bool __result)
    {
        // The original post-raid operation handles the unavailable-screen path.
        // This patch deliberately receives no health, inventory, or session objects.
        return HealingScreenPolicy.Apply((Enabled?.Value ?? true) && (_skipHealing?.Value ?? false)
            && !(ViewOnlyHealing?.Value ?? true), ref __result);
    }

    internal void Report(Exception exception) => Logger.LogError($"Insurance skip failed; preserving the original flow: {exception}");

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
    }
}
