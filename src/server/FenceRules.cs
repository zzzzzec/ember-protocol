using System.Globalization;
using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Spt.Config;

namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    private const string StorageContainerClass = "5795f317245977243854e041";
    public const string FenceId = "579dc571d53a0658a154fbec";
    private const string FenceMigration = "codex.ember.fence-standing";
    private Dictionary<string, double>? _oldFenceQuestCredit;
    public bool FenceEnabled => (bool?)Config["fence"]?["enabled"] == true;
    public bool ActiveTrader(MongoId id) => id == (MongoId)TraderId || FenceEnabled && id == (MongoId)FenceId || IsOpeningTrader(id.ToString());

    private void EnsureFenceProfile(MongoId session, SptProfile profile)
    {
        if (!FenceEnabled || profile.CharacterData?.PmcData?.TradersInfo is not { } infos) return;
        infos.TryAdd((MongoId)FenceId, new TraderInfo { Standing = 0, LoyaltyLevel = 1, SalesSum = 0, Unlocked = true, Disabled = false });
        var info = infos[(MongoId)FenceId];
        info.Unlocked = true; info.Disabled = false; info.NextResupply = RestockPolicy.RaidClock;
        profile.SptData ??= new Spt(); profile.SptData.Migrations ??= [];
        if (!profile.SptData.Migrations.ContainsKey(FenceMigration))
        {
            var quests = Read("data/quests.json").AsObject();
            _oldFenceQuestCredit ??= Config["fence"]!["secureQuestIds"]!.AsArray().ToDictionary(x => x!.ToString(), x =>
                quests[x!.ToString()]!["rewards"]!["Success"]!.AsArray().Where(r => r?["type"]?.ToString() == "TraderStanding" && r["target"]?.ToString() == FenceId).Sum(r => (double?)r!["value"] ?? 0));
            var completed = profile.CharacterData.PmcData.Quests?.Where(q => (int)q.Status == 4).Select(q => q.QId.ToString()).ToHashSet() ?? [];
            var credit = _oldFenceQuestCredit.Where(x => completed.Contains(x.Key)).Sum(x => x.Value);
            info.Standing = Math.Min(15, (info.Standing ?? 0) + credit);
            profile.SptData.Migrations[FenceMigration] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            saves.SaveProfileAsync(session).GetAwaiter().GetResult();
            if (credit > 0) Trace("fence.legacy_quest_credit", session, new { standingAdded = credit, itemRewardsRepeated = false });
        }
        traderHelper.LevelUp((MongoId)FenceId, profile.CharacterData.PmcData);
    }

    private void ConfigureFence(string questRifle)
    {
        if (!FenceEnabled) return;
        var balance = Config["fence"]!;
        var trader = db.GetTrader((MongoId)FenceId)!;
        trader.Base.UnlockedByDefault = true; trader.Base.NextResupply = RestockPolicy.RaidClock;
        trader.Base.Repair.Availability = false; trader.Base.Insurance.Availability = false;
        for (var i = 0; i < trader.Base.LoyaltyLevels.Count; i++)
        {
            trader.Base.LoyaltyLevels[i].BuyPriceCoefficient = (double)balance["buyPriceCoef"]!;
            trader.Base.LoyaltyLevels[i].MinLevel = (int)balance["loyaltyLevelRequirements"]![i]!;
        }
        var options = configs.GetConfig<TraderConfig>().Fence;
        EditInPlace(options, n => {
            n["assortSize"] = (int)balance["looseOffers"]!;
            n["weaponPresetMinMax"] = new JsonObject { ["min"] = 0, ["max"] = 1 };
            n["equipmentPresetMinMax"] = new JsonObject { ["min"] = 0, ["max"] = 1 };
            n["itemPriceMult"] = balance["itemPriceMult"]!.DeepClone();
            n["presetPriceMult"] = balance["presetPriceMult"]!.DeepClone();
            n["regenerateAssortsOnRefresh"] = false;
            n["partialRefreshChangePercent"] = 0;
            n["ammoMaxPenLimit"] = balance["ammoMaxPen"]!.DeepClone();
            var blacklist = n["blacklist"]!.AsArray();
            var listed = blacklist.Select(item => item!.ToString()).ToHashSet();
            if (listed.Add(questRifle)) blacklist.Add(questRifle);
            foreach (var tpl in _items.Keys.Where(IsPortableStorage).Order(StringComparer.Ordinal))
                if (listed.Add(tpl)) blacklist.Add(tpl);
            var discount = n["discountOptions"]!;
            discount["assortSize"] = (int)balance["discountLooseOffers"]!;
            discount["itemPriceMult"] = balance["discountItemPriceMult"]!.DeepClone();
            discount["presetPriceMult"] = balance["discountPresetPriceMult"]!.DeepClone();
            discount["weaponPresetMinMax"] = new JsonObject { ["min"] = 0, ["max"] = 1 };
            discount["equipmentPresetMinMax"] = new JsonObject { ["min"] = 0, ["max"] = 1 };
            n["weaponDurabilityPercentMinMax"] = new JsonObject { ["current"] = new JsonObject { ["min"] = 30, ["max"] = 55 }, ["max"] = new JsonObject { ["min"] = 60, ["max"] = 85 } };
            n["armorMaxDurabilityPercentMinMax"] = new JsonObject { ["current"] = new JsonObject { ["min"] = 40, ["max"] = 60 }, ["max"] = new JsonObject { ["min"] = 65, ["max"] = 90 } };
            var limits = n["itemTypeLimits"]!.AsObject();
            limits["5485a8684bdc2da71d8b4567"] = 4;
            limits["5448f39d4bdc2d0a728b4568"] = 1;
            limits["5448f3ac4bdc2dce718b4569"] = 2;
            limits["5448e8d04bdc2ddf718b4569"] = 2;
            limits["5448e8d64bdc2dce718b4568"] = 2;
            var stacks = n["itemStackSizeOverrideMinMax"]!.AsObject();
            foreach (var key in stacks.Select(x => x.Key).ToArray()) stacks[key] = new JsonObject { ["min"] = 1, ["max"] = 3 };
            stacks["5485a8684bdc2da71d8b4567"] = new JsonObject { ["min"] = 2, ["max"] = (int)balance["ammoStack"]! };
        });
        // SPT builds this source assort before late-loading economy mods run.
        // Regenerating the visible stock alone otherwise retains old ammunition/part prices.
        EditInPlace(trader.Assort, n => {
            var all=n["items"]!.AsArray();
            var children=all.Where(x=>x!["parentId"] is not null).GroupBy(x=>x!["parentId"]!.ToString()).ToDictionary(g=>g.Key,g=>g.ToArray());
            double Value(JsonNode item,bool root,HashSet<string> seen)
            {
                var id=item["_id"]!.ToString();if(!seen.Add(id))throw new InvalidDataException("Fence base attachment cycle");
                var value=handbookHelper.GetTemplatePrice((MongoId)item["_tpl"]!.ToString())*(root?1:(double?)item["upd"]?["StackObjectsCount"]??1);
                if(children.TryGetValue(id,out var list))foreach(var child in list)value+=Value(child!,false,seen);
                return value;
            }
            foreach(var item in all.Where(x=>x!["parentId"]?.ToString()=="hideout"))
            {
                var id=item!["_id"]!.ToString();
                if(n["barter_scheme"]?[id]?[0]?[0] is JsonObject scheme)
                    scheme["count"]=Math.Ceiling(Value(item,true,[])*(double)balance["itemPriceMult"]!);
            }
        });
    }

    public TraderAssort EnforceFenceLoyalty(PmcData profile,TraderAssort assort)
    {
        if(!FenceEnabled||profile.TradersInfo![(MongoId)FenceId].LoyaltyLevel>=2)return assort;
        return Edit(assort,n=>{
            var removed=n["loyal_level_items"]!.AsObject().Where(x=>(int)x.Value!>1).Select(x=>x.Key).ToHashSet();
            var list=n["items"]!.AsArray();
            bool changed;
            do{changed=false;foreach(var item in list)if(removed.Contains(item!["parentId"]?.ToString()??""))changed|=removed.Add(item["_id"]!.ToString());}while(changed);
            n["items"]=new JsonArray(list.Where(x=>!removed.Contains(x!["_id"]!.ToString())).Select(x=>x!.DeepClone()).ToArray());
            foreach(var id in removed){n["barter_scheme"]!.AsObject().Remove(id);n["loyal_level_items"]!.AsObject().Remove(id);}
        });
    }

    private bool FenceHasParent(string tpl, string parent)
    {
        var visited = new HashSet<string>();
        while (visited.Add(tpl) && _items.TryGetValue(tpl, out var item))
        { if (tpl == parent) return true; tpl = item["_parent"]?.ToString() ?? ""; }
        return false;
    }

    private bool IsPortableStorage(string tpl) => FenceHasParent(tpl, StorageContainerClass);

    // Fence still buys containers from the player, but cannot put them back on sale.
    public bool AllowFenceResaleStock(IEnumerable<Item> items, Item root) =>
        !FenceEnabled || !items.GetItemWithChildren(root.Id).Any(item => IsPortableStorage(item.Template.ToString()));

    private bool IsFenceExcludedContainer(string tpl) =>
        IsPortableStorage(tpl) ||
        handbookHelper.GetTemplatePrice((MongoId)tpl) <= 1 &&
        (FenceHasParent(tpl, "5448bf274bdc2dfc2f8b456a") ||
         FenceHasParent(tpl, "62f109593b54472778797866"));

    public void RebalanceFenceStock()
    {
        if (!FenceEnabled || _items.Count == 0) return;
        fenceService.SetFenceAssort(BalanceFenceAssort(fenceService.GetMainFenceAssort(), false));
        fenceService.SetFenceDiscountAssort(BalanceFenceAssort(fenceService.GetDiscountFenceAssort(), true));
        db.GetTrader((MongoId)FenceId)!.Base.NextResupply = RestockPolicy.RaidClock;
    }

    private TraderAssort BalanceFenceAssort(TraderAssort source, bool discount)
    {
        var balance = Config["fence"]!;
        return Edit(source, n => {
            var list = n["items"]!.AsArray();
            var byId = list.OfType<JsonObject>().ToDictionary(i => i["_id"]!.ToString());
            var removed = new HashSet<string>();
            var weaponBudget = Random.Shared.NextDouble() < (double)balance[discount ? "discountWeaponChance" : "weaponChance"]! ? 1 : 0;
            var armorBudget = Random.Shared.NextDouble() < (double)balance[discount ? "discountArmorChance" : "armorChance"]! ? 1 : 0;
            var forbidden = Read("data/quests.json").AsObject().Select(x=>x.Value).SelectMany(q => q!["rewards"]?["Success"]?.AsArray() ?? []).Where(r => r?["type"]?.ToString() == "Item").SelectMany(r => r!["items"]?.AsArray() ?? []).Select(i => i?["_tpl"]?.ToString()).Where(tpl => tpl is not null && (FenceHasParent(tpl, "5448bf274bdc2dfc2f8b456a") || FenceHasParent(tpl, "5795f317245977243854e041"))).ToHashSet();
            List<JsonObject> Tree(JsonObject root)
            {
                var result = new List<JsonObject> { root }; var ids = new HashSet<string> { root["_id"]!.ToString() };
                for (var index = 0; index < result.Count; index++) foreach (var child in list.OfType<JsonObject>().Where(i => i["parentId"]?.ToString() == result[index]["_id"]!.ToString())) if (ids.Add(child["_id"]!.ToString())) result.Add(child);
                return result;
            }
            double Number(JsonNode? node, double fallback = 0) => double.TryParse(node?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
            foreach (var root in list.OfType<JsonObject>().Where(i => i["parentId"]?.ToString() == "hideout").ToArray())
            {
                var tpl = root["_tpl"]!.ToString(); var tree = Tree(root); var category = Category(tpl); var props = _items[tpl]["_props"]!;
                var price = n["barter_scheme"]?[root["_id"]!.ToString()];
                var cashSale = price is JsonArray { Count: 1 } && price[0] is JsonArray { Count: 1 } payment &&
                    payment[0]?["_tpl"]?.ToString() == TraderSalePolicy.Roubles && (double?)payment[0]?["count"] > 0;
                var reject = !cashSale || forbidden.Contains(tpl) || FenceHasParent(tpl, "543be5dd4bdc2deb348b4569") || IsFenceExcludedContainer(tpl);
                if (category is "pistol" or "longGun") { if (weaponBudget == 0) reject = true; else weaponBudget--; }
                var armorTree=category is "armor" or "plate" || tree.Any(i=>Number(_items[i["_tpl"]!.ToString()]["_props"]?["armorClass"])>0);
                if (armorTree)
                {
                    if (armorBudget == 0 || tree.Any(i => Number(_items[i["_tpl"]!.ToString()]["_props"]?["armorClass"]) > (discount ? 5 : 4))) reject = true;
                    else armorBudget--;
                }
                if (category == "ammo" && (props["ammoType"] is null || Number(props["PenetrationPower"]) > (double)balance["ammoMaxPen"]!)) reject = true;
                if (reject) { foreach (var i in tree) removed.Add(i["_id"]!.ToString()); continue; }
                var cap = category is "medical" or "advancedMedical" or "drug" or "stim" ? (int)balance["medicalStack"]!
                    : category is "food" or "drink" ? (int)balance["consumableStack"]! : (int)balance["materialStack"]!;
                if (category is "pistol" or "longGun" || armorTree) cap = 1;
                if (category == "ammo")
                {
                    var calibre = props["Caliber"]?.ToString() ?? "";
                    cap = calibre.Contains("762x51") || calibre.Contains("762x54") || calibre.Contains("9x39") || calibre.Contains("86x70") || calibre.Contains("127x") ? (int)balance["largeAmmoStack"]! : (int)balance["ammoStack"]!;
                }
                var upd = root["upd"] as JsonObject ?? new JsonObject(); if (upd.Parent is null) root["upd"] = upd;
                upd["StackObjectsCount"] = Math.Max(0, Math.Min(Number(upd["StackObjectsCount"], 1), cap)); upd["UnlimitedCount"] = false;
                foreach (var child in tree.Skip(1).Where(i => Category(i["_tpl"]!.ToString()) == "ammo"))
                {
                    var childProps = _items[child["_tpl"]!.ToString()]["_props"]!;
                    if (childProps["ammoType"] is null || Number(childProps["PenetrationPower"]) > (double)balance["ammoMaxPen"]!) { removed.Add(child["_id"]!.ToString()); continue; }
                    if (child["upd"] is JsonObject childUpd) childUpd["StackObjectsCount"] = Math.Min(Number(childUpd["StackObjectsCount"], 1), 3);
                }
            }
            n["items"] = new JsonArray(list.Where(i => !removed.Contains(i!["_id"]!.ToString())).Select(i => i!.DeepClone()).ToArray());
            foreach (var id in removed) { n["barter_scheme"]?.AsObject().Remove(id); n["loyal_level_items"]?.AsObject().Remove(id); }
            n["nextResupply"] = RestockPolicy.RaidClock;
        });
    }
}
