using System.Threading;

namespace Codex.StashQuickSell;

internal sealed class SaleQuote
{
    public string ItemId { get; }
    public string TraderId { get; }
    public int Count { get; }
    public int Amount { get; }
    public string CurrencyId { get; }
    public string Fingerprint { get; }

    public SaleQuote(string itemId, string traderId, int count, int amount, string currencyId, string fingerprint)
    {
        ItemId = itemId; TraderId = traderId; Count = count; Amount = amount;
        CurrencyId = currencyId; Fingerprint = fingerprint;
    }

    public bool Matches(SaleQuote other) => ItemId == other.ItemId && TraderId == other.TraderId &&
        Count == other.Count && Amount == other.Amount && CurrencyId == other.CurrencyId && Fingerprint == other.Fingerprint;
}

internal enum SaleStartResult { Submitted, Busy, Unavailable, Changed }

internal sealed class SaleBatchQuote
{
    public IReadOnlyList<SaleQuote> Items { get; }
    public string TraderId => Items[0].TraderId;
    public string CurrencyId => Items[0].CurrencyId;
    public int Amount { get; }

    private SaleBatchQuote(SaleQuote[] items, int amount)
    { Items = Array.AsReadOnly(items); Amount = amount; }

    // A missing quote rejects the entire trader, never just the unsellable item.
    internal static SaleBatchQuote? Create(IEnumerable<SaleQuote?> quotes)
    {
        var items = quotes.ToArray();
        if (items.Length == 0 || items.Any(item => item == null)) return null;
        var valid = items.Select(item => item!).OrderBy(item => item.ItemId, StringComparer.Ordinal).ToArray();
        if (valid.Any(item => item.Count <= 0 || item.Amount <= 0 ||
            item.TraderId != valid[0].TraderId || item.CurrencyId != valid[0].CurrencyId) ||
            valid.Select(item => item.ItemId).Distinct().Count() != valid.Length) return null;
        long amount = valid.Sum(item => (long)item.Amount);
        return amount > int.MaxValue ? null : new SaleBatchQuote(valid, (int)amount);
    }

    public bool Matches(SaleBatchQuote other) => Items.Count == other.Items.Count &&
        Amount == other.Amount && Items.Zip(other.Items, (left, right) => left.Matches(right)).All(match => match);
}

internal sealed class SaleCoordinator
{
    private int _pending;
    public bool IsBusy => Volatile.Read(ref _pending) != 0;

    // One native operation at a time; callbacks, never optimistic notifications, determine success.
    public SaleStartResult TrySell(SaleBatchQuote quote, Func<SaleBatchQuote?> reprice,
        Action<SaleBatchQuote, Action<bool>> submit, Action<bool> completed)
    {
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0)
            return SaleStartResult.Busy;
        try
        {
            var current = reprice();
            if (current == null || current.Amount <= 0)
            {
                Interlocked.Exchange(ref _pending, 0);
                return SaleStartResult.Unavailable;
            }
            if (!quote.Matches(current))
            {
                Interlocked.Exchange(ref _pending, 0);
                return SaleStartResult.Changed;
            }
            int callbackReceived = 0;
            submit(current, success =>
            {
                if (Interlocked.Exchange(ref callbackReceived, 1) != 0)
                    return;
                Interlocked.Exchange(ref _pending, 0);
                completed(success);
            });
            return SaleStartResult.Submitted;
        }
        catch
        {
            Interlocked.Exchange(ref _pending, 0);
            throw;
        }
    }
}
