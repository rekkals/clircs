namespace Clircs.Sessions;

// Automatic CTCP replies are protocol conveniences, not connection-critical traffic.
// A per-source limit stops one client; the global limit also covers distributed floods.
internal sealed class AutomaticCtcpReplyLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private const int PerSourceLimit = 5;
    private const int GlobalLimit = 20;
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _global = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _bySource =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastWarning = DateTimeOffset.MinValue;

    public bool TryAcquire(string source, DateTimeOffset now, out bool reportSuppression)
    {
        lock (_gate)
        {
            Prune(_global, now);
            if (!_bySource.TryGetValue(source, out var sourceEvents))
            {
                sourceEvents = new Queue<DateTimeOffset>();
                _bySource[source] = sourceEvents;
            }
            Prune(sourceEvents, now);

            if (_global.Count >= GlobalLimit || sourceEvents.Count >= PerSourceLimit)
            {
                reportSuppression = now - _lastWarning >= Window;
                if (reportSuppression) _lastWarning = now;
                return false;
            }

            _global.Enqueue(now);
            sourceEvents.Enqueue(now);
            reportSuppression = false;
            if (_bySource.Count > 256)
            {
                foreach (var stale in _bySource.Where(entry => entry.Value.Count == 0).Select(entry => entry.Key).ToArray())
                {
                    _bySource.Remove(stale);
                }
            }
            return true;
        }
    }

    private static void Prune(Queue<DateTimeOffset> events, DateTimeOffset now)
    {
        while (events.TryPeek(out var timestamp) && now - timestamp >= Window) events.Dequeue();
    }
}
