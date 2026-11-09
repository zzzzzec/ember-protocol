using System.Text.Json.Nodes;
using HarmonyLib;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Config;

namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    private void ApplyEconomy()
    {
        if(Config["economy"]?["handbookPrices"] is not JsonObject authored)return;
        var prices=authored.DeepClone().AsObject();
        if((bool?)Config["economy"]?["retailReference"]==true)
        {
            var baseline=db.GetHandbook().Items.ToDictionary(x=>x.Id.ToString(),x=>x.Price??0);
            foreach(var (tpl,price) in prices)baseline[tpl]=price!.GetValue<double>();
            var selected=Config["economy"]?["retailTemplates"]?.AsArray().Select(x=>x!.ToString()).ToHashSet();
            foreach(var entry in EconomyPolicy.RetailValuations(Read("data/trader.json").AsObject(),baseline,_items,selected))prices[entry.Key]=entry.Value;
        }
        var report=new JsonArray();
        foreach(var (tpl,node) in prices)
        {
            var id=(MongoId)tpl;var price=node!.GetValue<double>();
            if(!double.IsFinite(price)||price<=0)throw new InvalidDataException("Invalid SPT-Z valuation: "+tpl);
            var entry=db.GetHandbook().Items.Single(x=>x.Id==id);
            var before=entry.Price;entry.Price=price;
            // Also use the native override mechanism, so any later cache hydration keeps this value.
            configs.GetConfig<ItemConfig>().HandbookPriceOverride[id]=new HandbookPriceOverride {ParentId=entry.ParentId,Price=price};
            db.GetPrices()[id]=price;
            report.Add(new JsonObject{["tpl"]=tpl,["before"]=before,["price"]=price});
        }
        // Native handbook and trader helpers cache prices before late-loading mods run.
        // Invalidate once at startup; no per-frame work and no on-disk vanilla database edits.
        var cache=AccessTools.Field(typeof(HandbookHelper),"_handbookPriceCache")??throw new MissingFieldException("Handbook price cache");
        cache.SetValue(handbookHelper,null);
        var highest=AccessTools.Field(typeof(TraderHelper),"HighestTraderPriceItems")??throw new MissingFieldException("Trader price cache");
        ((Dictionary<MongoId,double>)highest.GetValue(traderHelper)!).Clear();
        var staticPrices=ragfairPrices.GetAllStaticPrices();
        foreach(var (tpl,node) in prices)
        {
            var price=node!.GetValue<double>();var id=(MongoId)tpl;staticPrices[id]=price;
            if(handbookHelper.GetTemplatePrice(id)!=price||ragfairPrices.GetStaticPriceForItem(id)!=price)
                throw new InvalidDataException("SPT-Z price cache mismatch: "+tpl);
        }
        File.WriteAllText(Path.Combine(Folder,"economy-report.json"),new JsonObject{
            ["version"]=Config["version"]?.DeepClone(),["retailReference"]=(bool?)Config["economy"]?["retailReference"]==true,["referenceMode"]=Config["economy"]?["referenceMode"]?.DeepClone(),["generatedUtc"]=DateTimeOffset.UtcNow.ToString("O"),
            ["valuations"]=report,["handbookAndClientPricesAgree"]=true
        }.ToJsonString(new(){WriteIndented=true}));
        log.Success($"[SPT-Z] Economy {Config["version"]}: {prices.Count} unified valuations; cash offers, handbook and client prices agree.",null);
    }
}
