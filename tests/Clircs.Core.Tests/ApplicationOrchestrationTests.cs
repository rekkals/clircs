using Clircs.ConsoleClient;
using Clircs.Identity;
using Clircs.Networking;
using Clircs.Protocol;
using Clircs.Sessions;
using Clircs.Transport;

namespace Clircs.Core.Tests;

internal static class ApplicationOrchestrationTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("application event delivery is serialized", SerializesApplicationEventsAsync);
        suite.Add("session event dispatch stages preserve the architectural order", SessionEventStagesPreserveOrder);
        suite.Add("completed event delivery rejects later events", RejectsEventsAfterCompletion);
        suite.Add("inbound event delivery does not block a producer during a large burst", InboundEventsDoNotBlockProducerAsync);
        suite.Add("inbound event delivery reports its emergency capacity boundary", InboundEventCapacityIsExplicitAsync);
        suite.Add("session shutdown waits for owned background work", WaitsForSessionWorkAsync);
        suite.Add("session work failures are reported", ReportsSessionWorkFailuresAsync);
        suite.Add("session shutdown cancels owned background work", CancelsSessionWorkAsync);
        suite.Add("session work never runs its synchronous prefix on the caller", SessionWorkStartsOffCallerThreadAsync);
        suite.Add("join attempts follow negotiated IRC case mapping", JoinAttemptsUseIrcCaseMapping);
        suite.Add("denied and disconnected joins discard cycle state", JoinFailuresDiscardCycleState);
        suite.Add("forwarded joins transfer cycle and return state", ForwardedJoinsTransferState);
        suite.Add("notify monitor ownership can restart after completion", NotifyMonitorCanRestart);
        suite.Add("notify refresh operations are serialized", SerializesNotifyRefreshAsync);
        suite.Add("failed ISON sends do not corrupt later reply correlation", FailedIsonDoesNotRemainPending);
        suite.Add("live session registry isolates route and profile ownership", LiveSessionRegistryIsolatesMetadataAsync);
        suite.Add("live session registry owns reconnect lifecycle", LiveSessionRegistryOwnsReconnectAsync);
        suite.Add("window context resolves active and explicit buffers", WindowContextResolvesBuffersAsync);
        suite.Add("client preferences own application-wide defaults", ClientPreferencesOwnDefaults);
        suite.Add("services notices identify their bracketed channel", ServicesNoticesIdentifyChannel);
    }

    private static void SessionEventStagesPreserveOrder()
    {
        Assert.True(ClientApplication.SessionEventDispatchStages.SequenceEqual(
        [
            SessionEventDispatchStage.AdmissionAndAwayState,
            SessionEventDispatchStage.ProtectionAndDcc,
            SessionEventDispatchStage.OutputRouting,
            SessionEventDispatchStage.HistoryStorage,
            SessionEventDispatchStage.EventDelivery,
            SessionEventDispatchStage.WindowCompletion
        ]));
    }

    private static void ClientPreferencesOwnDefaults()
    {
        var preferences = new ClientPreferences("slakker", "slakker", @"C:\downloads");

        Assert.Equal("slakker", preferences.Nickname);
        Assert.Equal("slakker_", preferences.AlternateNickname);
        Assert.Equal("slakker", preferences.Username);
        Assert.Equal("clircs user", preferences.RealName);
        Assert.Equal("away", preferences.AwayMessage);
        Assert.Equal(HostmaskVisibility.UserHost, preferences.JoinHostmasks);
        Assert.True(preferences.HighlightNickname);
        Assert.True(preferences.CloneDetection);
        Assert.True(preferences.NetworkReconnect);
        Assert.True(preferences.KillReconnect);
        Assert.Equal(DccPortRange.Random, preferences.DccPorts);
        Assert.Equal(@"C:\downloads", preferences.DccDownloads);
        Assert.Equal(BanmaskStyle.Host, preferences.BanmaskStyle);
    }

    private static void ServicesNoticesIdentifyChannel()
    {
        Assert.Equal("#clircs", ClientApplication.BracketedChannelNoticeTarget("[#clircs] Welcome")!);
        Assert.Equal("##chat", ClientApplication.BracketedChannelNoticeTarget("[##chat] Channel rules")!);
        Assert.True(ClientApplication.BracketedChannelNoticeTarget("Ordinary private notice") is null);
        Assert.True(ClientApplication.BracketedChannelNoticeTarget("[] empty") is null);
    }

    private static async ValueTask SerializesApplicationEventsAsync()
    {
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var delivered = new List<int>();
        var activeHandlers = 0;
        var maximumActiveHandlers = 0;
        var dispatcher = new SerializedEventDispatcher<int>(item =>
        {
            var active = Interlocked.Increment(ref activeHandlers);
            maximumActiveHandlers = Math.Max(maximumActiveHandlers, active);
            if (item == 1)
            {
                firstEntered.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(5));
            }
            delivered.Add(item);
            Interlocked.Decrement(ref activeHandlers);
        });

        var first = Task.Run(() => dispatcher.Dispatch(1));
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5)), "The first event did not begin delivery.");
        var second = Task.Run(() => dispatcher.Dispatch(2));
        await Task.Delay(50);

        Assert.False(second.IsCompleted, "A second event entered delivery before the first event finished.");
        releaseFirst.Set();
        await Task.WhenAll(first, second);

        Assert.Equal(1, maximumActiveHandlers);
        Assert.True(delivered.SequenceEqual([1, 2]), "Events were not delivered in arrival order.");
    }

    private static void RejectsEventsAfterCompletion()
    {
        var delivered = 0;
        var dispatcher = new SerializedEventDispatcher<int>(_ => delivered++);

        Assert.True(dispatcher.Dispatch(1));
        dispatcher.Complete();
        Assert.False(dispatcher.Dispatch(2));
        Assert.Equal(1, delivered);
    }

    private static async ValueTask InboundEventsDoNotBlockProducerAsync()
    {
        const int eventCount = 10_000;
        using var consumerEntered = new ManualResetEventSlim();
        using var releaseConsumer = new ManualResetEventSlim();
        var delivered = new List<int>(eventCount);
        var batchSizes = new List<int>();
        var failures = new List<Exception>();
        await using var pump = new InboundSessionEventPump<int>(items =>
        {
            batchSizes.Add(items.Count);
            if (items[0] == 0)
            {
                consumerEntered.Set();
                releaseConsumer.Wait(TimeSpan.FromSeconds(5));
            }
            delivered.AddRange(items);
        }, failures.Add);

        Assert.Equal(ResourceQueueWriteResult.Accepted, pump.Enqueue(0));
        Assert.True(consumerEntered.Wait(TimeSpan.FromSeconds(5)), "The event consumer did not start.");
        for (var index = 1; index < eventCount; index++)
        {
            Assert.Equal(ResourceQueueWriteResult.Accepted, pump.Enqueue(index));
        }

        Assert.Equal(0, delivered.Count);
        releaseConsumer.Set();
        await pump.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, failures.Count);
        Assert.Equal(eventCount, delivered.Count);
        Assert.True(delivered.SequenceEqual(Enumerable.Range(0, eventCount)),
            "The inbound event pump changed event order during the burst.");
        Assert.True(batchSizes.All(size => size <= InboundSessionEventPump<int>.MaximumBatchSize));
        Assert.True(batchSizes.Any(size => size > 1), "The queued burst was not delivered in batches.");
        await pump.CompleteAsync();
        Assert.Equal(ResourceQueueWriteResult.Completed, pump.Enqueue(eventCount));
    }

    private static async ValueTask InboundEventCapacityIsExplicitAsync()
    {
        using var consumerEntered = new ManualResetEventSlim();
        using var releaseConsumer = new ManualResetEventSlim();
        var delivered = new List<int>();
        await using var pump = new InboundSessionEventPump<int>(items =>
        {
            if (items[0] == 0)
            {
                consumerEntered.Set();
                releaseConsumer.Wait(TimeSpan.FromSeconds(5));
            }
            delivered.AddRange(items);
        }, _ => { }, maximumPendingItems: 3);

        Assert.Equal(ResourceQueueWriteResult.Accepted, pump.Enqueue(0));
        Assert.True(consumerEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(ResourceQueueWriteResult.Accepted, pump.Enqueue(1));
        Assert.Equal(ResourceQueueWriteResult.Accepted, pump.Enqueue(2));
        Assert.Equal(ResourceQueueWriteResult.Accepted, pump.Enqueue(3));
        Assert.Equal(ResourceQueueWriteResult.CapacityExceeded, pump.Enqueue(4));

        var drain = pump.DrainAsync();
        Assert.False(drain.IsCompleted, "A full inbound queue reported itself drained.");
        releaseConsumer.Set();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(delivered.SequenceEqual([0, 1, 2, 3]));
    }

    private static async ValueTask WaitsForSessionWorkAsync()
    {
        var tracker = new SessionWorkTracker();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new List<Exception>();

        Assert.True(tracker.TryStart("test operation", () => release.Task, (_, exception) => failures.Add(exception)));
        var shutdown = tracker.StopAndWaitAsync();

        Assert.False(shutdown.IsCompleted, "Shutdown completed while session work was still running.");
        Assert.False(tracker.TryStart("late operation", () => Task.CompletedTask, (_, _) => { }));
        release.SetResult();
        await shutdown;
        Assert.Equal(0, failures.Count);
    }

    private static async ValueTask ReportsSessionWorkFailuresAsync()
    {
        var tracker = new SessionWorkTracker();
        var reported = new TaskCompletionSource<(string Operation, Exception Exception)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(tracker.TryStart(
            "failing operation",
            () => Task.FromException(new InvalidOperationException("failure")),
            (operation, exception) => reported.TrySetResult((operation, exception))));

        var failure = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await tracker.StopAndWaitAsync();

        Assert.Equal("failing operation", failure.Operation);
        Assert.True(failure.Exception is InvalidOperationException);
    }

    private static async ValueTask CancelsSessionWorkAsync()
    {
        using var tracker = new SessionWorkTracker();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(tracker.TryStart("cancellable operation", async () =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, tracker.Token); }
            catch (OperationCanceledException) when (tracker.Token.IsCancellationRequested)
            {
                cancelled.TrySetResult();
                throw;
            }
        }, (_, _) => { }));

        await tracker.StopAndWaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(tracker.Token.IsCancellationRequested);
    }

    private static async ValueTask SessionWorkStartsOffCallerThreadAsync()
    {
        using var tracker = new SessionWorkTracker();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var startCall = Task.Run(() => tracker.TryStart("blocking prefix", () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return Task.CompletedTask;
        }, (_, _) => { }));

        Assert.True(await startCall.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        release.Set();
        await tracker.StopAndWaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static void JoinAttemptsUseIrcCaseMapping()
    {
        var registry = new JoinAttemptRegistry(new IrcNameComparer(IrcCaseMapping.Rfc1459));
        var destination = BufferId.New();
        registry.RecordStart("#[room]", 42, overwrite: true);
        Assert.True(registry.IsPending("#{room}"));
        registry.RecordReturnRoute("#[room]", destination);
        registry.MarkCycle("#[room]");

        Assert.True(registry.TryTakeStart("#{room}", out var timestamp));
        Assert.Equal(42L, timestamp);
        Assert.True(registry.Complete("#{room}", denied: false, out var routed));
        Assert.Equal(destination, routed);
        Assert.True(registry.IsCyclePending("#{room}"));
    }

    private static void JoinFailuresDiscardCycleState()
    {
        var registry = new JoinAttemptRegistry(new IrcNameComparer(IrcCaseMapping.Ascii));
        registry.MarkCycle("#one");
        registry.Complete("#one", denied: true, out _);
        Assert.False(registry.IsCyclePending("#one"));

        registry.MarkCycle("#two");
        registry.RecordStart("#two", 10, overwrite: true);
        registry.Reset();
        Assert.False(registry.IsCyclePending("#two"));
        Assert.False(registry.TryTakeStart("#two", out _));
    }

    private static void ForwardedJoinsTransferState()
    {
        var registry = new JoinAttemptRegistry(new IrcNameComparer(IrcCaseMapping.Ascii));
        var destination = BufferId.New();
        registry.RecordStart("#old", 71, overwrite: true);
        registry.RecordReturnRoute("#old", destination);
        registry.MarkCycle("#old");
        registry.Forward("#old", "#new");

        Assert.False(registry.IsCyclePending("#old"));
        Assert.True(registry.IsCyclePending("#new"));
        Assert.True(registry.TryTakeStart("#new", out var timestamp));
        Assert.Equal(71L, timestamp);
        Assert.True(registry.Complete("#new", denied: false, out var routed));
        Assert.Equal(destination, routed);
    }

    private static void NotifyMonitorCanRestart()
    {
        var coordinator = new NotifyCoordinator(StringComparer.OrdinalIgnoreCase);
        Assert.True(coordinator.TryStartMonitor(CancellationToken.None, out var first));
        Assert.False(coordinator.TryStartMonitor(CancellationToken.None, out _));
        coordinator.MonitorCompleted(first);
        Assert.True(coordinator.TryStartMonitor(CancellationToken.None, out var second));
        coordinator.MonitorCompleted(second);
    }

    private static async ValueTask SerializesNotifyRefreshAsync()
    {
        var coordinator = new NotifyCoordinator(StringComparer.OrdinalIgnoreCase);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;

        var first = coordinator.RefreshAsync(async _ =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            entered.TrySetResult();
            await release.Task;
            Interlocked.Decrement(ref active);
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.RefreshAsync(_ =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            Interlocked.Decrement(ref active);
            return Task.CompletedTask;
        }, CancellationToken.None);

        await Task.Delay(50);
        Assert.False(second.IsCompleted, "A second notify refresh entered before the first completed.");
        release.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, maximum);
    }

    private static void FailedIsonDoesNotRemainPending()
    {
        var coordinator = new NotifyCoordinator(StringComparer.OrdinalIgnoreCase);
        coordinator.ApplyMonitor(online: true, ["Alice"]);
        var failed = new[] { "Alice" };
        coordinator.EnqueueIson(failed);
        coordinator.RemoveFailedIson(failed);

        var changes = coordinator.ApplyIson(["Bob"], [], StringComparer.OrdinalIgnoreCase);
        var online = coordinator.OnlineSnapshot(StringComparer.OrdinalIgnoreCase);
        Assert.True(online.Contains("Alice"));
        Assert.Equal(0, changes.Offline.Length);
    }

    private static async ValueTask LiveSessionRegistryIsolatesMetadataAsync()
    {
        var identity = new IrcIdentity(["test"], "test", "Test User");
        var firstRoute = new IrcConnectionOptions(new IrcEndpoint("first.example", 6667, false), identity);
        var secondRoute = new IrcConnectionOptions(new IrcEndpoint("second.example", 6697, true), identity);
        var firstProfile = NetworkProfileId.New();
        var secondProfile = NetworkProfileId.New();
        var replacementProfile = NetworkProfileId.New();
        await using var first = new IrcNetworkSession("first", firstRoute, new TcpIrcTransportFactory());
        await using var second = new IrcNetworkSession("second", secondRoute, new TcpIrcTransportFactory());
        var registry = new LiveNetworkSessionRegistry();

        registry.Add(first, firstRoute, firstProfile, CancellationToken.None);
        registry.Add(second, secondRoute, secondProfile, CancellationToken.None);
        registry.AssociateProfile(first.State.Id, replacementProfile);

        Assert.Equal(firstRoute, registry.ConnectionRoute(first.State.Id, secondRoute));
        Assert.Equal(secondRoute, registry.ConnectionRoute(second.State.Id, firstRoute));
        Assert.True(registry.ProfileId(first.State.Id) == replacementProfile);
        Assert.True(registry.ProfileId(second.State.Id) == secondProfile);
        Assert.True(registry.Find(first.State.Id) == first);
        Assert.True(registry.Remove(first.State.Id)?.Session == first);
        Assert.True(registry.Find(first.State.Id) is null);
        Assert.True(registry.Find(second.State.Id) == second);
    }

    private static async ValueTask LiveSessionRegistryOwnsReconnectAsync()
    {
        var identity = new IrcIdentity(["test"], "test", "Test User");
        var route = new IrcConnectionOptions(new IrcEndpoint("test.example", 6667, false), identity);
        await using var session = new IrcNetworkSession("test", route, new TcpIrcTransportFactory());
        var registry = new LiveNetworkSessionRegistry();
        registry.Add(session, route, profileId: null, CancellationToken.None);

        Assert.True(registry.TryBeginReconnect(session.State.Id, CancellationToken.None, out var reconnect));
        Assert.True(reconnect is not null);
        Assert.True(registry.IsReconnecting(session.State.Id));
        Assert.False(registry.TryBeginReconnect(session.State.Id, CancellationToken.None, out _));
        Assert.True(registry.CancelReconnect(session.State.Id));
        Assert.True(reconnect!.IsCancellationRequested);
        Assert.True(registry.CompleteReconnect(session.State.Id, reconnect));
        Assert.False(registry.IsReconnecting(session.State.Id));
        reconnect.Dispose();
    }

    private static async ValueTask WindowContextResolvesBuffersAsync()
    {
        var identity = new IrcIdentity(["test"], "test", "Test User");
        var firstRoute = new IrcConnectionOptions(new IrcEndpoint("first.example", 6667, false), identity);
        var secondRoute = new IrcConnectionOptions(new IrcEndpoint("second.example", 6697, true), identity);
        await using var first = new IrcNetworkSession("first", firstRoute, new TcpIrcTransportFactory());
        await using var second = new IrcNetworkSession("second", secondRoute, new TcpIrcTransportFactory());
        var sessions = new LiveNetworkSessionRegistry();
        sessions.Add(first, firstRoute, profileId: null, CancellationToken.None);
        sessions.Add(second, secondRoute, profileId: null, CancellationToken.None);
        var windows = new WindowStateRegistry();
        windows.Activate(first.State.Id, first.State.StatusBuffer.Id);

        Assert.True(windows.ResolveSession(sessions) == first);
        Assert.True(windows.ResolveBuffer(sessions) == first.State.StatusBuffer);
        Assert.True(windows.ResolveSession(sessions, second.State.Id) == second);
        Assert.True(windows.ResolveBuffer(
            sessions,
            second.State.Id,
            second.State.StatusBuffer.Id) == second.State.StatusBuffer);

        windows.ClearActive();
        Assert.True(windows.ResolveActive(sessions) == (null, null));
    }
}
