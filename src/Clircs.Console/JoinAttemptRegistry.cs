using Clircs.Identity;

namespace Clircs.ConsoleClient;

// Owns the transient state created while joining or cycling channels on one
// IRC session. The registry is reset when the transport disconnects so an old
// attempt cannot affect a later connection made by the same session.
internal sealed class JoinAttemptRegistry(IEqualityComparer<string> comparer)
{
    private readonly object _gate = new();
    private IEqualityComparer<string> _comparer = comparer;
    private Dictionary<string, long> _startedAt = new(comparer);
    private Dictionary<string, BufferId> _returnRoutes = new(comparer);
    private HashSet<string> _cycles = new(comparer);

    public void RecordStart(string channel, long timestamp, bool overwrite)
    {
        lock (_gate)
        {
            if (overwrite || !_startedAt.ContainsKey(channel)) _startedAt[channel] = timestamp;
        }
    }

    public bool TryTakeStart(string channel, out long timestamp)
    {
        lock (_gate) return _startedAt.Remove(channel, out timestamp);
    }

    public bool IsPending(string channel)
    {
        lock (_gate) return _startedAt.ContainsKey(channel);
    }

    public void RecordReturnRoute(string channel, BufferId destination)
    {
        lock (_gate) _returnRoutes[channel] = destination;
    }

    public bool Complete(string channel, bool denied, out BufferId destination)
    {
        lock (_gate)
        {
            _startedAt.Remove(channel);
            if (denied) _cycles.Remove(channel);
            return _returnRoutes.Remove(channel, out destination);
        }
    }

    public void ForgetJoin(string channel)
    {
        lock (_gate)
        {
            _startedAt.Remove(channel);
            _returnRoutes.Remove(channel);
        }
    }

    public void Forward(string requested, string forwarded)
    {
        lock (_gate)
        {
            if (_startedAt.Remove(requested, out var timestamp)) _startedAt[forwarded] = timestamp;
            if (_returnRoutes.Remove(requested, out var destination)) _returnRoutes[forwarded] = destination;
            if (_cycles.Remove(requested)) _cycles.Add(forwarded);
        }
    }

    public void MarkCycle(string channel)
    {
        lock (_gate) _cycles.Add(channel);
    }

    public bool IsCyclePending(string channel)
    {
        lock (_gate) return _cycles.Contains(channel);
    }

    public bool CompleteCycle(string channel)
    {
        lock (_gate) return _cycles.Remove(channel);
    }

    public void Reindex(IEqualityComparer<string> comparer)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_comparer, comparer)) return;
            _comparer = comparer;
            var startedAt = new Dictionary<string, long>(comparer);
            foreach (var (channel, timestamp) in _startedAt) startedAt[channel] = timestamp;
            _startedAt = startedAt;
            var returnRoutes = new Dictionary<string, BufferId>(comparer);
            foreach (var (channel, destination) in _returnRoutes) returnRoutes[channel] = destination;
            _returnRoutes = returnRoutes;
            _cycles = new HashSet<string>(_cycles, comparer);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _startedAt.Clear();
            _returnRoutes.Clear();
            _cycles.Clear();
        }
    }
}
