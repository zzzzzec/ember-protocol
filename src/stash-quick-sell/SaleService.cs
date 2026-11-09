using System.Globalization;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.Hideout;
using EFT.InventoryLogic;
using EFT.Trading;
using EFT.UI;
using SPT.Reflection.Utils;

namespace Codex.StashQuickSell;

internal sealed class TraderOffer
{
    public TraderClass Trader { get; }
    public SaleBatchQuote Quote { get; }
    public string PriceText { get; }
    public double RubleValue { get; }
    public TraderOffer(TraderClass trader, SaleBatchQuote quote, string priceText, double rubleValue)
    { Trader = trader; Quote = quote; PriceText = priceText; RubleValue = rubleValue; }
}

internal sealed class SaleService
{
    private readonly Plugin _plugin;
    private ISession? _supplySession;
    private readonly Dictionary<TraderClass, Task<bool>> _supplyRequests = new();
    public SaleService(Plugin plugin) => _plugin = plugin;

    internal ISession? Session() => ClientAppUtils.GetMainApp()?.GetClientBackEndSession();

    internal bool Eligible(Item item, ISession session)
    {
        if (!_plugin.Enabled.Value || item == null || item.StackObjectsCount <= 0) return false;
        if (Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance is not HideoutGameWorld) return false;
        if (Singleton<MenuUI>.Instantiated && Singleton<MenuUI>.Instance.HideoutAreaTransferItemsScreen != null &&
            Singleton<MenuUI>.Instance.HideoutAreaTransferItemsScreen.isActiveAndEnabled) return false;
        var stash = session.Profile?.Inventory?.Stash;
        if (stash == null || ReferenceEquals(item, stash)) return false;
        var parents = item.GetAllParentItems().ToArray();
        if (!parents.Any(parent => ReferenceEquals(parent, stash))) return false;
        if (parents.Any(parent => parent is InventoryEquipment || parent.PinLockState == EItemPinLockState.Locked)) return false;
        if (item.GetAllItems().Any(child => child.PinLockState == EItemPinLockState.Locked)) return false;
        return session.Profile!.Examined(item);
    }

    private static bool AvailableTrader(TraderClass trader) =>
        ZombieSurvival.Client.Plugin.TraderVisible(trader) && trader.Info.Available &&
        !trader.Settings.AvailableInRaid && trader.ITraderInteractions != null;

    internal async Task<bool> LoadPrices(ISession session)
    {
        if (!ReferenceEquals(_supplySession, session))
        {
            _supplySession = session;
            _supplyRequests.Clear();
        }
        var tasks = new List<Task<bool>>();
        foreach (var trader in session.Traders.Where(AvailableTrader))
        {
            if (trader.SupplyData_0 != null) continue;
            if (!_supplyRequests.TryGetValue(trader, out var task))
            {
                task = LoadOne(trader);
                _supplyRequests.Add(trader, task);
            }
            tasks.Add(task);
        }
        bool[] results = await Task.WhenAll(tasks);
        // Failed reads may be retried on the next explicit menu open.
        foreach (var entry in _supplyRequests.Where(entry => entry.Value.IsCompleted && !entry.Value.Result).ToArray())
            _supplyRequests.Remove(entry.Key);
        return results.All(success => success);
    }

    private async Task<bool> LoadOne(TraderClass trader)
    {
        try
        {
            var result = await trader.ITraderInteractions.GetSupplyData(trader.Id);
            if (result.Succeed && result.Value != null)
            {
                trader.SupplyData_0 = result.Value;
                return true;
            }
            _plugin.Warn($"Could not load native supply prices for trader {trader.Id}");
        }
        catch (Exception exception) { _plugin.Log(exception); }
        return false;
    }

    internal List<TraderOffer> Offers(SellSelection selection, ISession session)
    {
        if (!ReferenceEquals(Session(), session) || !selection.IsCurrent() ||
            selection.Items.Length == 0 || !selection.Items.All(item => Eligible(item, session))) return new();
        var offers = new List<TraderOffer>();
        foreach (var trader in session.Traders.Where(AvailableTrader))
        {
            var offer = Offer(selection.Items, trader);
            if (offer != null) offers.Add(offer);
        }
        return offers.OrderByDescending(offer => offer.RubleValue).ToList();
    }

    internal TraderOffer? Offer(IReadOnlyList<Item> items, TraderClass trader)
    {
        if (!AvailableTrader(trader) || trader.SupplyData_0 == null) return null;
        try
        {
            var quote = SaleBatchQuote.Create(items.Select(item => ItemQuote(item, trader)));
            if (quote == null) return null;
            string currency = GClass3130.GetCurrencyCharById(new MongoID(quote.CurrencyId));
            // GetUserItemPrice already includes the entire stack and its contents. Do not multiply again.
            string priceText = quote.Amount.ToString("N0", CultureInfo.InvariantCulture) + " " + currency;
            double course = trader.SupplyData_0.CurrencyCourses.TryGetValue(quote.CurrencyId, out var value) ? value : 1;
            return new TraderOffer(trader, quote, priceText, quote.Amount * course);
        }
        catch (Exception exception) { _plugin.Log(exception); return null; }
    }

    private static SaleQuote? ItemQuote(Item item, TraderClass trader)
    {
        var nativePrice = trader.GetUserItemPrice(item);
        if (!nativePrice.HasValue || nativePrice.Value.Amount <= 0 || !nativePrice.Value.CurrencyId.HasValue) return null;
        var price = nativePrice.Value;
        return new SaleQuote(item.Id, trader.Id, item.StackObjectsCount, price.Amount,
            price.CurrencyId!.Value, Fingerprint(item));
    }

    private static string Fingerprint(Item item) => string.Join("|", item.GetAllItems()
        .OrderBy(child => child.Id).Select(child => $"{child.Id}:{child.Version}:{child.StackObjectsCount}:{child.PinLockState}"));

    internal void Sell(SellSelection selection, ISession session, TraderOffer offer, Action closeMenu)
    {
        try
        {
            var status = _plugin.Sales.TrySell(offer.Quote,
                () => ReferenceEquals(Session(), session) && selection.IsCurrent() &&
                    selection.Items.All(item => Eligible(item, session))
                    ? Offer(selection.Items, offer.Trader)?.Quote : null,
                (quote, completed) =>
                {
                    var items = selection.Items.ToDictionary(item => (string)item.Id);
                    var references = quote.Items.Select(line => new TradingItemReference
                        { Item = items[line.ItemId], Count = line.Count }).ToArray();
                    closeMenu();
                    offer.Trader.ITraderInteractions.ConfirmSell(quote.TraderId,
                        references, quote.Amount,
                        new Callback(result =>
                        {
                            if (!result.Succeed) _plugin.Warn($"Native sale rejected for {quote.Items.Count} item(s), trader {quote.TraderId}: {result}");
                            completed(result.Succeed);
                        }));
                },
                success =>
                {
                    if (success)
                    {
                        if (Singleton<GUISounds>.Instantiated)
                            Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.TradeOperationComplete);
                        Notify(_plugin.Text("sold", offer.Trader.LocalizedName, offer.PriceText));
                    }
                    else Warn(_plugin.Text("failed"));
                });
            switch (status)
            {
                case SaleStartResult.Busy: Warn(_plugin.Text("busy")); break;
                case SaleStartResult.Changed: Warn(_plugin.Text("changed")); closeMenu(); break;
                case SaleStartResult.Unavailable: Warn(_plugin.Text("unavailable")); closeMenu(); break;
            }
        }
        catch (Exception exception) { _plugin.Log(exception); Warn(_plugin.Text("error")); }
    }

    internal static void Notify(string text) => NotificationManagerClass.DisplayMessageNotification(text, ENotificationDurationType.Default);
    internal static void Warn(string text) => NotificationManagerClass.DisplayWarningNotification(text, ENotificationDurationType.Default);
}
