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

    public static float MemorySeconds(string kind) => kind == "gun" ? 18 : kind == "silencedGun" ? 8 : kind == "step" ? 5 : 24;
}
