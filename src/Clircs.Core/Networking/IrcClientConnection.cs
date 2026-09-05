using Clircs.Protocol;

namespace Clircs.Networking;

public sealed class IrcClientConnection : IAsyncDisposable
{
    private readonly IIrcTransportFactory _transportFactory;
    private CancellationTokenSource? _connectionLifetime;
    private IIrcTransport? _transport;
    private IrcOutboundScheduler? _outbound;
    private Task? _receiveTask;
    private IrcConnectionOptions? _options;
    private int _nicknameIndex;
    private bool _nicknameSelectionRequired;
    private readonly Dictionary<string, string?> _advertisedCapabilities = new(StringComparer.Ordinal);
    private readonly HashSet<string> _enabledCapabilities = new(StringComparer.Ordinal);
    private readonly HashSet<string> _requestedCapabilities = new(StringComparer.Ordinal);
    private CapabilityNegotiationStage _capabilityStage;
    private SaslRegistrationStage _saslStage;
    private bool _reportedExcessParameterDiagnostic;
    private int _finishStarted;
    private int _disposed;
    private TaskCompletionSource<bool>? _finishCompletion;
    private volatile IrcConnectionState _state = IrcConnectionState.Disconnected;

    public IrcClientConnection(IIrcTransportFactory transportFactory)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
    }

    public event Func<IrcMessage, ValueTask>? MessageReceived;

    public event Action<string>? Diagnostic;

    public event Action<NicknameFallbackEvent>? NicknameFallback;

    public event Action<Exception?>? ConnectionClosed;

    public event Action<SaslAuthenticationEvent>? SaslAuthenticationChanged;

    public event Action<IrcWireLine>? WireLineTransferred;

    public IrcConnectionState State => _state;

    public string? CurrentNickname { get; private set; }

    public string? RemoteDescription => _transport?.RemoteDescription;

    public DateTimeOffset LastReceivedAt { get; private set; } = DateTimeOffset.MinValue;

    public async ValueTask ConnectAsync(IrcConnectionOptions options, CancellationToken cancellationToken = default)
    {
        if (_state is not (IrcConnectionState.Disconnected or IrcConnectionState.Failed))
        {
            throw new InvalidOperationException($"Cannot connect while the connection state is {_state}.");
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _options = options.Validate();
        _connectionLifetime?.Dispose();
        _connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connectionToken = _connectionLifetime.Token;
        _finishStarted = 0;
        _finishCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _nicknameIndex = 0;
        _nicknameSelectionRequired = false;
        _advertisedCapabilities.Clear();
        _enabledCapabilities.Clear();
        _requestedCapabilities.Clear();
        _capabilityStage = CapabilityNegotiationStage.AwaitingCapabilities;
        _saslStage = options.Sasl is null ? SaslRegistrationStage.Disabled : SaslRegistrationStage.Pending;
        _reportedExcessParameterDiagnostic = false;
        _transport = null;
        _outbound = null;
        _receiveTask = null;
        _state = IrcConnectionState.Connecting;

        try
        {
            _transport = await _transportFactory.ConnectAsync(
                new IrcTransportOptions(options.Endpoint, options.Sasl?.ClientCertificate), connectionToken).ConfigureAwait(false);
            _outbound = new IrcOutboundScheduler(_transport);
            _state = IrcConnectionState.Registering;
            CurrentNickname = options.Identity.Nicknames[0];

            LastReceivedAt = DateTimeOffset.UtcNow;
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_connectionLifetime.Token), CancellationToken.None);

            if (!string.IsNullOrEmpty(options.Password))
            {
                await SendAsync("PASS", [options.Password], IrcOutboundPriority.Critical, connectionToken).ConfigureAwait(false);
            }

            await SendAsync("CAP", ["LS", "302"], IrcOutboundPriority.Critical, connectionToken).ConfigureAwait(false);
            await SendAsync("NICK", [CurrentNickname], IrcOutboundPriority.Critical, connectionToken).ConfigureAwait(false);
            await SendAsync(
                "USER",
                [options.Identity.Username, "0", "*", options.Identity.RealName],
                IrcOutboundPriority.Critical,
                connectionToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _state = IrcConnectionState.Failed;
            await FinishAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask SendAsync(
        string command,
        IReadOnlyList<string> parameters,
        IrcOutboundPriority priority = IrcOutboundPriority.Interactive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var outbound = _outbound ?? throw new InvalidOperationException("Not connected to a server.");
        var line = IrcLineBuilder.Build(command, parameters.ToArray());
        await outbound.EnqueueAsync(line, priority, cancellationToken).ConfigureAwait(false);
        RaiseWireLine(IrcWireDirection.Sent, IrcTextEncoding.Decode(line.AsSpan(0, line.Length - 2)));
    }

    public ValueTask SendNicknameAsync(string nickname, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);
        CurrentNickname = nickname;
        _nicknameSelectionRequired = false;
        return SendAsync("NICK", [nickname], IrcOutboundPriority.Interactive, cancellationToken);
    }

    public async ValueTask SendRawAsync(
        string rawLine,
        IrcOutboundPriority priority = IrcOutboundPriority.Interactive,
        CancellationToken cancellationToken = default)
    {
        var parsed = IrcMessageParser.Parse(rawLine);
        if (parsed.Prefix is not null)
        {
            throw new IrcProtocolException("Client raw commands cannot contain an IRC prefix.");
        }

        await SendAsync(parsed.Command, parsed.Parameters, priority, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisconnectAsync(string reason = "Leaving", CancellationToken cancellationToken = default)
    {
        if (_state == IrcConnectionState.Disconnected)
        {
            return;
        }
        if (Volatile.Read(ref _finishStarted) != 0)
        {
            await WaitForFinishAsync().ConfigureAwait(false);
            return;
        }

        _state = IrcConnectionState.Disconnecting;
        try
        {
            if (_outbound is not null)
            {
                await SendAsync("QUIT", [reason], IrcOutboundPriority.Critical, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            Diagnostic?.Invoke($"Could not send QUIT: {exception.Message}");
        }

        await FinishAsync(null).ConfigureAwait(false);
    }

    public ValueTask AbortAsync(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        _state = IrcConnectionState.Failed;
        return FinishAsync(error);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await DisconnectAsync().ConfigureAwait(false);
        _connectionLifetime?.Dispose();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var framer = new IrcLineFramer();

        try
        {
            while (true)
            {
                var transport = _transport ?? throw new InvalidOperationException("The IRC transport disappeared.");
                var count = await transport.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new IOException("The IRC server closed the connection.");
                }
                LastReceivedAt = DateTimeOffset.UtcNow;

                var framingResult = framer.Push(buffer.AsSpan(0, count));

                if (framingResult.DiscardedOversizedLineCount > 0)
                {
                    var description = framingResult.DiscardedOversizedLineCount == 1
                        ? "an oversized IRC line"
                        : $"{framingResult.DiscardedOversizedLineCount} oversized IRC lines";

                    Diagnostic?.Invoke(
                        $"Ignored {description} exceeding {IrcLineFramer.MaximumPayloadBytes} payload bytes.");
                }

                foreach (var framedLine in framingResult.Lines)
                {
                    var rawLine = IrcTextEncoding.Decode(framedLine);
                    RaiseWireLine(IrcWireDirection.Received, rawLine);
                    IrcMessage message;
                    try
                    {
                        message = IrcMessageParser.Parse(rawLine);
                    }
                    catch (Exception exception) when (exception is IrcProtocolException or ArgumentException)
                    {
                        Diagnostic?.Invoke($"Ignored malformed IRC line: {exception.Message}");
                        continue;
                    }
                    if (message.ExceedsTraditionalParameterLimit &&
                        !_reportedExcessParameterDiagnostic)
                    {
                        _reportedExcessParameterDiagnostic = true;

                        // TODO: Reassess whether this protocol-violation diagnostic should remain
                        // user-visible. It is currently exposed to help test real-world compatibility.
                        Diagnostic?.Invoke(
                            $"Accepted a nonstandard IRC message with {message.Parameters.Count} parameters; " +
                            $"the traditional limit is {IrcMessage.TraditionalParameterLimit}. " +
                            "Further occurrences will be accepted silently.");
                    }

                    await HandleProtocolMessageAsync(message, cancellationToken).ConfigureAwait(false);
                    await RaiseMessageReceivedAsync(message).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _state = IrcConnectionState.Failed;
            await FinishAsync(exception, calledFromReceiveLoop: true).ConfigureAwait(false);
        }
    }

    private async ValueTask HandleProtocolMessageAsync(IrcMessage message, CancellationToken cancellationToken)
    {
        if (message.Command == "PING")
        {
            await SendAsync("PONG", message.Parameters, IrcOutboundPriority.Critical, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (message.Command == "CAP" ||
            (_state == IrcConnectionState.Registering && IsCapabilityUnsupportedReply(message)))
        {
            await HandleCapabilityNegotiationAsync(message, cancellationToken).ConfigureAwait(false);
        }

        if (_options?.Sasl is { } sasl && _state == IrcConnectionState.Registering)
        {
            await HandleSaslAuthenticationAsync(message, sasl, cancellationToken).ConfigureAwait(false);
        }

        if (message.Command == "001")
        {
            if (_capabilityStage is not (CapabilityNegotiationStage.Complete or CapabilityNegotiationStage.Unsupported))
            {
                if (_options?.Sasl is { } registrationSasl && _saslStage is not SaslRegistrationStage.Complete)
                {
                    FailSasl(registrationSasl, "the server completed registration without SASL");
                }

                _capabilityStage = CapabilityNegotiationStage.Complete;
            }

            _state = IrcConnectionState.Online;
            if (message.Parameters.Count > 0)
            {
                CurrentNickname = message.Parameters[0];
            }

            return;
        }

        if (message.Command is "433" or "437" &&
            _state == IrcConnectionState.Registering &&
            !_nicknameSelectionRequired)
        {
            var rejectedNickname = message.Parameters.Count >= 2
                ? message.Parameters[1]
                : CurrentNickname ?? "unknown";
            await TryNextNicknameAsync(rejectedNickname, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask HandleCapabilityNegotiationAsync(
        IrcMessage message,
        CancellationToken cancellationToken)
    {
        if (IsCapabilityUnsupportedReply(message))
        {
            _capabilityStage = CapabilityNegotiationStage.Unsupported;
            if (_options?.Sasl is { } unsupportedSasl)
            {
                FailSasl(unsupportedSasl, "the server does not support capability negotiation");
            }
            return;
        }

        if (message.Parameters.Count < 2)
        {
            return;
        }

        var subcommand = message.Parameters[1].ToUpperInvariant();
        if (subcommand == "LS" && _capabilityStage == CapabilityNegotiationStage.AwaitingCapabilities)
        {
            AddAdvertisedCapabilities(message.Parameters[^1]);
            var continuation = message.Parameters.Count >= 4 && message.Parameters[^2] == "*";
            if (continuation)
            {
                return;
            }

            await RequestRegistrationCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (subcommand == "NEW")
        {
            AddAdvertisedCapabilities(message.Parameters[^1]);
            if (_state == IrcConnectionState.Online &&
                _advertisedCapabilities.ContainsKey("multi-prefix") &&
                !_enabledCapabilities.Contains("multi-prefix"))
            {
                await RequestCapabilitiesAsync(["multi-prefix"], cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (subcommand == "DEL")
        {
            foreach (var capability in ParseCapabilityNames(message.Parameters[^1]))
            {
                _advertisedCapabilities.Remove(capability);
                _enabledCapabilities.Remove(capability);
            }
            return;
        }

        if (subcommand is not ("ACK" or "NAK"))
        {
            return;
        }

        if (subcommand == "ACK")
        {
            ApplyCapabilityAcknowledgement(message.Parameters[^1]);
        }

        if (_state == IrcConnectionState.Online)
        {
            _requestedCapabilities.Clear();
            return;
        }

        if (_capabilityStage != CapabilityNegotiationStage.AwaitingAcknowledgement)
        {
            return;
        }

        var saslRequested = _requestedCapabilities.Contains("sasl");
        var saslAcknowledged = _enabledCapabilities.Contains("sasl");
        _requestedCapabilities.Clear();
        if (saslRequested && (subcommand == "NAK" || !saslAcknowledged))
        {
            FailSasl(_options!.Sasl!, "the server rejected the SASL capability");
        }

        if (saslRequested && saslAcknowledged)
        {
            _capabilityStage = CapabilityNegotiationStage.Authenticating;
            _saslStage = SaslRegistrationStage.AwaitingChallenge;
            await SendAsync("AUTHENTICATE", [_options!.Sasl!.Mechanism], IrcOutboundPriority.Critical, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await FinishCapabilityNegotiationAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RequestRegistrationCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var requested = new List<string>();
        if (_advertisedCapabilities.ContainsKey("multi-prefix"))
        {
            requested.Add("multi-prefix");
        }

        if (_options?.Sasl is { } sasl)
        {
            if (!_advertisedCapabilities.TryGetValue("sasl", out var mechanisms))
            {
                FailSasl(sasl, "the server does not advertise SASL");
            }
            else if (!string.IsNullOrWhiteSpace(mechanisms) &&
                     !mechanisms.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Contains(sasl.Mechanism, StringComparer.OrdinalIgnoreCase))
            {
                FailSasl(sasl, $"the server does not offer the {sasl.Mechanism} mechanism");
            }
            else
            {
                requested.Add("sasl");
            }
        }

        if (requested.Count == 0)
        {
            await FinishCapabilityNegotiationAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _capabilityStage = CapabilityNegotiationStage.AwaitingAcknowledgement;
        await RequestCapabilitiesAsync(requested, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RequestCapabilitiesAsync(
        IReadOnlyList<string> capabilities,
        CancellationToken cancellationToken)
    {
        _requestedCapabilities.Clear();
        foreach (var capability in capabilities)
        {
            _requestedCapabilities.Add(capability);
        }

        await SendAsync("CAP", ["REQ", string.Join(' ', capabilities)], IrcOutboundPriority.Critical, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask HandleSaslAuthenticationAsync(
        IrcMessage message,
        SaslAuthentication sasl,
        CancellationToken cancellationToken)
    {
        if (message.Command == "AUTHENTICATE" && _saslStage == SaslRegistrationStage.AwaitingChallenge)
        {
            if (message.Parameters.Count == 0 || message.Parameters[0] != "+")
            {
                FailSasl(sasl, $"the server sent an unexpected {sasl.Mechanism} challenge");
                await FinishCapabilityNegotiationAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var chunks = sasl.Mechanism == SaslMechanisms.Plain
                ? SaslPayload.Plain(sasl.Username!, sasl.Password!)
                : SaslPayload.External(sasl.AuthorizationIdentity);
            foreach (var chunk in chunks)
            {
                await SendAsync("AUTHENTICATE", [chunk], IrcOutboundPriority.Critical, cancellationToken).ConfigureAwait(false);
            }
            _saslStage = SaslRegistrationStage.AwaitingResult;
            return;
        }

        if (_saslStage == SaslRegistrationStage.AwaitingResult && message.Command is "903" or "907")
        {
            _saslStage = SaslRegistrationStage.Complete;
            SaslAuthenticationChanged?.Invoke(new SaslAuthenticationEvent(
                true, sasl.Required, sasl.Mechanism, sasl.AuthorizationIdentity,
                message.Command == "907" ? "already authenticated" : "authentication successful"));
            await FinishCapabilityNegotiationAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_saslStage is not (SaslRegistrationStage.Complete or SaslRegistrationStage.Disabled) &&
            message.Command is "902" or "904" or "905" or "906")
        {
            var detail = message.Parameters.Count > 0 ? message.Parameters[^1] : "authentication failed";
            FailSasl(sasl, detail);
            await FinishCapabilityNegotiationAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsCapabilityUnsupportedReply(IrcMessage message) =>
        message.Command == "421" &&
        message.Parameters.Count >= 2 &&
        message.Parameters[1].Equals("CAP", StringComparison.OrdinalIgnoreCase);

    private void ApplyCapabilityAcknowledgement(string value)
    {
        foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var disable = token[0] == '-';
            var capability = disable ? token[1..] : token;
            if (disable)
            {
                _enabledCapabilities.Remove(capability);
            }
            else
            {
                _enabledCapabilities.Add(capability);
            }
        }
    }

    private static IEnumerable<string> ParseCapabilityNames(string value)
    {
        foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = token.IndexOf('=');
            yield return separator < 0 ? token : token[..separator];
        }
    }

    private async ValueTask FinishCapabilityNegotiationAsync(CancellationToken cancellationToken)
    {
        if (_capabilityStage is CapabilityNegotiationStage.Complete or CapabilityNegotiationStage.Unsupported)
        {
            return;
        }

        _capabilityStage = CapabilityNegotiationStage.Complete;
        _requestedCapabilities.Clear();
        await SendAsync("CAP", ["END"], IrcOutboundPriority.Critical, cancellationToken).ConfigureAwait(false);
    }

    private void AddAdvertisedCapabilities(string value)
    {
        foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = token.IndexOf('=');
            var name = separator < 0 ? token : token[..separator];
            _advertisedCapabilities[name] = separator < 0 ? null : token[(separator + 1)..];
        }
    }

    private void FailSasl(SaslAuthentication sasl, string detail)
    {
        _saslStage = SaslRegistrationStage.Complete;
        SaslAuthenticationChanged?.Invoke(new SaslAuthenticationEvent(
            false, sasl.Required, sasl.Mechanism, sasl.AuthorizationIdentity, detail));
        if (sasl.Required)
        {
            throw new IrcSaslException($"SASL authentication failed: {detail}");
        }
    }

    private async ValueTask TryNextNicknameAsync(string rejectedNickname, CancellationToken cancellationToken)
    {
        var options = _options ?? throw new InvalidOperationException("Registration options are unavailable.");
        _nicknameIndex++;
        if (_nicknameIndex < options.Identity.Nicknames.Count)
        {
            CurrentNickname = options.Identity.Nicknames[_nicknameIndex];
            NicknameFallback?.Invoke(new NicknameFallbackEvent(rejectedNickname, CurrentNickname));
            await SendAsync("NICK", [CurrentNickname], IrcOutboundPriority.Critical, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _nicknameSelectionRequired = true;
            NicknameFallback?.Invoke(new NicknameFallbackEvent(rejectedNickname, null));
        }
    }

    private async ValueTask RaiseMessageReceivedAsync(IrcMessage message)
    {
        var handlers = MessageReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (Func<IrcMessage, ValueTask> handler in handlers.GetInvocationList())
        {
            await handler(message).ConfigureAwait(false);
        }
    }

    private void RaiseWireLine(IrcWireDirection direction, string line)
    {
        var handlers = WireLineTransferred;
        if (handlers is null)
        {
            return;
        }

        var wireLine = new IrcWireLine(direction, line, DateTimeOffset.Now);
        foreach (Action<IrcWireLine> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(wireLine);
            }
            catch (Exception exception)
            {
                Diagnostic?.Invoke($"Raw IRC observer failed: {exception.Message}");
            }
        }
    }

    private async ValueTask FinishAsync(Exception? error, bool calledFromReceiveLoop = false)
    {
        if (Interlocked.Exchange(ref _finishStarted, 1) != 0)
        {
            if (!calledFromReceiveLoop)
            {
                await WaitForFinishAsync().ConfigureAwait(false);
            }
            return;
        }

        try
        {
            _connectionLifetime?.Cancel();

            if (_outbound is not null)
            {
                await _outbound.DisposeAsync().ConfigureAwait(false);
            }

            if (_transport is not null)
            {
                try
                {
                    await _transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    await _transport.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (!calledFromReceiveLoop && _receiveTask is not null)
            {
                try
                {
                    await _receiveTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            _state = error is null ? IrcConnectionState.Disconnected : IrcConnectionState.Failed;
            _outbound = null;
            _transport = null;
            _connectionLifetime?.Dispose();
            _connectionLifetime = null;
            _finishCompletion?.TrySetResult(true);
            ConnectionClosed?.Invoke(error);
        }
        catch (Exception exception)
        {
            _state = IrcConnectionState.Failed;
            _finishCompletion?.TrySetException(exception);
            throw;
        }
    }

    private Task WaitForFinishAsync() => _finishCompletion?.Task ?? Task.CompletedTask;

    private enum CapabilityNegotiationStage
    {
        AwaitingCapabilities,
        AwaitingAcknowledgement,
        Authenticating,
        Complete,
        Unsupported
    }

    private enum SaslRegistrationStage
    {
        Disabled,
        Pending,
        AwaitingChallenge,
        AwaitingResult,
        Complete
    }
}

public sealed record NicknameFallbackEvent(string RejectedNickname, string? NextNickname);
