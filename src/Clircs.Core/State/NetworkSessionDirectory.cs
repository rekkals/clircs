using Clircs.Identity;

namespace Clircs.State;

public sealed class NetworkSessionDirectory
{
    private readonly Dictionary<NetworkSessionId, NetworkSessionState> _sessions = [];

    public IReadOnlyCollection<NetworkSessionState> Sessions => _sessions.Values;

    public void Add(NetworkSessionState session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_sessions.TryAdd(session.Id, session))
        {
            throw new InvalidOperationException($"Network session '{session.Id}' already exists.");
        }
    }

    public NetworkSessionState GetRequired(NetworkSessionId id) =>
        _sessions.TryGetValue(id, out var session)
            ? session
            : throw new KeyNotFoundException($"Network session '{id}' does not exist.");

    public bool Remove(NetworkSessionId id) => _sessions.Remove(id);
}
