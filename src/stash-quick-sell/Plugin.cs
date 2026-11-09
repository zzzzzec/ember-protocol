using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;

namespace Codex.StashQuickSell;

[BepInPlugin(Guid, "仓库快捷出售 / Stash Quick Sell", "1.1.1")]
[BepInDependency("codex.zombie-survival.client")]
[BepInDependency("com.SPT.core", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("Tyfon.UIFixes", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "com.codex.spt.stashquicksell";
    internal static Plugin Instance = null!;
    internal ConfigEntry<bool> Enabled = null!;
    private ConfigEntry<string> _language = null!;
    private Localization _localization = null!;
    private Harmony? _harmony;
    internal readonly SaleCoordinator Sales = new();
    internal SaleService Service = null!;

    private void Awake()
    {
        Instance = this;
        Enabled = Config.Bind("常规", "启用", true, "在玩家仓库物品右键菜单加入卖出二级菜单。点击商人报价立即出售。");
        _language = Config.Bind("常规", "语言", "auto", new ConfigDescription("auto 跟随游戏语言；ch 简体中文；en English。", new AcceptableValueList<string>("auto", "ch", "en")));
        _localization = new Localization(Path.Combine(Path.GetDirectoryName(Info.Location)!, "i18n"), message => Logger.LogWarning(message));
        Service = new SaleService(this);
        try
        {
            MultiSelectInterop.Initialize();
            MenuIntegration.VerifyBindings();
            var contextMethod = typeof(SimpleContextMenu).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Single(method => method.Name == "method_0" && method.IsGenericMethodDefinition && method.GetParameters().Length == 5)
                .MakeGenericMethod(typeof(EItemInfoButton));
            _harmony = new Harmony(Guid);
            _harmony.Patch(contextMethod, prefix: new HarmonyMethod(typeof(MenuIntegration), nameof(MenuIntegration.AddSellEntry)));
            _harmony.Patch(AccessTools.Method(typeof(InteractionButtonsContainer), "method_3", new[] { typeof(DynamicInteractionClass) }),
                prefix: new HarmonyMethod(typeof(MenuIntegration), nameof(MenuIntegration.CreateOurButton)));
            Logger.LogInfo("Loaded for SPT 4.0.13: UI Fixes selection; all-items trader offers and totals; native batch ConfirmSell; ch/en localization.");
        }
        catch (Exception exception)
        {
            _harmony?.UnpatchSelf();
            Logger.LogError($"Could not install stash quick sell patches: {exception}");
        }
    }

    internal string Text(string key, params object[] args)
    {
        string language = _language.Value;
        if (language == "auto")
            language = Singleton<SharedGameSettingsClass>.Instantiated
                ? Singleton<SharedGameSettingsClass>.Instance.Game?.Settings?.Language?.GetValue() ?? "ch" : "ch";
        return _localization.Text(language, key, args);
    }

    internal void Log(Exception exception) => Logger.LogError(exception);
    internal void Warn(string text) => Logger.LogWarning(text);
    private void OnDestroy() => _harmony?.UnpatchSelf();
}
