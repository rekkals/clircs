using Clircs.ConsoleClient;

namespace Clircs.Core.Tests;

internal static class ReconnectPresentationTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("reconnect timeouts are distinguished from connection errors", FormatsReconnectFailures);
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
            "Reconnect attempt 9 timed out after 20s while connecting or waiting for IRC registration.",
            ClientApplication.ReconnectTimeoutMessage(9, TimeSpan.FromSeconds(20)));
        Assert.Equal(
            "Reconnect attempt 4 failed: No route to host.",
            ClientApplication.ReconnectFailureMessage(4, new IOException("No route to host.")));
        Assert.Equal(
            "Reconnect attempt 1 failed: Unknown host.",
            ClientApplication.ReconnectFailureMessage(1, new IOException("No such host is known.")));
    }
}
