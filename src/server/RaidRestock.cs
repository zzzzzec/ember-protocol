using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Match;

namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    private JsonObject _fullRaidAssort = new();
    public bool RaidRestockEnabled => Config["restockMode"]?.ToString() == "raid";

    private void InitializeRaidRestock()
    {
        if (!RaidRestockEnabled) return;
        _fullRaidAssort = Node(db.GetTrader((MongoId)TraderId)!.Assort).AsObject();
        db.GetTrader((MongoId)TraderId)!.Base.NextResupply = RestockPolicy.RaidClock;
    }

    public void RestockAfterRaid(MongoId session, EndLocalRaidRequestData request)
    {
        if (!RaidRestockEnabled || !saves.GetProfiles().TryGetValue(session, out var profile)) return;
        try
        {
            lock (_supplyActionGate)
            {
                var trader = db.GetTrader((MongoId)TraderId) ?? throw new InvalidOperationException("Quartermaster missing.");
                var full = Parse<TraderAssort>(_fullRaidAssort);
                if (!RestockPolicy.Apply(profile, trader, full, request.ServerId, request.Results?.Result)) return;
                if(FenceEnabled){profile.TraderPurchases?.Remove((MongoId)FenceId);fenceService.GenerateFenceAssorts();profile.CharacterData!.PmcData!.TradersInfo![(MongoId)FenceId].NextResupply=RestockPolicy.RaidClock;}
                RestockOpeningTrader(profile);
                // EndLocalRaid also saves synchronously through this native API before returning.
                saves.SaveProfileAsync(session).GetAwaiter().GetResult();
                Trace("trader.raid_restock", session, new
                {
                    outcome = request.Results?.Result?.ToString(),
                    assortItems = full.Items?.Count ?? 0,
                    personalQuotaReset = true,
                    timedRestock = false
                });
            }
        }
        catch (Exception e)
        {
            log.Error("[SPT-Z] Raid saved, but resupply failed: " + Redact(e.ToString()), null);
            Trace("trader.raid_restock_error", session, new { error = Redact(e.ToString()) });
        }
    }
}
