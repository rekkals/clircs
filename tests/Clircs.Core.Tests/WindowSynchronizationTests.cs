using Clircs.ConsoleClient;
using Clircs.Identity;
using Clircs.Sessions;

namespace Clircs.Core.Tests;

internal static class WindowSynchronizationTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("window numbers group connections and insert new windows", WindowNumbersFollowConnectionGroups);
        suite.Add("window removal compacts numbers without disturbing retained state", RenumberingPreservesWindowState);
        suite.Add("replacement connections inherit their previous group position", ReplacementPreservesConnectionPosition);
        suite.Add("window snapshots remain coherent during concurrent activation", ActiveWindowSnapshotsRemainCoherentAsync);
        suite.Add("viewport snapshots remain isolated during concurrent event storage", ViewportSnapshotsRemainIsolatedAsync);
        suite.Add("transient session state admits one acknowledgement per sender", AwayAcknowledgementsAreAtomicAsync);
        suite.Add("channel synchronization owns automatic WHO and clone state", ChannelSynchronizationIsCoherent);
    }

    private static void WindowNumbersFollowConnectionGroups()
    {
        var windows = new WindowStateRegistry();
        var first = NetworkSessionId.New();
        var second = NetworkSessionId.New();
        var firstStatus = BufferId.New();
        var firstChat = BufferId.New();
        var secondStatus = BufferId.New();
        var secondChat = BufferId.New();
        var local = BufferId.New();

        windows.RegisterWindow(local, null);
        windows.RegisterConnection(first, firstStatus);
        windows.RegisterWindow(firstChat, first);
        windows.RegisterConnection(second, secondStatus);
        windows.RegisterWindow(secondChat, second);

        Assert.True(
            new[] { firstStatus, firstChat, secondStatus, secondChat, local }
                .Select(id => windows.NumberOr(id, 0))
                .SequenceEqual([1, 2, 3, 4, 5]));

        var added = BufferId.New();
        windows.StoreEvent(
            new SessionEvent(first, added, SessionEventKind.Join, "joined", DateTimeOffset.UtcNow),
            incomingRows: 1,
            measureRows: _ => 1,
            isReplay: false,
            trackUnread: true);

        Assert.Equal(3, windows.NumberOr(added, 0));
        Assert.True(
            new[] { firstStatus, firstChat, added, secondStatus, secondChat, local }
                .Select(id => windows.NumberOr(id, 0))
                .SequenceEqual([1, 2, 3, 4, 5, 6]));
    }

    private static void RenumberingPreservesWindowState()
    {
        var windows = new WindowStateRegistry();
        var first = NetworkSessionId.New();
        var second = NetworkSessionId.New();
        var firstStatus = BufferId.New();
        var closing = BufferId.New();
        var secondStatus = BufferId.New();
        var active = BufferId.New();
        var unread = BufferId.New();

        windows.RegisterConnection(first, firstStatus);
        windows.RegisterWindow(closing, first);
        windows.RegisterConnection(second, secondStatus);
        windows.RegisterWindow(active, second);
        windows.RegisterWindow(unread, second);
        windows.Activate(second, active);

        var message = new SessionEvent(
            second, active, SessionEventKind.Message, "retained", DateTimeOffset.UtcNow);
        windows.AppendHistory(message);
        windows.SetScrollOffset(active, 24);
        windows.MarkUnread(unread, SessionEventKind.Highlight);

        Assert.True(windows.TryRemoveInactiveEmpty(closing));
        Assert.True(
            new[] { firstStatus, secondStatus, active, unread }
                .Select(id => windows.NumberOr(id, 0))
                .SequenceEqual([1, 2, 3, 4]));

        windows.Remove(firstStatus);

        Assert.True(
            new[] { secondStatus, active, unread }
                .Select(id => windows.NumberOr(id, 0))
                .SequenceEqual([1, 2, 3]));
        Assert.True(windows.ConnectionPosition(first) is null);
        Assert.True(windows.ConnectionPosition(second) == 0);
        Assert.True(windows.ActiveLocation() == (second, active));
        Assert.Equal(message, windows.HistorySnapshot(active).Single());
        Assert.Equal(24, windows.ScrollOffset(active));

        var activity = windows.ChromeState(active).Activity.Single();
        Assert.Equal(unread, activity.BufferId);
        Assert.Equal(3, activity.Number);
        Assert.Equal(SessionEventKind.Highlight, activity.Kinds.Single());
    }

    private static void ReplacementPreservesConnectionPosition()
    {
        var windows = new WindowStateRegistry();
        var first = NetworkSessionId.New();
        var original = NetworkSessionId.New();
        var last = NetworkSessionId.New();
        var replacement = NetworkSessionId.New();
        var firstStatus = BufferId.New();
        var originalStatus = BufferId.New();
        var originalChat = BufferId.New();
        var lastStatus = BufferId.New();
        var replacementStatus = BufferId.New();

        windows.RegisterConnection(first, firstStatus);
        windows.RegisterConnection(original, originalStatus);
        windows.RegisterWindow(originalChat, original);
        windows.RegisterConnection(last, lastStatus);

        var position = windows.ConnectionPosition(original);
        Assert.True(position == 1);

        windows.Remove(originalStatus);
        windows.Remove(originalChat);
        windows.RegisterConnection(replacement, replacementStatus, position);

        Assert.True(
            new[] { firstStatus, replacementStatus, lastStatus }
                .Select(id => windows.NumberOr(id, 0))
                .SequenceEqual([1, 2, 3]));
        Assert.True(windows.ConnectionPosition(original) is null);
        Assert.True(windows.ConnectionPosition(replacement) == 1);
        Assert.True(windows.ConnectionPosition(last) == 2);
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
                windows.StoreEvent(sessionEvent, 1, _ => 1, false, true);
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
        Assert.Equal(ScrollbackRetention.MaximumEntries, final.Length);
        Assert.Equal(
            (20_000 - ScrollbackRetention.MaximumEntries).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            final[0].Text);
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
