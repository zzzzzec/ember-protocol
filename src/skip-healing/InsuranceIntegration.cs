using System.Reflection;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.Screens;
using HarmonyLib;

namespace Codex.SkipPostRaidHealing;

internal static class InsuranceIntegration
{
    private static readonly DeploymentSkipCoordinator Coordinator = new();
    private static readonly HashSet<object> Bypass = new();
    private static MethodInfo _entry = null!;
    private static MethodInfo _next = null!;
    private static FieldInfo _session = null!;
    private static FieldInfo _ui = null!;
    private static PropertyInfo _current = null!;

    internal static void Install(Harmony harmony)
    {
        // These bindings were checked against this installation's original game assembly.
        _entry = AccessTools.Method(typeof(MainMenuControllerClass), "method_51", Type.EmptyTypes);
        _next = AccessTools.Method(typeof(MainMenuControllerClass), "method_52", Type.EmptyTypes);
        _session = AccessTools.Field(typeof(MainMenuControllerClass), "ISession");
        var uiType = typeof(UserInterfaceClass<EEftScreenType>);
        _ui = AccessTools.Field(uiType, "Gclass3858_0");
        _current = AccessTools.Property(uiType, "CurrentBaseScreenController");
        var button = typeof(InteractionButtonsContainer).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method => method.Name == "method_2" && method.IsGenericMethodDefinition && method.GetParameters().Length == 5)
            .MakeGenericMethod(typeof(EItemInfoButton));
        if (_entry == null || _next == null || _entry.ReturnType != typeof(void) || _next.ReturnType != typeof(void) ||
            _session?.FieldType != typeof(ISession) || _ui == null || !_ui.IsStatic || _current == null || button.ReturnType != typeof(void))
            throw new MissingMemberException("Unsupported game insurance menu or deployment layout.");
        harmony.Patch(button, prefix: new HarmonyMethod(typeof(InsuranceIntegration), nameof(MenuPrefix)));
        harmony.Patch(_entry, prefix: new HarmonyMethod(typeof(InsuranceIntegration), nameof(DeploymentPrefix)));
    }

    private static bool MenuPrefix(EItemInfoButton __0) =>
        !(Plugin.Enabled?.Value ?? true) || !(Plugin.HideInsuranceMenu?.Value ?? true) || __0 != EItemInfoButton.Insure;

    private static object? CurrentScreen()
    {
        var ui = _ui.GetValue(null);
        return ui == null ? null : _current.GetValue(ui);
    }

    private static bool DeploymentPrefix(MainMenuControllerClass __instance)
    {
        if (!(Plugin.Enabled?.Value ?? true) || !(Plugin.SkipInsuranceScreen?.Value ?? true) || Bypass.Contains(__instance))
            return true;
        try
        {
            var session = _session.GetValue(__instance) as ISession;
            var screen = CurrentScreen();
            if (session == null || screen == null) return true;
            // Skip at the page builder, so the insurance controller is never added to the Back stack.
            // Flush the same native operation queue that leaving insurance would flush.
            _ = Coordinator.Run(__instance,
                async () => (await session.FlushOperationQueue()).Succeed,
                () => ReferenceEquals(screen, CurrentScreen()) && ReferenceEquals(session, _session.GetValue(__instance)),
                () =>
                {
                    if ((Plugin.Enabled?.Value ?? true) && (Plugin.SkipInsuranceScreen?.Value ?? true))
                        _next.Invoke(__instance, null);
                    else Original(__instance);
                },
                () => Original(__instance), Plugin.Instance.Report);
            return false;
        }
        catch (Exception exception)
        {
            Plugin.Instance.Report(exception);
            return true;
        }
    }

    private static void Original(MainMenuControllerClass owner)
    {
        Bypass.Add(owner);
        try { _entry.Invoke(owner, null); }
        finally { Bypass.Remove(owner); }
    }
}
