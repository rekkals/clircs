using Clircs.Identity;
using Clircs.Networking;
using Clircs.Sessions;
using Clircs.State;

namespace Clircs.ConsoleClient;

internal enum SessionEventDispatchStage
{
    AdmissionAndAwayState,
    ProtectionAndDcc,
    OutputRouting,
    HistoryStorage,
    EventDelivery,
    WindowCompletion
}

// Owns inbound event routing, storage, logging, activity, and script delivery.
internal sealed partial class ClientApplication
{
    internal static IReadOnlyList<SessionEventDispatchStage> SessionEventDispatchStages { get; } =
        Array.AsReadOnly(
        [
            SessionEventDispatchStage.AdmissionAndAwayState,
            SessionEventDispatchStage.ProtectionAndDcc,
            SessionEventDispatchStage.OutputRouting,
            SessionEventDispatchStage.HistoryStorage,
            SessionEventDispatchStage.EventDelivery,
            SessionEventDispatchStage.WindowCompletion
        ]);

    private void QueueInboundSessionEvent(SessionEvent sessionEvent)
    {
        if (_inboundSessionEvents.Enqueue(sessionEvent) == ResourceQueueWriteResult.CapacityExceeded)
        {
            OpenInboundResourceCircuit(
                sessionEvent.NetworkSessionId,
                $"the pending IRC event backlog exceeded {InboundSessionEventPump<SessionEvent>.MaximumPendingItems:N0} entries");
        }
    }

    private void OnSessionEvents(IReadOnlyList<SessionEvent> sessionEvents) =>
        _applicationEvents.Dispatch(() => _presenter.RunEventBatch(() =>
        {
            foreach (var sessionEvent in sessionEvents)
            {
                try
                {
                    DispatchSessionEvent(sessionEvent);
                }
                catch (Exception exception)
                {
                    LogUnexpectedApplicationWorkFailure("inbound IRC event delivery", exception);
                }
            }
        }));

    // Locally generated events use the same presentation path without making command
    // execution wait behind unrelated socket traffic already queued by a session.
    private void OnSessionEvent(SessionEvent sessionEvent) => OnSessionEvents([sessionEvent]);

    private void DispatchSessionEvent(SessionEvent sessionEvent)
    {
        var context = new SessionEventDispatchContext(sessionEvent);
        foreach (var stage in SessionEventDispatchStages)
        {
            if (!DispatchSessionEventStage(stage, context))
            {
                return;
            }
        }
    }

    private bool DispatchSessionEventStage(
        SessionEventDispatchStage stage,
        SessionEventDispatchContext context) => stage switch
        {
            SessionEventDispatchStage.AdmissionAndAwayState => AdmitSessionEvent(context),
            SessionEventDispatchStage.ProtectionAndDcc => ApplyProtectionAndDcc(context),
            SessionEventDispatchStage.OutputRouting => RouteSessionEvent(context),
            SessionEventDispatchStage.HistoryStorage => StoreSessionEvent(context),
            SessionEventDispatchStage.EventDelivery => DeliverSessionEvent(context),
            SessionEventDispatchStage.WindowCompletion => CompleteSessionEvent(context),
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
        };

    private bool AdmitSessionEvent(SessionEventDispatchContext context)
    {
        var sessionEvent = context.Event;
        if (sessionEvent.Fields?.GetValueOrDefault("prefill") is { } prefill)
        {
            _presenter.PrefillInput(prefill);
        }
        if (IsPersonallyIgnored(sessionEvent))
        {
            return false;
        }

        RecordAwayMessage(sessionEvent);
        var awayChanged = sessionEvent.Fields?.GetValueOrDefault("event") == "away";
        context.ReturnedFromAway = awayChanged &&
            sessionEvent.Fields!.GetValueOrDefault("away") == "false";
        if (awayChanged && !context.ReturnedFromAway)
        {
            _sessionTransientState.ResetAwayAcknowledgements(sessionEvent.NetworkSessionId);
        }

        if (sessionEvent.Kind == SessionEventKind.Part &&
            sessionEvent.Fields?.GetValueOrDefault("self") == "true" &&
            sessionEvent.Fields.GetValueOrDefault("channel") is { } departedChannel &&
            FindSession(sessionEvent.NetworkSessionId) is { } departedSession)
        {
            var cloneKey = CloneChannelKey(departedSession, departedChannel);
            _channelSynchronization.ForgetChannel(cloneKey);
        }

        context.ActiveBefore = ActiveLocation();
        context.IsHighlightEcho = sessionEvent.Fields?.GetValueOrDefault("highlightEcho") == "true";
        context.IsReplay = sessionEvent.Fields?.GetValueOrDefault("replay") == "true";
        return true;
    }

    private bool ApplyProtectionAndDcc(SessionEventDispatchContext context)
    {
        if (!context.IsHighlightEcho && !context.IsReplay)
        {
            HandleProtectionMonitoring(context.Event);
            HandleAutomaticKickRejoin(context.Event);
        }

        context.Event = RouteDccProtocolEvent(context.Event);
        if (context.Event.Kind == SessionEventKind.Highlight &&
            (!_preferences.HighlightNickname || context.IsReplay))
        {
            context.Event = context.Event with { Kind = SessionEventKind.Message };
        }
        context.EchoHighlight = context.Event.Kind == SessionEventKind.Highlight && !context.IsHighlightEcho;
        return true;
    }

    private bool RouteSessionEvent(SessionEventDispatchContext context)
    {
        context.Event = HandleJoinForward(context.Event);
        context.Event = RoutePendingJoinEvent(context.Event);
        context.Event = RoutePendingChannelNotice(context.Event);
        context.Event = ActivateConfirmedCycleJoin(context.Event);
        context.Event = RouteActiveResponse(context.Event);
        if (ActiveLocation() != context.ActiveBefore)
        {
            RedrawActiveBuffer(context.Event);
        }

        var routedEvent = RouteOutputEvent(context.Event);
        if (routedEvent is null)
        {
            return false;
        }
        context.Event = routedEvent;
        return true;
    }

    private bool StoreSessionEvent(SessionEventDispatchContext context)
    {
        var sessionEvent = context.Event;
        var eventSessionBeforeStore = FindSession(sessionEvent.NetworkSessionId);
        var eventBufferNameBeforeStore =
            eventSessionBeforeStore?.State.TryGetBuffer(sessionEvent.BufferId, out var bufferBeforeStore) == true
                ? bufferBeforeStore!.Name
                : "status";
        lock (_windowTransactionGate)
        {
            context.Stored = FindSession(sessionEvent.NetworkSessionId)?.State.TryGetBuffer(sessionEvent.BufferId, out _) == true
                ? StoreWindowEventUnsafe(sessionEvent, eventBufferNameBeforeStore, context.IsReplay, trackUnread: true)
                : new StoredWindowEvent(false, false, false, false);
        }
        if (!context.Stored.Stored)
        {
            return false;
        }
        if (context.Stored.EmergencyLimitReached)
        {
            OpenInboundResourceCircuit(
                sessionEvent.NetworkSessionId,
                $"a window reached the emergency scrollback limit of {ScrollbackRetention.EmergencyMaximumEntries:N0} entries");
        }
        else if (context.Stored.TotalEmergencyLimitReached)
        {
            OpenInboundResourceCircuit(
                sessionEvent.NetworkSessionId,
                $"application scrollback reached the emergency limit of {ScrollbackRetention.EmergencyMaximumTotalEntries:N0} entries");
        }

        context.EventSession = FindSession(sessionEvent.NetworkSessionId);
        if (context.EventSession?.State.TryGetBuffer(sessionEvent.BufferId, out var foundBuffer) == true)
        {
            context.EventBuffer = foundBuffer;
        }
        context.BufferName = context.EventBuffer?.Name ?? "status";
        return true;
    }

    private bool DeliverSessionEvent(SessionEventDispatchContext context)
    {
        if (LogSessionEvent(context.Event, context.EventSession, context.EventBuffer) ==
            ResourceQueueWriteResult.CapacityExceeded)
        {
            OpenInboundResourceCircuit(
                context.Event.NetworkSessionId,
                $"the pending log backlog exceeded {EventLogWriter.MaximumPendingEntries:N0} entries");
        }
        if (context.Stored.IsActive && !context.Stored.IsScrolled)
        {
            if (context.Stored.Replaced)
            {
                RedrawActiveBuffer();
                context.RedrewForReplacement = true;
            }
            else
            {
                _presenter.Event(context.Event, context.BufferName);
            }
        }

        if (context.Event.Fields?.GetValueOrDefault("event") != "irc.wire")
        {
            _scriptManager.Publish(context.Event);
        }
        return true;
    }

    private bool CompleteSessionEvent(SessionEventDispatchContext context)
    {
        var closedActivePart = CloseConfirmedPartBuffer(context.Event);
        if (closedActivePart)
        {
            RedrawActiveBuffer();
        }
        else if (!context.RedrewForReplacement)
        {
            RefreshWindowChrome();
        }
        if (context.EchoHighlight && !context.Stored.IsActive && context.EventSession is not null)
        {
            EchoHighlightInActiveBuffer(context.Event, context.EventSession, context.BufferName);
        }
        if (context.ReturnedFromAway && context.EventSession is not null)
        {
            var network = AwayNetwork(context.EventSession);
            if (_awayMessageStore.ForNetwork(network.Key).Any(entry => !entry.Read))
            {
                DisplayCommandResult(OpenAwayMessageIndex(context.EventSession, network.Key, network.Name));
            }
        }
        return true;
    }

    private sealed class SessionEventDispatchContext(SessionEvent sessionEvent)
    {
        public SessionEvent Event { get; set; } = sessionEvent;
        public (NetworkSessionId? SessionId, BufferId? BufferId) ActiveBefore { get; set; }
        public bool IsHighlightEcho { get; set; }
        public bool IsReplay { get; set; }
        public bool ReturnedFromAway { get; set; }
        public bool EchoHighlight { get; set; }
        public StoredWindowEvent Stored { get; set; }
        public IrcNetworkSession? EventSession { get; set; }
        public BufferState? EventBuffer { get; set; }
        public string BufferName { get; set; } = "status";
        public bool RedrewForReplacement { get; set; }
    }

    private void OnWireLineTransferred(IrcNetworkSession session, IrcWireLine wireLine)
    {
        if (!session.State.TryGetBuffer("=debug", out var buffer) || buffer!.Kind != BufferKind.Diagnostics)
        {
            return;
        }

        OnSessionEvent(new SessionEvent(
            session.State.Id,
            buffer.Id,
            SessionEventKind.Diagnostic,
            FormatWireDebugLine(wireLine),
            wireLine.Timestamp,
            new Dictionary<string, string?>
            {
                ["event"] = "irc.wire",
                ["direction"] = wireLine.Direction == IrcWireDirection.Received ? "received" : "sent",
                ["suppressActivity"] = "true"
            }));
    }

    private ResourceQueueWriteResult LogSessionEvent(
        SessionEvent sessionEvent,
        IrcNetworkSession? session,
        BufferState? buffer)
    {
        if (session is null || buffer is null || !CanLog(buffer)) return ResourceQueueWriteResult.Accepted;
        var profile = ProfileFor(session);
        if (profile is null) return ResourceQueueWriteResult.Accepted;
        var target = LoggingTarget(buffer);
        if (!_loggingStore.IsEnabled(profile.Id, target)) return ResourceQueueWriteResult.Accepted;
        var lines = TranscriptFormatter.FormatLines(
            sessionEvent, _preferences.JoinHostmasks, _preferences.PartHostmasks, _preferences.QuitHostmasks);
        if (lines.Count == 0) return ResourceQueueWriteResult.Accepted;
        return _logWriter.Enqueue(profile.DisplayName, buffer.Kind, target, sessionEvent.Timestamp, lines);
    }

    private void OpenInboundResourceCircuit(NetworkSessionId sessionId, string reason)
    {
        if (!_inboundResourceCircuitBreaker.TryOpen(sessionId)) return;
        var session = FindSession(sessionId);
        if (session is null) return;

        StartSessionWork(session, "emergency inbound resource circuit breaker", async () =>
        {
            try
            {
                await session.DisconnectAsync("Client input overload", SessionWorkToken(session));
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
            {
                // The connection may already be closing. The local incident remains
                // authoritative and is published below either way.
            }

            OnSessionEvent(new SessionEvent(
                session.State.Id,
                session.State.StatusBuffer.Id,
                SessionEventKind.Error,
                $"Emergency flood circuit breaker disconnected {session.State.DisplayName}: {reason}; use /reconnect to reconnect",
                DateTimeOffset.Now,
                new Dictionary<string, string?>
                {
                    ["clientResult"] = "true",
                    ["suppressActivity"] = "true",
                    ["event"] = "resource.circuitOpen"
                }));
        });
    }

    private SessionEvent RoutePendingJoinEvent(SessionEvent sessionEvent)
    {
        var isConfirmedJoin = sessionEvent.Kind == SessionEventKind.Join &&
            sessionEvent.Fields?.GetValueOrDefault("self") == "true";
        var isDeniedJoin = sessionEvent.Fields?.GetValueOrDefault("joinError") == "true";
        if ((!isConfirmedJoin && !isDeniedJoin) ||
            sessionEvent.Fields?.GetValueOrDefault("channel") is not { } channel)
        {
            return sessionEvent;
        }

        var runtime = RuntimeFor(sessionEvent.NetworkSessionId);
        if (runtime is null)
        {
            return sessionEvent;
        }
        if (!runtime.Joins.Complete(channel, isDeniedJoin, out var destination)) return sessionEvent;

        lock (_windowTransactionGate)
        {
            if (isConfirmedJoin)
            {
                _windowStates.Activate(sessionEvent.NetworkSessionId, sessionEvent.BufferId);
                return sessionEvent;
            }

            return sessionEvent with { BufferId = destination };
        }
    }

    private SessionEvent RoutePendingChannelNotice(SessionEvent sessionEvent)
    {
        if (sessionEvent.Kind != SessionEventKind.Notice ||
            sessionEvent.Fields?.GetValueOrDefault("private") != "true" ||
            BracketedChannelNoticeTarget(sessionEvent.Fields.GetValueOrDefault("message")) is not { } channel ||
            FindSession(sessionEvent.NetworkSessionId) is not { } session ||
            !session.Features.IsChannel(channel))
        {
            return sessionEvent;
        }

        var joins = RuntimeFor(session)?.Joins;
        if (!session.State.TryGetBuffer(channel, out var buffer) && joins?.IsPending(channel) != true)
        {
            return sessionEvent;
        }

        buffer ??= session.State.GetOrCreateBuffer(BufferKind.Channel, channel);
        var fields = new Dictionary<string, string?>(sessionEvent.Fields, StringComparer.OrdinalIgnoreCase)
        {
            ["channel"] = channel,
            ["private"] = null,
            ["outputFamily"] = null,
            ["routeConfigured"] = null
        };
        return sessionEvent with { BufferId = buffer.Id, Fields = fields };
    }

    internal static string? BracketedChannelNoticeTarget(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) || message[0] != '[') return null;
        var closingBracket = message.IndexOf(']');
        return closingBracket > 1 ? message[1..closingBracket] : null;
    }

    private SessionEvent HandleJoinForward(SessionEvent sessionEvent)
    {
        if (sessionEvent.Fields?.GetValueOrDefault("joinForward") != "true" ||
            sessionEvent.Fields.GetValueOrDefault("forwardFrom") is not { } requested ||
            sessionEvent.Fields.GetValueOrDefault("channel") is not { } forwarded)
        {
            return sessionEvent;
        }

        RuntimeFor(sessionEvent.NetworkSessionId)?.Joins.Forward(requested, forwarded);
        return sessionEvent;
    }

    private void HandleAutomaticKickRejoin(SessionEvent sessionEvent)
    {
        if (!_preferences.AutoRejoinOnKick || sessionEvent.Kind != SessionEventKind.Part ||
            sessionEvent.Fields?.GetValueOrDefault("event") != "kick" ||
            sessionEvent.Fields.GetValueOrDefault("self") != "true" ||
            sessionEvent.Fields.GetValueOrDefault("channel") is not { } channel ||
            FindSession(sessionEvent.NetworkSessionId) is not { } session)
        {
            return;
        }

        MarkCyclePending(session, channel);
        StartSessionWork(
            session,
            "automatic channel rejoin",
            () => RejoinAfterKickAsync(session, channel));
    }

    private async Task RejoinAfterKickAsync(IrcNetworkSession session, string channel)
    {
        try
        {
            await SendJoinAsync(session, [channel], IrcOutboundPriority.Automation, SessionWorkToken(session));
        }
        catch (OperationCanceledException) when (SessionWorkToken(session).IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            ClearCyclePending(session.State.Id, channel);
            PublishStatus(session, SessionEventKind.Error, $"Automatic rejoin failed for {channel}: {exception.Message}");
        }
    }

    private void OnSessionStateChanged(IrcNetworkSession session)
    {
        if (_windowStates.IsActiveSession(session.State.Id))
        {
            RefreshWindowChrome();
        }
    }

    private SessionEvent RouteActiveResponse(SessionEvent sessionEvent)
    {
        var routeRequested = sessionEvent.Fields?.GetValueOrDefault("routeActive") == "true";
        if (!routeRequested)
        {
            return sessionEvent;
        }

        return _windowStates.ActiveBufferFor(sessionEvent.NetworkSessionId) is { } activeBuffer
            ? sessionEvent with { BufferId = activeBuffer }
            : sessionEvent;
    }

    private SessionEvent ActivateConfirmedCycleJoin(SessionEvent sessionEvent)
    {
        if (sessionEvent.Kind != SessionEventKind.Join ||
            sessionEvent.Fields?.GetValueOrDefault("self") != "true" ||
            sessionEvent.Fields.GetValueOrDefault("channel") is not { } channel)
        {
            return sessionEvent;
        }

        var runtime = RuntimeFor(sessionEvent.NetworkSessionId);
        if (runtime is null || !runtime.Joins.CompleteCycle(channel)) return sessionEvent;
        lock (_windowTransactionGate)
        {
            _windowStates.Activate(sessionEvent.NetworkSessionId, sessionEvent.BufferId);
        }
        return sessionEvent;
    }

    private bool CloseConfirmedPartBuffer(SessionEvent sessionEvent)
    {
        if (sessionEvent.Kind != SessionEventKind.Part || sessionEvent.Fields is null ||
            sessionEvent.Fields.GetValueOrDefault("self") != "true" ||
            sessionEvent.Fields.GetValueOrDefault("event") == "kick")
        {
            return false;
        }

        var session = FindSession(sessionEvent.NetworkSessionId);
        if (session is null || sessionEvent.BufferId == session.State.StatusBuffer.Id)
        {
            return false;
        }

        if (sessionEvent.Fields.GetValueOrDefault("channel") is { } partedChannel &&
            IsCyclePending(sessionEvent.NetworkSessionId, partedChannel))
        {
            return false;
        }

        var wasActive = false;
        lock (_windowTransactionGate)
        {
            if (_windowStates.IsActiveBuffer(sessionEvent.BufferId))
            {
                wasActive = true;
                SelectPreviousBufferUnsafe(sessionEvent.BufferId, session);
            }
            _windowStates.Remove(sessionEvent.BufferId);
            session.State.RemoveBuffer(sessionEvent.BufferId);
        }
        _presenter.ForgetInputHistory(sessionEvent.BufferId);
        return wasActive;
    }

    private void TrackOutputRequest(IrcNetworkSession session, string family, Guid? requestId = null)
    {
        var configured = _outputRouting.DestinationFor(family);
        var destination = configured switch
        {
            OutputDestination.Status => session.State.StatusBuffer.Id,
            OutputDestination.Dedicated => session.State.GetOrCreateBuffer(BufferKind.Results, $"={family}").Id,
            _ when _windowStates.ActiveBufferFor(session.State.Id) is { } active => active,
            _ => session.State.StatusBuffer.Id
        };
        if (requestId is { } id)
        {
            _outputRouting.SetRequest(session.State.Id, id, destination);
        }
        else
        {
            _outputRouting.SetFamily(session.State.Id, family, destination);
        }
        AssignBufferNumberUnsafe(destination);
    }

    private bool TryTrackExclusiveOutputRequest(IrcNetworkSession session, string family)
    {
        var configured = _outputRouting.DestinationFor(family);
        var destination = configured switch
        {
            OutputDestination.Status => session.State.StatusBuffer.Id,
            OutputDestination.Dedicated => session.State.GetOrCreateBuffer(BufferKind.Results, $"={family}").Id,
            _ when _windowStates.ActiveBufferFor(session.State.Id) is { } active => active,
            _ => session.State.StatusBuffer.Id
        };
        if (!_outputRouting.TrySetExclusiveFamily(session.State.Id, family, destination)) return false;
        AssignBufferNumberUnsafe(destination);
        return true;
    }

    private void CancelOutputRequest(IrcNetworkSession session, string family, Guid? requestId = null)
    {
        if (requestId is { } id)
        {
            _outputRouting.RemoveRequest(session.State.Id, id);
        }
        else
        {
            _outputRouting.RemoveFamily(session.State.Id, family);
        }
    }

    private SessionEvent? RouteOutputEvent(SessionEvent sessionEvent)
    {
        if (sessionEvent.Fields is null ||
            !sessionEvent.Fields.TryGetValue("outputFamily", out var family) ||
            string.IsNullOrWhiteSpace(family))
        {
            return sessionEvent;
        }

        if (family.Equals("who", StringComparison.OrdinalIgnoreCase) &&
            sessionEvent.Fields.GetValueOrDefault("automatic") == "true")
        {
            if (sessionEvent.Fields.TryGetValue("outputTarget", out var automaticTarget) &&
                automaticTarget is not null)
            {
                _channelSynchronization.CompleteAutomaticWho(
                    sessionEvent.NetworkSessionId,
                    automaticTarget);
            }
            return null;
        }

        if (!_outputRouting.TryResolve(sessionEvent, out var destination))
        {
            if (family.Equals("who", StringComparison.OrdinalIgnoreCase) &&
                sessionEvent.Fields.TryGetValue("outputTarget", out var target) &&
                target is not null &&
                _channelSynchronization.IsAutomaticWho(sessionEvent.NetworkSessionId, target))
            {
                if (sessionEvent.Fields.TryGetValue("outputEnd", out var autoEnd) &&
                    autoEnd?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _channelSynchronization.CompleteAutomaticWho(sessionEvent.NetworkSessionId, target);
                }
                return null;
            }
            if (sessionEvent.Fields.GetValueOrDefault("routeConfigured") != "true") return sessionEvent;
            var routeSession = _liveSessions.Find(sessionEvent.NetworkSessionId);
            var configured = _outputRouting.DestinationFor(family);
            destination = configured switch
            {
                OutputDestination.Dedicated => routeSession?.State
                    .GetOrCreateBuffer(BufferKind.Results, $"={family}").Id ?? sessionEvent.BufferId,
                _ => routeSession?.State.StatusBuffer.Id ?? sessionEvent.BufferId
            };
            AssignBufferNumberUnsafe(destination);
            return sessionEvent with { BufferId = destination };
        }

        return sessionEvent with { BufferId = destination };
    }

    private void EchoInActiveBuffer(
        IrcNetworkSession session,
        SessionEventKind kind,
        string text,
        BufferId? excludedBufferId = null)
    {
        var activeBufferId = _windowStates.ActiveBufferId;

        if (activeBufferId is null || activeBufferId == excludedBufferId ||
            !session.State.TryGetBuffer(activeBufferId.Value, out _))
        {
            return;
        }

        OnSessionEvent(new SessionEvent(
            session.State.Id,
            activeBufferId.Value,
            kind,
            TerminalTextSanitizer.Sanitize(text),
            DateTimeOffset.Now));
    }

    private void EchoHighlightInActiveBuffer(SessionEvent sourceEvent, IrcNetworkSession sourceSession, string sourceBuffer)
    {
        var (activeSession, buffer) = _windowStates.ResolveActive(_liveSessions);
        var activeBuffer = buffer?.Id;
        if (activeSession is null || activeBuffer is null ||
            !activeSession.State.TryGetBuffer(activeBuffer.Value, out _)) return;

        var network = ProfileFor(sourceSession)?.DisplayName ?? sourceSession.Features.NetworkName ?? sourceSession.State.DisplayName;
        var source = activeSession.State.Id == sourceSession.State.Id ? sourceBuffer : $"{network}/{sourceBuffer}";
        var fields = (sourceEvent.Fields ?? new Dictionary<string, string?>())
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        fields["highlightEcho"] = "true";
        fields["source"] = source;
        OnSessionEvent(sourceEvent with
        {
            NetworkSessionId = activeSession.State.Id,
            BufferId = activeBuffer.Value,
            Fields = fields
        });
    }

}
