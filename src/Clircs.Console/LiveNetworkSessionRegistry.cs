using Clircs.Identity;
using Clircs.Networking;
using Clircs.Sessions;

namespace Clircs.ConsoleClient;

/// <summary>
/// Owns the application's live IRC-session membership and mutable connection metadata.
/// Window selection and presentation remain application concerns; route, profile, reconnect,
/// and per-session worker ownership cannot be mutated through parallel dictionaries.
/// </summary>
internal sealed class LiveNetworkSessionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<NetworkSessionId, LiveNetworkSession> _sessions = [];

    internal LiveNetworkSession Add(
        IrcNetworkSession session,
        IrcConnectionOptions route,
        NetworkProfileId? profileId,
        CancellationToken applicationToken)
    {
        var runtime = new LiveNetworkSession(session, route, profileId, applicationToken);
        lock (_gate) _sessions.Add(session.State.Id, runtime);
        return runtime;
    }

    internal LiveNetworkSession? Remove(NetworkSessionId id)
    {
        LiveNetworkSession? runtime;
        lock (_gate)
        {
            if (!_sessions.Remove(id, out runtime)) return null;
        }
        runtime.ReconnectCancellation?.Cancel();
        return runtime;
    }

    internal LiveNetworkSession? Runtime(NetworkSessionId id)
    {
        lock (_gate) return _sessions.GetValueOrDefault(id);
    }

    internal LiveNetworkSession? Runtime(IrcNetworkSession session) => Runtime(session.State.Id);

    internal IrcNetworkSession? Find(NetworkSessionId id) => Runtime(id)?.Session;

    private LiveNetworkSession[] RuntimeSnapshot()
    {
        lock (_gate) return [.. _sessions.Values];
    }

    internal IrcNetworkSession[] SessionSnapshot() =>
        [.. RuntimeSnapshot().Select(runtime => runtime.Session)
            .OrderBy(session => session.State.DisplayName, StringComparer.OrdinalIgnoreCase)];

    internal bool UsesProfile(NetworkProfileId profileId)
    {
        lock (_gate) return _sessions.Values.Any(runtime => runtime.ProfileId == profileId);
    }

    internal NetworkProfileId? ProfileId(NetworkSessionId id)
    {
        lock (_gate) return _sessions.GetValueOrDefault(id)?.ProfileId;
    }

    internal void AssociateProfile(NetworkSessionId id, NetworkProfileId profileId)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(id, out var runtime)) runtime.ProfileId = profileId;
        }
    }

    internal IrcConnectionOptions ConnectionRoute(NetworkSessionId id, IrcConnectionOptions fallback)
    {
        lock (_gate) return _sessions.GetValueOrDefault(id)?.ConnectionRoute ?? fallback;
    }

    internal bool IsReconnecting(NetworkSessionId id)
    {
        lock (_gate) return _sessions.GetValueOrDefault(id)?.ReconnectCancellation is not null;
    }

    internal bool TryBeginReconnect(
        NetworkSessionId id,
        CancellationToken applicationToken,
        out CancellationTokenSource? cancellation)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var runtime) || runtime.ReconnectCancellation is not null)
            {
                cancellation = null;
                return false;
            }
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);
            runtime.ReconnectCancellation = cancellation;
            return true;
        }
    }

    internal bool CompleteReconnect(NetworkSessionId id, CancellationTokenSource expected)
    {
        lock (_gate)
        {
            if (_sessions.GetValueOrDefault(id)?.ReconnectCancellation != expected) return false;
            _sessions[id].ReconnectCancellation = null;
            return true;
        }
    }

    internal bool CancelReconnect(NetworkSessionId id)
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _sessions.GetValueOrDefault(id)?.ReconnectCancellation;
            if (cancellation is null || cancellation.IsCancellationRequested) return false;
        }
        cancellation.Cancel();
        return true;
    }
}
