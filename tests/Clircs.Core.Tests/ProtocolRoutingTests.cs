using Clircs.ConsoleClient;
using Clircs.Identity;
using Clircs.Sessions;

namespace Clircs.Core.Tests;

internal static class ProtocolRoutingTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("overlapping protocol requests and immediate follow-ups keep their routes", OverlappingRequestsKeepRoutes);
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

        var aliceComplete = Result(
            session,
            aliceRequest,
            complete: true,
            followUp: true);
        routes.ExpireUnmatchedFollowUps(aliceComplete);

        Assert.True(routes.TryResolve(
            aliceComplete,
            out var aliceDestination));
        Assert.Equal(aliceBuffer, aliceDestination);

        var aliceFollowUp = Result(
            session,
            aliceRequest,
            complete: true);
        routes.ExpireUnmatchedFollowUps(aliceFollowUp);

        Assert.True(routes.TryResolve(
            aliceFollowUp,
            out aliceDestination));
        Assert.Equal(aliceBuffer, aliceDestination);
        Assert.False(routes.TryResolve(aliceFollowUp, out _));

        var bobPartial = Result(
            session,
            bobRequest,
            complete: false);
        routes.ExpireUnmatchedFollowUps(bobPartial);

        Assert.True(routes.TryResolve(
            bobPartial,
            out var bobDestination));
        Assert.Equal(bobBuffer, bobDestination);

        var bobComplete = Result(
            session,
            bobRequest,
            complete: true,
            followUp: true);
        routes.ExpireUnmatchedFollowUps(bobComplete);

        Assert.True(routes.TryResolve(
            bobComplete,
            out bobDestination));
        Assert.Equal(bobBuffer, bobDestination);

        var unrelated = new SessionEvent(
            session,
            BufferId.New(),
            SessionEventKind.Server,
            "unrelated",
            DateTimeOffset.Now);
        routes.ExpireUnmatchedFollowUps(unrelated);

        var lateBobFollowUp = Result(
            session,
            bobRequest,
            complete: true);
        routes.ExpireUnmatchedFollowUps(lateBobFollowUp);

        Assert.False(routes.TryResolve(lateBobFollowUp, out _));
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
        Assert.Equal(OutputDestination.Active, routing.DestinationFor("wallops"));
        Assert.True(routing.TrySetDestination("whois", OutputDestination.Status));
        Assert.Equal(OutputDestination.Status, routing.DestinationFor("whois"));
        Assert.True(routing.TrySetDestination("wallops", OutputDestination.Dedicated));
        Assert.Equal(OutputDestination.Dedicated, routing.DestinationFor("wallops"));
        Assert.False(routing.TrySetDestination("made-up-family", OutputDestination.Status));

        var snapshot = routing.DestinationSnapshot();
        Assert.Equal(OutputDestination.Status, snapshot["whois"]);
        Assert.Equal(OutputDestination.Dedicated, snapshot["wallops"]);
        Assert.False(snapshot.ContainsKey("made-up-family"));
    }

    private static SessionEvent Result(
        NetworkSessionId session,
        Guid requestId,
        bool complete,
        bool followUp = false) =>
            new(session, BufferId.New(), SessionEventKind.Server, "WHOIS", DateTimeOffset.Now,
            new Dictionary<string, string?>
            {
                ["outputFamily"] = "whois",
                ["outputRequestId"] = requestId.ToString("D"),
                ["outputEnd"] = complete ? "true" : null,
                ["outputFollowUp"] = followUp ? "true" : null
            });
}
