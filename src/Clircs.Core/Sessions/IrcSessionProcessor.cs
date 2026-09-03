using System.Globalization;
using Clircs.Dcc;
using Clircs.Protocol;
using Clircs.State;

namespace Clircs.Sessions;

public sealed class IrcSessionProcessor
{
    private readonly NetworkSessionState _state;
    private readonly SessionEventBuilder _eventBuilder;
    private readonly IdentityQueryResponseProcessor _identityQueries;
    private readonly NetworkQueryResponseProcessor _networkQueries;
    private readonly ChannelListResponseProcessor _channelLists;
    private readonly List<string> _acceptResults = [];
    private PendingMessageGuard? _pendingMessageGuard;

    public IrcSessionProcessor(NetworkSessionState state, string initialNickname)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        ArgumentException.ThrowIfNullOrWhiteSpace(initialNickname);
        CurrentNickname = initialNickname;
        _eventBuilder = new SessionEventBuilder(_state);
        _identityQueries = new IdentityQueryResponseProcessor(_state, Features, _eventBuilder);
        _networkQueries = new NetworkQueryResponseProcessor(_eventBuilder);
        _channelLists = new ChannelListResponseProcessor(_state, _eventBuilder);
    }

    public string CurrentNickname { get; private set; }

    public ServerFeatures Features { get; } = new();

    public void ResetForReconnect(string nickname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);
        CurrentNickname = nickname;
        Features.Reset();
        _identityQueries.Reset();
        _networkQueries.Reset();
        _acceptResults.Clear();
        _pendingMessageGuard = null;
    }

    public Guid BeginWhoRequest(IReadOnlyList<string> arguments, bool automatic = false)
    {
        return _identityQueries.BeginWho(arguments, automatic);
    }

    public void CancelWhoRequest(Guid requestId)
    {
        _identityQueries.CancelWho(requestId);
    }

    public Guid BeginWhoisRequest(string nickname, bool includeIdle, bool automatic = false)
    {
        return _identityQueries.BeginWhois(nickname, includeIdle, automatic);
    }

    public void CancelWhoisRequest(Guid requestId)
    {
        _identityQueries.CancelWhois(requestId);
    }

    public IReadOnlyList<SessionEvent> Process(IrcMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var events = new List<SessionEvent>();
        var now = DateTimeOffset.Now;
        var sender = NickFromPrefix(message.Prefix);
        var senderIdentity = ParsePrefix(message.Prefix);
        ObserveConnectionMetadata(message);

        if (_pendingMessageGuard is { } pending && message.Command != "717")
        {
            events.Add(MessageGuardEvent(pending.Target, pending.Text, notified: false, now));
            _pendingMessageGuard = null;
        }

        if (_identityQueries.TryProcess(message, now, out var identityEvents))
        {
            events.AddRange(identityEvents);
            return events;
        }

        if (_networkQueries.TryProcess(message, now, out var networkEvents))
        {
            events.AddRange(networkEvents);
            return events;
        }

        if (_channelLists.TryProcess(message, now, out var channelListEvents))
        {
            events.AddRange(channelListEvents);
            return events;
        }

        switch (message.Command)
        {
            case "PING":
            case "PONG":
                break;
            case "001":
                if (message.Parameters.Count > 0)
                {
                    CurrentNickname = message.Parameters[0];
                }

                events.Add(Status(SessionEventKind.Server, Last(message), now));
                break;
            case "005":
                var previousCaseMapping = _state.CaseMapping;
                Features.ApplyIsupport(message, _state);
                if (_state.CaseMapping != previousCaseMapping)
                {
                    _identityQueries.ReindexNames();
                }
                events.Add(Status(SessionEventKind.Server, $"Server features: {string.Join(' ', message.Parameters.Skip(1))}", now));
                break;
            case var welcomeNumeric when IsWelcomeNumeric(welcomeNumeric):
                events.Add(Status(SessionEventKind.Server, string.Join(' ', message.Parameters.Skip(1)), now));
                break;
            case "JOIN":
                if (message.Parameters.Count == 0)
                {
                    break;
                }

                var joinedChannel = message.Parameters[0];
                var joinBuffer = _state.GetOrCreateBuffer(BufferKind.Channel, joinedChannel);
                var joinedState = _state.GetOrCreateChannel(joinedChannel);
                joinedState.GetOrAddMember(sender, senderIdentity.Username, senderIdentity.Host);
                if (IsCurrentNickname(sender) && !string.IsNullOrWhiteSpace(senderIdentity.Host))
                {
                    _state.SetVisibleHost(senderIdentity.Host);
                }
                events.Add(Event(joinBuffer, SessionEventKind.Join, $"{sender} joined {joinedChannel}", now,
                    Fields(("nick", sender), ("username", senderIdentity.Username), ("host", senderIdentity.Host),
                        ("channel", joinedChannel), ("self", IsCurrentNickname(sender) ? "true" : null))));
                break;
            case "PART":
                if (message.Parameters.Count == 0)
                {
                    break;
                }

                var partedChannel = message.Parameters[0];
                var partBuffer = _state.GetOrCreateBuffer(BufferKind.Channel, partedChannel);
                if (_state.TryGetChannel(partedChannel, out var partedState))
                {
                    partedState!.RemoveMember(sender);
                }
                var partReason = message.Parameters.Count > 1 ? $" ({message.Parameters[1]})" : string.Empty;
                events.Add(Event(partBuffer, SessionEventKind.Part, $"{sender} left {partedChannel}{partReason}", now,
                    Fields(("nick", sender), ("username", senderIdentity.Username), ("host", senderIdentity.Host),
                        ("channel", partedChannel), ("reason", message.Parameters.Count > 1 ? message.Parameters[1] : null),
                        ("self", IsCurrentNickname(sender) ? "true" : null))));
                if (IsCurrentNickname(sender))
                {
                    _state.RemoveChannel(partedChannel);
                }
                break;
            case "QUIT":
                var quitReason = message.Parameters.Count > 0 ? $" ({message.Parameters[0]})" : string.Empty;
                foreach (var channel in _state.Channels.Where(channel => channel.RemoveMember(sender)).ToArray())
                {
                    var quitBuffer = _state.GetOrCreateBuffer(BufferKind.Channel, channel.Name);
                    events.Add(Event(quitBuffer, SessionEventKind.Part, $"{sender} quit{quitReason}", now,
                        Fields(("nick", sender), ("username", senderIdentity.Username), ("host", senderIdentity.Host),
                            ("channel", channel.Name), ("reason", message.Parameters.Count > 0 ? message.Parameters[0] : null), ("event", "quit"))));
                }
                break;
            case "KILL":
                if (message.Parameters.Count >= 1 && IsCurrentNickname(message.Parameters[0]))
                {
                    var reason = message.Parameters.Count > 1 ? message.Parameters[1] : "No reason given";
                    events.Add(Status(
                        SessionEventKind.Error,
                        $"You were killed by {sender}: {reason}",
                        now,
                        Fields(("event", "kill"), ("actor", sender), ("reason", reason), ("self", "true"))));
                }
                break;
            case "KICK":
                if (message.Parameters.Count >= 2)
                {
                    var kickChannel = message.Parameters[0];
                    var kickedNick = message.Parameters[1];
                    var kickReason = message.Parameters.Count > 2 ? $" ({message.Parameters[2]})" : string.Empty;
                    var kickBuffer = _state.GetOrCreateBuffer(BufferKind.Channel, kickChannel);
                    if (_state.TryGetChannel(kickChannel, out var kickState))
                    {
                        kickState!.RemoveMember(kickedNick);
                    }
                    var selfKick = IsCurrentNickname(kickedNick);
                    events.Add(Event(kickBuffer, SessionEventKind.Part,
                        selfKick ? $"You were kicked by {sender}{kickReason}" : $"{kickedNick} was kicked by {sender}{kickReason}",
                        now,
                        Fields(("nick", kickedNick), ("actor", sender), ("channel", kickChannel),
                            ("reason", message.Parameters.Count > 2 ? message.Parameters[2] : null), ("event", "kick"),
                            ("self", selfKick ? "true" : null))));
                    if (selfKick)
                    {
                        _state.RemoveChannel(kickChannel);
                    }
                }
                break;
            case "PRIVMSG":
                ProcessPrivmsg(message, sender, now, events);
                break;
            case "NOTICE":
                ProcessNotice(message, sender, now, events);
                break;
            case "INVITE":
                if (message.Parameters.Count >= 2)
                {
                    events.Add(Status(
                        SessionEventKind.Notice,
                        $"{sender} invited you to {message.Parameters[1]}",
                        now,
                        Fields(("event", "invite"), ("nick", sender), ("username", senderIdentity.Username),
                            ("host", senderIdentity.Host), ("channel", message.Parameters[1]), ("private", "true"),
                            ("outputFamily", "invite"), ("routeConfigured", "true"))));
                }
                break;
            case "305":
                _state.SetAway(false);
                events.Add(Status(
                    SessionEventKind.Status,
                    "You are no longer marked away",
                    now,
                    Fields(("event", "away"), ("away", "false"), ("routeActive", "true"))));
                break;
            case "306":
                _state.SetAway(true);
                events.Add(Status(
                    SessionEventKind.Status,
                    "You are now marked away",
                    now,
                    Fields(("event", "away"), ("away", "true"), ("routeActive", "true"))));
                break;
            case "464":
                events.Add(Status(
                    SessionEventKind.Error,
                    $"Authentication failed: {Last(message).TrimEnd('.')} Automatic reconnect was not started",
                    now,
                    Fields(("event", "authentication"), ("numeric", "464"))));
                break;
            case "502":
                events.Add(Status(
                    SessionEventKind.Error,
                    $"Could not set user modes: {Last(message)}",
                    now,
                    Fields(("event", "usermodes"), ("numeric", "502"))));
                break;
            case "302":
                foreach (var entry in Last(message).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var equals = entry.IndexOf('=');
                    var at = entry.LastIndexOf('@');
                    if (equals <= 0 || at <= equals || at + 1 >= entry.Length) continue;
                    var nickname = entry[..equals].TrimEnd('*');
                    if (IsCurrentNickname(nickname))
                    {
                        _state.SetVisibleHost(entry[(at + 1)..]);
                        break;
                    }
                }
                break;
            case "900":
                if (message.Parameters.Count >= 3)
                {
                    var account = message.Parameters[2];
                    _state.SetAccountName(account);
                    var identity = message.Parameters[1];
                    var hostSeparator = identity.LastIndexOf('@');
                    if (hostSeparator >= 0 && hostSeparator + 1 < identity.Length)
                    {
                        _state.SetVisibleHost(identity[(hostSeparator + 1)..]);
                    }
                    events.Add(Status(
                        SessionEventKind.Status,
                        $"Logged in as {account}",
                        now,
                        Fields(("event", "account.login"), ("account", account), ("numeric", "900"))));
                }
                break;
            case "396":
                if (message.Parameters.Count >= 2)
                {
                    var visibleHost = message.Parameters[1];
                    _state.SetVisibleHost(visibleHost);
                    var source = message.Parameters.Count >= 3
                        ? ParentheticalDetail(Last(message))
                        : null;
                    var suffix = source is null
                        ? string.Empty
                        : $" ({char.ToLowerInvariant(source[0])}{source[1..]})";
                    events.Add(Status(
                        SessionEventKind.Status,
                        $"Hidden host is now {visibleHost}{suffix}",
                        now,
                        Fields(("event", "host.hidden"), ("host", visibleHost), ("numeric", "396"))));
                }
                break;
            case "NICK":
                if (message.Parameters.Count == 0)
                {
                    break;
                }

                var newNickname = message.Parameters[0];
                var selfNickChange = new IrcNameComparer(_state.CaseMapping).Equals(sender, CurrentNickname);
                if (selfNickChange)
                {
                    CurrentNickname = newNickname;
                }

                var nickChannels = _state.Channels.Where(channel => channel.RenameMember(sender, newNickname)).ToArray();
                if (selfNickChange)
                {
                    foreach (var buffer in _state.Buffers.Where(buffer =>
                                 buffer.Kind is BufferKind.Status or BufferKind.Channel or BufferKind.Query or BufferKind.Results))
                    {
                        events.Add(Event(buffer, SessionEventKind.Nick, $"You are now known as {newNickname}", now,
                            Fields(("oldNick", sender), ("newNick", newNickname), ("self", "true"))));
                    }
                }
                else if (nickChannels.Length == 0)
                {
                    events.Add(Status(SessionEventKind.Nick, $"{sender} is now known as {newNickname}", now));
                }
                else
                {
                    foreach (var channel in nickChannels)
                    {
                        events.Add(Event(
                            _state.GetOrCreateBuffer(BufferKind.Channel, channel.Name),
                            SessionEventKind.Nick,
                            $"{sender} is now known as {newNickname}",
                            now,
                            Fields(("oldNick", sender), ("newNick", newNickname), ("username", senderIdentity.Username),
                                ("host", senderIdentity.Host), ("channel", channel.Name))));
                    }
                }
                break;
            case "TOPIC":
                if (message.Parameters.Count >= 2)
                {
                    var topicBuffer = _state.GetOrCreateBuffer(BufferKind.Channel, message.Parameters[0]);
                    var formattedTopic = IrcTextFormatting.Parse(message.Parameters[1]);
                    _state.GetOrCreateChannel(message.Parameters[0]).Topic = message.Parameters[1];
                    events.Add(Event(
                        topicBuffer,
                        SessionEventKind.Topic,
                        $"{sender} changed the topic to: {formattedTopic.PlainText}",
                        now,
                        Fields(("channel", message.Parameters[0]), ("topic", formattedTopic.PlainText)),
                        formattedContent: formattedTopic));
                }

                break;
            case "MODE":
                if (message.Parameters.Count >= 2)
                {
                    var modeTarget = message.Parameters[0];
                    var modeChange = string.Join(' ', message.Parameters.Skip(1));
                    if (Features.IsChannel(modeTarget))
                    {
                        var modeBuffer = _state.GetOrCreateBuffer(BufferKind.Channel, modeTarget);
                        ApplyModes(_state.GetOrCreateChannel(modeTarget), message.Parameters.Skip(1).ToArray(), reset: false);
                        events.Add(Event(modeBuffer, SessionEventKind.Mode, $"{sender} sets mode {modeChange}", now,
                            Fields(("actor", sender), ("channel", modeTarget), ("modes", message.Parameters[1]),
                                ("parameters", string.Join(' ', message.Parameters.Skip(2))))));
                    }
                    else
                    {
                        if (new IrcNameComparer(_state.CaseMapping).Equals(modeTarget, CurrentNickname))
                        {
                            _state.ApplyUserModes(message.Parameters[1]);
                        }
                        events.Add(Status(SessionEventKind.Mode, $"{sender} sets mode {modeChange} on {modeTarget}", now));
                    }
                }

                break;
            case "332":
                if (message.Parameters.Count >= 3)
                {
                    var topicBuffer = _state.GetOrCreateBuffer(BufferKind.Channel, message.Parameters[1]);
                    var formattedTopic = IrcTextFormatting.Parse(message.Parameters[2]);
                    _state.GetOrCreateChannel(message.Parameters[1]).Topic = message.Parameters[2];
                    events.Add(Event(
                        topicBuffer,
                        SessionEventKind.Topic,
                        $"Topic: {formattedTopic.PlainText}",
                        now,
                        Fields(("channel", message.Parameters[1]), ("topic", formattedTopic.PlainText)),
                        formattedContent: formattedTopic));
                }

                break;
            case "333":
                if (message.Parameters.Count >= 4)
                {
                    var topicChannel = message.Parameters[1];
                    var topicSetter = NickFromPrefix(message.Parameters[2]);
                    DateTimeOffset? topicSetAt = long.TryParse(message.Parameters[3], out var topicTimestamp)
                        ? DateTimeOffset.FromUnixTimeSeconds(topicTimestamp).ToLocalTime()
                        : null;
                    var topicState = _state.GetOrCreateChannel(topicChannel);
                    topicState.TopicSetBy = topicSetter;
                    topicState.TopicSetAt = topicSetAt;
                    var setAtText = topicSetAt is null
                        ? string.Empty
                        : $" on {topicSetAt:yyyy-MM-dd 'at' HH:mm:ss}";
                    events.Add(Event(
                        _state.GetOrCreateBuffer(BufferKind.Channel, topicChannel),
                        SessionEventKind.Topic,
                        $"Set by: {topicSetter}{setAtText}",
                        now,
                        Fields(("setter", topicSetter), ("timestamp", message.Parameters[3]), ("channel", topicChannel))));
                }
                break;
            case "341":
                if (message.Parameters.Count >= 2)
                {
                    var invitedNick = message.Parameters[^2];
                    var invitedChannel = message.Parameters[^1];
                    events.Add(Status(
                        SessionEventKind.Notice,
                        $"Invited {invitedNick} to {invitedChannel}",
                        now,
                        Fields(("event", "invite.confirmation"), ("nick", invitedNick),
                            ("channel", invitedChannel), ("numeric", "341"),
                            ("outputFamily", "invite"), ("routeConfigured", "true"),
                            ("outputEnd", "true"))));
                }
                break;
            case "329":
                if (message.Parameters.Count >= 3)
                {
                    var createdChannel = message.Parameters[1];
                    DateTimeOffset? createdAt = long.TryParse(message.Parameters[2], out var createdTimestamp)
                        ? DateTimeOffset.FromUnixTimeSeconds(createdTimestamp).ToLocalTime()
                        : null;
                    _state.GetOrCreateChannel(createdChannel).CreatedAt = createdAt;
                    events.Add(Event(
                        _state.GetOrCreateBuffer(BufferKind.Channel, createdChannel),
                        SessionEventKind.ChannelInfo,
                        createdAt is null ? "Created: unknown" : $"Created: {createdAt:yyyy-MM-dd 'at' HH:mm:ss}",
                        now,
                        Fields(("timestamp", message.Parameters[2]), ("channel", createdChannel))));
                }
                break;
            case "328":
                if (message.Parameters.Count >= 3)
                {
                    var urlChannel = message.Parameters[1];
                    events.Add(Event(
                        _state.GetOrCreateBuffer(BufferKind.Channel, urlChannel),
                        SessionEventKind.ChannelInfo,
                        $"URL: {message.Parameters[2]}",
                        now,
                        Fields(("channel", urlChannel), ("url", message.Parameters[2]), ("numeric", "328"))));
                }
                break;
            case "324":
                if (message.Parameters.Count >= 3)
                {
                    ApplyModes(_state.GetOrCreateChannel(message.Parameters[1]), message.Parameters.Skip(2).ToArray(), reset: true);
                }

                break;
            case "221":
                if (message.Parameters.Count >= 2)
                {
                    _state.ApplyUserModes(message.Parameters[1], reset: true);
                    events.Add(Status(SessionEventKind.Mode, $"User modes: {string.Join(' ', message.Parameters.Skip(1))}", now));
                }

                break;
            case "470":
                if (message.Parameters.Count >= 3)
                {
                    var requestedChannel = message.Parameters[1];
                    var forwardedChannel = message.Parameters[2];
                    events.Add(Event(
                        _state.GetOrCreateBuffer(BufferKind.Channel, forwardedChannel),
                        SessionEventKind.ChannelInfo,
                        $"Forwarded from {requestedChannel} to {forwardedChannel}",
                        now,
                        Fields(("channel", forwardedChannel), ("forwardFrom", requestedChannel),
                            ("numeric", "470"), ("joinForward", "true"))));
                }
                break;
            case "433":
                var usedNick = message.Parameters.Count >= 2 ? message.Parameters[1] : "unknown";
                events.Add(Status(SessionEventKind.Error, $"Nickname is already in use: {usedNick}", now,
                    Fields(("routeActive", "true"))));
                break;
            case "437":
                var unavailableTarget = message.Parameters.Count >= 2 ? message.Parameters[1] : "unknown";
                var unavailableKind = Features.IsChannel(unavailableTarget) ? "Channel" : "Nickname";
                events.Add(Status(SessionEventKind.Error,
                    $"{unavailableKind} is temporarily unavailable: {unavailableTarget}", now,
                    Fields(("routeActive", "true"))));
                break;
            case "303":
                // ISON replies are consumed by the session's internal notify monitor.
                break;
            case "730":
            case "731":
            case "732":
            case "733":
                // MONITOR replies are consumed by the session's notify monitor.
                break;
            case "734":
                events.Add(Status(SessionEventKind.Error, "The server MONITOR list is full", now,
                    Fields(("numeric", "734"))));
                break;
            case "281" when Features.Supports("ACCEPT") || Features.Supports("CALLERID"):
                if (message.Parameters.Count >= 2)
                {
                    _acceptResults.AddRange(message.Parameters[^1]
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
                break;
            case "282" when Features.Supports("ACCEPT") || Features.Supports("CALLERID"):
                var acceptedNicknames = _acceptResults.Distinct(NameComparer())
                    .OrderBy(nickname => IrcCaseFold.Fold(nickname, _state.CaseMapping), StringComparer.Ordinal).ToArray();
                _acceptResults.Clear();
                events.Add(Status(
                    SessionEventKind.Server,
                    $"ACCEPT: {acceptedNicknames.Length} nickname(s)",
                    now,
                    Fields(("numeric", "282")),
                    new PresentationBlock(
                        "ACCEPT",
                        Grid: acceptedNicknames,
                        Summary: acceptedNicknames.Length == 0 ? "The ACCEPT list is empty" : null)));
                break;
            case "404":
                var blockedTarget = message.Parameters.Count >= 2 ? message.Parameters[1] : "that target";
                events.Add(Status(SessionEventKind.Error,
                    $"Cannot send to {blockedTarget}: {Last(message).TrimEnd('.')}", now,
                    Fields(("numeric", "404"), ("routeActive", "true"))));
                break;
            case "442":
                var missingMembershipChannel = message.Parameters.Count >= 2 ? message.Parameters[1] : "that channel";
                events.Add(Status(SessionEventKind.Error, $"You are not on {missingMembershipChannel}", now,
                    Fields(("numeric", "442"), ("routeActive", "true"))));
                break;
            case "443":
                var existingMember = message.Parameters.Count >= 2 ? message.Parameters[1] : "That nickname";
                var existingMemberChannel = message.Parameters.Count >= 3 ? message.Parameters[2] : "that channel";
                events.Add(Status(SessionEventKind.Error,
                    $"{existingMember} is already on {existingMemberChannel}", now,
                    Fields(("numeric", "443"), ("routeActive", "true"))));
                break;
            case "461":
                var missingParametersCommand = message.Parameters.Count >= 2 ? message.Parameters[1] : "Command";
                events.Add(Status(SessionEventKind.Error,
                    $"{missingParametersCommand} requires more parameters", now,
                    Fields(("numeric", "461"), ("routeActive", "true"))));
                break;
            case "456" when Features.Supports("ACCEPT") || Features.Supports("CALLERID"):
                events.Add(Status(SessionEventKind.Error, "The server ACCEPT list is full", now,
                    Fields(("numeric", "456"), ("routeActive", "true"))));
                break;
            case "457" when Features.Supports("ACCEPT") || Features.Supports("CALLERID"):
                var existingAcceptNickname = message.Parameters.Count >= 2 ? message.Parameters[1] : "That nickname";
                events.Add(Status(SessionEventKind.Error,
                    $"{existingAcceptNickname} is already on your ACCEPT list", now,
                    Fields(("numeric", "457"), ("routeActive", "true"))));
                break;
            case "458" when Features.Supports("ACCEPT") || Features.Supports("CALLERID"):
                var missingAcceptNickname = message.Parameters.Count >= 2 ? message.Parameters[1] : "That nickname";
                events.Add(Status(SessionEventKind.Error,
                    $"{missingAcceptNickname} is not on your ACCEPT list", now,
                    Fields(("numeric", "458"), ("routeActive", "true"))));
                break;
            case "467":
                var keyedChannel = message.Parameters.Count >= 2 ? message.Parameters[1] : "that channel";
                events.Add(Status(SessionEventKind.Error, $"A channel key is already set on {keyedChannel}", now,
                    Fields(("numeric", "467"), ("routeActive", "true"))));
                break;
            case "472":
                var unknownMode = message.Parameters.Count >= 2 ? message.Parameters[1] : "unknown";
                events.Add(Status(SessionEventKind.Error, $"Unknown mode: {unknownMode}", now,
                    Fields(("numeric", "472"), ("routeActive", "true"))));
                break;
            case "481":
                events.Add(Status(SessionEventKind.Error, "Permission denied", now,
                    Fields(("numeric", "481"), ("routeActive", "true"))));
                break;
            case "482":
                var operatorChannel = message.Parameters.Count >= 2 ? message.Parameters[1] : "that channel";
                events.Add(Status(SessionEventKind.Error,
                    $"You are not a channel operator in {operatorChannel}", now,
                    Fields(("numeric", "482"), ("routeActive", "true"))));
                break;
            case "353":
                if (message.Parameters.Count >= 4)
                {
                    var namesChannel = message.Parameters[2];
                    var namesBuffer = _state.GetOrCreateBuffer(BufferKind.Channel, namesChannel);
                    var namesState = _state.GetOrCreateChannel(namesChannel);
                    namesState.BeginNames();
                    foreach (var token in message.Parameters[3].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        AddName(namesState, token);
                    }
                }

                break;
            case "366":
                if (message.Parameters.Count >= 2)
                {
                    var channelName = message.Parameters[1];
                    var channel = _state.GetOrCreateChannel(channelName);
                    channel.NamesSynchronized = true;
                    var names = channel.Members
                        .OrderBy(member => member.Nickname, StringComparer.OrdinalIgnoreCase)
                        .Select(member => $"{Features.HighestPrefix(member.PrefixModes)}{member.Nickname}")
                        .ToArray();
                    var ops = channel.Members.Count(member => member.PrefixModes.Contains('o'));
                    var voices = channel.Members.Count(member => member.PrefixModes.Contains('v') && !member.PrefixModes.Contains('o'));
                    events.Add(Event(
                        _state.GetOrCreateBuffer(BufferKind.Channel, channelName),
                        SessionEventKind.Server,
                        $"NAMES {channelName}",
                        now,
                        presentation: new PresentationBlock(
                            $"Users ({channelName}): {channel.Members.Count}, Ops: {ops}, Voice: {voices}, Normal: {channel.Members.Count - ops - voices}",
                            Grid: names,
                            BracketGridCells: true,
                            TitleHighlight: channelName)));
                }
                break;
            case "403":
            case "405":
            case "471":
            case "473":
            case "474":
            case "475":
            case "476":
            case "477":
                var deniedChannel = message.Parameters.Count >= 2 ? message.Parameters[1] : "unknown";
                var denialReason = message.Command switch
                {
                    "403" => "no such channel",
                    "405" => "you have joined too many channels",
                    "471" => "channel is full",
                    "473" => "channel is invite-only",
                    "474" => "you are banned from that channel",
                    "475" => "incorrect channel key",
                    "476" => "invalid channel name or mask",
                    _ => Last(message).TrimEnd('.')
                };
                events.Add(Status(
                    SessionEventKind.Error,
                    $"Cannot join {deniedChannel}: {denialReason}",
                    now,
                    Fields(("joinError", "true"), ("channel", deniedChannel), ("numeric", message.Command))));
                break;
            case "716":
                if (message.Parameters.Count >= 2)
                {
                    _pendingMessageGuard = new PendingMessageGuard(message.Parameters[1], Last(message));
                }
                break;
            case "717":
                var guardTarget = message.Parameters.Count >= 2
                    ? message.Parameters[1]
                    : _pendingMessageGuard?.Target ?? "unknown";
                var pairedGuard = _pendingMessageGuard is { } pendingGuard &&
                    new IrcNameComparer(_state.CaseMapping).Equals(pendingGuard.Target, guardTarget);
                if (_pendingMessageGuard is { } unpairedGuard && !pairedGuard)
                {
                    events.Add(MessageGuardEvent(unpairedGuard.Target, unpairedGuard.Text, notified: false, now));
                }
                var guardText = pairedGuard ? _pendingMessageGuard!.Text : Last(message);
                events.Add(MessageGuardEvent(guardTarget, guardText, pairedGuard, now, notificationOnly: !pairedGuard));
                _pendingMessageGuard = null;
                break;
            case "718":
                var blockedSender = message.Parameters.Count >= 2 ? message.Parameters[1] : "Someone";
                var blockedSenderAddress = message.Parameters.Count >= 3 ? message.Parameters[2] : null;
                var blockedSenderLabel = string.IsNullOrWhiteSpace(blockedSenderAddress)
                    ? blockedSender
                    : $"{blockedSender} [{blockedSenderAddress}]";
                events.Add(Status(
                    SessionEventKind.MessageGuard,
                    $"{blockedSenderLabel} tried to message you while you have user mode +{Features.CallerIdMode}",
                    now,
                    Fields(("numeric", "718"), ("outputFamily", "messageguard"),
                        ("nick", blockedSender), ("address", blockedSenderAddress),
                        ("serverText", Last(message)), ("routeConfigured", "true"))));
                break;
            case "421":
                var unknownCommand = message.Parameters.Count >= 2
                    ? message.Parameters[1]
                    : "unknown";
                events.Add(Status(
                    SessionEventKind.Error,
                    $"Unknown command: {unknownCommand}",
                    now,
                    Fields(("numeric", "421"), ("routeActive", "true"))));
                break;
            case "372":
            case "375":
            case "376":
            case "422":
                events.Add(Status(SessionEventKind.Server, Last(message), now));
                break;
            case "ERROR":
                events.Add(Status(SessionEventKind.Error, Last(message), now));
                break;
            default:
                if (_identityQueries.TryCollectUnknownWhois(message))
                {
                    break;
                }
                events.Add(Status(
                    SessionEventKind.Server,
                    message.Command.All(char.IsDigit)
                        ? $"[{message.Command}] {string.Join(' ', message.Parameters)}"
                        : $"{message.Command} {string.Join(' ', message.Parameters)}",
                    now));
                break;
        }

        return events;
    }

    private void ProcessPrivmsg(IrcMessage message, string sender, DateTimeOffset now, ICollection<SessionEvent> events)
    {
        if (message.Parameters.Count < 2)
        {
            return;
        }

        var wireTarget = message.Parameters[0];
        var target = Features.NormalizeMessageTarget(wireTarget);
        var text = message.Parameters[1];
        var isChannel = Features.IsChannel(target);

        if (text.Length >= 2 && text[0] == '\u0001' && text[^1] == '\u0001')
        {
            var ctcp = text[1..^1];
            if (ctcp.StartsWith("DCC ", StringComparison.OrdinalIgnoreCase))
            {
                var identity = ParsePrefix(message.Prefix);
                if (DccOfferParser.TryParse(ctcp, out var offer, out var error))
                {
                    var protocol = offer!.IsSecure
                        ? offer.Type == DccRequestType.Chat ? "SCHAT" : "SSEND"
                        : offer.Type.ToString().ToUpperInvariant();
                    events.Add(Status(SessionEventKind.Notice, $"DCC {protocol} request from {sender}", now,
                        Fields(("event", "dcc.request"), ("nick", sender), ("username", identity.Username),
                            ("host", identity.Host), ("dcc.type", offer.Type.ToString().ToLowerInvariant()),
                            ("dcc.filename", offer.Filename), ("dcc.address", offer.Address),
                            ("dcc.port", offer.Port.ToString(CultureInfo.InvariantCulture)),
                            ("dcc.size", offer.Size?.ToString(CultureInfo.InvariantCulture)),
                            ("dcc.token", offer.PassiveToken), ("dcc.passive", offer.IsPassive ? "true" : "false"),
                            ("dcc.secure", offer.IsSecure ? "true" : "false"),
                            ("dcc.raw", offer.RawPayload), ("message", offer.RawPayload),
                            ("channel", isChannel ? target : null), ("private", isChannel ? null : "true"))));
                }
                else if (DccResumeParser.TryParse(ctcp, out var resume, out _))
                {
                    events.Add(Status(SessionEventKind.Notice,
                        $"DCC {resume!.Operation.ToString().ToUpperInvariant()} from {sender}", now,
                        Fields(("event", "dcc.control"), ("nick", sender),
                            ("username", identity.Username), ("host", identity.Host),
                            ("dcc.operation", resume.Operation.ToString().ToLowerInvariant()),
                            ("dcc.filename", resume.Filename),
                            ("dcc.port", resume.Port.ToString(CultureInfo.InvariantCulture)),
                            ("dcc.position", resume.Position.ToString(CultureInfo.InvariantCulture)),
                            ("dcc.token", resume.PassiveToken), ("dcc.raw", resume.RawPayload),
                            ("message", resume.RawPayload), ("channel", isChannel ? target : null),
                            ("private", isChannel ? null : "true"))));
                }
                else
                {
                    events.Add(Status(SessionEventKind.Error, $"Invalid DCC request from {sender}: {error}", now,
                        Fields(("event", "dcc.invalid"), ("nick", sender),
                            ("username", identity.Username), ("host", identity.Host), ("dcc.raw", ctcp),
                            ("dcc.error", error), ("message", ctcp), ("channel", isChannel ? target : null),
                            ("private", isChannel ? null : "true"))));
                }
            }
            else if (ctcp.StartsWith("ACTION ", StringComparison.OrdinalIgnoreCase))
            {
                var actionText = IrcTextFormatting.Parse(ctcp[7..]);
                var actionBuffer = isChannel
                    ? _state.GetOrCreateBuffer(BufferKind.Channel, target)
                    : AutomaticQueryBuffer(sender);
                events.Add(Event(actionBuffer, SessionEventKind.Action, $"* {sender} {actionText.PlainText}", now,
                    Fields(("nick", sender), ("username", ParsePrefix(message.Prefix).Username),
                        ("host", ParsePrefix(message.Prefix).Host), ("message", actionText.PlainText),
                        ("channel", isChannel ? target : null), ("private", isChannel ? null : "true")),
                    formattedContent: actionText));
            }
            else
            {
                var ctcpBuffer = isChannel
                    ? _state.GetOrCreateBuffer(BufferKind.Channel, target)
                    : _state.StatusBuffer;
                var displayedCtcp = ctcp.StartsWith("PING ", StringComparison.OrdinalIgnoreCase)
                    ? "PING"
                    : ctcp;
                events.Add(Event(ctcpBuffer, SessionEventKind.Notice, $"CTCP from {sender}: {displayedCtcp}", now,
                    Fields(("event", "ctcp"), ("outputFamily", "ctcp"), ("routeConfigured", "true"),
                        ("nick", sender), ("username", ParsePrefix(message.Prefix).Username),
                        ("host", ParsePrefix(message.Prefix).Host), ("message", ctcp),
                        ("channel", isChannel ? target : null), ("private", isChannel ? null : "true"))));
            }

            return;
        }

        var formattedText = IrcTextFormatting.Parse(text);
        var plainText = formattedText.PlainText;
        var buffer = isChannel
            ? _state.GetOrCreateBuffer(BufferKind.Channel, target)
            : AutomaticQueryBuffer(sender);

        var kind = Features.IsChannel(target) && ContainsNickname(plainText, CurrentNickname)
            ? SessionEventKind.Highlight
            : SessionEventKind.Message;
        string? nickPrefix = null;
        if (Features.IsChannel(target) && _state.TryGetChannel(target, out var messageChannel) &&
            messageChannel!.TryGetMember(sender, out var messageMember))
        {
            nickPrefix = Features.HighestPrefix(messageMember!.PrefixModes)?.ToString();
        }
        events.Add(Event(buffer, kind, $"<{nickPrefix}{sender}> {plainText}", now,
            Fields(("nick", sender), ("username", ParsePrefix(message.Prefix).Username),
                ("host", ParsePrefix(message.Prefix).Host), ("nickPrefix", nickPrefix), ("message", plainText),
                ("channel", isChannel ? target : null), ("private", isChannel ? null : "true"),
                ("controlCount", text.Count(character => char.IsControl(character)).ToString())),
            formattedContent: formattedText));
    }

    private BufferState AutomaticQueryBuffer(string sender)
    {
        const int maximumAutomaticQueries = 100;
        if (_state.TryGetBuffer(sender, out var existing)) return existing!;
        return _state.Buffers.Count(buffer => buffer.Kind == BufferKind.Query) >= maximumAutomaticQueries
            ? _state.StatusBuffer
            : _state.GetOrCreateBuffer(BufferKind.Query, sender);
    }

    private void ProcessNotice(IrcMessage message, string sender, DateTimeOffset now, ICollection<SessionEvent> events)
    {
        if (message.Parameters.Count < 2)
        {
            return;
        }

        var target = Features.NormalizeMessageTarget(message.Parameters[0]);
        var buffer = Features.IsChannel(target)
            ? _state.GetOrCreateBuffer(BufferKind.Channel, target)
            : _state.StatusBuffer;
        var text = message.Parameters[1];
        if (text.Length >= 2 && text[0] == '\u0001' && text[^1] == '\u0001')
        {
            var ctcp = text[1..^1];
            if (ctcp.StartsWith("PING ", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(ctcp[5..], out var sentAt))
            {
                var elapsed = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sentAt);
                events.Add(Event(buffer, SessionEventKind.Notice, $"CTCP PING reply from {sender}: {elapsed / 1000d:0.000} seconds", now,
                    Fields(("outputFamily", "ctcp"), ("sender", sender), ("outputEnd", "true"))));
            }
            else if (ctcp.Equals("PING", StringComparison.OrdinalIgnoreCase))
            {
                events.Add(Event(buffer, SessionEventKind.Notice, $"CTCP PING reply from {sender}.", now,
                    Fields(("outputFamily", "ctcp"), ("sender", sender), ("outputEnd", "true"))));
            }
            else
            {
                var separator = ctcp.IndexOf(' ');
                var command = (separator < 0 ? ctcp : ctcp[..separator]).ToUpperInvariant();
                var payload = separator < 0 ? string.Empty : ctcp[(separator + 1)..].TrimStart();
                var formatted = payload.Length == 0
                    ? $"CTCP {command} reply from {sender}."
                    : $"CTCP {command} reply from {sender}: {payload}";
                events.Add(Event(buffer, SessionEventKind.Notice, formatted, now,
                    Fields(("outputFamily", "ctcp"), ("sender", sender), ("outputEnd", "true"))));
            }

            return;
        }

        var noticeIsChannel = Features.IsChannel(target);
        var noticeIdentity = ParsePrefix(message.Prefix);
        var formattedText = IrcTextFormatting.Parse(text);
        events.Add(Event(buffer, SessionEventKind.Notice, $"-{sender}- {formattedText.PlainText}", now,
            Fields(("nick", sender), ("username", noticeIdentity.Username), ("host", noticeIdentity.Host),
                ("message", formattedText.PlainText), ("channel", noticeIsChannel ? target : null),
                ("private", noticeIsChannel ? null : "true"),
                ("outputFamily", noticeIsChannel ? null : "notice"),
                ("routeConfigured", noticeIsChannel ? null : "true")),
            formattedContent: formattedText));
    }

    private SessionEvent Status(
        SessionEventKind kind,
        string text,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string?>? fields = null,
        PresentationBlock? presentation = null) =>
        _eventBuilder.Status(kind, text, now, fields, presentation);

    private SessionEvent MessageGuardEvent(
        string target,
        string serverText,
        bool notified,
        DateTimeOffset now,
        bool notificationOnly = false)
    {
        var text = notificationOnly
            ? $"{target} was notified that your message was blocked by server-side ignore (+{Features.CallerIdMode})"
            : $"Message to {target} was blocked by server-side ignore (+{Features.CallerIdMode})" +
              (notified ? "; they were notified" : string.Empty);
        return Status(
            SessionEventKind.MessageGuard,
            text,
            now,
            Fields(("outputFamily", "messageguard"), ("target", target),
                ("serverText", serverText), ("routeConfigured", "true")));
    }

    private SessionEvent Event(
        BufferState buffer,
        SessionEventKind kind,
        string text,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string?>? fields = null,
        PresentationBlock? presentation = null,
        IrcFormattedText? formattedContent = null) =>
        _eventBuilder.Create(buffer, kind, text, now, fields, presentation, formattedContent);

    private static string Last(IrcMessage message) => message.Parameters.Count == 0 ? string.Empty : message.Parameters[^1];

    private static bool IsWelcomeNumeric(string command) => command is "002" or "003" or "004" or
        "250" or "251" or "252" or "253" or "254" or "255" or "256" or "257" or "258" or "259" or
        "260" or "261" or "262" or "263" or "264" or "265" or "266";

    private IEqualityComparer<string> NameComparer() => new IrcNameComparer(_state.CaseMapping);

    private static string? ParentheticalDetail(string value)
    {
        var open = value.LastIndexOf('(');
        var close = value.LastIndexOf(')');
        if (open < 0 || close <= open + 1) return null;
        var detail = value[(open + 1)..close].Trim().TrimEnd('.');
        return detail.Length == 0 ? null : detail;
    }

    private sealed record PendingMessageGuard(string Target, string Text);

    private void ObserveConnectionMetadata(IrcMessage message)
    {
        if (message.Command == "004" && message.Parameters.Count >= 2)
        {
            _state.SetServerName(message.Parameters[1]);
            if (message.Parameters.Count >= 3)
            {
                Features.ObserveServerSoftware(message.Parameters[2]);
            }
        }
        else if (message.Command == "001")
        {
            _state.SetServerName(message.Prefix);
        }

        if (message.Command == "671" && message.Parameters.Count >= 2 &&
            new IrcNameComparer(_state.CaseMapping).Equals(message.Parameters[1], CurrentNickname))
        {
            _state.SetUpstreamTls(true);
        }

        var irssiProxySignature = message.Command == "002" &&
            message.Parameters.Count >= 2 &&
            message.Parameters[^1].StartsWith(
                "Your host is irssi-proxy, running version ", StringComparison.OrdinalIgnoreCase);

        var zncToken = message.Command == "005" && message.Parameters
            .Skip(1)
            .Select(parameter => parameter.Split('=', 2)[0])
            .Any(name => name.Equals("ZNC", StringComparison.OrdinalIgnoreCase));
        var zncPrefix = message.Prefix?.Contains("znc.in", StringComparison.OrdinalIgnoreCase) == true ||
            message.Prefix?.StartsWith("*status", StringComparison.OrdinalIgnoreCase) == true;
        if (zncToken || zncPrefix)
        {
            _state.SetBouncer("ZNC");
        }
        else if (irssiProxySignature)
        {
            _state.SetBouncer("Irssi Proxy");
        }
    }

    public static string NickFromPrefix(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return "server";
        }

        var separator = prefix.IndexOfAny(['!', '@']);
        return separator < 0 ? prefix : prefix[..separator];
    }

    private bool IsCurrentNickname(string nickname) =>
        new IrcNameComparer(_state.CaseMapping).Equals(nickname, CurrentNickname);

    private void AddName(ChannelState channel, string token)
    {
        var prefixModes = new List<char>();
        var offset = 0;
        while (offset < token.Length && Features.TryGetPrefixMode(token[offset], out var mode))
        {
            prefixModes.Add(mode);
            offset++;
        }

        if (offset >= token.Length)
        {
            return;
        }

        var identity = ParsePrefix(token[offset..]);
        var member = channel.GetOrAddMember(identity.Nickname, identity.Username, identity.Host);
        member.ClearPrefixModes();
        foreach (var mode in prefixModes)
        {
            member.AddPrefixMode(mode);
        }
    }

    private void ApplyModes(ChannelState channel, IReadOnlyList<string> parameters, bool reset)
    {
        if (parameters.Count == 0)
        {
            return;
        }

        if (reset)
        {
            channel.ResetModes();
        }

        var adding = true;
        var parameterIndex = 1;
        foreach (var mode in parameters[0])
        {
            if (mode == '+')
            {
                adding = true;
                continue;
            }

            if (mode == '-')
            {
                adding = false;
                continue;
            }

            string? parameter = null;
            if (Features.ModeTakesParameter(mode, adding) && parameterIndex < parameters.Count)
            {
                parameter = parameters[parameterIndex++];
            }

            if (Features.IsPrefixMode(mode) && parameter is not null)
            {
                channel.SetMemberPrefix(parameter, mode, adding);
            }
            else if (Features.ChannelModesA.Contains(mode) && parameter is not null)
            {
                if (adding)
                {
                    channel.AddChannelListEntry(new ChannelListEntry(mode, parameter));
                }
                else
                {
                    channel.RemoveChannelListEntry(mode, parameter);
                }
            }
            else
            {
                channel.SetMode(mode, adding, parameter);
            }
        }
    }

    private static (string Nickname, string? Username, string? Host) ParsePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return ("server", null, null);
        }

        var bang = prefix.IndexOf('!');
        var at = prefix.IndexOf('@', bang + 1);
        if (bang <= 0 || at <= bang + 1 || at == prefix.Length - 1)
        {
            return (NickFromPrefix(prefix), null, null);
        }

        return (prefix[..bang], prefix[(bang + 1)..at], prefix[(at + 1)..]);
    }

    private static IReadOnlyDictionary<string, string?> Fields(params (string Name, string? Value)[] values) =>
        values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);

    private bool ContainsNickname(string text, string nickname)
    {
        var foldedText = IrcCaseFold.Fold(text, _state.CaseMapping);
        var foldedNick = IrcCaseFold.Fold(nickname, _state.CaseMapping);
        var start = 0;
        while ((start = foldedText.IndexOf(foldedNick, start, StringComparison.Ordinal)) >= 0)
        {
            var before = start == 0 || !IsNicknameCharacter(foldedText[start - 1]);
            var end = start + foldedNick.Length;
            var after = end == foldedText.Length || !IsNicknameCharacter(foldedText[end]);
            if (before && after) return true;
            start++;
        }
        return false;
    }

    private static bool IsNicknameCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is '-' or '_' or '[' or ']' or '\\' or '`' or '^' or '{' or '}' or '|';
}
