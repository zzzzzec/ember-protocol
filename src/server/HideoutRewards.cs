using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Spt.Config;

namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    private MongoId _ak50ReceiverRecipe;

    internal void StripReceiverPreset(HideoutProduction recipe, List<List<Item>> reward)
    {
        if (recipe.Id != _ak50ReceiverRecipe) return;
        // SPT expands every weapon product to its default preset. This recipe makes just the receiver.
        foreach (var bundle in reward)
            bundle.RemoveAll(part => part.Template != recipe.EndProduct);
    }

    private static readonly MongoId[] ScavCaseFallbackMaterials =
    [
        "57347cd0245977445a2d6ff1", // T-plug
        "590c31c586f774245e3141b2"  // Pack of nails
    ];

    private void ConfigureHideoutRewardPools()
    {
        var circle = configs.GetConfig<HideoutConfig>().CultistCircle;
        var excluded = circle.RewardItemBlacklist.ToHashSet();
        var before = excluded.Count;

        bool HighArmor(string tpl) => _items.TryGetValue(tpl, out var item)
            && int.TryParse(item["_props"]?["armorClass"]?.ToString(), out var armorClass)
            && armorClass >= 5;

        foreach (var (tpl, item) in _items)
        {
            var properties = item["_props"];
            var ammo = AmmoPackPolicy.Describe(_items, tpl)?.Ammunition;
            if (ammo is not null) properties = _items[ammo]["_props"];
            var calibre = properties?["Caliber"]?.ToString();
            var restrictedAmmo = properties?["ammoType"] is not null &&
                ((double?)properties["PenetrationPower"] >= 35 ||
                    calibre is "Caliber762x51" or "Caliber762x54R" or "Caliber68x51" or "Caliber86x70" or "Caliber9x39" ||
                    calibre?.StartsWith("Caliber127x", StringComparison.Ordinal) == true);
            var labCard = item["_parent"]?.ToString() == "5c164d2286f774194c5e69fa"
                && (item["_name"]?.ToString() is { } name &&
                    (name.StartsWith("item_keycard_lab_", StringComparison.Ordinal) || name == "item_lab_sec_room"));
            if (restrictedAmmo || labCard || HighArmor(tpl)) excluded.Add((MongoId)tpl);
        }

        // An empty carrier can be delivered as a default preset with high-grade inserts.
        foreach (var (_, preset) in Node(db.GetGlobals())["ItemPresets"]!.AsObject())
            if (preset?["_encyclopedia"]?.ToString() is { Length: 24 } root &&
                preset["_items"] is JsonArray parts && parts.Any(part => part?["_tpl"]?.ToString() is { } tpl && HighArmor(tpl)))
                excluded.Add((MongoId)root);

        // Native direct sacrifices have their own one-off recipes and do not use this pool.
        circle.RewardItemBlacklist = excluded.OrderBy(id => id.ToString(), StringComparer.Ordinal).ToList();
        log.Success($"[SPT-Z] Hideout rewards: {excluded.Count - before} additional cultist pool exclusions; fixed sacrifices preserved.", null);
    }

    public List<List<Item>> FilterScavCaseRewards(IEnumerable<List<Item>> rewards)
    {
        var filtered = FilterRewards(rewards);
        if (filtered.Count > 0) return filtered;

        // Even at the cheapest tier, neither material resells for the entry fee.
        var material = ScavCaseFallbackMaterials[Random.Shared.Next(ScavCaseFallbackMaterials.Length)];
        CaptureDiagnostic(() => _journal?.Record("hideout.scav_case_material_fallback", new { template = material.ToString() }));
        return [[new Item { Id = new MongoId(), Template = material, Upd = new Upd { StackObjectsCount = 1, SpawnedInSession = true } }]];
    }
}
