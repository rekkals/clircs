namespace Clircs.ConsoleClient;

internal readonly record struct NotifyChanges(
    string[] Online,
    string[] Offline,
    string[] Requested,
    long? GetnickGeneration);

// Owns shared nickname-availability transport and notify state for one
// session. Refreshes serialize MONITOR changes and ISON request tracking.
internal sealed class NotifyCoordinator(IEqualityComparer<string> comparer)
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private HashSet<string> _online = new(comparer);
    private HashSet<string> _monitorSubscriptions = new(comparer);
    private HashSet<string> _rejectedMonitor = new(comparer);
    private readonly Queue<(string[] Nicknames, long? GetnickGeneration)> _pendingIsonBatches = new();
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

    public async Task RefreshAsync(
        Func<CancellationToken, Task> refresh,
        CancellationToken cancellationToken,
        bool retryRejectedMonitor = false)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (retryRejectedMonitor)
            {
                lock (_gate) _rejectedMonitor.Clear();
            }

            await refresh(cancellationToken);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public HashSet<string> OnlineSnapshot(IEqualityComparer<string> comparer)
    {
        lock (_gate) return new HashSet<string>(_online, comparer);
    }

    public void EnqueueIson(string[] nicknames, long? getnickGeneration = null)
    {
        lock (_gate) _pendingIsonBatches.Enqueue((nicknames, getnickGeneration));
    }

    public void RemoveFailedIson(string[] nicknames)
    {
        lock (_gate)
        {
            if (_pendingIsonBatches.Count == 0) return;
            var retained = _pendingIsonBatches
                .Where(batch => !ReferenceEquals(batch.Nicknames, nicknames))
                .ToArray();
            _pendingIsonBatches.Clear();
            foreach (var batch in retained) _pendingIsonBatches.Enqueue(batch);
        }
    }

    public NotifyChanges ApplyIson(
        IReadOnlyCollection<string> configured,
        IReadOnlyList<string> reportedOnline,
        IEqualityComparer<string> comparer)
    {
        lock (_gate)
        {
            var requested = _pendingIsonBatches.Count > 0
                ? _pendingIsonBatches.Dequeue()
                : (Nicknames: configured.ToArray(), GetnickGeneration: (long?)null);
            var current = new HashSet<string>(
                reportedOnline.Where(nick =>
                    requested.Nicknames.Contains(nick, comparer) &&
                    configured.Contains(nick, comparer)),
                comparer);
            var becameOnline = current.Where(nick => !_online.Contains(nick)).ToArray();
            var becameOffline = _initialized
                ? requested.Nicknames.Where(nick =>
                    configured.Contains(nick, comparer) &&
                    _online.Contains(nick) &&
                    !current.Contains(nick)).ToArray()
                : [];
            foreach (var nickname in requested.Nicknames) _online.Remove(nickname);
            _online.UnionWith(current);
            _initialized = true;
            return new NotifyChanges(
                becameOnline, becameOffline, requested.Nicknames, requested.GetnickGeneration);
        }
    }

    public string[] ApplyMonitor(
        IReadOnlyCollection<string> configured,
        bool online,
        IReadOnlyList<string> nicknames)
    {
        lock (_gate)
        {
            // A status reply confirms that the server is monitoring these names.
            _rejectedMonitor.ExceptWith(nicknames);
            _monitorSubscriptions.UnionWith(nicknames);
            var relevant = nicknames.Where(nickname =>
                configured.Contains(nickname, _online.Comparer));
            var changed = online
                ? relevant.Where(nickname => _online.Add(nickname)).ToArray()
                : _initialized
                    ? relevant.Where(nickname => _online.Remove(nickname)).ToArray()
                    : [];
            _initialized = true;
            return changed;
        }
    }

    public HashSet<string> MonitorSubscriptionsSnapshot(IEqualityComparer<string> comparer)
    {
        lock (_gate) return new HashSet<string>(_monitorSubscriptions, comparer);
    }

    public HashSet<string> RejectedMonitorSnapshot(IEqualityComparer<string> comparer)
    {
        lock (_gate) return new HashSet<string>(_rejectedMonitor, comparer);
    }

    public void RejectMonitor(IReadOnlyList<string> nicknames)
    {
        lock (_gate)
        {
            _rejectedMonitor.UnionWith(nicknames);
            _monitorSubscriptions.ExceptWith(nicknames);
        }
    }

    public void SetMonitorSubscriptions(HashSet<string> desired)
    {
        lock (_gate)
        {
            _monitorSubscriptions = new HashSet<string>(
                desired.Where(nickname => !_rejectedMonitor.Contains(nickname)),
                desired.Comparer);
            _online.RemoveWhere(nickname => !desired.Contains(nickname));
        }
    }

    public void Reindex(IEqualityComparer<string> comparer)
    {
        lock (_gate)
        {
            _online = new HashSet<string>(_online, comparer);
            _monitorSubscriptions = new HashSet<string>(_monitorSubscriptions, comparer);
            _rejectedMonitor = new HashSet<string>(_rejectedMonitor, comparer);
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
            _rejectedMonitor.Clear();
            _pendingIsonBatches.Clear();
            _initialized = false;
        }
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }
}
