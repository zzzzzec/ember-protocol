using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Trade;
namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    private void ConfigureRoubleBuying()
    {
        foreach(var id in new[]{TraderId,FenceId}.Concat(OpeningTraderIds))
        {
            if(!ActiveTrader((MongoId)id))continue;
            var trader=db.GetTrader((MongoId)id)!;
            EditInPlace(trader.Base,n=>{
                n["items_buy_prohibited"]??=new JsonObject();
                n["items_buy_prohibited"]!["id_list"]??=new JsonArray();
                var ids=n["items_buy_prohibited"]!["id_list"]!.AsArray();
                if(!ids.Any(x=>x?.ToString()==TraderSalePolicy.Roubles))ids.Add(TraderSalePolicy.Roubles);
            });
        }
    }
    public bool AllowRoubleSale(PmcData profile,ProcessBaseTradeRequestData request,MongoId session,ref ItemEventRouterResponse result)
    {
        if(request is not ProcessSellTradeRequestData sell||!TraderSalePolicy.ContainsSettlementMoney(profile.Inventory.Items,sell.Items.Select(i=>i.Id.ToString())))return true;
        result=FieldDenied(session,"卢布用于付款，不能当作商品卖回。请先把现金移出待售物品。");
        Trace("trade.rouble_sale_rejected",session,new{trader=sell.TransactionId.ToString(),inventoryUnchanged=true});return false;
    }
}
