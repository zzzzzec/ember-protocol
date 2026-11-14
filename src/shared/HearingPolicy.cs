using System;
namespace Codex.ZombieSurvival.Rules;

public sealed class HearingSettings
{
    public float LoudMin = 25, LoudMax = 80, QuietMax = 20, ExplosionRange = 120, FootstepRange = 8;
    public float GroundZeroScale = .75f, InfectedShotScale = .45f, WallScale = .35f, FloorScale = .55f, CallRange = 30;
}

public static class HearingPolicy
{
    public static bool IsGroundZero(string? map) => string.Equals(map, "sandbox", StringComparison.OrdinalIgnoreCase) || string.Equals(map, "sandbox_high", StringComparison.OrdinalIgnoreCase);
    private static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
    public static float Radius(HearingSettings settings, string kind, float power, bool groundZero, bool infectedSource)
    {
        if (float.IsNaN(power) || float.IsInfinity(power) || power <= 0) return 0;
        float radius;
        switch (kind)
        {
            case "gun": radius = Clamp(power * .30f, settings.LoudMin, settings.LoudMax); break;
            case "silencedGun": radius = Clamp(power * .45f, 4, settings.QuietMax); break;
            case "step": radius = Clamp(power, 1, settings.FootstepRange); break;
            case "explosion": radius = settings.ExplosionRange; break;
            default: return 0;
        }
        if (groundZero) radius *= settings.GroundZeroScale;
        if (infectedSource && (kind == "gun" || kind == "silencedGun")) radius *= settings.InfectedShotScale;
        return radius;
    }
    public static float EffectiveRadius(HearingSettings settings, float radius, bool blocked, float heightDifference, float minimumWallScale = 0)
    {
        if (blocked) radius *= Math.Max(minimumWallScale, settings.WallScale);
        if (Math.Abs(heightDifference) > 3) radius *= settings.FloorScale;
        return radius;
    }
    public static (float Radius, float MinimumWallScale) CustomsGunfire(float radius, int shots)
    {
        var extra = Math.Clamp(shots - 1, 0, 2);
        return (radius + 12.5f * extra, .45f + .04f * extra);
    }

    public static float CustomsWallResponse(float baseline, int pressure, float heightDifference)
    {
        if (Math.Abs(heightDifference) > 3) return baseline;
        return pressure switch { 2 => Math.Max(.56f, baseline), >= 3 => Math.Max(.70f, baseline), _ => baseline };
    }

    public static bool ContinueCustomsBurst(float now, float expires, float displacementSquared)
        => now < expires && displacementSquared <= 28 * 28;

    public static int ApproachSector(float offsetX, float offsetZ)
    {
        var angle = Math.Atan2(offsetZ, offsetX) + Math.PI / 8;
        return (int)Math.Floor(angle / (Math.PI / 4) + 8) % 8;
    }

    public static float CustomsResponseDelay(float distance, float firstShotRadius, int pressure, int sector, float variation)
    {
        // A single shot recruits only the inner group. Later shots open the
        // surrounding approaches in stages, with one small offset per bearing.
        var inner = Math.Min(32, firstShotRadius * .45f);
        if (distance <= inner) return 0;
        if (pressure < 2 || (pressure == 2 && distance > firstShotRadius * .65f)) return -1;
        var outer = distance > firstShotRadius * .65f;
        return (outer ? 3.8f : 1.1f) + sector * .28f + variation * .8f;
    }

    public static float MemorySeconds(string kind) => kind == "gun" ? 18 : kind == "silencedGun" ? 8 : kind == "step" ? 5 : 24;
}
