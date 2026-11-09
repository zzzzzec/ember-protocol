using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Repair;

namespace Codex.ZombieSurvival;
public sealed partial class SurvivalService
{
    public bool AllowTraderRepair(MongoId session,TraderRepairActionDataRequest request,ref ItemEventRouterResponse result)
    {
        if(request.TraderId==(MongoId)TraderId)return true;
        result=FieldDenied(session,"没有修理工具的话，只能请军需官维修。");return false;
    }
    public bool AllowKitRepair(MongoId session,PmcData profile,RepairActionDataRequest request,ref ItemEventRouterResponse result)
    {
        string? reason=null;var items=profile.Inventory?.Items;
        if(request.Target is not { } target||items?.FirstOrDefault(i=>i.Id==target)?.Upd?.Repairable is null)reason="先选一件需要维修的装备。";
        else if(request.RepairKitsInfo is not {Count:>0})reason="先准备可用的修理工具。";
        else
        {
            var seen=new HashSet<MongoId>();
            foreach(var entry in request.RepairKitsInfo)
            {
                var kit=items!.FirstOrDefault(i=>i.Id==entry.Id);var amount=(double?)entry.Count??0;
                if(!seen.Add(entry.Id)||!double.IsFinite(amount)||amount<=0||kit is null){reason="修理工具或消耗数量不正确。";break;}
                var maximum=(double?)_items.GetValueOrDefault(kit.Template.ToString())?["_props"]?["MaxRepairResource"]??0;
                var available=(double?)Node(kit)["upd"]?["RepairKit"]?["Resource"]??maximum;
                if(maximum<=0||amount>available){reason="修理工具的剩余耗材不够。";break;}
            }
        }
        if(reason is null)return true;
        result=FieldDenied(session,reason);Trace("repair.rejected",session,new{reason});return false;
    }
}
