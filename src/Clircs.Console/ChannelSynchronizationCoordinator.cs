using Clircs.Identity;

namespace Clircs.ConsoleClient;

// Owns the relationship between background WHO requests and clone synchronization.
internal sealed class ChannelSynchronizationCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<NetworkSessionId, HashSet<string>> _automaticWhoRequests = [];
    private readonly Dictionary<(NetworkSessionId SessionId, string Channel), TaskCompletionSource<bool>> _cloneScans = [];
    private readonly HashSet<(NetworkSessionId SessionId, string Channel)> _reportedCloneSummaries = [];

    public void BeginAutomaticWho(NetworkSessionId sessionId, string channel)
    {
        lock (_gate) AutomaticWhoChannelsUnsafe(sessionId).Add(channel);
    }

    public bool IsAutomaticWho(NetworkSessionId sessionId, string channel)
    {
        lock (_gate)
            return _automaticWhoRequests.TryGetValue(sessionId, out var channels) && channels.Contains(channel);
    }

    public void CompleteAutomaticWho(NetworkSessionId sessionId, string channel)
    {
        lock (_gate)
        {
            if (!_automaticWhoRequests.TryGetValue(sessionId, out var channels)) return;
            channels.Remove(channel);
            if (channels.Count == 0) _automaticWhoRequests.Remove(sessionId);
        }
    }

    public void BeginCloneScan(
        (NetworkSessionId SessionId, string Channel) key,
        TaskCompletionSource<bool> completion)
    {
        lock (_gate)
        {
            if (_cloneScans.ContainsKey(key))
                throw new InvalidOperationException($"A clone scan for {key.Channel} is already running.");
            _cloneScans.Add(key, completion);
            AutomaticWhoChannelsUnsafe(key.SessionId).Add(key.Channel);
        }
    }

    public TaskCompletionSource<bool>? CloneScan(
        (NetworkSessionId SessionId, string Channel) key)
    {
        lock (_gate) return _cloneScans.GetValueOrDefault(key);
    }

    public void CompleteCloneScan((NetworkSessionId SessionId, string Channel) key)
    {
        lock (_gate)
        {
            _cloneScans.Remove(key);
            if (_automaticWhoRequests.TryGetValue(key.SessionId, out var channels))
            {
                channels.Remove(key.Channel);
                if (channels.Count == 0) _automaticWhoRequests.Remove(key.SessionId);
            }
        }
    }

    public bool TryReportCloneSummary((NetworkSessionId SessionId, string Channel) key)
    {
        lock (_gate) return _reportedCloneSummaries.Add(key);
    }

    public void ForgetChannel((NetworkSessionId SessionId, string Channel) key)
    {
        TaskCompletionSource<bool>? pending;
        lock (_gate)
        {
            _reportedCloneSummaries.Remove(key);
            _cloneScans.Remove(key, out pending);
            if (_automaticWhoRequests.TryGetValue(key.SessionId, out var channels))
            {
                channels.Remove(key.Channel);
                if (channels.Count == 0) _automaticWhoRequests.Remove(key.SessionId);
            }
        }
        pending?.TrySetCanceled();
    }

    public void ClearSession(NetworkSessionId sessionId)
    {
        TaskCompletionSource<bool>[] pending;
        lock (_gate)
        {
            _automaticWhoRequests.Remove(sessionId);
            var keys = _cloneScans.Keys.Where(key => key.SessionId == sessionId).ToArray();
            pending = keys.Select(key => _cloneScans[key]).ToArray();
            foreach (var key in keys) _cloneScans.Remove(key);
            _reportedCloneSummaries.RemoveWhere(key => key.SessionId == sessionId);
        }
        foreach (var completion in pending) completion.TrySetCanceled();
    }

    private HashSet<string> AutomaticWhoChannelsUnsafe(NetworkSessionId sessionId)
    {
        if (_automaticWhoRequests.TryGetValue(sessionId, out var channels)) return channels;
        channels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _automaticWhoRequests.Add(sessionId, channels);
        return channels;
    }
}
