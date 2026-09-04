using Clircs.Identity;
using Clircs.Networking;
using Clircs.Protocol;
using Clircs.State;

namespace Clircs.Sessions;

public sealed class IrcNetworkSession : IAsyncDisposable
{
    private readonly IrcClientConnection _connection;
    private readonly OutboundEchoTracker _echoTracker = new();
    private readonly AutomaticCtcpReplyLimiter _ctcpReplyLimiter = new();
    private readonly IrcSessionProcessor _processor;
    private int _disposed;
    private SessionDisconnectInfo? _pendingDisconnect;
    private Dictionary<string, string?> _channelsToRestore = new(new IrcNameComparer(IrcCaseMapping.Rfc1459));
    private Dictionary<string, string?> _pendingJoinKeys = new(new IrcNameComparer(IrcCaseMapping.Rfc1459));
    private CancellationTokenSource? _healthMonitor;
    private string? _healthPingToken;
    private DateTimeOffset? _healthPingSentAt;
    private bool _synchronizationCompleted;
    private bool? _initialBouncerAwayState;
    private int _synchronizationVersion;
    private TaskCompletionSource<bool>? _registrationCompletion;
    private bool _bouncerMetadataProbeStarted;
    private bool _connectionAttemptInProgress;

    public IrcNetworkSession(
        string displayName,
        IrcConnectionOptions options,
        IIrcTransportFactory transportFactory,
        Func<string?>? versionQuote = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        Options = options.Validate();
        State = new NetworkSessionState(NetworkSessionId.New(), displayName, IrcCaseMapping.Rfc1459);
        State.ResetForReconnect(Options.Endpoint.UseTls);
        _processor = new IrcSessionProcessor(State, options.Identity.Nicknames[0]);
        _connection = new IrcClientConnection(transportFactory);
        _connection.MessageReceived += OnMessageReceivedAsync;
        _connection.Diagnostic += OnDiagnostic;
        _connection.NicknameFallback += OnNicknameFallback;
        _connection.ConnectionClosed += OnConnectionClosed;
        _connection.SaslAuthenticationChanged += OnSaslAuthenticationChanged;
        _connection.WireLineTransferred += OnWireLineTransferred;
    }

    public event Action<SessionEvent>? EventRaised;

    public event Action<IrcNetworkSession>? RegistrationCompleted;

    public event Action<IrcNetworkSession>? SynchronizationCompleted;

    public event Action<IrcNetworkSession, SessionDisconnectInfo>? Disconnected;

    public event Action<IrcNetworkSession>? ServerFeaturesUpdated;

    public event Action<IrcNetworkSession>? StateChanged;

    public event Action<IrcNetworkSession, ChannelState>? ChannelSynchronized;

    public event Action<IrcNetworkSession, ChannelState>? ChannelNamesSynchronized;

    public event Action<IrcNetworkSession, ChannelState, ChannelMemberState>? ChannelMemberJoined;

    public event Action<IrcNetworkSession, IReadOnlyList<string>>? IsonReplyReceived;

    public event Action<IrcNetworkSession, bool, IReadOnlyList<string>>? MonitorStatusReceived;

    public event Action<IrcNetworkSession, IrcWireLine>? WireLineTransferred;

    public NetworkSessionState State { get; }

    public IrcConnectionOptions Options { get; private set; }

    public IrcConnectionState ConnectionState => _connection.State;

    public string CurrentNickname => _processor.CurrentNickname;

    public ServerFeatures Features => _processor.Features;

    public IReadOnlyDictionary<string, string?> ChannelsToRestore => _channelsToRestore;

    public bool IsSynchronizing => _connection.State == IrcConnectionState.Online && !_synchronizationCompleted;

    public Guid BeginWhoRequest(IReadOnlyList<string> arguments, bool automatic = false) =>
        _processor.BeginWhoRequest(arguments, automatic);

    public void CancelWhoRequest(Guid requestId) => _processor.CancelWhoRequest(requestId);

    public Guid BeginWhoisRequest(string nickname, bool includeIdle, bool automatic = false) =>
        _processor.BeginWhoisRequest(nickname, includeIdle, automatic);

    public void CancelWhoisRequest(Guid requestId) => _processor.CancelWhoisRequest(requestId);

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _pendingDisconnect = null;
        _synchronizationCompleted = false;
        _initialBouncerAwayState = null;
        _synchronizationVersion = 0;
        _bouncerMetadataProbeStarted = false;
        var registrationCompletion =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _registrationCompletion = registrationCompletion;
        Raise(State.StatusBuffer, SessionEventKind.Status, $"Connecting to {Options.Endpoint}...");
        _connectionAttemptInProgress = true;
        try
        {
            await _connection.ConnectAsync(Options, cancellationToken).ConfigureAwait(false);
            Raise(State.StatusBuffer, SessionEventKind.Status, $"Connected to {Options.Endpoint}; registering as {_connection.CurrentNickname}.");
            await registrationCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_registrationCompletion, registrationCompletion) &&
                _connection.State is not (IrcConnectionState.Disconnected or IrcConnectionState.Failed))
            {
                await _connection.AbortAsync(exception).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            _connectionAttemptInProgress = false;
        }
    }

    public async ValueTask ReconnectAsync(
        IrcConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (_connection.State is not (IrcConnectionState.Disconnected or IrcConnectionState.Failed))
        {
            await DisconnectAsync("Reconnecting", cancellationToken).ConfigureAwait(false);
        }
        Options = (options ?? Options).Validate();
        State.ResetForReconnect(Options.Endpoint.UseTls);
        _processor.ResetForReconnect(Options.Identity.Nicknames[0]);
        ReindexChannelsToRestore();
        _pendingJoinKeys.Clear();
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public void PrepareJoin(string channel, string? key = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        _pendingJoinKeys[channel] = string.IsNullOrWhiteSpace(key) ? null : key;
    }

    public void ForgetJoin(string channel)
    {
        _pendingJoinKeys.Remove(channel);
        _channelsToRestore.Remove(channel);
    }

    public ValueTask SendAsync(
        string command,
        IReadOnlyList<string> parameters,
        IrcOutboundPriority priority = IrcOutboundPriority.Interactive,
        CancellationToken cancellationToken = default) =>
        _connection.SendAsync(command, parameters, priority, cancellationToken);

    public ValueTask SendRawAsync(string rawLine, CancellationToken cancellationToken = default) =>
        _connection.SendRawAsync(rawLine, IrcOutboundPriority.Interactive, cancellationToken);

    public ValueTask SendNicknameAsync(string nickname, CancellationToken cancellationToken = default) =>
        _connection.SendNicknameAsync(nickname, cancellationToken);

    public async ValueTask SendMessageAsync(
        string target,
        string text,
        CancellationToken cancellationToken = default,
        bool createQueryBuffer = true)
    {
        var pendingEcho = _echoTracker.Track("PRIVMSG", target, text);
        try
        {
            await SendAsync("PRIVMSG", [target, text], cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _echoTracker.Cancel(pendingEcho);
            throw;
        }

        var displayTarget = Features.NormalizeMessageTarget(target);
        BufferState? buffer;
        if (Features.IsChannel(displayTarget))
        {
            buffer = State.GetOrCreateBuffer(BufferKind.Channel, displayTarget);
        }
        else if (State.TryGetBuffer(target, out var existingQuery))
        {
            buffer = existingQuery;
        }
        else if (createQueryBuffer)
        {
            buffer = State.GetOrCreateBuffer(BufferKind.Query, target);
        }
        else
        {
            buffer = null;
        }
        string? nickPrefix = null;
        if (Features.IsChannel(displayTarget) && State.TryGetChannel(displayTarget, out var messageChannel) &&
            messageChannel!.TryGetMember(CurrentNickname, out var messageMember))
        {
            nickPrefix = Features.HighestPrefix(messageMember!.PrefixModes)?.ToString();
        }
        if (buffer is null) return;
        var formattedText = IrcTextFormatting.Parse(text);
        Raise(buffer, SessionEventKind.Message, $"<{nickPrefix}{CurrentNickname}> {text}",
            new Dictionary<string, string?>
            {
                ["nick"] = CurrentNickname,
                ["nickPrefix"] = nickPrefix,
                ["message"] = formattedText.PlainText
            },
            formattedText);
    }

    public async ValueTask SendNoticeAsync(string target, string text, CancellationToken cancellationToken = default)
    {
        var pendingEcho = _echoTracker.Track("NOTICE", target, text);
        try
        {
            await SendAsync("NOTICE", [target, text], cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _echoTracker.Cancel(pendingEcho);
            throw;
        }

        var buffer = ResolveNoticeBuffer(target);
        Raise(buffer, SessionEventKind.Notice, $"->{target}<- {text}");
    }

    internal BufferState ResolveNoticeBuffer(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var displayTarget = Features.NormalizeMessageTarget(target);
        return Features.IsChannel(displayTarget) && State.TryGetBuffer(displayTarget, out var existing)
            ? existing!
            : State.StatusBuffer;
    }

    public async ValueTask SendActionAsync(string target, string text, CancellationToken cancellationToken = default)
    {
        var wireText = $"\u0001ACTION {text}\u0001";
        var pendingEcho = _echoTracker.Track("PRIVMSG", target, wireText);
        try
        {
            await SendAsync("PRIVMSG", [target, wireText], cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _echoTracker.Cancel(pendingEcho);
            throw;
        }

        var displayTarget = Features.NormalizeMessageTarget(target);
        var buffer = Features.IsChannel(displayTarget)
            ? State.GetOrCreateBuffer(BufferKind.Channel, displayTarget)
            : State.GetOrCreateBuffer(BufferKind.Query, target);
        var formattedText = IrcTextFormatting.Parse(text);
        Raise(buffer, SessionEventKind.Action, $"* {CurrentNickname} {formattedText.PlainText}",
            new Dictionary<string, string?>
            {
                ["nick"] = CurrentNickname,
                ["message"] = formattedText.PlainText
            },
            formattedText);
    }

    public ValueTask DisconnectAsync(string reason = "Leaving", CancellationToken cancellationToken = default)
    {
        CaptureJoinedChannels();
        _pendingDisconnect = new SessionDisconnectInfo(
            SessionDisconnectKind.Intentional,
            "Disconnected by user.",
            RetryRecommended: false);
        return _connection.DisconnectAsync(reason, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _healthMonitor?.Cancel();
        _healthMonitor?.Dispose();
        _healthMonitor = null;
        _connection.MessageReceived -= OnMessageReceivedAsync;
        _connection.WireLineTransferred -= OnWireLineTransferred;
        _connection.Diagnostic -= OnDiagnostic;
        _connection.NicknameFallback -= OnNicknameFallback;
        _connection.ConnectionClosed -= OnConnectionClosed;
        _connection.SaslAuthenticationChanged -= OnSaslAuthenticationChanged;
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask OnMessageReceivedAsync(IrcMessage message)
    {
        if (message.Command == "KILL" && message.Parameters.Count >= 1 &&
            new IrcNameComparer(State.CaseMapping).Equals(message.Parameters[0], CurrentNickname))
        {
            var actor = IrcSessionProcessor.NickFromPrefix(message.Prefix);
            var reason = message.Parameters.Count > 1 ? message.Parameters[1] : "No reason given";
            _pendingDisconnect = new SessionDisconnectInfo(
                SessionDisconnectKind.Killed,
                $"Killed by {actor}: {reason}",
                Actor: actor,
                Reason: reason);
        }
        else if (message.Command is "464" or "465" or "466")
        {
            var authenticationFailure = message.Command == "464";
            var detail = message.Parameters.Count > 0 ? message.Parameters[^1] : "The server refused this connection.";
            _pendingDisconnect = new SessionDisconnectInfo(
                SessionDisconnectKind.Accidental,
                authenticationFailure ? $"Authentication failed: {detail}" : detail,
                RetryRecommended: false);
            _registrationCompletion?.TrySetException(new IrcProtocolException(
                authenticationFailure ? $"Authentication failed: {detail}" : detail));
        }
        else if (message.Command == "ERROR" && _pendingDisconnect?.Kind != SessionDisconnectKind.Killed &&
            message.Parameters.Count > 0 &&
            message.Parameters[^1].Contains("Killed", StringComparison.OrdinalIgnoreCase))
        {
            var reason = message.Parameters[^1];
            _pendingDisconnect = new SessionDisconnectInfo(
                SessionDisconnectKind.Killed,
                reason,
                Reason: reason);
        }

        if (_echoTracker.TryConsume(message, _processor.CurrentNickname, State.CaseMapping))
        {
            return;
        }

        await RespondToCtcpAsync(message).ConfigureAwait(false);
        var registrationNicknameFailure =
            _connection.State == IrcConnectionState.Registering &&
            message.Command is "433" or "437";
        var processedEvents = IsRegistrationProtocolMessage(message)
            ? []
            : _processor.Process(message);
        if (_synchronizationCompleted)
        {
            StartBouncerMetadataProbe();
        }
        foreach (var processedEvent in processedEvents)
        {
            if (registrationNicknameFailure)
            {
                continue;
            }
            if (!_synchronizationCompleted && State.BouncerName is not null &&
                processedEvent.Fields?.GetValueOrDefault("event") == "away")
            {
                _initialBouncerAwayState =
                    processedEvent.Fields.GetValueOrDefault("away") == "true";
                continue;
            }
            var sessionEvent = processedEvent;
            if (!_synchronizationCompleted && message.Command is "PRIVMSG" or "NOTICE")
            {
                var fields = sessionEvent.Fields?.ToDictionary(entry => entry.Key, entry => entry.Value)
                    ?? new Dictionary<string, string?>();
                fields["replay"] = "true";
                sessionEvent = sessionEvent with { Fields = fields };
            }
            EventRaised?.Invoke(sessionEvent);
        }

        if (message.Command == "PART" && message.Parameters.Count >= 1 &&
            new IrcNameComparer(State.CaseMapping).Equals(
                IrcSessionProcessor.NickFromPrefix(message.Prefix),
                CurrentNickname))
        {
            ForgetJoin(message.Parameters[0]);
        }
        else if (message.Command == "KICK" && message.Parameters.Count >= 2 &&
            new IrcNameComparer(State.CaseMapping).Equals(message.Parameters[1], CurrentNickname))
        {
            ForgetJoin(message.Parameters[0]);
        }

        if (message.Command == "001")
        {
            _connectionAttemptInProgress = false;
            _registrationCompletion?.TrySetResult(true);
            RegistrationCompleted?.Invoke(this);
            StartHealthMonitor();
            ScheduleSynchronizationCompletion();
        }

        if (message.Command is "376" or "422")
        {
            ScheduleSynchronizationCompletion();
        }

        if (message.Command == "PONG" && _healthPingToken is not null &&
            message.Parameters.Any(parameter => parameter.Contains(_healthPingToken, StringComparison.Ordinal)))
        {
            _healthPingToken = null;
            _healthPingSentAt = null;
        }

        if (!_synchronizationCompleted && message.Command is "PRIVMSG" or "NOTICE")
        {
            ScheduleSynchronizationCompletion();
        }

        if (message.Command == "005")
        {
            ReindexChannelsToRestore();
            ServerFeaturesUpdated?.Invoke(this);
        }

        if (message.Command == "366" && message.Parameters.Count >= 2 &&
            State.TryGetChannel(message.Parameters[1], out var namesChannel))
        {
            ChannelNamesSynchronized?.Invoke(this, namesChannel!);
        }

        if (message.Command == "315" &&
            processedEvents.Any(sessionEvent => sessionEvent.Fields?.GetValueOrDefault("automatic") == "true") &&
            message.Parameters.Count >= 2 &&
            State.TryGetChannel(message.Parameters[1], out var synchronizedChannel))
        {
            ChannelSynchronized?.Invoke(this, synchronizedChannel!);
        }

        if (message.Command == "JOIN" && message.Parameters.Count >= 1)
        {
            var nickname = IrcSessionProcessor.NickFromPrefix(message.Prefix);
            if (State.TryGetChannel(message.Parameters[0], out var joinedChannel) &&
                joinedChannel!.TryGetMember(nickname, out var joinedMember))
            {
                ChannelMemberJoined?.Invoke(this, joinedChannel, joinedMember!);
            }
            if (new IrcNameComparer(State.CaseMapping).Equals(nickname, CurrentNickname))
            {
                var channel = message.Parameters[0];
                _pendingJoinKeys.Remove(channel, out var key);
                _channelsToRestore[channel] = key;
                await SendAsync("MODE", [message.Parameters[0]], IrcOutboundPriority.Automation).ConfigureAwait(false);
            }
        }

        if (message.Command == "303")
        {
            var online = message.Parameters.Count == 0
                ? []
                : message.Parameters[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            IsonReplyReceived?.Invoke(this, online);
        }

        if (message.Command is "730" or "731")
        {
            var nicknames = message.Parameters.Count == 0
                ? []
                : message.Parameters[^1]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(IrcSessionProcessor.NickFromPrefix)
                    .Where(nickname => nickname.Length > 0)
                    .Distinct(new IrcNameComparer(State.CaseMapping))
                    .ToArray();
            MonitorStatusReceived?.Invoke(this, message.Command == "730", nicknames);
        }

        StateChanged?.Invoke(this);
    }

    private async ValueTask RespondToCtcpAsync(IrcMessage message)
    {
        if (message.Command != "PRIVMSG" || message.Parameters.Count < 2)
        {
            return;
        }

        var text = message.Parameters[1];
        if (text.Length < 2 || text[0] != '\u0001' || text[^1] != '\u0001')
        {
            return;
        }

        var request = text[1..^1];
        var separator = request.IndexOf(' ');
        var command = (separator < 0 ? request : request[..separator]).ToUpperInvariant();
        var argument = separator < 0 ? string.Empty : request[(separator + 1)..];
        var reply = command switch
        {
            "PING" => argument.Length == 0 ? "PING" : $"PING {argument}",
            "VERSION" => VersionReply(),
            "TIME" => $"TIME {DateTimeOffset.Now:F}",
            _ => null
        };

        if (reply is null)
        {
            return;
        }

        var sender = IrcSessionProcessor.NickFromPrefix(message.Prefix);
        var source = message.Prefix ?? sender;
        if (!_ctcpReplyLimiter.TryAcquire(source, DateTimeOffset.UtcNow, out var reportSuppression))
        {
            if (reportSuppression)
            {
                Raise(
                    State.StatusBuffer,
                    SessionEventKind.Status,
                    "Automatic CTCP replies are being suppressed due to flooding",
                    new Dictionary<string, string?>
                    {
                        ["event"] = "ctcp.replySuppressed",
                        ["suppressActivity"] = "true"
                    });
            }
            return;
        }
        await SendAsync("NOTICE", [sender, $"\u0001{reply}\u0001"], IrcOutboundPriority.Control).ConfigureAwait(false);
    }

    private static string VersionReply() => $"VERSION {ProductInfo.DisplayName}";

    private void OnDiagnostic(string message) => Raise(State.StatusBuffer, SessionEventKind.Diagnostic, message);

    private void OnWireLineTransferred(IrcWireLine wireLine) => WireLineTransferred?.Invoke(this, wireLine);

    private void OnNicknameFallback(NicknameFallbackEvent fallback)
    {
        if (fallback.NextNickname is { } nextNickname)
        {
            Raise(
                State.StatusBuffer,
                SessionEventKind.Status,
                $"Nickname {fallback.RejectedNickname} is in use; trying alternate {nextNickname}.",
                new Dictionary<string, string?>
                {
                    ["routeActive"] = "true",
                    ["event"] = "nicknameFallback"
                });
            return;
        }

        Raise(
            State.StatusBuffer,
            SessionEventKind.Error,
            $"Nickname {fallback.RejectedNickname} is also unavailable. Choose a different one.",
            new Dictionary<string, string?>
            {
                ["routeActive"] = "true",
                ["event"] = "nicknameRequired",
                ["prefill"] = "/nick "
            });
    }

    private void OnSaslAuthenticationChanged(SaslAuthenticationEvent authentication)
    {
        var text = authentication.Succeeded
            ? authentication.Mechanism == SaslMechanisms.Plain
                ? $"SASL authentication successful for account {authentication.Identity}"
                : "SASL EXTERNAL authentication successful using the TLS client certificate"
            : authentication.Required
                ? $"SASL authentication failed: {authentication.Detail}"
                : $"SASL authentication failed: {authentication.Detail}; continuing without authentication";
        Raise(
            State.StatusBuffer,
            authentication.Succeeded ? SessionEventKind.Status : SessionEventKind.Error,
            text,
            new Dictionary<string, string?>
            {
                ["event"] = authentication.Succeeded ? "sasl.success" : "sasl.failure",
                ["mechanism"] = authentication.Mechanism,
                ["account"] = authentication.Identity,
                ["required"] = authentication.Required ? "true" : "false",
                ["suppressActivity"] = "true"
            });
    }

    private static bool IsRegistrationProtocolMessage(IrcMessage message) =>
        message.Command is "CAP" or "AUTHENTICATE" or "902" or "903" or "904" or "905" or "906" or "907" or "908" ||
        message.Command == "421" &&
        message.Parameters.Count >= 2 &&
        message.Parameters[1].Equals("CAP", StringComparison.OrdinalIgnoreCase);

    private void OnConnectionClosed(Exception? exception)
    {
        _healthMonitor?.Cancel();
        _healthMonitor?.Dispose();
        _healthMonitor = null;
        _healthPingToken = null;
        _healthPingSentAt = null;
        CaptureJoinedChannels();
        var info = _pendingDisconnect ?? (exception is IrcSaslException saslFailure
            ? new SessionDisconnectInfo(
                SessionDisconnectKind.Accidental,
                saslFailure.Message,
                saslFailure,
                RetryRecommended: false)
            : new SessionDisconnectInfo(
                SessionDisconnectKind.Accidental,
                NormalizeDisconnectMessage(exception),
                exception));
        info = info with { AnnounceToBuffers = !_connectionAttemptInProgress };
        _pendingDisconnect = null;
        _registrationCompletion?.TrySetException(
            exception ?? new IOException(info.Message));
        if (!_connectionAttemptInProgress &&
            !info.Message.StartsWith("Authentication failed:", StringComparison.OrdinalIgnoreCase))
        {
            Raise(
                State.StatusBuffer,
                info.Kind == SessionDisconnectKind.Intentional ? SessionEventKind.Status : SessionEventKind.Error,
                info.Kind == SessionDisconnectKind.Intentional
                    ? "Disconnected"
                    : $"Connection lost: {info.Message}");
        }
        Disconnected?.Invoke(this, info);
    }

    private static string NormalizeDisconnectMessage(Exception? exception)
    {
        if (exception is null) return "The server closed the connection.";
        var message = exception.Message;
        return message.Contains("forcibly closed by the remote host", StringComparison.OrdinalIgnoreCase)
            ? "Remote host closed the connection."
            : message;
    }

    private void CaptureJoinedChannels()
    {
        foreach (var channel in State.Channels)
        {
            _channelsToRestore.TryAdd(channel.Name, null);
        }
    }

    private void ReindexChannelsToRestore()
    {
        var reindexed = new Dictionary<string, string?>(new IrcNameComparer(State.CaseMapping));
        foreach (var (channel, key) in _channelsToRestore) reindexed[channel] = key;
        _channelsToRestore = reindexed;
        var pending = new Dictionary<string, string?>(new IrcNameComparer(State.CaseMapping));
        foreach (var (channel, key) in _pendingJoinKeys) pending[channel] = key;
        _pendingJoinKeys = pending;
    }

    private void CompleteSynchronization()
    {
        if (_synchronizationCompleted)
        {
            return;
        }
        _synchronizationCompleted = true;
        if (_initialBouncerAwayState == true)
        {
            Raise(
                State.StatusBuffer,
                SessionEventKind.Status,
                "You are now marked away.",
                new Dictionary<string, string?>
                {
                    ["event"] = "away",
                    ["away"] = "true"
                });
        }
        _initialBouncerAwayState = null;
        StartBouncerMetadataProbe();
        SynchronizationCompleted?.Invoke(this);
    }

    private void StartBouncerMetadataProbe()
    {
        if (_bouncerMetadataProbeStarted ||
            !_synchronizationCompleted ||
            State.BouncerName is null ||
            _connection.State != IrcConnectionState.Online)
        {
            return;
        }

        _bouncerMetadataProbeStarted = true;
        _ = ProbeBouncerMetadataAsync();
    }

    private async Task ProbeBouncerMetadataAsync()
    {
        if (string.Equals(
        State.BouncerName,
        "Irssi Proxy",
        StringComparison.OrdinalIgnoreCase))
        {
            _processor.BeginAutomaticVersionProbe();

            try
            {
                await SendAsync(
                    "VERSION",
                    [],
                    IrcOutboundPriority.Automation).ConfigureAwait(false);
            }
            catch
            {
                _processor.CancelAutomaticVersionProbe();
            }
        }

        var requestId = BeginWhoisRequest(
            CurrentNickname,
            includeIdle: false,
            automatic: true);

        try
        {
            await SendAsync(
                "WHOIS",
                [CurrentNickname],
                IrcOutboundPriority.Automation).ConfigureAwait(false);
        }
        catch
        {
            CancelWhoisRequest(requestId);
        }
    }

    private void ScheduleSynchronizationCompletion()
    {
        if (_synchronizationCompleted || _healthMonitor is null)
        {
            return;
        }
        var version = Interlocked.Increment(ref _synchronizationVersion);
        _ = CompleteSynchronizationAfterDelayAsync(version, _healthMonitor.Token);
    }

    private async Task CompleteSynchronizationAfterDelayAsync(int version, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            if (version == Volatile.Read(ref _synchronizationVersion))
            {
                CompleteSynchronization();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void StartHealthMonitor()
    {
        _healthMonitor?.Cancel();
        _healthMonitor?.Dispose();
        _healthMonitor = new CancellationTokenSource();
        _ = MonitorConnectionHealthAsync(_healthMonitor.Token);
    }

    private async Task MonitorConnectionHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                if (_connection.State != IrcConnectionState.Online)
                {
                    continue;
                }
                if (_healthPingSentAt is { } sentAt)
                {
                    if (DateTimeOffset.UtcNow - sentAt >= TimeSpan.FromSeconds(30))
                    {
                        await _connection.AbortAsync(new TimeoutException("No PONG was received for the connection health check."))
                            .ConfigureAwait(false);
                        return;
                    }
                    continue;
                }
                if (DateTimeOffset.UtcNow - _connection.LastReceivedAt >= TimeSpan.FromSeconds(90))
                {
                    _healthPingToken = $"clircs-{Guid.NewGuid():N}";
                    _healthPingSentAt = DateTimeOffset.UtcNow;
                    await SendAsync("PING", [_healthPingToken], IrcOutboundPriority.Critical, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_connection.State == IrcConnectionState.Online)
            {
                await _connection.AbortAsync(exception).ConfigureAwait(false);
            }
        }
    }

    private void Raise(
        BufferState buffer,
        SessionEventKind kind,
        string text,
        IReadOnlyDictionary<string, string?>? fields = null,
        IrcFormattedText? formattedContent = null) =>
        EventRaised?.Invoke(new SessionEvent(
            State.Id,
            buffer.Id,
            kind,
            TerminalTextSanitizer.Sanitize(text),
            DateTimeOffset.Now,
            fields?.ToDictionary(
                pair => pair.Key,
                pair => pair.Value is null ? null : TerminalTextSanitizer.Sanitize(pair.Value)),
            FormattedContent: formattedContent));
}
