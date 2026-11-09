using BepInEx.Bootstrap;
using EFT.InventoryLogic;

namespace Codex.StashQuickSell;

internal static class MultiSelectInterop
{
    private static Func<IEnumerable<Item>>? _getItems;

    internal static void Initialize()
    {
        if (!Chainloader.PluginInfos.TryGetValue("Tyfon.UIFixes", out var plugin)) return;
        var type = plugin.Instance.GetType().Assembly.GetType("UIFixes.MultiSelectController");
        var method = type?.GetMethod("GetItems", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
            null, Type.EmptyTypes, null);
        if (method == null || method.ReturnType != typeof(IEnumerable<Item>))
            throw new MissingMethodException("UI Fixes MultiSelectController.GetItems is unavailable.");
        _getItems = (Func<IEnumerable<Item>>)Delegate.CreateDelegate(typeof(Func<IEnumerable<Item>>), method);
    }

    internal static Item[] Capture(Item clicked) => SelectionPolicy.Resolve(clicked,
        _getItems?.Invoke() ?? Array.Empty<Item>(), item => item.Id, item => item.GetAllParentItems());
}

internal sealed class SellSelection
{
    internal readonly Item Clicked;
    internal readonly Item[] Items;
    internal SellSelection(Item clicked)
    { Clicked = clicked; Items = MultiSelectInterop.Capture(clicked); }

    internal bool IsCurrent() => SelectionPolicy.SameItems(Items.Select(item => (string)item.Id),
        MultiSelectInterop.Capture(Clicked).Select(item => (string)item.Id));
}
