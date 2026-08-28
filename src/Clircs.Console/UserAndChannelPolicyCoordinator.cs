using Clircs.Identity;
using Clircs.Protection;
using Clircs.Users;

namespace Clircs.ConsoleClient;

/// <summary>
/// Owns the mutable runtime state shared by userlist automation and personal
/// and channel protection. Persistent files remain owned by their stores.
/// </summary>
internal sealed class UserAndChannelPolicyCoordinator
{
    private readonly object _gate = new();
    private readonly ProtectionMonitor _monitor = new();
    private readonly ProtectionExpiryTracker _personalIgnores = new();
    private readonly ProtectionExpiryTracker _protectionActionCooldowns = new();
    private readonly ProtectionExpiryTracker _userActionReservations = new();
    private readonly Dictionary<NetworkProfileId, NetworkUserDirectory> _directories = [];
    private readonly Dictionary<(NetworkSessionId SessionId, string Channel), SemaphoreSlim> _channelGates = [];

    public ProtectionDetection? Evaluate(ProtectionEvidence evidence, ProtectionRule rule) =>
        _monitor.Evaluate(evidence, rule);

    public IReadOnlyList<ProtectionCounter> Counters(DateTimeOffset now) => _monitor.Counters(now);

    public void IgnorePersonally(NetworkSessionId sessionId, string identity, DateTimeOffset expiresAt) =>
        _personalIgnores.Set(sessionId, identity, expiresAt);

    public bool IsPersonallyIgnored(NetworkSessionId sessionId, string identity, DateTimeOffset now) =>
        _personalIgnores.Contains(sessionId, identity, now);

    public bool TryBeginProtectionAction(
        NetworkSessionId sessionId,
        string actionKey,
        DateTimeOffset now,
        DateTimeOffset expiresAt) =>
        _protectionActionCooldowns.TryReserve(sessionId, actionKey, now, expiresAt);

    public bool TryReserveUserAction(
        NetworkSessionId sessionId,
        string actionKey,
        DateTimeOffset now,
        DateTimeOffset expiresAt) =>
        _userActionReservations.TryReserve(sessionId, actionKey, now, expiresAt);

    public NetworkUserDirectory GetDirectory(
        NetworkProfileId profileId,
        Func<NetworkUserDirectory> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        lock (_gate)
        {
            if (_directories.TryGetValue(profileId, out var directory)) return directory;
            directory = load();
            if (directory.NetworkProfileId != profileId)
                throw new InvalidDataException("The user directory belongs to a different network profile");
            _directories.Add(profileId, directory);
            return directory;
        }
    }

    public void ReplaceDirectory(NetworkUserDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        lock (_gate) _directories[directory.NetworkProfileId] = directory;
    }

    public SemaphoreSlim ChannelGate(NetworkSessionId sessionId, string foldedChannel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foldedChannel);
        lock (_gate)
        {
            var key = (sessionId, foldedChannel);
            if (_channelGates.TryGetValue(key, out var gate)) return gate;
            gate = new SemaphoreSlim(1, 1);
            _channelGates.Add(key, gate);
            return gate;
        }
    }

    public void ClearSession(NetworkSessionId sessionId)
    {
        _personalIgnores.Clear(sessionId);
        _protectionActionCooldowns.Clear(sessionId);
        _userActionReservations.Clear(sessionId);
        _monitor.Clear(sessionId);
        lock (_gate)
        {
            foreach (var key in _channelGates.Keys.Where(key => key.SessionId == sessionId).ToArray())
                _channelGates.Remove(key);
        }
    }
}
