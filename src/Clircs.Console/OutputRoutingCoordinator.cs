using Clircs.Identity;
using Clircs.Sessions;

namespace Clircs.ConsoleClient;

/// <summary>
/// Owns the user's output-destination policy and the temporary routes that
/// correlate stateful IRC replies with the window that requested them.
/// </summary>
internal sealed class OutputRoutingCoordinator
{
    private static readonly IReadOnlyDictionary<string, OutputDestination> DefaultDestinations =
        new Dictionary<string, OutputDestination>(StringComparer.OrdinalIgnoreCase)
        {
            ["who"] = OutputDestination.Active,
            ["whois"] = OutputDestination.Active,
            ["whowas"] = OutputDestination.Active,
            ["ctcp"] = OutputDestination.Active,
            ["notice"] = OutputDestination.Active,
            ["invite"] = OutputDestination.Active,
            ["links"] = OutputDestination.Status,
            ["list"] = OutputDestination.Dedicated,
            ["dns"] = OutputDestination.Active,
            ["messageguard"] = OutputDestination.Active
        };

    public static readonly IReadOnlyList<string> SettingOrder =
    [
        "who", "whois", "whowas", "ctcp", "notice", "invite", "links", "list", "dns", "messageguard"
    ];

    private readonly object _gate = new();
    private readonly Dictionary<string, OutputDestination> _destinations =
        new(DefaultDestinations, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(NetworkSessionId SessionId, string Family), BufferId> _familyRoutes = [];
    private readonly Dictionary<(NetworkSessionId SessionId, Guid RequestId), BufferId> _requestRoutes = [];

    public bool Supports(string family)
    {
        lock (_gate) return _destinations.ContainsKey(family);
    }

    public OutputDestination DestinationFor(string family)
    {
        lock (_gate) return _destinations.GetValueOrDefault(family, OutputDestination.Active);
    }

    public bool TryGetDestination(string family, out OutputDestination destination)
    {
        lock (_gate) return _destinations.TryGetValue(family, out destination);
    }

    public bool TrySetDestination(string family, OutputDestination destination)
    {
        lock (_gate)
        {
            if (!_destinations.ContainsKey(family)) return false;
            _destinations[family] = destination;
            return true;
        }
    }

    public IReadOnlyDictionary<string, OutputDestination> DestinationSnapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, OutputDestination>(_destinations, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SetFamily(NetworkSessionId sessionId, string family, BufferId destination)
    {
        lock (_gate) _familyRoutes[(sessionId, family)] = destination;
    }

    public bool TrySetExclusiveFamily(NetworkSessionId sessionId, string family, BufferId destination)
    {
        lock (_gate) return _familyRoutes.TryAdd((sessionId, family), destination);
    }

    public void SetRequest(NetworkSessionId sessionId, Guid requestId, BufferId destination)
    {
        lock (_gate) _requestRoutes[(sessionId, requestId)] = destination;
    }

    public void RemoveFamily(NetworkSessionId sessionId, string family)
    {
        lock (_gate) _familyRoutes.Remove((sessionId, family));
    }

    public void RemoveRequest(NetworkSessionId sessionId, Guid requestId)
    {
        lock (_gate) _requestRoutes.Remove((sessionId, requestId));
    }

    public bool TryResolve(SessionEvent sessionEvent, out BufferId destination)
    {
        lock (_gate)
        {
            destination = default;
            if (sessionEvent.Fields is null) return false;

            if (sessionEvent.Fields.TryGetValue("outputRequestId", out var requestText) &&
                Guid.TryParse(requestText, out var requestId))
            {
                if (!_requestRoutes.TryGetValue((sessionEvent.NetworkSessionId, requestId), out destination)) return false;
                if (IsComplete(sessionEvent)) _requestRoutes.Remove((sessionEvent.NetworkSessionId, requestId));
                return true;
            }

            if (!sessionEvent.Fields.TryGetValue("outputFamily", out var family) ||
                string.IsNullOrWhiteSpace(family) ||
                !_familyRoutes.TryGetValue((sessionEvent.NetworkSessionId, family), out destination)) return false;

            if (IsComplete(sessionEvent)) _familyRoutes.Remove((sessionEvent.NetworkSessionId, family));
            return true;
        }
    }

    public void ClearSession(NetworkSessionId sessionId)
    {
        lock (_gate)
        {
            foreach (var key in _familyRoutes.Keys.Where(key => key.SessionId == sessionId).ToArray())
                _familyRoutes.Remove(key);
            foreach (var key in _requestRoutes.Keys.Where(key => key.SessionId == sessionId).ToArray())
                _requestRoutes.Remove(key);
        }
    }

    private static bool IsComplete(SessionEvent sessionEvent) =>
        sessionEvent.Fields?.GetValueOrDefault("outputEnd")
            ?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
}
