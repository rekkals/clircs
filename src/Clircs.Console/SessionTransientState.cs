using Clircs.Identity;

namespace Clircs.ConsoleClient;

// Owns short-lived automation state that is discarded when an IRC session ends.
internal sealed class SessionTransientState
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (NetworkSessionId SessionId, CancellationTokenSource Timer)> _timedBans = [];
    private readonly HashSet<NetworkSessionId> _autojoinStarted = [];
    private readonly Dictionary<NetworkSessionId, HashSet<string>> _awayAcknowledgedSenders = [];

    public void AddTimedBan(Guid timerId, NetworkSessionId sessionId, CancellationTokenSource timer)
    {
        lock (_gate) _timedBans.Add(timerId, (sessionId, timer));
    }

    public void RemoveTimedBan(Guid timerId)
    {
        lock (_gate) _timedBans.Remove(timerId);
    }

    public CancellationTokenSource[] TimedBansFor(NetworkSessionId sessionId)
    {
        lock (_gate)
        {
            return _timedBans.Values
                .Where(item => item.SessionId == sessionId)
                .Select(item => item.Timer)
                .ToArray();
        }
    }

    public bool TryStartAutojoin(NetworkSessionId sessionId)
    {
        lock (_gate) return _autojoinStarted.Add(sessionId);
    }

    public void ResetAutojoin(NetworkSessionId sessionId)
    {
        lock (_gate) _autojoinStarted.Remove(sessionId);
    }

    public bool TryAcknowledgeAwaySender(
        NetworkSessionId sessionId,
        string nickname,
        IEqualityComparer<string> comparer)
    {
        lock (_gate)
        {
            if (!_awayAcknowledgedSenders.TryGetValue(sessionId, out var senders))
            {
                senders = new HashSet<string>(comparer);
                _awayAcknowledgedSenders.Add(sessionId, senders);
            }
            return senders.Add(nickname);
        }
    }

    public void ResetAwayAcknowledgements(NetworkSessionId sessionId)
    {
        lock (_gate) _awayAcknowledgedSenders.Remove(sessionId);
    }

    public void ClearSession(NetworkSessionId sessionId)
    {
        lock (_gate)
        {
            _autojoinStarted.Remove(sessionId);
            _awayAcknowledgedSenders.Remove(sessionId);
        }
    }
}
