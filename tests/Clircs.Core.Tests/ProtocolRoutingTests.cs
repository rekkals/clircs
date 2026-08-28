using Clircs.ConsoleClient;
using Clircs.Identity;
using Clircs.Sessions;

namespace Clircs.Core.Tests;

internal static class ProtocolRoutingTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("overlapping protocol requests keep independent output routes", OverlappingRequestsKeepRoutes);
        suite.Add("uncorrelatable protocol reply families are serialized", UncorrelatableFamiliesAreSerialized);
        suite.Add("closing a network clears all pending output routes", ClosingNetworkClearsRoutes);
        suite.Add("output policy and pending routes share one owner", OutputPolicyAndRoutesShareOwner);
    }

    private static void OverlappingRequestsKeepRoutes()
    {
        var routes = new OutputRoutingCoordinator();
        var session = NetworkSessionId.New();
        var aliceRequest = Guid.NewGuid();
        var bobRequest = Guid.NewGuid();
        var aliceBuffer = BufferId.New();
        var bobBuffer = BufferId.New();
        routes.SetRequest(session, aliceRequest, aliceBuffer);
        routes.SetRequest(session, bobRequest, bobBuffer);

        Assert.True(routes.TryResolve(Result(session, aliceRequest, complete: true), out var aliceDestination));
        Assert.Equal(aliceBuffer, aliceDestination);
        Assert.True(routes.TryResolve(Result(session, bobRequest, complete: false), out var bobDestination));
        Assert.Equal(bobBuffer, bobDestination);
        Assert.False(routes.TryResolve(Result(session, aliceRequest, complete: true), out _));
        Assert.True(routes.TryResolve(Result(session, bobRequest, complete: true), out bobDestination));
        Assert.Equal(bobBuffer, bobDestination);
    }

    private static void UncorrelatableFamiliesAreSerialized()
    {
        var routes = new OutputRoutingCoordinator();
        var session = NetworkSessionId.New();
        var first = BufferId.New();
        Assert.True(routes.TrySetExclusiveFamily(session, "list", first));
        Assert.False(routes.TrySetExclusiveFamily(session, "list", BufferId.New()));

        var completed = new SessionEvent(session, BufferId.New(), SessionEventKind.Server, "LIST", DateTimeOffset.Now,
            new Dictionary<string, string?> { ["outputFamily"] = "list", ["outputEnd"] = "true" });
        Assert.True(routes.TryResolve(completed, out var destination));
        Assert.Equal(first, destination);
        Assert.True(routes.TrySetExclusiveFamily(session, "list", BufferId.New()));
    }

    private static void ClosingNetworkClearsRoutes()
    {
        var routes = new OutputRoutingCoordinator();
        var session = NetworkSessionId.New();
        var request = Guid.NewGuid();
        routes.SetRequest(session, request, BufferId.New());
        routes.SetFamily(session, "links", BufferId.New());
        routes.ClearSession(session);

        Assert.False(routes.TryResolve(Result(session, request, complete: true), out _));
        var links = new SessionEvent(session, BufferId.New(), SessionEventKind.Server, "LINKS", DateTimeOffset.Now,
            new Dictionary<string, string?> { ["outputFamily"] = "links", ["outputEnd"] = "true" });
        Assert.False(routes.TryResolve(links, out _));
    }

    private static void OutputPolicyAndRoutesShareOwner()
    {
        var routing = new OutputRoutingCoordinator();
        Assert.Equal(OutputDestination.Active, routing.DestinationFor("whois"));
        Assert.Equal(OutputDestination.Dedicated, routing.DestinationFor("list"));
        Assert.True(routing.TrySetDestination("whois", OutputDestination.Status));
        Assert.Equal(OutputDestination.Status, routing.DestinationFor("whois"));
        Assert.False(routing.TrySetDestination("made-up-family", OutputDestination.Status));

        var snapshot = routing.DestinationSnapshot();
        Assert.Equal(OutputDestination.Status, snapshot["whois"]);
        Assert.False(snapshot.ContainsKey("made-up-family"));
    }

    private static SessionEvent Result(NetworkSessionId session, Guid requestId, bool complete) =>
        new(session, BufferId.New(), SessionEventKind.Server, "WHOIS", DateTimeOffset.Now,
            new Dictionary<string, string?>
            {
                ["outputFamily"] = "whois",
                ["outputRequestId"] = requestId.ToString("D"),
                ["outputEnd"] = complete ? "true" : null
            });
}
