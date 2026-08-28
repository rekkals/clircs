using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Clircs.Commands;
using Clircs.Dcc;
using Clircs.Identity;
using Clircs.Networking;
using Clircs.Protocol;
using Clircs.Sessions;
using Clircs.State;
using Clircs.Transport;
using Clircs.Users;

namespace Clircs.ConsoleClient;

// Owns session connection, disconnection, reconnection, synchronization, and background work.
internal sealed partial class ClientApplication
{
    private Task StartSessionAsync(
        IrcConnectionOptions options,
        CancellationToken cancellationToken,
        string? requestedDisplayName = null,
        NetworkProfileId? profileId = null,
        int? preferredStatusNumber = null)
    {
        var displayName = UniqueDisplayName(requestedDisplayName ?? options.Endpoint.Host);
        var session = new IrcNetworkSession(displayName, options, new TcpIrcTransportFactory(_tlsCertificatePolicy), () => _quotes.Next(220));
        lock (_windowTransactionGate)
        {
            _liveSessions.Add(session, options, profileId, _lifetime.Token);

            if (preferredStatusNumber is not { } number ||
                !_windowStates.TryAssignPreferredNumber(session.State.StatusBuffer.Id, number))
            {
                _windowStates.AssignNumber(session.State.StatusBuffer.Id);
            }
            _windowStates.Activate(session.State.Id, session.State.StatusBuffer.Id);
            _windowStates.ReplaceHistory(
                session.State.StatusBuffer.Id,
                [StartupEvent(session, session.State.StatusBuffer)]);
        }

        session.EventRaised += QueueInboundSessionEvent;
        session.StateChanged += OnSessionStateChanged;
        session.RegistrationCompleted += OnSessionRegistrationCompleted;
        session.SynchronizationCompleted += OnSessionSynchronizationCompleted;
        session.Disconnected += OnSessionDisconnected;
        session.ServerFeaturesUpdated += OnSessionServerFeaturesUpdated;
        session.ChannelSynchronized += OnChannelSynchronized;
        session.ChannelNamesSynchronized += OnChannelNamesSynchronized;
        session.ChannelMemberJoined += OnChannelMemberJoined;
        session.IsonReplyReceived += OnIsonReplyReceived;
        session.MonitorStatusReceived += OnMonitorStatusReceived;
        session.WireLineTransferred += OnWireLineTransferred;
        RedrawActiveBuffer(session, session.State.StatusBuffer, pendingEvent: null);
        StartSessionWork(
            session,
            "initial connection",
            () => ConnectInitialSessionAsync(session, cancellationToken));
        return Task.CompletedTask;
    }

    private async Task ConnectInitialSessionAsync(IrcNetworkSession session, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(ConnectionAttemptTimeout);
        try
        {
            await session.ConnectAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedConnectionFailure(exception))
        {
            // Connection and registration failures are already rendered by the session.
        }
    }

    private static bool IsExpectedConnectionFailure(Exception exception) => exception is
        IOException or
        SocketException or
        TimeoutException or
        System.Security.Authentication.AuthenticationException or
        IrcProtocolException or
        InvalidOperationException;

    private async Task CloseSessionAsync(IrcNetworkSession session, string reason)
    {
        await CloseDccForSessionAsync(session.State.Id);
        InvalidateDccRequests(session, "The network session closed.");
        session.EventRaised -= QueueInboundSessionEvent;
        session.StateChanged -= OnSessionStateChanged;
        session.RegistrationCompleted -= OnSessionRegistrationCompleted;
        session.SynchronizationCompleted -= OnSessionSynchronizationCompleted;
        session.Disconnected -= OnSessionDisconnected;
        session.ServerFeaturesUpdated -= OnSessionServerFeaturesUpdated;
        session.ChannelSynchronized -= OnChannelSynchronized;
        session.ChannelNamesSynchronized -= OnChannelNamesSynchronized;
        session.ChannelMemberJoined -= OnChannelMemberJoined;
        session.IsonReplyReceived -= OnIsonReplyReceived;
        session.MonitorStatusReceived -= OnMonitorStatusReceived;
        session.WireLineTransferred -= OnWireLineTransferred;
        await _inboundSessionEvents.DrainAsync();
        var closingRuntime = _liveSessions.Runtime(session);
        CancelReconnectUnsafe(session.State.Id);
        closingRuntime?.Notify.Stop();
        foreach (var timedBan in _sessionTransientState.TimedBansFor(session.State.Id))
            timedBan.Cancel();

        Exception? disconnectFailure = null;
        try
        {
            await session.DisconnectAsync(reason);
        }
        catch (Exception exception)
        {
            disconnectFailure = exception;
        }
        finally
        {
            if (closingRuntime is not null)
            {
                await closingRuntime.Work.StopAndWaitAsync();
                closingRuntime.Work.Dispose();
            }
        }

        BufferId[] closedBufferIds;
        lock (_windowTransactionGate)
        {
            var closingActiveSession = _windowStates.IsActiveSession(session.State.Id);
            if (closingActiveSession)
            {
                SelectAfterSessionCloseUnsafe(session);
            }
            _liveSessions.Remove(session.State.Id);
            closedBufferIds = session.State.Buffers.Select(buffer => buffer.Id).ToArray();
            foreach (var bufferId in closedBufferIds)
            {
                _windowStates.Remove(bufferId);
            }

            if (closingActiveSession && _windowStates.IsActiveSession(session.State.Id))
            {
                SelectFallbackSessionUnsafe();
            }
        }
        _userAndChannelPolicy.ClearSession(session.State.Id);
        foreach (var bufferId in closedBufferIds) _dcc.ClearChatBuffer(bufferId);
        _outputRouting.ClearSession(session.State.Id);
        _inboundResourceCircuitBreaker.Reset(session.State.Id);
        _sessionTransientState.ClearSession(session.State.Id);
        _channelSynchronization.ClearSession(session.State.Id);
        foreach (var bufferId in closedBufferIds) _presenter.ForgetInputHistory(bufferId);

        await session.DisposeAsync();
        if (disconnectFailure is not null)
        {
            LogUnexpectedSessionWorkFailure(session, "session disconnect", disconnectFailure);
        }
    }

    private async Task CloseDccForSessionAsync(NetworkSessionId sessionId)
    {
        var requests = _dcc.Requests.Snapshot()
            .Where(request => request.NetworkSessionId == sessionId)
            .ToArray();
        foreach (var request in requests)
        {
            CancelDccExpiration(request.Id);
            CancelPendingDccResume(request.Id);
            CancelDccChatConnection(request.Id);
            await StopDccChatListenerAsync(request.Id);
            if (request.Offer.Type == DccRequestType.Send)
            {
                CancelDccTransfer(request.Id);
                if (request.Offer.IsPassiveRequest && request.State == DccRequestState.Pending)
                    CleanupUnstartedPassiveSend(request.Id);
            }
            if (request.Offer.Type == DccRequestType.Chat && request.State == DccRequestState.Connected)
            {
                await EndDccChatAsync(request.Id, DccRequestState.Closed,
                    $"DCC {DccProtocolName(request.Offer)} closed with the network session");
                continue;
            }
            if (!DccRequestRegistry.IsTerminal(request.State) &&
                _dcc.Requests.TryTransition(request.Id, DccRequestState.Invalidated,
                    "The network session closed", out var invalidated))
            {
                var peer = invalidated!.Direction == DccRequestDirection.Incoming ? "from" : "to";
                PublishDccState(invalidated,
                    $"DCC {DccProtocolName(request.Offer)} #{request.Id} {peer} {request.Sender} " +
                    "ended because the network session closed");
            }
        }
        await _dcc.AwaitTasksAsync(requests.Select(request => request.Id));
    }

    private async Task CloseAllSessionsAsync(string reason)
    {
        foreach (var session in SessionsSnapshot())
        {
            await CloseSessionAsync(session, reason);
        }
    }

    private void StartSessionWork(IrcNetworkSession session, string operation, Func<Task> start)
    {
        var runtime = _liveSessions.Runtime(session);
        runtime?.Work.TryStart(
            operation,
            start,
            (name, exception) => LogUnexpectedSessionWorkFailure(session, name, exception));
    }

    private void LogUnexpectedApplicationWorkFailure(string operation, Exception exception)
    {
        try
        {
            var directory = Path.Combine(_dataDirectory, "logs");
            Directory.CreateDirectory(directory);
            var entry =
                $"[{DateTimeOffset.Now:O}] Application work failed: {operation}{Environment.NewLine}" +
                $"{exception}{Environment.NewLine}{Environment.NewLine}";
            lock (_errorLogGate)
            {
                File.AppendAllText(
                    Path.Combine(directory, "application-errors.log"),
                    entry,
                    new UTF8Encoding(false));
            }
        }
        catch (Exception logException) when (
            logException is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }

        DisplayCommandResult(CommandResult.Failure(
            $"Background operation failed unexpectedly: {operation}; details were written to the clircs error log"));
    }

    private void LogUnexpectedSessionWorkFailure(
        IrcNetworkSession session,
        string operation,
        Exception exception)
    {
        try
        {
            var directory = Path.Combine(_dataDirectory, "logs");
            Directory.CreateDirectory(directory);
            var entry =
                $"[{DateTimeOffset.Now:O}] Session work failed: {operation}{Environment.NewLine}" +
                $"Network session: {session.State.Id}{Environment.NewLine}" +
                $"{exception}{Environment.NewLine}{Environment.NewLine}";
            lock (_errorLogGate)
            {
                File.AppendAllText(
                    Path.Combine(directory, "application-errors.log"),
                    entry,
                    new UTF8Encoding(false));
            }
        }
        catch (Exception logException) when (
            logException is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The operational-output audit will consolidate error-log ownership.
        }

        if (FindSession(session.State.Id) is not null)
        {
            PublishStatus(
                session,
                SessionEventKind.Error,
                $"Background operation failed unexpectedly: {operation}; details were written to the clircs error log");
        }
    }

    private void OnSessionRegistrationCompleted(IrcNetworkSession session)
    {
        _inboundResourceCircuitBreaker.Reset(session.State.Id);
        StartNotifyMonitor(session);
        StartSessionWork(
            session,
            "visible-host discovery",
            () => DiscoverVisibleHostAsync(session));
    }

    private async Task DiscoverVisibleHostAsync(IrcNetworkSession session)
    {
        try
        {
            await session.SendAsync(
                "USERHOST",
                [session.CurrentNickname],
                IrcOutboundPriority.Automation,
                SessionWorkToken(session));
        }
        catch (OperationCanceledException) when (SessionWorkToken(session).IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // Automatic DCC address discovery is best-effort. /set dcc.address remains available.
        }
    }

    private void OnSessionSynchronizationCompleted(IrcNetworkSession session)
    {
        _sessionTransientState.ResetAutojoin(session.State.Id);
        if (_windowStates.IsActiveSession(session.State.Id))
        {
            RedrawActiveBuffer();
        }
        StartSessionWork(
            session,
            "configured user modes",
            () => ApplyConfiguredUserModesAsync(session));
        StartSessionWork(
            session,
            "configured autojoin",
            () => RunConfiguredAutojoinAsync(session));
    }

    private void OnSessionDisconnected(IrcNetworkSession session, SessionDisconnectInfo info)
    {
        InvalidateDccRequests(session, "The IRC connection closed.");
        _sessionTransientState.ResetAutojoin(session.State.Id);
        _channelSynchronization.ClearSession(session.State.Id);
        if (_liveSessions.Runtime(session) is { } runtime)
        {
            runtime.Notify.Stop();
            runtime.Joins.Reset();
        }

        if (info.AnnounceToBuffers)
        {
            PublishDisconnectedToSessionBuffers(session);
        }

        if (info.Kind == SessionDisconnectKind.Intentional || !info.RetryRecommended)
        {
            CancelReconnect(session.State.Id);
            if (!info.RetryRecommended && info.Kind != SessionDisconnectKind.Intentional &&
                !info.Message.StartsWith("Authentication failed:", StringComparison.OrdinalIgnoreCase))
            {
                PublishStatus(session, SessionEventKind.Error,
                    "Automatic reconnect was not started because the server explicitly refused this connection.");
            }
            return;
        }

        var enabled = info.Kind == SessionDisconnectKind.Killed ? _preferences.KillReconnect : _preferences.NetworkReconnect;
        if (!enabled)
        {
            PublishStatus(session, SessionEventKind.Status,
                info.Kind == SessionDisconnectKind.Killed
                    ? "kill.reconnect is off; this network session will remain offline."
                    : "network.reconnect is off; this network session will remain offline.");
            return;
        }

        ScheduleReconnect(session, info.Kind);
    }

    private void PublishDisconnectedToSessionBuffers(IrcNetworkSession session)
    {
        var timestamp = DateTimeOffset.Now;
        foreach (var buffer in session.State.Buffers.Where(buffer =>
                     buffer.Kind is not (BufferKind.Status or BufferKind.DccChat or BufferKind.DccTransfer)))
        {
            OnSessionEvent(new SessionEvent(
                session.State.Id,
                buffer.Id,
                SessionEventKind.Status,
                "Disconnected",
                timestamp,
                new Dictionary<string, string?>
                {
                    ["clientResult"] = "true",
                    ["suppressActivity"] = "true",
                    ["event"] = "disconnect"
                }));
        }
    }

    private void ScheduleReconnect(IrcNetworkSession session, SessionDisconnectKind kind)
    {
        if (!_liveSessions.TryBeginReconnect(session.State.Id, _lifetime.Token, out var cancellation))
        {
            return;
        }
        StartSessionWork(
            session,
            "automatic reconnect loop",
            () => RunReconnectLoopAsync(session, kind, cancellation!));
    }

    private async Task RunReconnectLoopAsync(
        IrcNetworkSession session,
        SessionDisconnectKind kind,
        CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        var profile = ProfileFor(session);
        var policy = profile?.Reconnect ?? ReconnectPolicy.Default;
        try
        {
            var route = ConnectionRouteFor(session);
            var loop = new AutomaticReconnectLoop(
                policy,
                async (attempt, attemptToken) =>
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(attemptToken);
                    timeout.CancelAfter(ConnectionAttemptTimeout);
                    try
                    {
                        await session.ReconnectAsync(route, timeout.Token);
                    }
                    catch (OperationCanceledException) when (!attemptToken.IsCancellationRequested)
                    {
                        throw new TimeoutException(ReconnectTimeoutMessage(attempt, ConnectionAttemptTimeout));
                    }
                    PublishStatus(session, SessionEventKind.Status,
                        $"Reconnect socket opened to {route.Endpoint}; waiting for IRC registration.");
                });
            var outcome = await loop.RunAsync(
                (attempt, attempts, delay) => PublishStatus(session, SessionEventKind.Status,
                    $"{(kind == SessionDisconnectKind.Killed ? "Kill reconnect" : "Reconnect")} attempt {attempt}/{attempts} in {FormatDuration(delay)}. Use /reconnect cancel to stop."),
                (attempt, exception) => PublishStatus(session, SessionEventKind.Error,
                    exception is TimeoutException ? exception.Message : ReconnectFailureMessage(attempt, exception)),
                token);

            if (outcome == AutomaticReconnectOutcome.NoAttemptsConfigured)
            {
                PublishStatus(session, SessionEventKind.Status,
                    "This network profile allows no automatic reconnect attempts. Use /reconnect to try manually.");
            }
            else if (outcome == AutomaticReconnectOutcome.Exhausted)
            {
                PublishStatus(session, SessionEventKind.Error,
                    $"Reconnect stopped after {policy.MaximumAttempts} unsuccessful attempts. Use /reconnect to try again.");
            }
        }
        finally
        {
            _liveSessions.CompleteReconnect(session.State.Id, cancellation);
            cancellation.Dispose();
        }
    }

    internal static string ReconnectTimeoutMessage(int attempt, TimeSpan timeout) =>
        $"Reconnect attempt {attempt} timed out after {FormatDuration(timeout)} while connecting or waiting for IRC registration.";

    internal static string ReconnectFailureMessage(int attempt, Exception exception) =>
        $"Reconnect attempt {attempt} failed: {NormalizeConnectionFailure(exception)}";

    private static string NormalizeConnectionFailure(Exception exception)
    {
        var message = TerminalTextSanitizer.Sanitize(exception.Message);
        return message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase)
            ? "Unknown host."
            : message;
    }

    private IrcConnectionOptions ConnectionRouteFor(IrcNetworkSession session)
    {
        var route = _liveSessions.ConnectionRoute(session.State.Id, session.Options);
        var profile = ProfileFor(session);
        return profile is null ? route : ApplyProfileSasl(profile, route);
    }

    private bool CancelReconnect(NetworkSessionId sessionId)
        => CancelReconnectUnsafe(sessionId);

    private bool CancelReconnectUnsafe(NetworkSessionId sessionId)
    {
        return _liveSessions.CancelReconnect(sessionId);
    }

    private async Task ApplyConfiguredUserModesAsync(IrcNetworkSession session)
    {
        try
        {
            var profile = ProfileFor(session);
            if (profile is null)
            {
                var matches = _profileStore.Entries.Where(candidate => candidate.Endpoints.Any(endpoint =>
                    endpoint.Port == session.Options.Endpoint.Port && endpoint.UseTls == session.Options.Endpoint.UseTls &&
                    endpoint.Host.Equals(session.Options.Endpoint.Host, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (matches.Length == 1)
                {
                    profile = matches[0];
                    AssociateProfile(session, profile);
                }
            }
            var modes = profile?.UserModes ?? "+i";
            if (modes.Length > 0)
            {
                await session.SendAsync("MODE", [session.CurrentNickname, modes], IrcOutboundPriority.Automation, SessionWorkToken(session));
            }
        }
        catch (OperationCanceledException) when (SessionWorkToken(session).IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            PublishStatus(session, SessionEventKind.Error, $"Could not apply configured user modes: {exception.Message}");
        }
    }

    private void StartNotifyMonitor(IrcNetworkSession session)
    {
        var runtime = RuntimeFor(session);
        if (runtime is null || !runtime.Notify.TryStartMonitor(runtime.Work.Token, out var cancellation)) return;
        StartSessionWork(
            session,
            "notify monitor",
            () => RunNotifyMonitorAsync(session, runtime.Notify, cancellation));
    }

    private async Task RunNotifyMonitorAsync(
        IrcNetworkSession session,
        NotifyCoordinator coordinator,
        CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var profile = ProfileFor(session);
                if (profile is not null && profile.NotifyNicknames.Count > 0 && session.ConnectionState == IrcConnectionState.Online)
                    await coordinator.RefreshAsync(
                        token => RequestNotifyStatusAsync(session, profile, coordinator, token).AsTask(),
                        cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            coordinator.MonitorCompleted(cancellation);
        }
    }

    private async ValueTask RequestNotifyStatusAsync(
        IrcNetworkSession session,
        NetworkProfile profile,
        NotifyCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        if (session.Features.Supports("MONITOR"))
        {
            await SynchronizeMonitorAsync(session, profile, coordinator, cancellationToken);
            return;
        }

        var batches = new List<string[]>();
        var current = new List<string>();
        var length = 5;
        foreach (var nickname in profile.NotifyNicknames)
        {
            if (current.Count > 0 && length + nickname.Length + 1 > 350)
            {
                batches.Add(current.ToArray());
                current.Clear();
                length = 5;
            }
            current.Add(nickname);
            length += nickname.Length + 1;
        }
        if (current.Count > 0) batches.Add(current.ToArray());
        foreach (var batch in batches)
        {
            coordinator.EnqueueIson(batch);
            try
            {
                await session.SendAsync("ISON", batch, IrcOutboundPriority.Automation, cancellationToken);
            }
            catch
            {
                coordinator.RemoveFailedIson(batch);
                throw;
            }
        }
    }

    private async ValueTask SynchronizeMonitorAsync(
        IrcNetworkSession session,
        NetworkProfile profile,
        NotifyCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        var comparer = IrcNameComparerFor(session);
        var desired = new HashSet<string>(profile.NotifyNicknames, comparer);
        var existing = coordinator.MonitorSubscriptionsSnapshot(comparer);

        var removed = existing.Except(desired, comparer).ToArray();
        var added = desired.Except(existing, comparer).ToArray();
        foreach (var batch in MonitorBatches(removed))
            await session.SendAsync("MONITOR", ["-", string.Join(',', batch)], IrcOutboundPriority.Automation, cancellationToken);
        foreach (var batch in MonitorBatches(added))
            await session.SendAsync("MONITOR", ["+", string.Join(',', batch)], IrcOutboundPriority.Automation, cancellationToken);

        coordinator.SetMonitorSubscriptions(desired);
    }

    private static IEnumerable<string[]> MonitorBatches(IEnumerable<string> nicknames)
    {
        var current = new List<string>();
        var length = "MONITOR + ".Length;
        foreach (var nickname in nicknames)
        {
            if (current.Count > 0 && length + nickname.Length + 1 > 350)
            {
                yield return current.ToArray();
                current.Clear();
                length = "MONITOR + ".Length;
            }
            current.Add(nickname);
            length += nickname.Length + 1;
        }
        if (current.Count > 0) yield return current.ToArray();
    }

    private void OnIsonReplyReceived(IrcNetworkSession session, IReadOnlyList<string> reportedOnline)
    {
        var profile = ProfileFor(session);
        if (profile is null) return;
        var comparer = IrcNameComparerFor(session);
        var runtime = RuntimeFor(session);
        if (runtime is null) return;
        var changes = runtime.Notify.ApplyIson(profile.NotifyNicknames, reportedOnline, comparer);
        foreach (var nickname in changes.Online)
            PublishStatus(session, SessionEventKind.Notice, $"Notify: {nickname} is online");
        foreach (var nickname in changes.Offline)
            PublishStatus(session, SessionEventKind.Notice, $"Notify: {nickname} is offline");
    }

    private void OnMonitorStatusReceived(
        IrcNetworkSession session,
        bool online,
        IReadOnlyList<string> nicknames)
    {
        var runtime = RuntimeFor(session);
        if (runtime is null) return;
        var changed = runtime.Notify.ApplyMonitor(online, nicknames);

        foreach (var nickname in changed)
            PublishStatus(session, SessionEventKind.Notice,
                $"Notify: {nickname} is {(online ? "online" : "offline")}");
    }

    private void OnSessionServerFeaturesUpdated(IrcNetworkSession session)
    {
        ReindexLiveIrcNames(session);
        StartSessionWork(
            session,
            "advertised-network association",
            () => AssociateAdvertisedNetworkAsync(session));
        if (session.Features.Supports("MONITOR"))
        {
            StartSessionWork(
                session,
                "notify transport refresh",
                () => RefreshNotifyTransportAsync(session));
        }
    }

    private static IEqualityComparer<string> IrcNameComparerFor(IrcNetworkSession session) =>
        new IrcNameComparer(session.State.CaseMapping);

    private void ReindexLiveIrcNames(IrcNetworkSession session)
    {
        var runtime = RuntimeFor(session);
        if (runtime is null) return;
        var comparer = IrcNameComparerFor(session);
        runtime.Notify.Reindex(comparer);
        runtime.Joins.Reindex(comparer);
    }

    private async Task RefreshNotifyTransportAsync(IrcNetworkSession session)
    {
        var runtime = RuntimeFor(session);
        var profile = ProfileFor(session);
        if (runtime is null || profile is null || session.ConnectionState != IrcConnectionState.Online) return;
        await runtime.Notify.RefreshAsync(
            token => RequestNotifyStatusAsync(session, profile, runtime.Notify, token).AsTask(),
            runtime.Work.Token);
    }

    private void OnChannelSynchronized(IrcNetworkSession session, ChannelState channel)
    {
        var key = CloneChannelKey(session, channel.Name);
        var pending = _channelSynchronization.CloneScan(key);
        if (pending is not null)
        {
            pending.TrySetResult(true);
            return;
        }

        ReportJoinSynchronization(session, channel);
        StartSessionWork(
            session,
            "channel user policies",
            () => ApplyUserPoliciesAsync(session, channel, channel.Members));
        var reportClones = _preferences.CloneDetection &&
            _channelSynchronization.TryReportCloneSummary(key);
        if (reportClones) PublishCloneSummary(session, channel);
    }

    private void OnChannelNamesSynchronized(IrcNetworkSession session, ChannelState channel) =>
        StartSessionWork(
            session,
            "channel WHO synchronization",
            () => RequestChannelWhoAsync(session, channel));

    private async Task RequestChannelWhoAsync(IrcNetworkSession session, ChannelState channel)
    {
        Guid requestId = default;
        try
        {
            _channelSynchronization.BeginAutomaticWho(session.State.Id, channel.Name);
            requestId = session.BeginWhoRequest([channel.Name], automatic: true);
            await session.SendAsync("WHO", [channel.Name], IrcOutboundPriority.Automation, SessionWorkToken(session));
        }
        catch (OperationCanceledException) when (SessionWorkToken(session).IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            if (requestId != default) session.CancelWhoRequest(requestId);
            _channelSynchronization.CompleteAutomaticWho(session.State.Id, channel.Name);
            PublishStatus(session, SessionEventKind.Error, $"WHO synchronization failed for {channel.Name}: {exception.Message}");
        }
    }

    private async Task RefreshCloneDataAsync(
        IrcNetworkSession session,
        ChannelState channel,
        CancellationToken cancellationToken)
    {
        var key = CloneChannelKey(session, channel.Name);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _channelSynchronization.BeginCloneScan(key, completion);

        var requestId = session.BeginWhoRequest([channel.Name], automatic: true);
        try
        {
            await session.SendAsync("WHO", [channel.Name], IrcOutboundPriority.Interactive, cancellationToken);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        catch
        {
            session.CancelWhoRequest(requestId);
            throw;
        }
        finally
        {
            _channelSynchronization.CompleteCloneScan(key);
        }
    }

    private CommandResult ClonePresentation(ChannelState channel)
    {
        var groups = CloneGroups(channel);
        if (groups.Length == 0) return CommandResult.Success($"No clones found in {channel.Name}.");
        var rows = new List<IReadOnlyList<string>>();
        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            if (groupIndex > 0) rows.Add(["", "", ""]);
            rows.AddRange(groups[groupIndex].Members.Select(member => (IReadOnlyList<string>)new[]
            {
                member.Nickname,
                member.Username is null ? member.Host! : $"{member.Username}@{member.Host}",
                member.RealName ?? string.Empty
            }));
        }
        var users = groups.Sum(group => group.Members.Length);
        return CommandResult.Success(new PresentationBlock(
            "CLONES:",
            Table: new PresentationTable(["Nick", "Address", "Name"], rows, new HashSet<int> { 1 }),
            Summary: $"{users} {(users == 1 ? "user" : "users")} share " +
                     $"{groups.Length} {(groups.Length == 1 ? "host" : "hosts")}.",
            TitleHighlight: channel.Name));
    }

    private void PublishCloneSummary(IrcNetworkSession session, ChannelState channel)
    {
        var groups = CloneGroups(channel);
        if (groups.Length == 0) return;
        var users = groups.Sum(group => group.Members.Length);
        PublishChannelNotice(session, channel,
            $"Clone detection: {users} users share {groups.Length} " +
            $"{(groups.Length == 1 ? "host" : "hosts")}. Use /clones for details.");
    }

    private void PublishChannelNotice(IrcNetworkSession session, ChannelState channel, string text)
    {
        var buffer = session.State.GetOrCreateBuffer(BufferKind.Channel, channel.Name);
        OnSessionEvent(new SessionEvent(
            session.State.Id,
            buffer.Id,
            SessionEventKind.Notice,
            TerminalTextSanitizer.Sanitize(text),
            DateTimeOffset.Now,
            new Dictionary<string, string?> { ["cloneDetection"] = "true", ["channel"] = channel.Name }));
    }

    private static CloneGroup[] CloneGroups(ChannelState channel) => channel.Members
        .Where(member => !string.IsNullOrWhiteSpace(member.Host))
        .GroupBy(member => member.Host!, StringComparer.OrdinalIgnoreCase)
        .Where(group => group.Count() > 1)
        .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
        .Select(group => new CloneGroup(
            group.Key,
            group.OrderBy(member => member.Nickname, StringComparer.OrdinalIgnoreCase).ToArray()))
        .ToArray();

    private static (NetworkSessionId SessionId, string Channel) CloneChannelKey(
        IrcNetworkSession session,
        string channel) =>
        (session.State.Id, IrcCaseFold.Fold(channel, session.State.CaseMapping));

    private void OnChannelMemberJoined(IrcNetworkSession session, ChannelState channel, ChannelMemberState member)
    {
        if (new IrcNameComparer(session.State.CaseMapping).Equals(member.Nickname, session.CurrentNickname))
        {
            RecordJoinStart(session, channel.Name, overwrite: false);
        }
        if (channel.NamesSynchronized)
        {
            StartSessionWork(
                session,
                "joined-user policies",
                () => ApplyUserPoliciesAsync(session, channel, [member]));
        }
        if (_preferences.AnnounceUserInfoOnJoin && !new IrcNameComparer(session.State.CaseMapping).Equals(member.Nickname, session.CurrentNickname))
        {
            StartSessionWork(
                session,
                "joined-user information",
                () => AnnounceUserInfoAsync(session, channel, member));
        }
        if (_preferences.CloneDetection && !string.IsNullOrWhiteSpace(member.Host) &&
            !new IrcNameComparer(session.State.CaseMapping).Equals(member.Nickname, session.CurrentNickname))
        {
            var matches = channel.Members
                .Where(candidate => !new IrcNameComparer(session.State.CaseMapping)
                    .Equals(candidate.Nickname, member.Nickname) &&
                    candidate.Host?.Equals(member.Host, StringComparison.OrdinalIgnoreCase) == true)
                .Select(candidate => candidate.Nickname)
                .Append(member.Nickname)
                .OrderBy(nickname => nickname, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (matches.Length > 1)
            {
                PublishChannelNotice(session, channel,
                    $"Clone detected: {string.Join(", ", matches)} using shared address {member.Host}");
            }
        }
    }

    private async Task AnnounceUserInfoAsync(IrcNetworkSession session, ChannelState channel, ChannelMemberState member)
    {
        try
        {
            var profile = ProfileFor(session);
            if (profile is null || member.FullMask is null) return;
            var directory = _userAndChannelPolicy.GetDirectory(
                profile.Id,
                () => _userDirectoryStore.Load(profile.Id));
            var match = directory.Match(member.FullMask, session.State.CaseMapping);
            if (match.Conflict || match.User is null) return;
            var info = match.User.GetChannelComment(channel.Name, session.State.CaseMapping);
            if (string.IsNullOrWhiteSpace(info)) info = match.User.Comment;
            if (string.IsNullOrWhiteSpace(info)) return;
            await session.SendMessageAsync(channel.Name, $"[{member.Nickname}] {info}", SessionWorkToken(session));
        }
        catch (OperationCanceledException) when (SessionWorkToken(session).IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            PublishStatus(session, SessionEventKind.Error, $"Could not announce user infoline for {member.Nickname}: {exception.Message}");
        }
    }

    private async Task ApplyUserPoliciesAsync(
        IrcNetworkSession session,
        ChannelState channel,
        IEnumerable<ChannelMemberState> members)
    {
        var policyGate = UserPolicyGate(session, channel.Name);
        var entered = false;
        try
        {
            var sessionToken = SessionWorkToken(session);
            await policyGate.WaitAsync(sessionToken);
            entered = true;
            var profile = ProfileFor(session);
            if (profile is null || !channel.NamesSynchronized || !channel.WhoSynchronized ||
                !channel.TryGetMember(session.CurrentNickname, out var self) ||
                !HasOperatorPrivilege(session.Features, self!))
            {
                return;
            }

            var directory = _userAndChannelPolicy.GetDirectory(
                profile.Id,
                () => _userDirectoryStore.Load(profile.Id));

            var policyBans = directory.PolicyBans
                .Where(ban => PolicyAppliesToChannel(ban, channel.Name, session.State.CaseMapping))
                .Where(ban => !channel.Bans.Contains(ban.Mask, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            var appliedBans = 0;
            foreach (var ban in policyBans)
            {
                if (!ReserveUserPolicyAction(session, channel.Name, "+b", ban.Mask)) continue;
                await session.SendAsync(
                    "MODE",
                    [channel.Name, "+b", ban.Mask],
                    IrcOutboundPriority.Automation,
                    sessionToken);
                appliedBans++;
            }

            var comparer = new IrcNameComparer(session.State.CaseMapping);
            var op = new List<string>();
            var voice = new List<string>();
            var deop = new List<string>();
            var kick = new List<string>();
            foreach (var member in members)
            {
                if (comparer.Equals(member.Nickname, session.CurrentNickname) || member.FullMask is null)
                {
                    continue;
                }

                var match = directory.Match(member.FullMask, session.State.CaseMapping);
                if (match.Conflict)
                {
                    PublishStatus(session, SessionEventKind.Error,
                        $"User policy conflict for {member.FullMask}: {string.Join(", ", match.Candidates.Select(user => user.Handle))}");
                    continue;
                }

                var roles = match.User?.EffectiveRoles(channel.Name, session.State.CaseMapping) ?? UserRole.None;
                if (roles.HasFlag(UserRole.OperatorEligible) && roles.HasFlag(UserRole.AutoOp) && !member.PrefixModes.Contains('o'))
                {
                    if (ReserveUserPolicyAction(session, channel.Name, "+o", member.Nickname)) op.Add(member.Nickname);
                }

                if (roles.HasFlag(UserRole.AutoVoice) && !member.PrefixModes.Contains('v'))
                {
                    if (ReserveUserPolicyAction(session, channel.Name, "+v", member.Nickname)) voice.Add(member.Nickname);
                }

                if (!roles.HasFlag(UserRole.Protected) && roles.HasFlag(UserRole.Deop) && member.PrefixModes.Contains('o'))
                {
                    if (ReserveUserPolicyAction(session, channel.Name, "-o", member.Nickname)) deop.Add(member.Nickname);
                }

                if (!roles.HasFlag(UserRole.Protected) && roles.HasFlag(UserRole.KickOnJoin))
                {
                    if (ReserveUserPolicyAction(session, channel.Name, "kick", member.Nickname)) kick.Add(member.Nickname);
                }
            }

            await SendModeBatchesAsync(session, channel, 'o', true, op, sessionToken, IrcOutboundPriority.Automation);
            await SendModeBatchesAsync(session, channel, 'v', true, voice, sessionToken, IrcOutboundPriority.Automation);
            await SendModeBatchesAsync(session, channel, 'o', false, deop, sessionToken, IrcOutboundPriority.Automation);
            foreach (var nickname in kick)
            {
                await session.SendAsync(
                    "KICK",
                    [channel.Name, nickname, "User policy"],
                    IrcOutboundPriority.Automation,
                    sessionToken);
            }

            if (op.Count + voice.Count + deop.Count + kick.Count > 0)
            {
                PublishStatus(session, SessionEventKind.Status,
                    $"Applied user policy in {channel.Name}: +o {op.Count}, +v {voice.Count}, -o {deop.Count}, kicks {kick.Count}.");
            }
            if (appliedBans > 0)
            {
                PublishStatus(session, SessionEventKind.Status,
                    $"Applied {appliedBans} persistent policy ban(s) in {channel.Name}.");
            }
        }
        catch (OperationCanceledException) when (SessionWorkToken(session).IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            PublishStatus(session, SessionEventKind.Error, $"User policy failed in {channel.Name}: {exception.Message}");
        }
        finally
        {
            if (entered) policyGate.Release();
        }
    }

    private SemaphoreSlim UserPolicyGate(IrcNetworkSession session, string channel)
    {
        return _userAndChannelPolicy.ChannelGate(
            session.State.Id,
            IrcCaseFold.Fold(channel, session.State.CaseMapping));
    }

    private bool ReserveUserPolicyAction(IrcNetworkSession session, string channel, string action, string target)
    {
        var mapping = session.State.CaseMapping;
        var key = string.Join('\0',
            IrcCaseFold.Fold(channel, mapping),
            action,
            IrcCaseFold.Fold(target, mapping));
        var now = DateTimeOffset.UtcNow;
        return _userAndChannelPolicy.TryReserveUserAction(
            session.State.Id,
            key,
            now,
            now.AddSeconds(5));
    }

    private static bool PolicyAppliesToChannel(PolicyBan ban, string channel, IrcCaseMapping mapping)
    {
        var comparer = new IrcNameComparer(mapping);
        return ban.Channels.Any(configured => configured == "*" || comparer.Equals(configured, channel));
    }

    private async Task AssociateAdvertisedNetworkAsync(IrcNetworkSession session)
    {
        try
        {
            var profile = ProfileFor(session);
            if (profile is null)
            {
                var entries = _profileStore.Entries;
                var endpointMatches = entries.Where(candidate => candidate.Endpoints.Any(endpoint =>
                    endpoint.Port == session.Options.Endpoint.Port &&
                    endpoint.UseTls == session.Options.Endpoint.UseTls &&
                    endpoint.Host.Equals(session.Options.Endpoint.Host, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (endpointMatches.Length == 1)
                {
                    profile = endpointMatches[0];
                }
                else if (!string.IsNullOrWhiteSpace(session.Features.NetworkName))
                {
                    var networkMatches = entries.Where(candidate =>
                        candidate.NetworkName?.Equals(session.Features.NetworkName, StringComparison.OrdinalIgnoreCase) == true).ToArray();
                    if (networkMatches.Length == 1)
                    {
                        profile = networkMatches[0];
                    }
                }
            }

            if (profile is null)
            {
                return;
            }

            profile = UpdateProfileFromSession(profile, session);
            AssociateProfile(session, profile);
            if (profile.NetworkName is not null &&
                session.Features.NetworkName is not null &&
                !profile.NetworkName.Equals(session.Features.NetworkName, StringComparison.OrdinalIgnoreCase))
            {
                PublishStatus(
                    session,
                    SessionEventKind.Error,
                    $"Profile network is {profile.NetworkName}, but this server advertises NETWORK={session.Features.NetworkName}.");
            }

        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            PublishStatus(session, SessionEventKind.Error, $"Could not associate network profile: {exception.Message}");
        }
    }

    private async Task RunConfiguredAutojoinAsync(IrcNetworkSession session)
    {
        var profile = ProfileFor(session);

        if (!_sessionTransientState.TryStartAutojoin(session.State.Id))
        {
            return;
        }

        var restore = session.ChannelsToRestore;
        var channels = (profile?.AutojoinChannels ?? [])
            .Concat(restore.Keys)
            .Distinct(new IrcNameComparer(session.State.CaseMapping))
            .Where(channel => !session.State.TryGetChannel(channel, out _))
            .ToArray();
        if (channels.Length == 0)
        {
            return;
        }

        try
        {
            PublishStatus(session, SessionEventKind.Status,
                $"Restoring channels: {string.Join(", ", channels)}");
            foreach (var channel in channels)
            {
                var parameters = restore.TryGetValue(channel, out var key) && !string.IsNullOrWhiteSpace(key)
                    ? new[] { channel, key }
                    : new[] { channel };
                await SendJoinAsync(session, parameters, IrcOutboundPriority.Automation, SessionWorkToken(session));
            }
        }
        catch (OperationCanceledException) when (SessionWorkToken(session).IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            PublishStatus(session, SessionEventKind.Error, $"Autojoin failed: {exception.Message}");
        }
    }

    private async Task RunAutojoinAsync(
        IrcNetworkSession session,
        NetworkProfile profile,
        CancellationToken cancellationToken)
    {
        if (profile.AutojoinChannels.Count == 0)
        {
            PublishStatus(session, SessionEventKind.Status, $"{profile.DisplayName} has no autojoin channels.");
            return;
        }

        PublishStatus(session, SessionEventKind.Status, $"Autojoining: {string.Join(", ", profile.AutojoinChannels)}");
        foreach (var channel in profile.AutojoinChannels)
        {
            await SendJoinAsync(session, [channel], IrcOutboundPriority.Automation, cancellationToken);
        }
    }

    private async ValueTask SendJoinAsync(
        IrcNetworkSession session,
        IReadOnlyList<string> parameters,
        IrcOutboundPriority priority,
        CancellationToken cancellationToken,
        BufferId? returnBuffer = null)
    {
        var channels = parameters.Count == 0
            ? []
            : parameters[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var channel in channels)
        {
            RecordJoinStart(session, channel, overwrite: true);
            if (returnBuffer is { } destination)
            {
                RecordPendingJoinRoute(session, channel, destination);
            }
        }
        var keys = parameters.Count > 1
            ? parameters[1].Split(',', StringSplitOptions.TrimEntries)
            : [];
        for (var index = 0; index < channels.Length; index++)
        {
            session.PrepareJoin(channels[index], index < keys.Length ? keys[index] : null);
        }
        try
        {
            await session.SendAsync("JOIN", parameters, priority, cancellationToken);
        }
        catch
        {
            var joins = RuntimeFor(session)?.Joins;
            if (joins is not null)
                foreach (var channel in channels) joins.ForgetJoin(channel);
            throw;
        }
    }

    private void RecordPendingJoinRoute(IrcNetworkSession session, string channel, BufferId destination)
    {
        RuntimeFor(session)?.Joins.RecordReturnRoute(channel, destination);
    }

    private void RecordJoinStart(IrcNetworkSession session, string channel, bool overwrite)
    {
        RuntimeFor(session)?.Joins.RecordStart(channel, Stopwatch.GetTimestamp(), overwrite);
    }

    private void ReportJoinSynchronization(IrcNetworkSession session, ChannelState channel)
    {
        if (RuntimeFor(session)?.Joins.TryTakeStart(channel.Name, out var startedAt) != true) return;
        var elapsed = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
        var buffer = session.State.GetOrCreateBuffer(BufferKind.Channel, channel.Name);
        OnSessionEvent(new SessionEvent(
            session.State.Id,
            buffer.Id,
            SessionEventKind.ChannelSync,
            $"Join synchronized in {elapsed:0.000} seconds",
            DateTimeOffset.Now,
            new Dictionary<string, string?>
            {
                ["channel"] = channel.Name,
                ["seconds"] = elapsed.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
            }));
    }

    private void MarkCyclePending(IrcNetworkSession session, string channel)
    {
        RuntimeFor(session)?.Joins.MarkCycle(channel);
    }

    private bool IsCyclePending(NetworkSessionId sessionId, string channel)
        => _liveSessions.Runtime(sessionId)?.Joins.IsCyclePending(channel) == true;

    private void ClearCyclePending(NetworkSessionId sessionId, string channel)
        => _liveSessions.Runtime(sessionId)?.Joins.CompleteCycle(channel);

    private CommandResult SaveAutojoin(NetworkProfile profile, IEnumerable<string> channels, string successMessage)
    {
        try
        {
            _profileStore.Replace(profile.WithAutojoin(channels));
            return CommandResult.Success(successMessage);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return CommandResult.Failure(exception.Message);
        }
    }

    private void PublishStatus(IrcNetworkSession session, SessionEventKind kind, string text) =>
        OnSessionEvent(new SessionEvent(
            session.State.Id,
            session.State.StatusBuffer.Id,
            kind,
            TerminalTextSanitizer.Sanitize(FormatLocalCommandResult(text)),
            DateTimeOffset.Now));

    private void OnTlsCertificateNotice(TlsCertificateNotice notice)
    {
        var session = _liveSessions.SessionSnapshot()
            .Where(candidate =>
                candidate.Options.Endpoint.Host.Equals(notice.Endpoint.Host, StringComparison.OrdinalIgnoreCase) &&
                candidate.Options.Endpoint.Port == notice.Endpoint.Port)
            .OrderByDescending(candidate => _windowStates.IsActiveSession(candidate.State.Id))
            .FirstOrDefault();
        if (session is null)
        {
            _presenter.Result(notice.Text, notice.Success);
            return;
        }
        PublishStatus(
            session,
            notice.Success ? SessionEventKind.Status : SessionEventKind.Error,
            notice.Text);
    }

}
