using Clircs.Identity;

namespace Clircs.Dcc;

public sealed class DccRequestRegistry
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private readonly Dictionary<int, DccRequest> _requests = [];
    private int _nextId = 1;

    public DccRequest Add(
        NetworkSessionId sessionId,
        string network,
        string sender,
        DccOffer offer,
        DateTimeOffset now,
        TimeSpan? lifetime = null,
        DccRequestDirection direction = DccRequestDirection.Incoming)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(network);
        ArgumentException.ThrowIfNullOrWhiteSpace(sender);
        ArgumentNullException.ThrowIfNull(offer);
        var duration = lifetime ?? DefaultLifetime;
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));

        lock (_gate)
        {
            var request = new DccRequest(_nextId++, sessionId, network, sender, offer,
                now, now.Add(duration), DccRequestState.Pending, Direction: direction);
            _requests.Add(request.Id, request);
            TrimUnsafe();
            return request;
        }
    }

    public IReadOnlyList<DccRequest> Snapshot()
    {
        lock (_gate)
        {
            return _requests.Values.OrderBy(request => request.Id).ToArray();
        }
    }

    public bool TryGet(int id, out DccRequest? request)
    {
        lock (_gate)
        {
            return _requests.TryGetValue(id, out request);
        }
    }

    public bool TryTransition(int id, DccRequestState state, string? reason, out DccRequest? request)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(id, out var current) || !CanTransition(current.State, state))
            {
                request = current;
                return false;
            }

            request = current with { State = state, StateReason = reason };
            _requests[id] = request;
            return true;
        }
    }

    public bool TryTransitionWithOffer(
        int id,
        DccRequestState state,
        DccOffer offer,
        string? reason,
        out DccRequest? request)
    {
        ArgumentNullException.ThrowIfNull(offer);
        lock (_gate)
        {
            if (!_requests.TryGetValue(id, out var current) || !CanTransition(current.State, state))
            {
                request = current;
                return false;
            }

            request = current with { State = state, Offer = offer, StateReason = reason };
            _requests[id] = request;
            return true;
        }
    }

    public bool TryTransitionAfter(
        int id,
        DccRequestState state,
        Func<string?> reasonFactory,
        out DccRequest? request)
    {
        ArgumentNullException.ThrowIfNull(reasonFactory);
        lock (_gate)
        {
            if (!_requests.TryGetValue(id, out var current) || !CanTransition(current.State, state))
            {
                request = current;
                return false;
            }

            var reason = reasonFactory();
            request = current with { State = state, StateReason = reason };
            _requests[id] = request;
            return true;
        }
    }

    public IReadOnlyList<DccRequest> Expire(DateTimeOffset now)
    {
        lock (_gate)
        {
            var expired = _requests.Values
                .Where(request => request.State == DccRequestState.Pending && request.ExpiresAt <= now)
                .Select(request => request with
                {
                    State = DccRequestState.Expired,
                    StateReason = "The request expired"
                })
                .ToArray();
            foreach (var request in expired) _requests[request.Id] = request;
            return expired;
        }
    }

    public IReadOnlyList<DccRequest> Invalidate(NetworkSessionId sessionId, string reason)
    {
        lock (_gate)
        {
            var invalidated = _requests.Values
                .Where(request => request.NetworkSessionId == sessionId &&
                    request.State is DccRequestState.Pending or DccRequestState.Connecting)
                .Select(request => request with { State = DccRequestState.Invalidated, StateReason = reason })
                .ToArray();
            foreach (var request in invalidated) _requests[request.Id] = request;
            return invalidated;
        }
    }

    private void TrimUnsafe()
    {
        const int maximumEntries = 200;
        if (_requests.Count <= maximumEntries) return;
        var removeCount = _requests.Count - maximumEntries;
        var completed = _requests.Values
                     .Where(request => IsTerminal(request.State))
                     .OrderBy(request => request.Id)
                     .Select(request => request.Id)
                     .Take(removeCount)
                     .ToArray();
        foreach (var id in completed)
        {
            _requests.Remove(id);
        }
        // Live requests own listeners, sockets, timers, buffers, or partial files elsewhere in the
        // application. Keeping more than the preferred history limit is safer than orphaning them.
    }

    private static bool CanTransition(DccRequestState current, DccRequestState next) => current switch
    {
        DccRequestState.Pending => next is DccRequestState.Connecting or
            DccRequestState.Rejected or DccRequestState.Cancelled or DccRequestState.Expired or
            DccRequestState.Invalidated or DccRequestState.Failed,
        DccRequestState.Connecting => next is DccRequestState.Connected or DccRequestState.Cancelled or
            DccRequestState.Invalidated or DccRequestState.Failed,
        DccRequestState.Connected => next is DccRequestState.Closed or DccRequestState.Completed or
            DccRequestState.Cancelled or DccRequestState.Invalidated or DccRequestState.Failed,
        _ => false
    };

    public static bool IsTerminal(DccRequestState state) => state is
        DccRequestState.Rejected or DccRequestState.Cancelled or DccRequestState.Expired or
        DccRequestState.Invalidated or DccRequestState.Closed or DccRequestState.Completed or
        DccRequestState.Failed;
}
