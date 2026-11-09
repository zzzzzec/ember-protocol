using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Dialog;

namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    private readonly HashSet<string> _blockedCatalogGifts = new(StringComparer.Ordinal);
    private readonly HashSet<(string Trader, string Template)> _vanillaGiftMails = [];
    private readonly ConcurrentDictionary<string, byte> _giftBlockReports = new();
    private readonly object _giftMailGate = new();

    private void InitializeVanillaGiftPolicy()
    {
        foreach (var (id, gift) in (bool?)Config["disableVanillaTraderGifts"] == true
            ? configs.GetConfig<GiftsConfig>().Gifts : [])
        {
            // Catalog gifts are separate from native quest, loan, insurance and admin mail.
            if (gift.Sender != GiftSenderType.Trader || !gift.Trader.HasValue) continue;
            var sender = gift.Trader.Value.ToString();
            if (sender == TraderId || IsOpeningTrader(sender)) continue;
            _blockedCatalogGifts.Add(id);
            if (!string.IsNullOrEmpty(gift.LocaleTextId)) _vanillaGiftMails.Add((sender, gift.LocaleTextId));
        }
        if ((bool?)Config["disableVanillaCoopRewards"] == true)
        {
            var coop = configs.GetConfig<TraderConfig>().Fence.CoopExtractGift;
            foreach (var template in coop.MessageLocaleIds) _vanillaGiftMails.Add((FenceId, template));
            // Native EndLocalRaid gates both the gift and its reputation gain on this flag.
            coop.SendGift = false;
        }
    }

    public bool AllowVanillaCoopBenefit(MongoId session)
    {
        if ((bool?)Config["disableVanillaCoopRewards"] != true) return true;
        if (_giftBlockReports.TryAdd(session + ":coop-extract", 0))
            Trace("economy.vanilla_coop_reward_blocked", session, new { gift = true, standing = true });
        return false;
    }

    public bool AllowCatalogGift(MongoId session, string giftId, ref GiftSentResult result)
    {
        if (!_blockedCatalogGifts.Contains(giftId)) return true;
        result = GiftSentResult.FAILED_GIFT_DOESNT_EXIST;
        if (_giftBlockReports.TryAdd(session + ":" + giftId, 0))
            Trace("economy.vanilla_gift_blocked", session, new { giftId, source = "catalog" });
        return false;
    }

    private bool IsVanillaGiftMail(string? sender, string? template, MessageType? type)
        => sender is not null && template is not null && type == MessageType.MessageWithItems
            && _vanillaGiftMails.Contains((sender, template));

    public bool AllowGiftMail(SendMessageDetails message)
    {
        if (!IsVanillaGiftMail(message.Trader, message.TemplateId, message.Sender)) return true;
        Trace("economy.vanilla_gift_blocked", message.RecipientId, new { source = "mail", trader = message.Trader, template = message.TemplateId });
        return false;
    }

    public void QuarantinePendingVanillaGifts(MongoId session)
    {
        if (_vanillaGiftMails.Count == 0 || !saves.GetProfiles().TryGetValue(session, out var profile)
            || profile.DialogueRecords is null) return;
        lock (_giftMailGate)
        {
            JsonArray? quarantine = null;
            var removed = 0;
            foreach (var (sender, dialogue) in profile.DialogueRecords)
            {
                var inDialogue = 0;
                foreach (var message in dialogue.Messages ?? [])
                {
                    if (!IsVanillaGiftMail(sender.ToString(), message.TemplateId, message.MessageType)
                        || message.RewardCollected == true || message.Items is null) continue;
                    var originalItems = Node(message.Items);
                    if ((originalItems["data"] as JsonArray)?.Count is not > 0) continue;
                    if (quarantine is null)
                    {
                        profile.SptData ??= new Spt();
                        profile.SptData.ExtensionData ??= [];
                        quarantine = profile.SptData.ExtensionData.TryGetValue("emberBlockedGifts", out var old)
                            ? JsonSerializer.SerializeToNode(old)!.AsArray() : new JsonArray();
                        profile.SptData.ExtensionData["emberBlockedGifts"] = quarantine;
                    }
                    quarantine.Add(new JsonObject
                    {
                        ["schemaVersion"] = 1, ["messageId"] = message.Id.ToString(),
                        ["trader"] = sender.ToString(), ["templateId"] = message.TemplateId,
                        ["utc"] = DateTimeOffset.UtcNow.ToString("O"), ["items"] = originalItems,
                        ["reason"] = "vanilla-trader-gift-disabled"
                    });
                    // Keep the conversation and audit payload. Never touch stash items or collected gifts.
                    message.Items = null; message.HasRewards = false; message.MaxStorageTime = 0;
                    removed++; inDialogue++;
                }
                if (inDialogue > 0) dialogue.AttachmentsNew = Math.Max(0, (dialogue.AttachmentsNew ?? 0) - inDialogue);
            }
            if (removed == 0) return;
            saves.SaveProfileAsync(session).GetAwaiter().GetResult();
            Trace("economy.vanilla_gift_quarantined", session, new { messages = removed, stashItemsChanged = false });
        }
    }
}
