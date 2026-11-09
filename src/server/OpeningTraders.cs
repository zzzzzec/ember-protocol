using System.Text.Json;
using System.Text.Json.Nodes;
using Path = System.IO.Path;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Eft.Quests;
using SPTarkov.Server.Core.Models.Spt.Config;

namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    public const string NinaId = "09ce24e1c055c45828a1922f";
    public const string ClaireId = "39a130d714ed6d61f6b1c2aa";
    private const string NarrativeKey = "emberNarrative";
    private readonly object _openingGate = new();
    private JsonObject _opening = new();
    private Dictionary<MongoId, MongoId> _fenceOfferTransfers = [];
    private sealed record Contact(JsonObject Definition, HashSet<string> Offers, JsonObject Assort);
    private readonly Dictionary<string, Contact> _contacts = [];
    public bool NinaEnabled => IsOpeningTrader(NinaId);
    public bool IsOpeningTrader(string id) => _contacts.ContainsKey(id);
    public IEnumerable<string> OpeningTraderIds => _contacts.Keys;

    private static JsonObject SelectOpeningOffers(JsonObject source, HashSet<string> roots, bool keep)
    {
        var result = source.DeepClone().AsObject();
        var all = result["items"]!.AsArray();
        var selected = roots.ToHashSet();
        bool changed;
        do
        {
            changed = false;
            foreach (var item in all)
                if (selected.Contains(item!["parentId"]?.ToString() ?? "")) changed |= selected.Add(item["_id"]!.ToString());
        } while (changed);
        result["items"] = new JsonArray(all.Where(i => selected.Contains(i!["_id"]!.ToString()) == keep).Select(i => i!.DeepClone()).ToArray());
        foreach (var name in new[] { "barter_scheme", "loyal_level_items" })
            foreach (var id in result[name]!.AsObject().Select(p => p.Key).ToArray())
                if (roots.Contains(id) != keep) result[name]!.AsObject().Remove(id);
        return result;
    }

    private void RegisterOpeningTrader()
    {
        _opening = Read("data/opening.json").AsObject();
        if ((bool?)_opening["enabled"] != true) return;
        _fenceOfferTransfers = Read("data/manifest.json")["supplyRoutes"]!["fenceTransfers"]!.AsObject()
            .ToDictionary(transfer => (MongoId)transfer.Key, transfer => (MongoId)transfer.Value!.ToString());
        var source = Read("data/trader.json").AsObject();
        var original = source["assort"]!.AsObject();
        var transferred = new HashSet<string>();
        foreach (var definition in _opening["contacts"]!.AsArray().OfType<JsonObject>())
        {
            var id = definition["id"]!.ToString();
            var offers = definition["offers"]!.AsArray().Select(x => x!.ToString()).ToHashSet();
            if (offers.Any(oid => !transferred.Add(oid) || original["barter_scheme"]?[oid] is null))
                throw new InvalidDataException("Missing or duplicated contact offer: " + id);
            var full = SelectOpeningOffers(original, offers, true);
            full["nextResupply"] = RestockPolicy.RaidClock;
            var contact = source.DeepClone().AsObject(); contact["assort"] = full.DeepClone();
            var basis = contact["base"]!.AsObject();
            foreach (var key in new[] { "name", "surname", "nickname", "location", "avatar" }) basis[key] = definition[key]!.DeepClone();
            basis["_id"] = id; basis["unlockedByDefault"] = false; basis["medic"] = false;
            basis["nextResupply"] = RestockPolicy.RaidClock;
            for (var i = 0; i < basis["loyaltyLevels"]!.AsArray().Count; i++)
                basis["loyaltyLevels"]![i]!["minStanding"] = definition["standing"]![i]!.DeepClone();
            basis["repair"]!["availability"] = false; basis["insurance"]!["availability"] = false;
            if (definition["buyerTemplate"] is { } buyer)
                basis["items_buy"] = Node(db.GetTrader((MongoId)buyer.ToString())!.Base)["items_buy"]!.DeepClone();
            if (definition["buyCategories"] is { } categories)
                basis["items_buy"] = new JsonObject { ["category"] = categories.DeepClone(), ["id_list"] = new JsonArray() };
            foreach (var (_, links) in contact["questassort"]!.AsObject())
                foreach (var oid in links!.AsObject().Select(x => x.Key).ToArray())
                    if (!offers.Contains(oid)) links.AsObject().Remove(oid);
            if (!db.GetTraders().TryAdd((MongoId)id, Parse<Trader>(contact)) || !_contacts.TryAdd(id, new Contact(definition, offers, full)))
                throw new InvalidDataException("Narrative trader id collision: " + id);
            configs.GetConfig<TraderConfig>().UpdateTime.Add(new UpdateTime { TraderId = (MongoId)id, Seconds = new MinMax<int>((int)Config["restockSeconds"]!, (int)Config["restockSeconds"]!) });
            images.AddRoute(definition["avatar"]!.ToString().Replace(".png", ""), Path.Combine(Folder, "data", definition["avatarFile"]!.ToString()));
        }
        EditInPlace(db.GetTrader((MongoId)TraderId)!, n => {
            n["assort"] = SelectOpeningOffers(original, transferred, false);
            foreach (var (_, links) in n["questassort"]!.AsObject())
                foreach (var oid in links!.AsObject().Select(x => x.Key).Where(transferred.Contains).ToArray()) links.AsObject().Remove(oid);
        });
        images.AddRoute("/files/trader/avatar/ember_rin_v2", Path.Combine(Folder, "data/avatar-rin.png"));
    }

    private JsonObject NarrativeLedger(SptProfile profile)
    {
        profile.SptData ??= new Spt(); profile.SptData.ExtensionData ??= [];
        if (profile.SptData.ExtensionData.TryGetValue(NarrativeKey, out var raw))
        {
            var node = raw switch { JsonObject obj => obj, JsonElement e => JsonNode.Parse(e.GetRawText()), _ => JsonSerializer.SerializeToNode(raw) };
            if (node is JsonObject result) { profile.SptData.ExtensionData[NarrativeKey] = result; return result; }
        }
        var ledger = new JsonObject { ["schemaVersion"] = 1, ["campaign"] = "receipts", ["contacts"] = new JsonObject() };
        profile.SptData.ExtensionData[NarrativeKey] = ledger; return ledger;
    }

    public bool ContactAvailable(PmcData pmc, string id, MongoId session)
    {
        if (!_contacts.TryGetValue(id, out var contact) || pmc.Quests?.Any(q => q.QId.ToString() == contact.Definition["unlockQuest"]!.ToString() && (int)q.Status == 4) != true) return false;
        if (saves.GetProfiles().TryGetValue(session, out var profile))
        {
            var state = NarrativeLedger(profile)["contacts"]?[id]?["state"]?.ToString();
            if (state is "dead" or "departed" or "suspended") return false;
        }
        return true;
    }

    public bool TraderAvailable(PmcData pmc, string? id, MongoId session)
        => id == TraderId || FenceEnabled && id == FenceId || id is not null && ContactAvailable(pmc, id, session);

    public bool ContactAvailable(MongoId session, string id)
        => saves.GetProfiles().TryGetValue(session, out var profile) && profile.CharacterData?.PmcData is { } pmc && ContactAvailable(pmc, id, session);

    private static bool MovePurchase(SptProfile profile, MongoId from, MongoId to, MongoId offer)
    {
        if (profile.TraderPurchases is not { } purchases ||
            !purchases.TryGetValue(from, out var oldOffers) ||
            oldOffers is null || !oldOffers.Remove(offer, out var previous) || previous is null) return false;

        if (!purchases.TryGetValue(to, out var offers) || offers is null)
            purchases[to] = offers = [];
        if (!offers.TryGetValue(offer, out var current) || current is null || (current.PurchaseCount ?? 0) < (previous.PurchaseCount ?? 0))
            offers[offer] = previous;
        return true;
    }

    private void EnsureOpeningProfile(MongoId session, SptProfile profile)
    {
        if (profile.CharacterData?.PmcData is not { TradersInfo: { } infos } pmc) return;
        lock (_openingGate)
        {
            // The original workbench multitool offer moved from Emma back to Rin.
            var multitoolOffer = (MongoId)"aa638667b4f507e923348123";
            var emma = (MongoId)_opening["emmaId"]!.ToString();
            var rin = (MongoId)TraderId;
            var migrated = MovePurchase(profile, emma, rin, multitoolOffer);
            foreach (var (offer, trader) in _fenceOfferTransfers)
                migrated |= MovePurchase(profile, (MongoId)FenceId, trader, offer);
            if (migrated) saves.SaveProfileAsync(session).GetAwaiter().GetResult();

            foreach (var (id, contactData) in _contacts)
            {
                var added = infos.TryAdd((MongoId)id, new TraderInfo { Standing = 0, LoyaltyLevel = 1, SalesSum = 0, Unlocked = false, Disabled = false });
                if (added && profile.TraderPurchases?.TryGetValue((MongoId)TraderId, out var previous) == true && previous is not null)
                    profile.TraderPurchases.TryAdd((MongoId)id, previous.Where(p => contactData.Offers.Contains(p.Key.ToString())).ToDictionary(p => p.Key, p => Edit(p.Value, _ => { })));
                var ledger = NarrativeLedger(profile); ledger["contacts"] ??= new JsonObject();
                var contact = ledger["contacts"]![id] as JsonObject;
                var available = ContactAvailable(pmc, id, session);
                var changed = added || contact is null || available && contact["state"]?.ToString() != "open";
                if (contact is null) ledger["contacts"]![id] = contact = new JsonObject { ["state"] = available ? "open" : "unknown" };
                else if (available) contact["state"] = "open";
                var info = infos[(MongoId)id]; info.Unlocked = available;
                info.Disabled = contact["state"]?.ToString() is "dead" or "departed" or "suspended";
                info.NextResupply = RestockPolicy.RaidClock;
                db.GetTrader((MongoId)id)!.Base.NextResupply = RestockPolicy.RaidClock;
                if (added)
                {
                    var done = pmc.Quests?.Where(q => (int)q.Status == 4).Select(q => q.QId).ToHashSet() ?? [];
                    info.Standing = db.GetQuests().Where(q => done.Contains(q.Key)).SelectMany(q => Node(q.Value)["rewards"]?["Success"]?.AsArray() ?? []).Where(r => r?["type"]?.ToString() == "TraderStanding" && r["target"]?.ToString() == id).Sum(r => (double?)r!["value"] ?? 0);
                }
                traderHelper.LevelUp((MongoId)id, pmc);
                if (!changed) continue;
                ledger["openingVersion"] = 2;
                saves.SaveProfileAsync(session).GetAwaiter().GetResult();
                Trace("narrative.contact", session, new { trader = id, state = contact["state"]?.ToString(), existingProgressPreserved = true });
            }
        }
    }

    public bool AllowAuthoredQuestAccept(PmcData pmc, AcceptQuestRequestData request, MongoId session, ref ItemEventRouterResponse result)
    {
        if (!_authoredQuestIds.Contains(request.QuestId)) return true;
        var existing = pmc.Quests?.FirstOrDefault(q => q.QId == request.QuestId);
        // Native AcceptQuest resets any existing record and sends its start mail
        // again, including ordinary main tasks with a partial item handover.
        if (existing is not null && (int)existing.Status is 2 or 3 or 4)
        {
            result = outputs.GetOutput(session);
            Trace("quest.accept_replayed", session, new { quest = request.QuestId.ToString(), status = (int)existing.Status });
            return false;
        }
        if (existing is not null && (int)existing.Status >= 5) { result = FieldDenied(session, "这份委托已经结束了。"); return false; }
        if (_opening["questRelease"]?[request.QuestId.ToString()] is null) return true;
        var reason = StoryQuestPolicy.PrerequisiteError(Node(pmc).AsObject(), StoryDefinition(request.QuestId));
        if (reason is null) return true;
        result = FieldDenied(session, reason); return false;
    }

    private List<Quest> VisibleReleasedQuests(PmcData pmc, List<Quest> quests)
    {
        // Native list generation trusts cached AvailableForStart records from older releases.
        // Filter the response only; never cancel accepted tasks or rewrite saved status/counters.
        var statuses = pmc.Quests?.ToDictionary(q => q.QId.ToString(), q => (int)q.Status) ?? [];
        var releases = _opening["questRelease"]!.AsObject();
        return quests.Where(q => releases[q.Id.ToString()] is not { } parent
            || statuses.GetValueOrDefault(q.Id.ToString()) >= 2
            || statuses.GetValueOrDefault(parent.ToString()) == 4).ToList();
    }

    public TraderAssort EmptyOpeningAssort() => Parse<TraderAssort>(new JsonObject { ["items"] = new JsonArray(), ["barter_scheme"] = new JsonObject(), ["loyal_level_items"] = new JsonObject(), ["nextResupply"] = RestockPolicy.RaidClock });

    private void RestockOpeningTrader(SptProfile profile)
    {
        foreach (var (id, contact) in _contacts)
        {
            profile.TraderPurchases?.Remove((MongoId)id);
            var assort = Parse<TraderAssort>(contact.Assort);
            RemoveClosedMapKeyOffers(assort);
            db.GetTrader((MongoId)id)!.Assort = assort;
            db.GetTrader((MongoId)id)!.Base.NextResupply = RestockPolicy.RaidClock;
            if (profile.CharacterData?.PmcData?.TradersInfo?.TryGetValue((MongoId)id, out var info) == true) info.NextResupply = RestockPolicy.RaidClock;
        }
    }
}
