using Clircs.Identity;
using Clircs.Networking;
using Clircs.Protocol;
using Clircs.Sessions;

namespace Clircs.ConsoleClient;

// Owns the application-level state for one live IRC session. Keeping the
// connection route, profile association, and reconnect operation beside the
// session prevents those values from drifting across parallel dictionaries.
internal sealed class LiveNetworkSession(
    IrcNetworkSession session,
    IrcConnectionOptions connectionRoute,
    NetworkProfileId? profileId,
    CancellationToken applicationToken)
{
    public IrcNetworkSession Session { get; } = session;

    public IrcConnectionOptions ConnectionRoute { get; } = connectionRoute;

    public NetworkProfileId? ProfileId { get; set; } = profileId;

    public CancellationTokenSource? ReconnectCancellation { get; set; }

    public SessionWorkTracker Work { get; } = new(applicationToken);

    public NotifyCoordinator Notify { get; } = new(new IrcNameComparer(session.State.CaseMapping));

    public JoinAttemptRegistry Joins { get; } = new(new IrcNameComparer(session.State.CaseMapping));
}
