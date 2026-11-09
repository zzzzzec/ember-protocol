using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.SessionEnd;
using HarmonyLib;

namespace Codex.SkipPostRaidHealing;

internal static class HealingViewIntegration
{
    private static readonly HashSet<HealthTreatmentServiceView> PostRaidViews = new();
    private static FieldInfo _screenView = null!;
    private static FieldInfo _applyButton = null!;
    private static MethodInfo _selectAll = null!;

    internal static void Install(Harmony harmony)
    {
        var screen = typeof(HealthTreatmentScreen);
        var service = typeof(HealthTreatmentServiceView);
        var show = AccessTools.Method(screen, "Show", new[]
        {
            typeof(Profile), typeof(InventoryController), typeof(HealthControllerClass),
            typeof(ItemUiContext), typeof(ISession)
        });
        var close = AccessTools.DeclaredMethod(screen, "Close", Type.EmptyTypes);
        var refresh = AccessTools.DeclaredMethod(service, "method_4", Type.EmptyTypes);
        var submit = AccessTools.DeclaredMethod(service, "method_1", Type.EmptyTypes);
        _screenView = AccessTools.Field(screen, "_healthTreatmentView");
        _applyButton = AccessTools.Field(service, "_applyButton");
        _selectAll = AccessTools.DeclaredMethod(service, "method_9", new[] { typeof(bool) });
        if (show?.ReturnType != typeof(void) || close?.ReturnType != typeof(void)
            || refresh?.ReturnType != typeof(void) || submit?.ReturnType != typeof(void)
            || _screenView?.FieldType != service || _applyButton?.FieldType != typeof(DefaultUIButton)
            || _selectAll?.ReturnType != typeof(void))
            throw new MissingMemberException("Unsupported post-raid treatment view or submit binding.");

        harmony.Patch(show, prefix: new HarmonyMethod(typeof(HealingViewIntegration), nameof(ShowPrefix)),
            postfix: new HarmonyMethod(typeof(HealingViewIntegration), nameof(ShowPostfix)));
        harmony.Patch(close, postfix: new HarmonyMethod(typeof(HealingViewIntegration), nameof(ClosePostfix)));
        harmony.Patch(refresh, postfix: new HarmonyMethod(typeof(HealingViewIntegration), nameof(RefreshPostfix)));
        harmony.Patch(submit, prefix: new HarmonyMethod(typeof(HealingViewIntegration), nameof(SubmitPrefix)));
    }

    private static HealthTreatmentServiceView View(HealthTreatmentScreen screen) =>
        (HealthTreatmentServiceView)_screenView.GetValue(screen);

    private static bool IsViewOnly(HealthTreatmentServiceView view) =>
        (Plugin.Enabled?.Value ?? true) && (Plugin.ViewOnlyHealing?.Value ?? true)
        && PostRaidViews.Contains(view);

    private static void ShowPrefix(HealthTreatmentScreen __instance)
    {
        // Register before native Show builds the rows and refreshes its buttons.
        PostRaidViews.Add(View(__instance));
    }

    private static void ShowPostfix(HealthTreatmentScreen __instance)
    {
        var view = View(__instance);
        if (!IsViewOnly(view)) return;
        // Selection changes the quote only; native damage history and health observers stay intact.
        _selectAll.Invoke(view, new object[] { false });
        DisableTreatment(view);
    }

    private static void ClosePostfix(HealthTreatmentScreen __instance) =>
        PostRaidViews.Remove(View(__instance));

    private static void RefreshPostfix(HealthTreatmentServiceView __instance)
    {
        // Natural regeneration and selecting a row can run native refresh again.
        if (IsViewOnly(__instance)) DisableTreatment(__instance);
    }

    private static void DisableTreatment(HealthTreatmentServiceView view)
    {
        var button = (DefaultUIButton)_applyButton.GetValue(view);
        button.Interactable = false;
        button.SetDisabledTooltip("仅查看伤情：战后治疗已禁用。", false);
    }

    private static bool SubmitPrefix(HealthTreatmentServiceView __instance) =>
        // Guard before native method_1 applies health changes, removes money or sends RestoreHealth.
        !IsViewOnly(__instance);
}
