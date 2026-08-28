using Clircs.ConsoleClient;
using Clircs.Identity;
using Clircs.Sessions;

namespace Clircs.Core.Tests;

internal static class WindowSynchronizationTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("window snapshots remain coherent during concurrent activation", ActiveWindowSnapshotsRemainCoherentAsync);
        suite.Add("viewport snapshots remain isolated during concurrent event storage", ViewportSnapshotsRemainIsolatedAsync);
        suite.Add("transient session state admits one acknowledgement per sender", AwayAcknowledgementsAreAtomicAsync);
        suite.Add("channel synchronization owns automatic WHO and clone state", ChannelSynchronizationIsCoherent);
    }

    private static async ValueTask ActiveWindowSnapshotsRemainCoherentAsync()
    {
        var windows = new WindowStateRegistry();
        var first = (Session: NetworkSessionId.New(), Buffer: BufferId.New());
        var second = (Session: NetworkSessionId.New(), Buffer: BufferId.New());
        windows.Activate(first.Session, first.Buffer);

        var writer = Task.Run(() =>
        {
            for (var index = 0; index < 50_000; index++)
            {
                var selected = index % 2 == 0 ? first : second;
                windows.Activate(selected.Session, selected.Buffer);
            }
        });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var index = 0; index < 50_000; index++)
            {
                var location = windows.ActiveLocation();
                var isFirst = location == (first.Session, first.Buffer);
                var isSecond = location == (second.Session, second.Buffer);
                Assert.True(isFirst || isSecond, "Active session and buffer came from different selections.");
            }
        }));

        await Task.WhenAll(readers.Append(writer));
    }

    private static async ValueTask ViewportSnapshotsRemainIsolatedAsync()
    {
        var windows = new WindowStateRegistry();
        var sessionId = NetworkSessionId.New();
        var bufferId = BufferId.New();
        windows.Activate(sessionId, bufferId);

        var writer = Task.Run(() =>
        {
            for (var index = 0; index < 20_000; index++)
            {
                var sessionEvent = new SessionEvent(
                    sessionId,
                    bufferId,
                    SessionEventKind.Message,
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    DateTimeOffset.UtcNow);
                windows.StoreEvent(sessionEvent, 1, _ => 1, false, true, DateTimeOffset.UtcNow);
            }
        });
        var reader = Task.Run(() =>
        {
            while (!writer.IsCompleted)
            {
                var viewport = windows.ViewportSnapshot(bufferId, 30);
                Assert.True(viewport.History.Length <= 30);
                Assert.True(viewport.History.All(item => item.BufferId == bufferId));
            }
        });

        await Task.WhenAll(writer, reader);
        var final = windows.HistorySnapshot(bufferId);
        Assert.Equal(20_000, final.Length);
        Assert.Equal("19999", final[^1].Text);
    }

    private static async ValueTask AwayAcknowledgementsAreAtomicAsync()
    {
        var state = new SessionTransientState();
        var sessionId = NetworkSessionId.New();
        var admitted = 0;
        var attempts = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            if (state.TryAcknowledgeAwaySender(sessionId, "Alice", StringComparer.OrdinalIgnoreCase))
                Interlocked.Increment(ref admitted);
        }));

        await Task.WhenAll(attempts);
        Assert.Equal(1, admitted);
        state.ResetAwayAcknowledgements(sessionId);
        Assert.True(state.TryAcknowledgeAwaySender(sessionId, "alice", StringComparer.OrdinalIgnoreCase));
    }

    private static void ChannelSynchronizationIsCoherent()
    {
        var state = new ChannelSynchronizationCoordinator();
        var key = (NetworkSessionId.New(), "#clircs");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        state.BeginCloneScan(key, completion);

        Assert.True(state.CloneScan(key) == completion);
        Assert.True(state.IsAutomaticWho(key.Item1, key.Item2));
        Assert.True(state.TryReportCloneSummary(key));
        Assert.False(state.TryReportCloneSummary(key));

        state.CompleteCloneScan(key);
        Assert.True(state.CloneScan(key) is null);
        Assert.False(state.IsAutomaticWho(key.Item1, key.Item2));
    }
}
