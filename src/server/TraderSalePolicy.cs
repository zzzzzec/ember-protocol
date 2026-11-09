using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Trade;
namespace Codex.ZombieSurvival;

public static class TraderSalePolicy
{
    public const string Roubles="5449016a4bdc2d6f028b456f";
    public static bool ContainsSettlementMoney(IEnumerable<Item> inventory,IEnumerable<string> soldIds)
    {
        var items=inventory.ToArray();
        var removed=soldIds.Select(id=>string.Concat(id.Where(c=>!char.IsWhiteSpace(c)))).ToHashSet();
        var changed=true;
        while(changed)
        {
            changed=false;
            foreach(var item in items)
                if(item.ParentId is { } parent&&removed.Contains(parent.ToString())&&removed.Add(item.Id.ToString()))changed=true;
        }
        return items.Any(item=>item.Template.ToString()==Roubles&&removed.Contains(item.Id.ToString()));
    }
}
