using System;
namespace Codex.ZombieSurvival.Rules;
public static class AwarenessPolicy
{
    public static bool SightConfirmed(bool nativeVisible, bool blocked, float distance, float facingDot)
    {
        if (float.IsNaN(distance) || float.IsInfinity(distance) || distance < 0 || blocked) return false;
        if (distance <= 2.6f) return true;
        return nativeVisible && facingDot >= .25f;
    }
    public static int CuePriority(string reason) => reason switch
    {
        "sighting" or "contact" => 4,
        "gun" or "explosion" => 3,
        "silencedGun" or "local_sighting" or "native_call" => 2,
        _ => 1
    };

    public static bool InformationAlive(float now, float until) => until > now && until > 0;
    public static float LocalDensity(int count, float radius) => radius > 0 ? count * 1000f / ((float)Math.PI * radius * radius) : 0;
}