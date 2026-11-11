using System;
using System.Collections.Generic;
using System.Linq;
using Codex.ZombieSurvival.Rules;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;

namespace Codex.ZombieSurvival.Client;

internal sealed class CombatSkillPractice(GameWorld world, Player player)
{
    private const float ReloadPointsPerRound = 0.60f;
    private const float ArmorPointsPerKill = 2.50f;
    private static readonly SkillManager.SkillActionClass PracticeAction = new();
    private static CombatSkillPractice? _raid;
    private readonly ReloadPractice _reloads = new();
    private readonly List<ArmorComponent> _armor = new();
    private readonly HashSet<string> _creditedInfected = new(StringComparer.Ordinal);
    private GameWorld World { get; } = world;
    private Player Owner { get; } = player;
    private int _reloadedRounds;
    private int _lightArmorKills;
    private int _heavyArmorKills;
    private bool _errorReported;

    internal static void Install(Harmony harmony)
    {
        var shot = AccessTools.DeclaredMethod(typeof(Player), nameof(Player.OnMakingShot));
        harmony.Patch(shot, postfix: new HarmonyMethod(typeof(CombatSkillPractice), nameof(RecordShot)));

        var reload = new HarmonyMethod(typeof(CombatSkillPractice), nameof(TrackReload));
        foreach (var method in new[] { nameof(Player.FirearmController.ReloadMag), nameof(Player.FirearmController.QuickReloadMag),
            nameof(Player.FirearmController.ReloadWithAmmo), nameof(Player.FirearmController.ReloadCylinderMagazine),
            nameof(Player.FirearmController.ReloadBarrels) })
        {
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Player.FirearmController), method), prefix: reload);
            // The client quick-reload override delegates to the patched base implementation.
            if (method != nameof(Player.FirearmController.QuickReloadMag))
                harmony.Patch(AccessTools.DeclaredMethod(typeof(ClientFirearmController), method), prefix: reload);
        }
    }

    internal static void Begin(GameWorld world)
    {
        if (_raid is { } previous) End(previous.World);
        if (world.MainPlayer is not { IsYourPlayer: true, IsAI: false } player
            || player.Profile.Side is not (EPlayerSide.Usec or EPlayerSide.Bear)
            || !Singleton<AbstractGame>.Instantiated
            || Singleton<AbstractGame>.Instance is not BaseLocalGame<EftGamePlayerOwner>)
            return;

        _raid = new CombatSkillPractice(world, player);
        Player.OnPlayerDeadStatic += OnDeath;
    }

    internal static void End(GameWorld world)
    {
        if (_raid is not { } raid || !ReferenceEquals(raid.World, world)) return;
        _raid = null;
        Player.OnPlayerDeadStatic -= OnDeath;
        ClientDiagnostics.Record("skills.combat_practice", new
        {
            reloadedRounds = raid._reloadedRounds,
            lightArmorKills = raid._lightArmorKills,
            heavyArmorKills = raid._heavyArmorKills
        });
    }

    private static void RecordShot(Player __instance, IWeapon __0)
    {
        if (_raid is { } raid && ReferenceEquals(raid.Owner, __instance) && __0 is Weapon weapon)
            raid._reloads.Fired(weapon.Id);
    }

    private static void TrackReload(Player.FirearmController __instance, ref Callback? callback)
    {
        if (_raid is not { } raid || !ReferenceEquals(raid.Owner.HandsController, __instance)
            || __instance.Item is not { } weapon || !raid._reloads.HasRounds(weapon.Id))
            return;

        var previousMagazine = weapon.GetCurrentMagazine();
        var previousRounds = weapon.ChamberAmmoCount + (previousMagazine?.Count ?? 0);
        var next = callback;
        callback = result =>
        {
            try
            {
                if (ReferenceEquals(_raid, raid) && result.Succeed && raid.Owner.HealthController.IsAlive)
                {
                    var magazine = weapon.GetCurrentMagazine();
                    var loaded = weapon.ChamberAmmoCount + (magazine?.Count ?? 0) > previousRounds
                        || magazine is { Count: > 0 } && magazine.Id != previousMagazine?.Id;
                    var rounds = raid._reloads.Finish(weapon.Id, succeeded: true, loaded);
                    if (rounds > 0)
                    {
                        raid.Owner.Skills.MagDrills.OnTrigger(PracticeAction, rounds * ReloadPointsPerRound);
                        raid._reloadedRounds += rounds;
                    }
                }
            }
            catch (Exception error) { raid.ReportError(error); }
            finally { next?.Invoke(result); }
        };
    }

    private static void OnDeath(Player victim, IPlayer? aggressor, DamageInfoStruct _, EBodyPart __)
    {
        if (_raid is not { } raid || aggressor?.ProfileId != raid.Owner.ProfileId || !Plugin.Infected(victim)
            || !raid._creditedInfected.Add(victim.ProfileId))
            return;

        try
        {
            raid._armor.Clear();
            raid.Owner.Inventory.GetPutOnArmorsNonAlloc(raid._armor);
            ArmorComponent? strongest = null;
            foreach (var armor in raid._armor)
            {
                if (armor.IsDestroyed || armor.ArmorClass <= 0 || armor.ArmorType is not (EArmorType.Light or EArmorType.Heavy)
                    || !ProtectsTorso(armor))
                    continue;
                if (strongest is null || armor.ArmorClass > strongest.ArmorClass
                    || armor.ArmorClass == strongest.ArmorClass && armor.ArmorType == EArmorType.Heavy)
                    strongest = armor;
            }

            var skills = raid.Owner.Skills;
            if (strongest?.ArmorType == EArmorType.Light)
            {
                skills.LightVests.OnTrigger(PracticeAction, ArmorPointsPerKill);
                raid._lightArmorKills++;
            }
            else if (strongest?.ArmorType == EArmorType.Heavy)
            {
                skills.HeavyVests.OnTrigger(PracticeAction, ArmorPointsPerKill);
                raid._heavyArmorKills++;
            }
        }
        catch (Exception error) { raid.ReportError(error); }
    }

    private static bool ProtectsTorso(ArmorComponent armor)
        => armor.ArmorPlateCollidersMask != 0 || armor.ArmorColliders.Any(zone => zone is
            EBodyPartColliderType.RibcageUp or EBodyPartColliderType.RibcageLow or EBodyPartColliderType.SpineTop
            or EBodyPartColliderType.SpineDown or EBodyPartColliderType.LeftSideChestUp or EBodyPartColliderType.RightSideChestUp
            or EBodyPartColliderType.LeftSideChestDown or EBodyPartColliderType.RightSideChestDown);

    private void ReportError(Exception error)
    {
        if (_errorReported) return;
        _errorReported = true;
        ClientDiagnostics.Record("skills.combat_practice_error", new { error = error.ToString() });
    }
}
