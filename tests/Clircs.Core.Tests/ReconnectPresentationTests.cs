using System.Net.Sockets;
using Clircs.ConsoleClient;

namespace Clircs.Core.Tests;

internal static class ReconnectPresentationTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("reconnect failures have concise descriptions", FormatsReconnectFailures);
        suite.Add("automatic reconnect stops immediately after success", StopsAfterSuccessAsync);
        suite.Add("automatic reconnect cancellation interrupts its delay", CancellationInterruptsDelayAsync);
        suite.Add("automatic reconnect exhausts the configured attempts", ExhaustsConfiguredAttemptsAsync);
    }

    private static async ValueTask StopsAfterSuccessAsync()
    {
        var attempts = new List<int>();
        var scheduled = new List<int>();
        var loop = new AutomaticReconnectLoop(
            new Clircs.Networking.ReconnectPolicy(5, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60)),
            (attempt, _) =>
            {
                attempts.Add(attempt);
                return Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask,
            () => 0d);

        var outcome = await loop.RunAsync(
            (attempt, _, _) => scheduled.Add(attempt),
            (_, _) => throw new InvalidOperationException("A successful attempt must not fail"),
            CancellationToken.None);

        Assert.Equal(AutomaticReconnectOutcome.Connected, outcome);
        Assert.True(attempts.SequenceEqual([1]));
        Assert.True(scheduled.SequenceEqual([1]));
    }

    private static async ValueTask CancellationInterruptsDelayAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        var loop = new AutomaticReconnectLoop(
            new Clircs.Networking.ReconnectPolicy(5, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60)),
            (_, _) =>
            {
                attempts++;
                return Task.CompletedTask;
            },
            async (_, token) =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            () => 0d);

        var outcome = await loop.RunAsync((_, _, _) => { }, (_, _) => { }, cancellation.Token);

        Assert.Equal(AutomaticReconnectOutcome.Canceled, outcome);
        Assert.Equal(0, attempts);
    }

    private static async ValueTask ExhaustsConfiguredAttemptsAsync()
    {
        var attempts = new List<int>();
        var failures = new List<int>();
        var loop = new AutomaticReconnectLoop(
            new Clircs.Networking.ReconnectPolicy(3, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60)),
            (attempt, _) =>
            {
                attempts.Add(attempt);
                throw new IOException($"failure {attempt}");
            },
            (_, _) => Task.CompletedTask,
            () => 0d);

        var outcome = await loop.RunAsync(
            (_, _, _) => { },
            (attempt, _) => failures.Add(attempt),
            CancellationToken.None);

        Assert.Equal(AutomaticReconnectOutcome.Exhausted, outcome);
        Assert.True(attempts.SequenceEqual([1, 2, 3]));
        Assert.True(failures.SequenceEqual([1, 2, 3]));
    }

    private static void FormatsReconnectFailures()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), ClientApplication.ConnectionAttemptTimeout);
        Assert.Equal(
            "Reconnect attempt 9 failed: Connection timed out.",
            ClientApplication.ReconnectTimeoutMessage(9));

        AssertSocketFailure(SocketError.TimedOut, "Connection timed out.");
        AssertSocketFailure(SocketError.ConnectionRefused, "Connection refused.");
        AssertSocketFailure(SocketError.HostNotFound, "Unknown host.");
        AssertSocketFailure(SocketError.NoData, "Unknown host.");
        AssertSocketFailure(SocketError.TryAgain, "Host lookup failed temporarily.");
        AssertSocketFailure(SocketError.NetworkUnreachable, "Network is unreachable.");
        AssertSocketFailure(SocketError.HostUnreachable, "Host is unreachable.");

        Assert.Equal(
            "Reconnect attempt 2 failed: Connection timed out.",
            ClientApplication.ReconnectFailureMessage(
                2,
                new IOException(
                    "The socket operation failed.",
                    new SocketException((int)SocketError.TimedOut))));

        Assert.Equal(
            "Reconnect attempt 4 failed: No route to host.",
            ClientApplication.ReconnectFailureMessage(4, new IOException("No route to host.")));
        Assert.Equal(
            "Reconnect attempt 1 failed: Unknown host.",
            ClientApplication.ReconnectFailureMessage(1, new IOException("No such host is known.")));
    }

    private static void AssertSocketFailure(SocketError error, string description) =>
        Assert.Equal(
            $"Reconnect attempt 1 failed: {description}",
            ClientApplication.ReconnectFailureMessage(
                1,
                new SocketException((int)error)));
}
