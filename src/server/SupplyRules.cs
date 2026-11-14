using System.Text.Json.Nodes;
using HarmonyLib;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Eft.Trade;
using SPTarkov.Server.Core.Models.Enums;

namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    private sealed record SupplyRecipe(int Area, int Level, Dictionary<MongoId, int> Inputs, Dictionary<MongoId, int> Tools);
    private sealed record SupplyOffer(MongoId Trader, int Level, int Limit, Dictionary<MongoId, int> Price);

    private readonly object _supplyActionGate = new();
    private readonly Dictionary<MongoId, SupplyRecipe> _supplyRecipes = [];
    private readonly Dictionary<MongoId, SupplyOffer> _supplyOffers = [];
    private readonly Dictionary<MongoId, MongoId> _questPurchaseUnlocks = [];

    private void ConfigureSupplyRoutes()
    {
        var manifest = Read("data/manifest.json");
        var owned = manifest["supplyRoutes"]!;
        var recipeIds = owned["recipes"]!.AsArray().Select(id => id!.ToString()).ToHashSet();
        var offerIds = owned["offers"]!.AsArray().Select(id => id!.ToString()).ToHashSet();
        foreach (var recipe in Read("data/recipes.json").AsArray().OfType<JsonObject>())
        {
            var id = recipe["_id"]!.ToString();
            if (!recipeIds.Contains(id)) continue;
            var requirements = recipe["requirements"]!.AsArray().OfType<JsonObject>().ToArray();
            Dictionary<MongoId, int> SelectRequirements(string type) => requirements.Where(row => row["type"]!.ToString() == type)
                .ToDictionary(row => (MongoId)row["templateId"]!.ToString(), row => (int?)row["count"] ?? 1);
            var facility = requirements.Single(row => row["type"]!.ToString() == "Area");
            _supplyRecipes.Add((MongoId)id, new SupplyRecipe((int)recipe["areaType"]!, (int)facility["requiredLevel"]!,
                SelectRequirements("Item"), SelectRequirements("Tool")));
        }

        var owners = Read("data/opening.json")["contacts"]!.AsArray().OfType<JsonObject>()
            .SelectMany(contact => contact["offers"]!.AsArray().Select(id => (Id: id!.ToString(), Trader: contact["id"]!.ToString())))
            .ToDictionary(row => row.Id, row => row.Trader);
        foreach (var offer in manifest["offers"]!.AsArray().OfType<JsonObject>())
        {
            var id = offer["id"]!.ToString();
            if (offer["unlock"] is { } quest)
                _questPurchaseUnlocks.Add((MongoId)id, (MongoId)quest.ToString());
            if (offerIds.Contains(id) || (bool?)offer["validatePayment"] == true)
                RegisterSupplyOffer(offer, owners.GetValueOrDefault(id, TraderId));
        }
    }

    private void RegisterSupplyOffer(JsonObject offer, string trader)
    {
        var price = offer["barter"] is JsonArray barter
            ? barter.OfType<JsonObject>().ToDictionary(ingredient => (MongoId)ingredient["_tpl"]!.ToString(), ingredient => (int)ingredient["count"]!)
            : new Dictionary<MongoId, int> { [(MongoId)TraderSalePolicy.Roubles] = (int)offer["price"]! };
        _supplyOffers.Add((MongoId)offer["id"]!.ToString(),
            new SupplyOffer((MongoId)trader, (int)offer["level"]!, (int)offer["limit"]!, price));
    }

    private static bool MatchesSupplyInputs(PmcData profile, IEnumerable<IdWithCount>? selections, Dictionary<MongoId, int> expected,
        HashSet<MongoId> seen)
    {
        var remaining = new Dictionary<MongoId, int>(expected);
        foreach (var selection in selections ?? [])
        {
            var item = profile.Inventory?.Items?.FirstOrDefault(row => row.Id == selection.Id);
            if (item is null || !seen.Add(selection.Id) || !remaining.TryGetValue(item.Template, out var required) ||
                selection.Count is not double count || count < 1 || count > required || count != Math.Truncate(count) ||
                count > (item.Upd?.StackObjectsCount ?? 1)) return false;
            remaining[item.Template] -= (int)count;
        }
        return remaining.Values.All(count => count == 0);
    }

    public bool CheckSupplyPurchase(PmcData profile, ProcessBaseTradeRequestData request, MongoId session,
        ref ItemEventRouterResponse result, ref bool entered)
    {
        if (request is not ProcessBuyTradeRequestData buy) return true;
        if (_questPurchaseUnlocks.TryGetValue(buy.ItemId, out var quest) &&
            profile.Quests?.Any(q => q.QId == quest && q.Status == QuestStatusEnum.Success) != true)
        {
            result = FieldDenied(session, "请先完成解锁这件商品的任务。");
            Trace("supply.purchase_rejected", session, new { offer = buy.ItemId.ToString(), reason = "quest" });
            return false;
        }
        if (!_supplyOffers.TryGetValue(buy.ItemId, out var offer)) return true;
        Monitor.Enter(_supplyActionGate, ref entered);
        var level = profile.TradersInfo?.GetValueOrDefault(offer.Trader)?.LoyaltyLevel ?? 0;
        if (buy.TransactionId != offer.Trader || buy.Count is not int count || count < 1 || count > offer.Limit ||
            level < offer.Level ||
            SupplyPurchaseCount(session, offer.Trader, buy.ItemId) + count > offer.Limit)
        {
            result = FieldDenied(session, "这份补给尚未开放，或本局补给已经换完。");
            Trace("supply.purchase_rejected", session, new { offer = buy.ItemId.ToString(), reason = "unavailable" });
            return false;
        }
        var price = count == 1 ? offer.Price : offer.Price.ToDictionary(entry => entry.Key, entry => entry.Value * count);
        if (buy.SchemeId == 0 && MatchesSupplyInputs(profile, buy.SchemeItems, price, [])) return true;
        result = FieldDenied(session, "这份补给所需的卢布或交换物品不齐。请重新选择后再交易。");
        Trace("supply.purchase_rejected", session, new { offer = buy.ItemId.ToString(), reason = "payment" });
        return false;
    }

    public bool CheckSupplyCraft(PmcData profile, HideoutSingleProductionStartRequestData request, MongoId session,
        ref ItemEventRouterResponse result, ref bool entered)
    {
        if (!_supplyRecipes.TryGetValue(request.RecipeId, out var recipe)) return true;
        Monitor.Enter(_supplyActionGate, ref entered);
        var level = profile.Hideout?.Areas?.FirstOrDefault(area => (int)area.Type! == recipe.Area)?.Level ?? 0;
        var existing = profile.Hideout?.Production?.GetValueOrDefault(request.RecipeId);
        var used = new HashSet<MongoId>();
        if (level >= recipe.Level && (existing is null || existing.SptIsComplete == true) && request.Items is not null &&
            request.Tools is not null && MatchesSupplyInputs(profile, request.Items, recipe.Inputs, used) &&
            MatchesSupplyInputs(profile, request.Tools, recipe.Tools, used))
        {
            UpdateSupplyProduction(session);
            return true;
        }
        result = FieldDenied(session, "设施等级、材料或工具不足，或上一批同样的物资还没领取。");
        Trace("supply.craft_start_rejected", session, new { recipe = request.RecipeId.ToString() });
        return false;
    }

    private void UpdateSupplyProduction(MongoId session)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<HideoutHelper>().UpdatePlayerHideout(session);
    }

    public bool CheckSupplyClaim(PmcData profile, HideoutTakeProductionRequestData request, MongoId session,
        ref ItemEventRouterResponse result, ref bool entered)
    {
        if (!_supplyRecipes.ContainsKey(request.RecipeId)) return true;
        Monitor.Enter(_supplyActionGate, ref entered);
        if (profile.Hideout?.Production?.GetValueOrDefault(request.RecipeId) is { SptIsComplete: not true } production)
        {
            UpdateSupplyProduction(session);
            if (production.Progress is double elapsed && production.ProductionTime is double total && total > 0 && elapsed >= total)
                return true;
        }
        result = FieldDenied(session, "这批物资还没做好，或已经领取了。");
        Trace("supply.craft_claim_rejected", session, new { recipe = request.RecipeId.ToString() });
        return false;
    }

    public void ReleaseSupplyAction() => Monitor.Exit(_supplyActionGate);

    private double SupplyPurchaseCount(MongoId session, MongoId trader, MongoId offer)
        => saves.GetProfiles().GetValueOrDefault(session)?.TraderPurchases?.GetValueOrDefault(trader)?
            .GetValueOrDefault(offer)?.PurchaseCount ?? 0;

    public void RecordSupplyPurchase(MongoId session, ProcessBaseTradeRequestData request, ItemEventRouterResponse result)
    {
        if (result.Warnings is { Count: > 0 } || request is not ProcessBuyTradeRequestData buy ||
            !_supplyOffers.TryGetValue(buy.ItemId, out var offer)) return;
        Trace("supply.purchased", session, new { offer = buy.ItemId.ToString(), trader = offer.Trader.ToString() });
    }

    public void RecordSupplyClaim(PmcData profile, MongoId recipe, MongoId session, ItemEventRouterResponse result)
    {
        if (result.Warnings is not { Count: > 0 } && profile.Hideout?.Production?.GetValueOrDefault(recipe)?.SptIsComplete == true)
            Trace("supply.craft_claimed", session, new { recipe = recipe.ToString() });
    }

    private void InstallSupplyPatches(Harmony harmony)
    {
        var release = new HarmonyMethod(typeof(Patches), nameof(Patches.ReleaseSupply));
        harmony.Patch(AccessTools.Method(typeof(TradeController), nameof(TradeController.ConfirmTrading)),
            prefix: new HarmonyMethod(typeof(Patches), nameof(Patches.Trade)),
            postfix: new HarmonyMethod(typeof(Patches), nameof(Patches.SupplyPurchase)), finalizer: release);
        harmony.Patch(AccessTools.Method(typeof(HideoutController), nameof(HideoutController.SingleProductionStart)),
            prefix: new HarmonyMethod(typeof(Patches), nameof(Patches.StartSupplyCraft)), finalizer: release);
        harmony.Patch(AccessTools.Method(typeof(HideoutController), nameof(HideoutController.TakeProduction)),
            prefix: new HarmonyMethod(typeof(Patches), nameof(Patches.ClaimSupplyCraft)),
            postfix: new HarmonyMethod(typeof(Patches), nameof(Patches.SupplyCraftClaimed)), finalizer: release);
    }
}
