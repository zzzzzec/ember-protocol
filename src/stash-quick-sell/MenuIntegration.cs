using System.Reflection;
using Comfort.Common;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using UnityEngine;

namespace Codex.StashQuickSell;

internal sealed class SellInteraction : DynamicInteractionClass
{
    internal readonly Func<IEnumerable<SellInteraction>>? Children;
    internal readonly bool Enabled;
    internal SellInteraction(string id, string caption, Action? action = null,
        Func<IEnumerable<SellInteraction>>? children = null, bool enabled = true)
        : base(id, caption, action, null)
    { Children = children; Enabled = enabled; }
}

internal sealed class SellInteractions : GClass3775
{
    internal SellInteractions(ItemUiContext context) : base(context) { }
}

internal sealed class SellMenuState
{
    internal readonly SellSelection Selection;
    internal readonly ISession Session;
    internal readonly SimpleContextMenu MainMenu;
    internal bool Loading;
    internal bool Loaded;
    internal bool Failed;
    internal Action? RefreshVisible;
    internal SellMenuState(SellSelection selection, ISession session, SimpleContextMenu mainMenu)
    { Selection = selection; Session = session; MainMenu = mainMenu; }

    internal async void Load()
    {
        if (Loading) return;
        Loading = true;
        Failed = false;
        try
        {
            Failed = !await Plugin.Instance.Service.LoadPrices(Session);
            Loaded = true;
        }
        catch (Exception exception) { Plugin.Instance.Log(exception); Failed = true; }
        finally
        {
            Loading = false;
            try { RefreshVisible?.Invoke(); } catch (Exception exception) { Plugin.Instance.Log(exception); }
        }
    }

    internal IEnumerable<SellInteraction> Entries()
    {
        var plugin = Plugin.Instance;
        if (Loading || !Loaded && !Failed)
        { yield return new SellInteraction("codex.sell.loading", plugin.Text("loading"), enabled: false); yield break; }
        if (plugin.Sales.IsBusy)
        { yield return new SellInteraction("codex.sell.busy", plugin.Text("busy"), enabled: false); yield break; }
        var offers = plugin.Service.Offers(Selection, Session);
        foreach (var offer in offers)
        {
            string caption = plugin.Text("offer", offer.Trader.LocalizedName, offer.PriceText);
            if (Selection.Items.Length > 1) caption += plugin.Text("selection", Selection.Items.Length);
            else if (offer.Quote.Items[0].Count > 1) caption += plugin.Text("stack", offer.Quote.Items[0].Count);
            if (Selection.Items.Any(item => item.GetAllItems().Any(child => !ReferenceEquals(child, item)))) caption += plugin.Text("contents");
            yield return new SellInteraction("codex.sell.trader." + offer.Trader.Id, caption,
                () => plugin.Service.Sell(Selection, Session, offer, () => { if (MainMenu != null) MainMenu.Close(); }));
        }
        if (Failed)
            yield return new SellInteraction("codex.sell.retry", plugin.Text("load_failed"), () => { Load(); RefreshVisible?.Invoke(); });
        else if (offers.Count == 0)
            yield return new SellInteraction("codex.sell.none", plugin.Text(Selection.Items.Length > 1 ? "no_batch_buyers" : "no_buyers"), enabled: false);
    }
}

internal static class MenuIntegration
{
    private const string EntryId = "codex.stashquicksell.sell";
    private static readonly FieldInfo Buttons = AccessTools.Field(typeof(InteractionButtonsContainer), "_buttonsContainer");
    private static readonly FieldInfo Template = AccessTools.Field(typeof(InteractionButtonsContainer), "_buttonTemplate");
    private static readonly FieldInfo Current = AccessTools.Field(typeof(InteractionButtonsContainer), "simpleContextMenuButton_0");

    internal static void VerifyBindings()
    {
        if (Buttons == null || Template == null || Current == null ||
            AccessTools.Method(typeof(InteractionButtonsContainer), "method_1")?.GetParameters().Length != 9 ||
            AccessTools.Method(typeof(InteractionButtonsContainer), "method_5") == null)
            throw new MissingMemberException("Unsupported game menu button layout.");
    }

    internal static void AddSellEntry(SimpleContextMenu __instance,
        ItemInfoInteractionsAbstractClass<EItemInfoButton> contextInteractions, Item item)
    {
        try
        {
            var dictionary = contextInteractions?.Dictionary_0;
            if (dictionary == null) return;
            dictionary.Remove(EntryId);
            var plugin = Plugin.Instance;
            if (!plugin.Enabled.Value || item == null || contextInteractions is not GClass3758) return;
            if ((contextInteractions as ContextInteractionsAbstractClass)?.ItemContextAbstractClass?.ViewType != EItemViewType.Inventory) return;
            var session = plugin.Service.Session();
            var selection = new SellSelection(item);
            if (session == null || selection.Items.Length == 0 || !selection.Items.All(selected => plugin.Service.Eligible(selected, session))) return;
            var state = new SellMenuState(selection, session, __instance);
            dictionary[EntryId] = new SellInteraction(EntryId, plugin.Text("sell"), children: state.Entries);
            state.Load();
            // The child's delegate keeps the state alive only for the lifetime of this menu.
        }
        catch (Exception exception) { Plugin.Instance.Log(exception); }
    }

    internal static bool CreateOurButton(InteractionButtonsContainer __instance, DynamicInteractionClass __0)
    {
        if (__0 is not SellInteraction interaction) return true;
        try
        {
            SimpleContextMenuButton? button = null;
            void OpenChildren()
            {
                if (!Plugin.Instance.Enabled.Value || !interaction.Enabled || interaction.Children == null ||
                    __instance == null || !__instance.isActiveAndEnabled || button == null) return;
                Current.SetValue(__instance, button);
                __instance.method_4();
                var subInteractions = new SellInteractions(ItemUiContext.Instance);
                foreach (var child in interaction.Children()) subInteractions.Dictionary_0[child.Id] = child;
                __instance.SetSubInteractions(subInteractions);
            }
            void Hover()
            {
                Current.SetValue(__instance, button);
                __instance.method_4();
                if (interaction.Children != null) OpenChildren();
            }
            button = __instance.method_1(interaction.Id, interaction.Key,
                (SimpleContextMenuButton)Template.GetValue(__instance), (RectTransform)Buttons.GetValue(__instance), null,
                () =>
                {
                    if (!Plugin.Instance.Enabled.Value || !interaction.Enabled) return;
                    if (interaction.Children != null) OpenChildren(); else interaction.Execute();
                }, Hover, interaction.Children != null, interaction.Children == null);
            button.SetButtonInteraction(interaction.Enabled ? SuccessfulResult.New : new FailedResult(interaction.Key, 0));
            __instance.method_5(button);
            if (interaction.Children?.Target is SellMenuState state)
            {
                state.RefreshVisible = () =>
                {
                    if (__instance != null && button != null && __instance.isActiveAndEnabled &&
                        ReferenceEquals(Current.GetValue(__instance), button)) OpenChildren();
                };
            }
            return false;
        }
        catch (Exception exception)
        {
            Plugin.Instance.Log(exception);
            return false;
        }
    }
}
