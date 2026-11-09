using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;

namespace Codex.ZombieSurvival;

public static class RestockPolicy
{
    // The native protocol requires a timestamp. The matching client shows a raid-based label.
    public const int RaidClock = int.MaxValue;
    private const string LedgerKey = "emberRaidRestock";

    public static bool Apply(SptProfile profile, Trader trader, TraderAssort replenished, string? raidId, ExitStatus? outcome)
    {
        if (string.IsNullOrWhiteSpace(raidId) || outcome is null || outcome == ExitStatus.TRANSIT
            || profile.CharacterData?.PmcData?.TradersInfo is not { } infos) return false;
        var traderId = trader.Base.Id;
        if (!infos.TryGetValue(traderId, out var info)) return false;
        var extension = profile.SptData?.ExtensionData;
        JsonObject ledger = new();
        if (extension?.TryGetValue(LedgerKey, out var raw) == true)
        {
            ledger = (raw switch
            {
                JsonNode node => node.DeepClone(),
                JsonElement element => JsonNode.Parse(element.GetRawText()),
                _ => JsonSerializer.SerializeToNode(raw)
            }) as JsonObject ?? new JsonObject();
        }
        var completed = ledger["completed"] as JsonArray ?? new JsonArray();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raidId)));
        if (completed.Any(x => x?.ToString() == key)) return false;

        // Reset only this profile's quota. Never use the native all-profile reset helper.
        profile.TraderPurchases?.Remove(traderId);
        trader.Assort = replenished;
        trader.Base.NextResupply = RaidClock;
        info.NextResupply = RaidClock;
        completed.Add(key);
        while (completed.Count > 64) completed.RemoveAt(0);
        if (completed.Parent is null) ledger["completed"] = completed;
        ledger["count"] = ((long?)ledger["count"] ?? 0) + 1;
        profile.SptData ??= new Spt();
        profile.SptData.ExtensionData ??= new();
        profile.SptData.ExtensionData[LedgerKey] = ledger;
        return true;
    }
}
