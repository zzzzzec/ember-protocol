namespace Codex.SkipPostRaidHealing;

// Coordinates the native save-before-continue operation without accepting game inventory data.
internal sealed class DeploymentSkipCoordinator
{
    private readonly HashSet<object> _pending = new();

    internal async Task Run(object owner, Func<Task<bool>> flush, Func<bool> stillCurrent,
        Action advance, Action fallback, Action<Exception> report)
    {
        lock (_pending) { if (!_pending.Add(owner)) return; }
        bool fallbackAttempted = false;
        void Restore() { fallbackAttempted = true; fallback(); }
        try
        {
            bool saved = await flush();
            if (!stillCurrent()) return;
            if (saved) advance(); else Restore();
        }
        catch (Exception exception)
        {
            report(exception);
            if (!fallbackAttempted && stillCurrent())
            {
                try { Restore(); } catch (Exception fallbackError) { report(fallbackError); }
            }
        }
        finally { lock (_pending) _pending.Remove(owner); }
    }
}
