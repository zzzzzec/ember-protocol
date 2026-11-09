using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Profile;
namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    private const string HealthMigration = "codex.ember.legacy-health-440";
    private void NormalizeLegacyHealth(MongoId session, SptProfile profile)
    {
        if ((bool?)Config["normalizeLegacyHealth"] != true || profile.SptData?.Migrations?.ContainsKey(HealthMigration) == true
            || profile.CharacterData?.PmcData?.Health is not { } health) return;
        var old = Node(health).AsObject();
        var normalized = old.DeepClone().AsObject();
        if (!LegacyHealthPolicy.Normalize(normalized)) return;
        var pmc = profile.CharacterData.PmcData;
        profile.SptData ??= new Spt();
        profile.SptData.Migrations ??= [];
        pmc.Health = ParseSame(normalized, health);
        profile.SptData.Migrations[HealthMigration] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            saves.SaveProfileAsync(session).GetAwaiter().GetResult();
            Trace("profile.legacy_health_normalized", session, new { previousMaximum = 950, maximum = 440, currentHealthProportionPreserved = true, injuriesPreserved = true });
        }
        catch (Exception e)
        {
            pmc.Health = health;
            profile.SptData.Migrations.Remove(HealthMigration);
            log.Error("[SPT-Z] Legacy health migration deferred: " + Redact(e.ToString()), null);
        }
    }
}
