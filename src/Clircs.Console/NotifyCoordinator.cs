namespace Clircs.ConsoleClient;

internal readonly record struct NotifyChanges(string[] Online, string[] Offline);

// Owns notify transport state for one IRC session. Refreshes are serialized so
// MONITOR deltas and ISON reply correlation cannot overlap each other.
internal sealed class NotifyCoordinator(IEqualityComparer<string> comparer)
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private HashSet<string> _online = new(comparer);
    private HashSet<string> _monitorSubscriptions = new(comparer);
    private readonly Queue<string[]> _pendingIsonBatches = new();
    private CancellationTokenSource? _monitorCancellation;
    private bool _initialized;

    public bool TryStartMonitor(CancellationToken sessionToken, out CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            if (_monitorCancellation is not null)
            {
                cancellation = null!;
                return false;
            }
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
            _monitorCancellation = cancellation;
            return true;
        }
    }

    public void MonitorCompleted(CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_monitorCancellation, cancellation)) return;
            _monitorCancellation = null;
        }
        cancellation.Dispose();
    }

    public async Task RefreshAsync(Func<CancellationToken, Task> refresh, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try { await refresh(cancellationToken); }
        finally { _refreshGate.Release(); }
    }

    public HashSet<string> OnlineSnapshot(IEqualityComparer<string> comparer)
    {
        lock (_gate) return new HashSet<string>(_online, comparer);
    }

    public void EnqueueIson(string[] nicknames)
    {
        lock (_gate) _pendingIsonBatches.Enqueue(nicknames);
    }

    public void RemoveFailedIson(string[] nicknames)
    {
        lock (_gate)
        {
            if (_pendingIsonBatches.Count == 0) return;
            var retained = _pendingIsonBatches.Where(batch => !ReferenceEquals(batch, nicknames)).ToArray();
            _pendingIsonBatches.Clear();
            foreach (var batch in retained) _pendingIsonBatches.Enqueue(batch);
        }
    }

    public NotifyChanges ApplyIson(
        IReadOnlyCollection<string> configured,
        IReadOnlyList<string> reportedOnline,
        IEqualityComparer<string> comparer)
    {
        var current = new HashSet<string>(
            reportedOnline.Where(nick => configured.Any(saved => comparer.Equals(saved, nick))), comparer);
        lock (_gate)
        {
            var requested = _pendingIsonBatches.Count > 0 ? _pendingIsonBatches.Dequeue() : configured.ToArray();
            var becameOnline = current.Where(nick => !_online.Contains(nick)).ToArray();
            var becameOffline = _initialized
                ? requested.Where(nick => _online.Contains(nick) && !current.Contains(nick)).ToArray()
                : [];
            foreach (var nickname in requested) _online.Remove(nickname);
            _online.UnionWith(current);
            _initialized = true;
            return new NotifyChanges(becameOnline, becameOffline);
        }
    }

    public string[] ApplyMonitor(bool online, IReadOnlyList<string> nicknames)
    {
        lock (_gate)
        {
            var changed = online
                ? nicknames.Where(nickname => _online.Add(nickname)).ToArray()
                : _initialized
                    ? nicknames.Where(nickname => _online.Remove(nickname)).ToArray()
                    : [];
            _initialized = true;
            return changed;
        }
    }

    public HashSet<string> MonitorSubscriptionsSnapshot(IEqualityComparer<string> comparer)
    {
        lock (_gate) return new HashSet<string>(_monitorSubscriptions, comparer);
    }

    public void SetMonitorSubscriptions(HashSet<string> desired)
    {
        lock (_gate)
        {
            _monitorSubscriptions = desired;
            _online.RemoveWhere(nickname => !desired.Contains(nickname));
        }
    }

    public void Reindex(IEqualityComparer<string> comparer)
    {
        lock (_gate)
        {
            _online = new HashSet<string>(_online, comparer);
            _monitorSubscriptions = new HashSet<string>(_monitorSubscriptions, comparer);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _monitorCancellation;
            _monitorCancellation = null;
            _online.Clear();
            _monitorSubscriptions.Clear();
            _pendingIsonBatches.Clear();
            _initialized = false;
        }
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }
}
