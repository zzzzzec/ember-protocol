using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;

namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    private HashSet<string> _closedMapKeys = new(StringComparer.Ordinal);

    private bool IsClosedMapKey(string template) => _closedMapKeys.Contains(template);

    private void InitializeClosedMapKeys()
    {
        var catalog = Read("data/map-keys.json").AsObject();
        var available = catalog.Where(map => Maps[map.Key] is JsonObject)
            .SelectMany(map => map.Value!.AsObject().Select(key => key.Key))
            .ToHashSet(StringComparer.Ordinal);
        _closedMapKeys = catalog.SelectMany(map => map.Value!.AsObject().Select(key => key.Key))
            .Where(key => !available.Contains(key) && db.GetItems().ContainsKey((MongoId)key))
            .ToHashSet(StringComparer.Ordinal);

        var removed = 0;
        foreach (var (_, trader) in db.GetTraders())
            removed += RemoveClosedMapKeyOffers(trader.Assort);

        foreach (var airdrop in configs.GetConfig<AirdropConfig>().Loot.Values)
        {
            foreach (var key in _closedMapKeys)
            {
                var id = (MongoId)key;
                if (!airdrop.ItemBlacklist.Contains(id)) airdrop.ItemBlacklist.Add(id);
                airdrop.ForcedLoot?.Remove(id);
            }
        }

        log.Info($"[SPT-Z] Closed-map keys: {_closedMapKeys.Count} blocked templates; {removed} base trader offers removed.", null);
    }

    private int RemoveClosedMapKeyOffers(TraderAssort? assort)
    {
        if (assort?.Items is not { } items) return 0;
        var blocked = items.Where(item => IsClosedMapKey(item.Template.ToString())).ToArray();
        if (blocked.Length == 0) return 0;

        var byId = items.ToDictionary(item => item.Id.ToString(), StringComparer.Ordinal);
        var offerIds = new HashSet<MongoId>();
        foreach (var item in blocked)
        {
            var root = item;
            while (root.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent))
                root = parent;
            offerIds.Add(root.Id);
        }

        var removed = offerIds.SelectMany(id => items.GetItemWithChildren(id)).Select(item => item.Id).ToHashSet();
        assort.Items.RemoveAll(item => removed.Contains(item.Id));
        foreach (var id in offerIds)
        {
            assort.BarterScheme?.Remove(id);
            assort.LoyalLevelItems?.Remove(id);
        }
        return offerIds.Count;
    }
}
