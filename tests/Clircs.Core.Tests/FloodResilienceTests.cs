using System.Diagnostics;
using Clircs.ConsoleClient;
using Clircs.Identity;
using Clircs.Networking;
using Clircs.Protocol;
using Clircs.Sessions;
using Clircs.State;

namespace Clircs.Core.Tests;

internal static class FloodResilienceTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("ordinary IRC output is paced after a small burst", OutboundTrafficIsPacedAsync);
        suite.Add("IRC output queue rejects excess pending work", OutboundQueueIsBoundedAsync);
        suite.Add("automatic CTCP replies have sender and network limits", AutomaticCtcpRepliesAreLimited);
        suite.Add("recent flood traffic remains available in scrollback", RecentFloodTrafficRemainsInScrollback);
        suite.Add("scrollback ages old traffic while retaining a useful minimum", OldScrollbackIsAged);
        suite.Add("scrollback reports and enforces its emergency resource boundary", EmergencyScrollbackLimitIsExplicit);
        suite.Add("window scrollback removes old flood traffic without shifting its retained history", WindowScrollbackAgesFromFront);
        suite.Add("all window scrollback shares an application emergency boundary", TotalWindowScrollbackIsBounded);
        suite.Add("bottom viewport selection measures only visible history", BottomViewportSelectionIsLocal);
        suite.Add("automatic query creation has a hard resource limit", AutomaticQueriesAreBounded);
    }

    private static async ValueTask OutboundTrafficIsPacedAsync()
    {
        var transport = new RecordingTransport();
        await using var scheduler = new IrcOutboundScheduler(
            transport,
            burstTokens: 2,
            tokenInterval: TimeSpan.FromMilliseconds(80));
        await scheduler.EnqueueAsync("one\r\n"u8.ToArray(), IrcOutboundPriority.Interactive, CancellationToken.None);
        await scheduler.EnqueueAsync("two\r\n"u8.ToArray(), IrcOutboundPriority.Interactive, CancellationToken.None);
        var stopwatch = Stopwatch.StartNew();
        await scheduler.EnqueueAsync("three\r\n"u8.ToArray(), IrcOutboundPriority.Interactive, CancellationToken.None);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(55), "The post-burst IRC line was not paced.");
    }

    private static async ValueTask OutboundQueueIsBoundedAsync()
    {
        var transport = new BlockingTransport();
        await using var scheduler = new IrcOutboundScheduler(
            transport,
            queueLimit: 2,
            criticalQueueLimit: 1,
            burstTokens: 100,
            tokenInterval: TimeSpan.FromMilliseconds(1));
        var active = scheduler.EnqueueAsync("active\r\n"u8.ToArray(), IrcOutboundPriority.Interactive, CancellationToken.None).AsTask();
        await transport.WriteStarted.Task;
        var pendingOne = scheduler.EnqueueAsync("one\r\n"u8.ToArray(), IrcOutboundPriority.Interactive, CancellationToken.None).AsTask();
        var pendingTwo = scheduler.EnqueueAsync("two\r\n"u8.ToArray(), IrcOutboundPriority.Interactive, CancellationToken.None).AsTask();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await scheduler.EnqueueAsync("excess\r\n"u8.ToArray(), IrcOutboundPriority.Interactive, CancellationToken.None));
        Assert.True(exception.Message.Contains("queue is full", StringComparison.OrdinalIgnoreCase));
        transport.Release.TrySetResult();
        await Task.WhenAll(active, pendingOne, pendingTwo);
    }

    private static void AutomaticCtcpRepliesAreLimited()
    {
        var limiter = new AutomaticCtcpReplyLimiter();
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 5; index++)
            Assert.True(limiter.TryAcquire("nick!user@host", now, out _));
        Assert.False(limiter.TryAcquire("nick!user@host", now, out var report));
        Assert.True(report);

        for (var index = 0; index < 15; index++)
            Assert.True(limiter.TryAcquire($"bot{index}!user@host", now, out _));
        Assert.False(limiter.TryAcquire("distributed!user@host", now, out _));

        Assert.True(limiter.TryAcquire("nick!user@host", now.AddSeconds(10), out _));
    }

    private static void RecentFloodTrafficRemainsInScrollback()
    {
        var session = NetworkSessionId.New();
        var buffer = BufferId.New();
        var now = DateTimeOffset.UtcNow;
        var history = Enumerable.Range(0, 5_000)
            .Select(index => Message(session, buffer, $"bot{index}", now))
            .ToList();

        Assert.Equal(0, ScrollbackRetention.Trim(history, now));
        Assert.Equal(5_000, history.Count);
    }

    private static void OldScrollbackIsAged()
    {
        var session = NetworkSessionId.New();
        var buffer = BufferId.New();
        var now = DateTimeOffset.UtcNow;
        var history = Enumerable.Range(0, 1_000)
            .Select(index => Message(session, buffer, $"old{index}", now.AddDays(-2)))
            .Concat(Enumerable.Range(0, 400)
                .Select(index => Message(session, buffer, $"recent{index}", now)))
            .ToList();

        Assert.Equal(900, ScrollbackRetention.Trim(history, now));
        Assert.Equal(ScrollbackRetention.MinimumEntries, history.Count);
        Assert.True(history[0].Text.Contains("old900", StringComparison.Ordinal));
        Assert.True(history[^1].Text.Contains("recent399", StringComparison.Ordinal));
    }

    private static void EmergencyScrollbackLimitIsExplicit()
    {
        var session = NetworkSessionId.New();
        var buffer = BufferId.New();
        var now = DateTimeOffset.UtcNow;
        var history = Enumerable.Range(0, 6)
            .Select(index => Message(session, buffer, $"bot{index}", now))
            .ToList();

        Assert.True(ScrollbackRetention.EnforceEmergencyLimit(history, maximumEntries: 5));
        Assert.Equal(5, history.Count);
        Assert.True(history[0].Text.Contains("bot1", StringComparison.Ordinal));
        Assert.False(ScrollbackRetention.EnforceEmergencyLimit(history, maximumEntries: 5));
    }

    private static void WindowScrollbackAgesFromFront()
    {
        var session = NetworkSessionId.New();
        var buffer = BufferId.New();
        var now = DateTimeOffset.UtcNow;
        var history = new WindowEventHistory();
        for (var index = 0; index < 10_000; index++) history.Add(Message(session, buffer, $"bot{index}", now));

        Assert.True(ScrollbackRetention.EnforceEmergencyLimit(history, maximumEntries: 5_000));
        Assert.Equal(5_000, history.Count);
        Assert.True(history[0].Text.Contains("bot5000", StringComparison.Ordinal));
        history.Add(Message(session, buffer, "latest", now));
        Assert.True(ScrollbackRetention.EnforceEmergencyLimit(history, maximumEntries: 5_000));
        Assert.True(history[^1].Text.Contains("latest", StringComparison.Ordinal));
    }

    private static void BottomViewportSelectionIsLocal()
    {
        var history = Enumerable.Range(0, 250_000).ToArray();
        var measured = 0;
        var selected = ViewportHistory.SelectRows(history, 0, 30, _ =>
        {
            measured++;
            return 1;
        });

        Assert.Equal(30, selected.Count);
        Assert.Equal(30, measured);
        Assert.Equal(249_970, selected[0].Item);
        Assert.Equal(249_999, selected[^1].Item);
    }

    private static void TotalWindowScrollbackIsBounded()
    {
        var states = new WindowStateRegistry();
        var session = NetworkSessionId.New();
        var first = BufferId.New();
        var second = BufferId.New();
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 8; index++)
        {
            states.AppendHistory(Message(session, first, $"old{index}", now.AddMinutes(-1)));
            states.AppendHistory(Message(session, second, $"new{index}", now));
        }

        Assert.True(states.EnforceTotalHistoryLimit(maximumEntries: 12, minimumEntriesPerWindow: 4));
        var firstHistory = states.HistorySnapshot(first);
        Assert.Equal(12, firstHistory.Length + states.HistorySnapshot(second).Length);
        Assert.Equal(4, firstHistory.Length);
        Assert.True(firstHistory[0].Text.Contains("old4", StringComparison.Ordinal));
        Assert.False(states.EnforceTotalHistoryLimit(maximumEntries: 12, minimumEntriesPerWindow: 4));
    }

    private static void AutomaticQueriesAreBounded()
    {
        var state = new NetworkSessionState(NetworkSessionId.New(), "test", IrcCaseMapping.Rfc1459);
        var processor = new IrcSessionProcessor(state, "me");
        SessionEvent? final = null;
        for (var index = 0; index <= 100; index++)
        {
            final = processor.Process(IrcMessageParser.Parse(
                $":bot{index}!user@host PRIVMSG me :flood")).Single();
        }

        Assert.Equal(100, state.Buffers.Count(buffer => buffer.Kind == BufferKind.Query));
        Assert.Equal(state.StatusBuffer.Id, final!.BufferId);
    }

    private static SessionEvent Message(NetworkSessionId session, BufferId buffer, string nick, DateTimeOffset now) =>
        new(session, buffer, SessionEventKind.Message, $"<{nick}> flood", now,
            new Dictionary<string, string?>
            {
                ["nick"] = nick,
                ["username"] = "user",
                ["host"] = "host"
            });

    private sealed class RecordingTransport : IIrcTransport
    {
        public string RemoteDescription => "test";
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => new(0);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask CloseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingTransport : IIrcTransport
    {
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string RemoteDescription => "test";
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => new(0);
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            WriteStarted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
        public ValueTask CloseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
