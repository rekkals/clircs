using System.Globalization;
using Clircs.Commands;
using Clircs.Dcc;
using Clircs.Identity;
using Clircs.Protocol;
using Clircs.Sessions;
using Clircs.State;

namespace Clircs.ConsoleClient;

// Owns DCC negotiation, transfers, chat sessions, and runtime transitions.
internal sealed partial class ClientApplication
{
    private SessionEvent RouteDccProtocolEvent(SessionEvent sessionEvent)
    {
        var eventName = sessionEvent.Fields?.GetValueOrDefault("event");
        if (eventName is not ("dcc.request" or "dcc.control" or "dcc.invalid") ||
            FindSession(sessionEvent.NetworkSessionId) is not { } session)
        {
            return sessionEvent;
        }

        var protocolFields = sessionEvent.Fields!;
        if (eventName == "dcc.invalid")
        {
            var invalidDestination = DccBuffer(session);
            return sessionEvent with
            {
                BufferId = invalidDestination.Id,
                Fields = WithFields(protocolFields, ("dcc.state", "invalid"))
            };
        }

        if (eventName == "dcc.control")
        {
            return RouteDccControlEvent(session, sessionEvent);
        }

        var raw = protocolFields.GetValueOrDefault("dcc.raw");
        string? error = raw is null ? "The DCC request payload is missing." : null;
        DccOffer? offer = null;
        if (raw is null || !DccOfferParser.TryParse(raw, out offer, out error))
        {
            var invalidDestination = DccBuffer(session);
            return sessionEvent with
            {
                BufferId = invalidDestination.Id,
                Kind = SessionEventKind.Error,
                Text = $"Invalid DCC request from {protocolFields.GetValueOrDefault("nick") ?? "unknown"}: {error}",
                Fields = WithFields(protocolFields, ("event", "dcc.invalid"), ("dcc.state", "invalid"))
            };
        }

        if (offer!.IsPassiveResponse)
        {
            var sender = protocolFields.GetValueOrDefault("nick") ?? "unknown";
            var match = _dcc.Requests.Snapshot().FirstOrDefault(candidate =>
                PassiveResponseMatches(candidate, session.State.Id, sender, offer, session.State.CaseMapping));
            if (match is null || !_dcc.Requests.TryTransitionWithOffer(
                    match.Id, DccRequestState.Connecting, offer,
                    "The passive DCC response was received", out var connecting))
            {
                var unmatchedDestination = DccBuffer(session);
                return sessionEvent with
                {
                    BufferId = unmatchedDestination.Id,
                    Kind = SessionEventKind.Error,
                    Text = $"Unmatched passive DCC response from {sender}",
                    Fields = WithFields(protocolFields, ("dcc.state", "unmatched"))
                };
            }

            CancelDccExpiration(match.Id);
            if (offer.Type == DccRequestType.Chat)
                StartOutgoingPassiveDccChat(connecting!);
            else
            {
                var outgoing = _dcc.OutgoingSend(match.Id);
                if (outgoing is null)
                {
                    _dcc.Requests.TryTransition(
                        match.Id,
                        DccRequestState.Failed,
                        $"The passive DCC {DccProtocolName(match.Offer)} runtime was no longer available",
                        out _);
                    var failedDestination = DccBuffer(session);
                    return sessionEvent with
                    {
                        BufferId = failedDestination.Id,
                        Kind = SessionEventKind.Error,
                        Text = $"Passive DCC SEND response #{match.Id} could not be started",
                        Fields = WithFields(protocolFields,
                            ("dcc.id", match.Id.ToString(CultureInfo.InvariantCulture)),
                            ("dcc.direction", "outgoing"), ("dcc.state", "failed"))
                    };
                }
                _dcc.TrackTask(match.Id, RunOutgoingPassiveDccSendAsync(connecting!, outgoing));
            }
            var responseDestination = offer.Type == DccRequestType.Chat
                ? DccChatBuffer(session, connecting!)
                : DccBuffer(session);
            return sessionEvent with
            {
                BufferId = responseDestination.Id,
                Kind = SessionEventKind.Status,
                Text = $"Passive DCC {DccProtocolName(offer)} response #{match.Id} received from {sender}; connecting",
                Fields = WithFields(protocolFields,
                    ("dcc.id", match.Id.ToString(CultureInfo.InvariantCulture)),
                    ("dcc.direction", "outgoing"), ("dcc.state", "connecting"))
            };
        }

        var network = ProfileFor(session)?.DisplayName ?? session.Features.NetworkName ?? session.State.DisplayName;
        var request = _dcc.Requests.Add(
            session.State.Id,
            network,
            protocolFields.GetValueOrDefault("nick") ?? "unknown",
            offer!,
            sessionEvent.Timestamp);
        ScheduleDccExpiration(request);
        _dcc.PruneTerminalRuntimes();
        var destination = request.Offer.Type == DccRequestType.Chat
            ? ActiveBufferFor(session) ?? session.State.StatusBuffer
            : DccBuffer(session);
        _dcc.SetNotificationBuffer(request.Id, destination.Id);
        var windowNumber = BufferNumber(destination.Id);
        EchoDccRequestInActiveBuffer(session, destination.Id, request, windowNumber);
        var description = request.Offer.Type == DccRequestType.Send
            ? $"DCC {DccProtocolName(request.Offer)} request #{request.Id} from {request.Sender}: {request.Offer.Filename} ({FormatFileSize(request.Offer.Size ?? 0)}) from {DccEndpoint(request.Offer)}"
            : $"DCC {DccProtocolName(request.Offer)} request #{request.Id} from {request.Sender} - Use: /dcc accept|reject {request.Id}";
        return sessionEvent with
        {
            BufferId = destination.Id,
            Kind = SessionEventKind.Status,
            Text = description,
            Presentation = request.Offer.Type == DccRequestType.Send ? DccRequestPresentation(request) : null,
            Fields = WithFields(protocolFields,
                ("message", description),
                ("dcc.id", request.Id.ToString(CultureInfo.InvariantCulture)),
                ("dcc.network", network),
                ("dcc.direction", "incoming"),
                ("dcc.state", "pending"),
                ("dcc.expires", request.ExpiresAt.ToString("O", CultureInfo.InvariantCulture)))
        };
    }

    private SessionEvent RouteDccControlEvent(IrcNetworkSession session, SessionEvent sessionEvent)
    {
        var fields = sessionEvent.Fields!;
        var raw = fields.GetValueOrDefault("dcc.raw");
        var sender = fields.GetValueOrDefault("nick") ?? "unknown";
        var destination = DccBuffer(session);
        string? error = raw is null ? "The DCC resume payload is missing" : null;
        DccResumeMessage? control = null;
        if (raw is null || !DccResumeParser.TryParse(raw, out control, out error))
        {
            return sessionEvent with
            {
                BufferId = destination.Id,
                Kind = SessionEventKind.Error,
                Text = $"Invalid DCC resume message from {sender}: {error}",
                Fields = WithFields(fields, ("dcc.state", "invalid"))
            };
        }

        if (control!.Operation == DccResumeOperation.Resume)
        {
            var request = _dcc.Requests.Snapshot().FirstOrDefault(candidate =>
                DccResumeMatches(candidate, session, sender, control, DccRequestDirection.Outgoing));
            var outgoing = request is null ? null : _dcc.OutgoingSend(request.Id);
            if (request is null || outgoing is null ||
                !CanResumeOutgoingFile(outgoing, control.Position) ||
                !(outgoing.TrySetResumeOffset(control.Position) || outgoing.ResumeOffset == control.Position))
            {
                return sessionEvent with
                {
                    BufferId = destination.Id,
                    Kind = SessionEventKind.Error,
                    Text = $"Unable to resume DCC SEND from {sender}: no matching transfer at that position",
                    Fields = WithFields(fields, ("dcc.state", "unmatched"))
                };
            }

            var accept = DccResumeParser.Format(
                DccResumeOperation.Accept,
                control.Filename,
                control.Port,
                control.Position,
                control.PassiveToken);
            StartSessionWork(
                session,
                "DCC resume acknowledgement",
                () => SendDccControlAsync(session, sender, accept, request.Id));
            return sessionEvent with
            {
                BufferId = destination.Id,
                Kind = SessionEventKind.Status,
                Text = $"DCC RESUME request #{request.Id} from {sender} accepted at {FormatFileSize(control.Position)}",
                Fields = WithFields(fields,
                    ("dcc.id", request.Id.ToString(CultureInfo.InvariantCulture)),
                    ("dcc.direction", "outgoing"), ("dcc.state", "accepted"))
            };
        }

        DccRequest? acceptedRequest = null;
        PendingDccResume? pending = null;
        foreach (var candidate in _dcc.Requests.Snapshot())
        {
            if (_dcc.PendingResume(candidate.Id) is not { } candidatePending ||
                !DccResumeMatches(candidate, session, sender, control, DccRequestDirection.Incoming) ||
                candidatePending.Position != control.Position ||
                !_dcc.TakePendingResume(candidate.Id, candidatePending))
                continue;
            acceptedRequest = candidate;
            pending = candidatePending;
            break;
        }
        if (acceptedRequest is null || pending is null)
        {
            return sessionEvent with
            {
                BufferId = destination.Id,
                Kind = SessionEventKind.Error,
                Text = $"Unmatched DCC ACCEPT from {sender}",
                Fields = WithFields(fields, ("dcc.state", "unmatched"))
            };
        }

        _dcc.TrackTask(acceptedRequest.Id, ContinueIncomingDccResumeAsync(acceptedRequest, pending.Target));
        return sessionEvent with
        {
            BufferId = destination.Id,
            Kind = SessionEventKind.Status,
            Text = $"DCC RESUME request #{acceptedRequest.Id} accepted by {sender} at {FormatFileSize(control.Position)}",
            Fields = WithFields(fields,
                ("dcc.id", acceptedRequest.Id.ToString(CultureInfo.InvariantCulture)),
                ("dcc.direction", "incoming"), ("dcc.state", "accepted"))
        };
    }

    private static bool DccResumeMatches(
        DccRequest request,
        IrcNetworkSession session,
        string sender,
        DccResumeMessage control,
        DccRequestDirection direction)
    {
        if (request.NetworkSessionId != session.State.Id || request.Direction != direction ||
            request.State != DccRequestState.Pending || request.Offer.Type != DccRequestType.Send ||
            !new IrcNameComparer(session.State.CaseMapping).Equals(request.Sender, sender) ||
            !string.Equals(request.Offer.Filename, control.Filename, StringComparison.Ordinal))
            return false;
        return request.Offer.IsPassiveRequest
            ? control.IsPassive &&
                string.Equals(request.Offer.PassiveToken, control.PassiveToken, StringComparison.Ordinal)
            : !control.IsPassive && request.Offer.Port == control.Port;
    }

    private static bool CanResumeOutgoingFile(OutgoingDccSend outgoing, long position)
    {
        if (position <= 0 || position >= outgoing.ExpectedBytes) return false;
        try
        {
            var info = new FileInfo(outgoing.FilePath);
            return info.Length == outgoing.ExpectedBytes && info.LastWriteTimeUtc == outgoing.LastWriteTimeUtc;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task SendDccControlAsync(
        IrcNetworkSession session,
        string target,
        string payload,
        int requestId)
    {
        try
        {
            await session.SendAsync("PRIVMSG", [target, $"\u0001{payload}\u0001"],
                cancellationToken: _lifetime.Token);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
            OperationCanceledException or ObjectDisposedException)
        {
            if (_dcc.Requests.TryTransition(requestId, DccRequestState.Failed,
                    exception.Message, out var failed))
            {
                CancelDccExpiration(requestId);
                CancelDccTransfer(requestId);
                PublishDccState(failed!, $"DCC RESUME request #{requestId} failed: {exception.Message}");
            }
        }
    }

    private async Task ContinueIncomingDccResumeAsync(DccRequest request, DccDownloadTarget target)
    {
        CommandResult result = request.Offer.IsPassiveRequest
            ? await StartIncomingPassiveDccSendAsync(request, _lifetime.Token, target)
            : StartIncomingDccSend(request, target);
        if (!result.Succeeded)
        {
            _dcc.Requests.TryGet(request.Id, out var current);
            PublishDccState(current ?? request,
                result.Message ?? $"DCC RESUME request #{request.Id} could not start");
        }
    }

    internal static bool PassiveResponseMatches(
        DccRequest request,
        NetworkSessionId sessionId,
        string sender,
        DccOffer response,
        IrcCaseMapping caseMapping)
    {
        if (request.NetworkSessionId != sessionId || request.Direction != DccRequestDirection.Outgoing ||
            request.State != DccRequestState.Pending || !request.Offer.IsPassiveRequest ||
            !response.IsPassiveResponse || request.Offer.Type != response.Type ||
            request.Offer.IsSecure != response.IsSecure ||
            !string.Equals(request.Offer.PassiveToken, response.PassiveToken, StringComparison.Ordinal) ||
            !new IrcNameComparer(caseMapping).Equals(request.Sender, sender))
            return false;
        return response.Type != DccRequestType.Send ||
            string.Equals(request.Offer.Filename, response.Filename, StringComparison.Ordinal) &&
            request.Offer.Size == response.Size;
    }

    private BufferState? ActiveBufferFor(IrcNetworkSession session)
    {
        return _windowStates.ActiveBufferFor(session.State.Id) is { } activeBufferId &&
            session.State.TryGetBuffer(activeBufferId, out var activeBuffer)
                ? activeBuffer
                : null;
    }

    private void EchoDccRequestInActiveBuffer(
        IrcNetworkSession sourceSession,
        BufferId destination,
        DccRequest request,
        int windowNumber)
    {
        var (activeSession, buffer) = _windowStates.ResolveActive(_liveSessions);
        var activeBuffer = buffer?.Id;
        if (activeSession is null || activeBuffer is null ||
            activeBuffer == destination ||
            !activeSession.State.TryGetBuffer(activeBuffer.Value, out _))
        {
            return;
        }

        var type = DccProtocolName(request.Offer);
        var networkSuffix = activeSession.State.Id == sourceSession.State.Id
            ? string.Empty
            : $" on {request.Network}";
        var text = request.Offer.Type == DccRequestType.Chat
            ? $"DCC {type} request #{request.Id} from {request.Sender} - Use: /dcc accept|reject {request.Id}"
            : $"DCC {type} request #{request.Id} from {request.Sender}{networkSuffix} (window {windowNumber})";
        OnSessionEvent(new SessionEvent(
            activeSession.State.Id,
            activeBuffer.Value,
            SessionEventKind.Status,
            text,
            DateTimeOffset.Now,
            new Dictionary<string, string?>
            {
                ["event"] = "dcc.alert",
                ["dcc.id"] = request.Id.ToString(CultureInfo.InvariantCulture),
                ["dcc.type"] = request.Offer.Type.ToString().ToLowerInvariant(),
                ["dcc.direction"] = request.Direction.ToString().ToLowerInvariant(),
                ["dcc.state"] = "pending",
                ["dcc.network"] = request.Network,
                ["dcc.sender"] = request.Sender,
                ["suppressActivity"] = "true"
            }));
    }

    private static IReadOnlyDictionary<string, string?> WithFields(
        IReadOnlyDictionary<string, string?> fields,
        params (string Key, string? Value)[] additions)
    {
        var result = new Dictionary<string, string?>(fields, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in additions) result[key] = value;
        return result;
    }

    private void ScheduleDccExpiration(DccRequest request)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _dcc.SetExpiration(request.Id, cancellation);
        _dcc.TrackTask(request.Id, ExpireDccRequestAsync(request, cancellation));
    }

    private async Task ExpireDccRequestAsync(DccRequest request, CancellationTokenSource cancellation)
    {
        try
        {
            var delay = request.ExpiresAt - DateTimeOffset.Now;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation.Token);
            if (_dcc.Requests.TryTransition(request.Id, DccRequestState.Expired, "The request expired", out var expired))
            {
                CancelPendingDccResume(request.Id);
                await StopDccChatListenerAsync(request.Id);
                if (request.Offer.Type == DccRequestType.Send)
                {
                    CancelDccTransfer(request.Id);
                    if (request.Offer.IsPassiveRequest) CleanupUnstartedPassiveSend(request.Id);
                }
                if (DccChatBufferFor(request.Id) is { } chatBuffer &&
                    FindSession(request.NetworkSessionId) is { } chatSession)
                {
                    OnSessionEvent(new SessionEvent(
                        chatSession.State.Id,
                        chatBuffer.Id,
                        SessionEventKind.Status,
                        $"DCC {DccProtocolName(request.Offer)} request #{request.Id} expired",
                        DateTimeOffset.Now,
                        new Dictionary<string, string?>
                        {
                            ["event"] = "dcc.chat.state",
                            ["dcc.id"] = request.Id.ToString(CultureInfo.InvariantCulture),
                            ["dcc.state"] = "expired"
                        }));
                }
                PublishDccState(expired!, $"DCC request #{request.Id} from {request.Sender} expired");
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _dcc.TakeExpiration(request.Id, cancellation);
            cancellation.Dispose();
        }
    }

    private void CancelDccExpiration(int id)
    {
        var cancellation = _dcc.TakeExpiration(id);
        DccCoordinator.CancelLifetime(cancellation);
    }

    private void InvalidateDccRequests(IrcNetworkSession session, string reason)
    {
        foreach (var request in _dcc.Requests.Invalidate(session.State.Id, reason))
        {
            CancelDccExpiration(request.Id);
            CancelPendingDccResume(request.Id);
            CancelDccChatConnection(request.Id);
            _ = StopDccChatListenerAsync(request.Id);
            if (request.Offer.Type == DccRequestType.Send)
            {
                CancelDccTransfer(request.Id);
                if (request.Offer.IsPassiveRequest) CleanupUnstartedPassiveSend(request.Id);
            }
            PublishDccState(request, $"DCC request #{request.Id} from {request.Sender} is no longer valid because the connection closed");
        }
    }

    private void PublishDccState(DccRequest request, string text)
    {
        if (FindSession(request.NetworkSessionId) is not { } session) return;
        var destination = request.Offer.Type == DccRequestType.Chat
            ? DccChatBufferFor(request.Id) ?? DccNotificationBufferFor(session, request.Id)
            : DccBuffer(session);
        var fields = new Dictionary<string, string?>
        {
            ["event"] = "dcc.state",
            ["dcc.id"] = request.Id.ToString(CultureInfo.InvariantCulture),
            ["dcc.type"] = request.Offer.Type.ToString().ToLowerInvariant(),
            ["dcc.state"] = request.State.ToString().ToLowerInvariant(),
            ["dcc.direction"] = request.Direction.ToString().ToLowerInvariant(),
            ["dcc.network"] = request.Network,
            ["nick"] = request.Sender,
            ["dcc.sender"] = request.Sender,
            ["dcc.filename"] = request.Offer.Filename,
            ["dcc.address"] = request.Offer.Address,
            ["dcc.port"] = request.Offer.Port.ToString(CultureInfo.InvariantCulture),
            ["dcc.size"] = request.Offer.Size?.ToString(CultureInfo.InvariantCulture),
            ["dcc.token"] = request.Offer.PassiveToken,
            ["dcc.secure"] = request.Offer.IsSecure ? "true" : "false",
            ["dcc.reason"] = request.StateReason
        };
        if (request.Offer.Type == DccRequestType.Send && DccRequestRegistry.IsTerminal(request.State))
        {
            fields["history.replaceKey"] = DccProgressHistoryKey(request.Id);
            fields["history.finalKey"] = DccProgressHistoryKey(request.Id);
        }
        OnSessionEvent(new SessionEvent(
            session.State.Id,
            destination.Id,
            SessionEventKind.Status,
            text,
            DateTimeOffset.Now,
            fields));

        if (DccRequestRegistry.IsTerminal(request.State))
        {
            _dcc.ClearNotificationBuffer(request.Id);
        }
    }

    private static string DccProgressHistoryKey(int requestId) =>
        $"dcc.transfer.{requestId.ToString(CultureInfo.InvariantCulture)}";

    private BufferState DccNotificationBufferFor(IrcNetworkSession session, int requestId)
    {
        if (_dcc.NotificationBufferId(requestId) is { } bufferId &&
            session.State.TryGetBuffer(bufferId, out var buffer))
        {
            return buffer!;
        }
        return session.State.StatusBuffer;
    }
}
