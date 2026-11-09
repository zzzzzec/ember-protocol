namespace Codex.SkipPostRaidHealing;

internal static class HealingScreenPolicy
{
    public static bool Apply(bool enabled, ref bool available)
    {
        if (!enabled)
        {
            return true;
        }

        available = false;
        return false;
    }
}
