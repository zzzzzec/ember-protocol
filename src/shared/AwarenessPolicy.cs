using System;
namespace Codex.ZombieSurvival.Rules;
public static class AwarenessPolicy
{
    public const float ContactDistance = 2.6f;

    public static bool InSightCone(float distance, float facingDot, float maxDistance, float angle)
    {
        if (!float.IsFinite(distance) || distance < 0 || distance > maxDistance) return false;
        return distance <= ContactDistance || facingDot >= Math.Cos(angle * Math.PI / 360);
    }
    public static bool ChaseDestinationMoved(float distanceSquared) => distanceSquared >= 2.25f;

    public static int CuePriority(string reason) => reason switch
    {
        "sighting" or "contact" => 4,
        "gun" or "explosion" => 3,
        "silencedGun" or "local_sighting" or "native_call" => 2,
        _ => 1
    };

    public static bool AcceptCue(string previous, string next, float age, bool preservePriority,
        bool previousPlayerShot = false, bool nextPlayerShot = false)
    {
        var priority = CuePriority(previous);
        var nextPriority = CuePriority(next);
        // Direct sight and contact still own combat. This replaces the stored
        // investigation point even when the previous cue was a sighting.
        if (nextPlayerShot) return true;
        if (previousPlayerShot && age < 2f && nextPriority < 4) return false;
        return nextPriority >= priority || (!preservePriority && (priority < 4 || age >= 1.25f));
    }

    public static bool InformationAlive(float now, float until) => until > now && until > 0;
    public static float LocalDensity(int count, float radius) => radius > 0 ? count * 1000f / ((float)Math.PI * radius * radius) : 0;
}

public sealed class InfectedTurnBudget
{
    private int _frame = -1;
    private float _remaining;

    public float Limit(float degrees, int frame, float seconds, bool movingOrFighting, bool consume = true)
    {
        if (float.IsNaN(degrees) || float.IsInfinity(degrees)) return degrees;
        if (frame != _frame)
        {
            _frame = frame;
            _remaining = (movingOrFighting ? 210f : 120f) * Math.Max(0f, Math.Min(seconds, .05f));
        }
        var step = Math.Max(-_remaining, Math.Min(degrees, _remaining));
        if (consume) _remaining -= Math.Abs(step);
        return step;
    }
}
