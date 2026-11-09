namespace Codex.StashQuickSell;

internal static class SelectionPolicy
{
    internal static T[] Resolve<T>(T clicked, IEnumerable<T> selected,
        Func<T, string> id, Func<T, IEnumerable<T>> parents)
    {
        var items = selected.GroupBy(id).Select(group => group.First()).ToArray();
        // A selection in another window must never redirect a single-item right click.
        if (!items.Any(item => id(item) == id(clicked))) return new[] { clicked };
        var ids = new HashSet<string>(items.Select(id));
        // A container/weapon quote already includes its contents and attachments.
        return items.Where(item => !parents(item).Any(parent => ids.Contains(id(parent)))).ToArray();
    }

    internal static bool SameItems(IEnumerable<string> expected, IEnumerable<string> current) =>
        new HashSet<string>(expected).SetEquals(current);
}
