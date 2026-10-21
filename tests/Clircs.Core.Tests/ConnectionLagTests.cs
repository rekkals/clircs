using System.Diagnostics;
using Clircs.Protocol;
using Clircs.Sessions;

namespace Clircs.Core.Tests;

internal static class ConnectionLagTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("lag probes use a thirty-second cadence with one pending probe", CadenceAndSinglePendingProbe);
        suite.Add("lag probes accept only an exact matching PONG", OnlyExactPongCompletesProbe);
        suite.Add("pending lag probes preserve the previous measurement", PendingDelayPreservesSample);
        suite.Add("expired lag probes retain their delay and can recover", ExpiredProbeCanRecover);
        suite.Add("lag cancellation and reset remain isolated per connection", CancellationAndResetAreIsolated);
    }

    private static void CadenceAndSinglePendingProbe()
    {
        var tracker = new ConnectionLagTracker();
        Assert.Equal(default(ConnectionLagSnapshot), tracker.Snapshot(At(0)));

        var first = Begin(tracker, 0);
        Assert.True(tracker.TryBeginProbe(At(1)) is null);
        Assert.True(tracker.TryAcceptPong(Pong(first), At(1)));

        Assert.True(tracker.TryBeginProbe(At(29)) is null);
        var second = Begin(tracker, 30);
        Assert.False(first == second);

        Assert.True(tracker.TryBeginProbe(At(60)) is null);
        Assert.True(tracker.TryBeginProbe(At(149)) is null);
    }

    private static void OnlyExactPongCompletesProbe()
    {
        var tracker = new ConnectionLagTracker();
        var token = Begin(tracker, 0);

        IrcMessage[] unrelated =
        [
            new("server", "NOTICE", [token]),
            new("server", "PONG", []),
            new("server", "PONG", ["another-probe"]),
            new("server", "PONG", [$"prefix-{token}"]),
            new("server", "PONG", [token.ToUpperInvariant()]),
            new("server", "PONG", [token, "different-token"])
        ];

        foreach (var message in unrelated)
        {
            Assert.False(tracker.TryAcceptPong(message, At(1)));
        }

        Assert.Equal(
            new ConnectionLagSnapshot(null, TimeSpan.FromSeconds(1)),
            tracker.Snapshot(At(1)));

        Assert.True(tracker.TryAcceptPong(Pong(token), At(2)));
        Assert.False(tracker.TryAcceptPong(Pong(token), At(3)));

        Assert.Equal(
            new ConnectionLagSnapshot(TimeSpan.FromSeconds(2), null),
            tracker.Snapshot(At(3)));
    }

    private static void PendingDelayPreservesSample()
    {
        var tracker = new ConnectionLagTracker();
        var first = Begin(tracker, 0);
        Assert.True(tracker.TryAcceptPong(Pong(first), At(1)));

        var second = Begin(tracker, 30);
        Assert.Equal(
            new ConnectionLagSnapshot(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(10)),
            tracker.Snapshot(At(40)));

        Assert.True(tracker.TryAcceptPong(Pong(second), At(41)));

        var third = Begin(tracker, 60);
        Assert.Equal(
            new ConnectionLagSnapshot(
                TimeSpan.FromSeconds(11),
                TimeSpan.FromSeconds(1)),
            tracker.Snapshot(At(61)));

        Assert.True(tracker.TryAcceptPong(Pong(third), At(62)));
        Assert.Equal(
            new ConnectionLagSnapshot(TimeSpan.FromSeconds(2), null),
            tracker.Snapshot(At(63)));
    }

    private static void ExpiredProbeCanRecover()
    {
        var tracker = new ConnectionLagTracker();
        var expired = Begin(tracker, 0);
        Assert.True(tracker.TryBeginProbe(At(119)) is null);

        var replacement = Begin(tracker, 120);
        Assert.False(expired == replacement);
        Assert.Equal(
            new ConnectionLagSnapshot(null, TimeSpan.FromSeconds(120)),
            tracker.Snapshot(At(121)));

        Assert.False(tracker.TryAcceptPong(Pong(expired), At(122)));
        tracker.CancelProbe(expired);

        Assert.True(tracker.TryAcceptPong(Pong(replacement), At(123)));
        Assert.Equal(
            new ConnectionLagSnapshot(TimeSpan.FromSeconds(3), null),
            tracker.Snapshot(At(123)));
    }

    private static void CancellationAndResetAreIsolated()
    {
        var first = new ConnectionLagTracker();
        var second = new ConnectionLagTracker();
        var firstToken = Begin(first, 0);
        var secondToken = Begin(second, 0);

        Assert.False(first.TryAcceptPong(Pong(secondToken), At(1)));
        Assert.False(second.TryAcceptPong(Pong(firstToken), At(1)));
        Assert.True(second.TryAcceptPong(Pong(secondToken), At(1)));

        first.CancelProbe(firstToken);
        Assert.Equal(default(ConnectionLagSnapshot), first.Snapshot(At(2)));
        Assert.Equal(
            new ConnectionLagSnapshot(TimeSpan.FromSeconds(1), null),
            second.Snapshot(At(2)));

        Assert.True(first.TryBeginProbe(At(29)) is null);
        var beforeReset = Begin(first, 30);
        first.Reset();

        Assert.Equal(default(ConnectionLagSnapshot), first.Snapshot(At(31)));
        Assert.False(first.TryAcceptPong(Pong(beforeReset), At(31)));

        var afterReset = Begin(first, 31);
        Assert.False(beforeReset == afterReset);
        Assert.False(first.TryAcceptPong(Pong(beforeReset), At(32)));

        // Some servers return only the echoed token.
        Assert.True(first.TryAcceptPong(
            new IrcMessage(null, "PONG", [afterReset]),
            At(33)));

        Assert.Equal(
            new ConnectionLagSnapshot(TimeSpan.FromSeconds(2), null),
            first.Snapshot(At(33)));
    }

    private static long At(int seconds) =>
        checked(Stopwatch.Frequency * seconds);

    private static string Begin(ConnectionLagTracker tracker, int seconds) =>
        tracker.TryBeginProbe(At(seconds))
        ?? throw new InvalidOperationException("Expected a lag probe to start.");

    private static IrcMessage Pong(string token) =>
        new("server", "PONG", ["server", token]);
}
