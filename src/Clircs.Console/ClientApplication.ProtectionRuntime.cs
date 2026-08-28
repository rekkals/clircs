using System.Globalization;
using Clircs.Commands;
using Clircs.Identity;
using Clircs.Networking;
using Clircs.Protocol;
using Clircs.Protection;
using Clircs.Sessions;
using Clircs.State;
using Clircs.Users;

namespace Clircs.ConsoleClient;

// Owns live protection detection, evaluation, actions, and audit presentation.
internal sealed partial class ClientApplication
{
    private void HandleProtectionMonitoring(SessionEvent sessionEvent)
    {
        if (sessionEvent.Kind == SessionEventKind.Protection || FindSession(sessionEvent.NetworkSessionId) is not { } session)
        {
            return;
        }
        try
        {
            var fields = sessionEvent.Fields;
            if (fields is null) return;
            var channel = fields.GetValueOrDefault("channel");
            if (channel is null && session.State.TryGetBuffer(sessionEvent.BufferId, out var buffer) && buffer!.Kind == BufferKind.Channel)
            {
                channel = buffer.Name;
            }
            var isPrivate = fields.GetValueOrDefault("private") == "true" ||
                session.State.TryGetBuffer(sessionEvent.BufferId, out var eventBuffer) && eventBuffer!.Kind == BufferKind.Query;
            var actor = sessionEvent.Kind switch
            {
                SessionEventKind.Part when fields.GetValueOrDefault("event") == "kick" => fields.GetValueOrDefault("actor"),
                SessionEventKind.Mode => fields.GetValueOrDefault("actor"),
                SessionEventKind.Nick => fields.GetValueOrDefault("oldNick"),
                _ => fields.GetValueOrDefault("nick")
            };
            if (string.IsNullOrWhiteSpace(actor) ||
                new IrcNameComparer(session.State.CaseMapping).Equals(actor, session.CurrentNickname))
            {
                return;
            }

            var effective = EffectiveProtection(session, channel);
            var settings = effective.Settings;
            var evidence = BuildProtectionEvidence(sessionEvent, session, actor, channel, isPrivate, settings);
            foreach (var item in evidence)
            {
                var detection = _userAndChannelPolicy.Evaluate(item, settings.Rules[item.Detector]);
                if (detection is null) continue;
                var exemption = ProtectionExemption(session, channel, actor, fields, settings, item.Detector);
                var location = channel ?? "private messages";
                var prefix =
                    $"{DetectorName(item.Detector)}: {actor} in {location} reached {detection.Count}/{detection.Rule.Threshold} " +
                    $"within {detection.Rule.WindowSeconds}s";
                if (exemption is not null)
                {
                    PublishProtectionAudit(session, $"{prefix}; SUPPRESSED - {exemption}.",
                        item.Detector, actor, channel, exemption);
                    continue;
                }
                if (settings.MonitorOnly || !isPrivate && settings.ChannelAction == ChannelProtectionAction.Monitor)
                {
                    PublishProtectionAudit(session, $"{prefix}; MONITOR - no IRC action sent.",
                        item.Detector, actor, channel, null);
                    continue;
                }
                if (item.Detector == ProtectionDetector.ServerOp)
                {
                    PublishProtectionAudit(session,
                        $"{prefix}; MONITOR - server-origin operator changes have no client offender to punish.",
                        item.Detector, actor, channel, "server-origin mode");
                    continue;
                }
                if (isPrivate)
                {
                    var identity = ProtectionIdentityKey(session, actor, fields);
                    _userAndChannelPolicy.IgnorePersonally(
                        session.State.Id,
                        identity,
                        DateTimeOffset.UtcNow.AddSeconds(settings.PersonalIgnoreSeconds));
                    PublishProtectionAudit(session,
                        $"{prefix}; IGNORED locally for {FormatDuration(TimeSpan.FromSeconds(settings.PersonalIgnoreSeconds))}.",
                        item.Detector, actor, channel, null);
                    continue;
                }
                if (channel is null)
                {
                    PublishProtectionAudit(session, $"{prefix}; SUPPRESSED - no channel target was available.",
                        item.Detector, actor, channel, "missing channel");
                    continue;
                }
                var actionKey = $"{IrcCaseFold.Fold(channel, session.State.CaseMapping)}\0" +
                    IrcCaseFold.Fold(actor, session.State.CaseMapping);
                var actionNow = DateTimeOffset.UtcNow;
                if (!_userAndChannelPolicy.TryBeginProtectionAction(
                        session.State.Id,
                        actionKey,
                        actionNow,
                        actionNow.AddSeconds(Math.Max(5, detection.Rule.WindowSeconds))))
                {
                    PublishProtectionAudit(session, $"{prefix}; SUPPRESSED - a protection action is already pending.",
                        item.Detector, actor, channel, "action already pending");
                    continue;
                }
                StartSessionWork(
                    session,
                    $"channel protection ({item.Detector})",
                    () => ExecuteChannelProtectionAsync(
                        session,
                        channel,
                        actor,
                        fields,
                        detection,
                        settings,
                        prefix));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            PublishProtectionAudit(session, $"Protection monitor error: {exception.Message}", null, null, null, "evaluation error");
        }
    }

    private bool IsPersonallyIgnored(SessionEvent sessionEvent)
    {
        if (sessionEvent.Kind == SessionEventKind.Protection ||
            FindSession(sessionEvent.NetworkSessionId) is not { } session ||
            sessionEvent.Fields is not { } fields)
        {
            return false;
        }
        var isPrivate = fields.GetValueOrDefault("private") == "true" ||
            session.State.TryGetBuffer(sessionEvent.BufferId, out var buffer) && buffer!.Kind == BufferKind.Query;
        var actor = fields.GetValueOrDefault("nick");
        if (!isPrivate || string.IsNullOrWhiteSpace(actor))
        {
            return false;
        }
        var ignored = _userAndChannelPolicy.IsPersonallyIgnored(
            session.State.Id,
            ProtectionIdentityKey(session, actor, fields),
            DateTimeOffset.UtcNow);
        if (ignored && session.State.TryGetBuffer(sessionEvent.BufferId, out var ignoredBuffer) &&
            ignoredBuffer!.Kind == BufferKind.Query)
        {
            var removeEmptyQuery = false;
            lock (_windowTransactionGate)
            {
                removeEmptyQuery = _windowStates.TryRemoveInactiveEmpty(ignoredBuffer.Id);
                if (removeEmptyQuery)
                {
                    session.State.RemoveBuffer(ignoredBuffer.Id);
                }
            }
            if (removeEmptyQuery) _presenter.ForgetInputHistory(ignoredBuffer.Id);
        }
        return ignored;
    }

    private static string ProtectionIdentityKey(
        IrcNetworkSession session,
        string actor,
        IReadOnlyDictionary<string, string?> fields)
    {
        var username = fields.GetValueOrDefault("username");
        var host = fields.GetValueOrDefault("host");
        var identity = string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(host)
            ? actor
            : $"{username}@{host}";
        return IrcCaseFold.Fold(identity, session.State.CaseMapping);
    }

    private async Task ExecuteChannelProtectionAsync(
        IrcNetworkSession session,
        string channelName,
        string actor,
        IReadOnlyDictionary<string, string?> fields,
        ProtectionDetection detection,
        ProtectionSettings settings,
        string auditPrefix)
    {
        var detector = detection.Evidence.Detector;
        try
        {
            if (FindSession(session.State.Id) is null ||
                !session.State.TryGetChannel(channelName, out var channel) ||
                !channel!.TryGetMember(session.CurrentNickname, out var self) ||
                !HasOperatorPrivilege(session.Features, self!))
            {
                PublishProtectionAudit(session,
                    $"{auditPrefix}; SUPPRESSED - you are not an operator in {channelName}.",
                    detector, actor, channelName, "client is not a channel operator");
                return;
            }

            var targetNick = fields.GetValueOrDefault("newNick") ?? actor;
            var comparer = new IrcNameComparer(session.State.CaseMapping);
            if (comparer.Equals(targetNick, session.CurrentNickname) ||
                !channel.TryGetMember(targetNick, out var target))
            {
                PublishProtectionAudit(session,
                    $"{auditPrefix}; SUPPRESSED - {targetNick} is no longer in {channelName}.",
                    detector, actor, channelName, "target is no longer present");
                return;
            }

            string? banMask = null;
            if (settings.ChannelAction == ChannelProtectionAction.KickBan)
            {
                var username = target!.Username ?? fields.GetValueOrDefault("username");
                var host = target.Host ?? fields.GetValueOrDefault("host");
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(host))
                {
                    PublishProtectionAudit(session,
                        $"{auditPrefix}; SUPPRESSED - no synchronized address is available for {targetNick}.",
                        detector, actor, channelName, "missing synchronized address");
                    return;
                }
                banMask = BanmaskFormatter.Create(
                    new ChannelMemberState(targetNick, username, host),
                    _preferences.BanmaskStyle);
                await session.SendAsync(
                    "MODE",
                    [channelName, "+b", banMask],
                    IrcOutboundPriority.Automation,
                    SessionWorkToken(session));
                if (settings.BanSeconds > 0)
                {
                    ScheduleTimedUnban(
                        session,
                        channelName,
                        banMask,
                        TimeSpan.FromSeconds(settings.BanSeconds));
                }
            }

            var reason = ProtectionKickReason(detection);
            await session.SendAsync(
                "KICK",
                [channelName, targetNick, reason],
                IrcOutboundPriority.Automation,
                SessionWorkToken(session));
            var action = settings.ChannelAction == ChannelProtectionAction.KickBan
                ? $"KICKBAN sent to {targetNick} using {banMask}"
                : $"KICK sent to {targetNick}";
            PublishProtectionAudit(session, $"{auditPrefix}; {action}.",
                detector, actor, channelName, null);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or
            UnauthorizedAccessException)
        {
            if (FindSession(session.State.Id) is not null)
            {
                PublishProtectionAudit(session, $"{auditPrefix}; ACTION FAILED - {exception.Message}",
                    detector, actor, channelName, "action failed");
            }
        }
    }

    internal static string ProtectionKickReason(ProtectionDetection detection)
    {
        var (label, singular, plural) = detection.Evidence.Detector switch
        {
            ProtectionDetector.Text => ("Text flood", "message", "messages"),
            ProtectionDetector.Repeat => ("Repeat flood", "repeat", "repeats"),
            ProtectionDetector.Join => ("Join flood", "join", "joins"),
            ProtectionDetector.Nick => ("Nick flood", "nick change", "nick changes"),
            ProtectionDetector.MassKick => ("Mass kick", "kick", "kicks"),
            ProtectionDetector.MassDeop => ("Mass deop", "deop", "deops"),
            ProtectionDetector.Caps => ("Caps flood", "message", "messages"),
            ProtectionDetector.Controls => ("Control-code flood", "message", "messages"),
            ProtectionDetector.ChannelCtcp => ("CTCP flood", "request", "requests"),
            _ => ("Channel protection", "event", "events")
        };
        var noun = detection.Count == 1 ? singular : plural;
        var seconds = detection.Elapsed.TotalSeconds.ToString("0.0##", CultureInfo.InvariantCulture);
        return $"{label}: {detection.Count} {noun} in {seconds} seconds";
    }

    private static IReadOnlyList<ProtectionEvidence> BuildProtectionEvidence(
        SessionEvent sessionEvent,
        IrcNetworkSession session,
        string actor,
        string? channel,
        bool isPrivate,
        ProtectionSettings settings)
    {
        var evidence = new List<ProtectionEvidence>();
        var fields = sessionEvent.Fields!;
        var text = fields.GetValueOrDefault("message");
        void Add(ProtectionDetector detector, int weight = 1, string? counterActor = null) =>
            evidence.Add(new ProtectionEvidence(
                session.State.Id, detector, counterActor ?? actor, channel, text, sessionEvent.Timestamp, weight));

        if (isPrivate && settings.PersonalEnabled)
        {
            if (fields.GetValueOrDefault("event") is "ctcp" or "dcc.request" or "dcc.invalid")
                Add(ProtectionDetector.Ctcp);
            else if (fields.GetValueOrDefault("event") == "invite") Add(ProtectionDetector.Invite);
            else if (sessionEvent.Kind == SessionEventKind.Notice) Add(ProtectionDetector.PrivateNotice);
            else if (sessionEvent.Kind is SessionEventKind.Message or SessionEventKind.Highlight or SessionEventKind.Action)
                Add(ProtectionDetector.PrivateMessage);
            return evidence;
        }
        if (channel is null || !settings.ChannelEnabled) return evidence;

        if (fields.GetValueOrDefault("event") is "ctcp" or "dcc.request" or "dcc.invalid")
        {
            Add(ProtectionDetector.ChannelCtcp);
            return evidence;
        }

        if (sessionEvent.Kind is SessionEventKind.Message or SessionEventKind.Highlight or SessionEventKind.Action or SessionEventKind.Notice)
        {
            Add(ProtectionDetector.Text);
            if (!string.IsNullOrWhiteSpace(text)) Add(ProtectionDetector.Repeat);
            if (HasExcessiveCaps(text)) Add(ProtectionDetector.Caps);
            if (int.TryParse(fields.GetValueOrDefault("controlCount"), out var controls) && controls >= 3)
                Add(ProtectionDetector.Controls);
        }
        var username = fields.GetValueOrDefault("username");
        var host = fields.GetValueOrDefault("host");
        if (sessionEvent.Kind == SessionEventKind.Join)
        {
            var fullPrefix = username is not null && host is not null ? $"{actor}!{username}@{host}" : actor;
            Add(ProtectionDetector.Join, counterActor: fullPrefix);
        }
        if (sessionEvent.Kind == SessionEventKind.Nick)
        {
            var stablePrefix = username is not null && host is not null ? $"{username}@{host}" : actor;
            Add(ProtectionDetector.Nick, counterActor: stablePrefix);
        }
        if (sessionEvent.Kind == SessionEventKind.Part && fields.GetValueOrDefault("event") == "kick")
            Add(ProtectionDetector.MassKick);
        if (sessionEvent.Kind == SessionEventKind.Mode)
        {
            var modes = fields.GetValueOrDefault("modes") ?? string.Empty;
            var deops = CountModeChanges(modes, 'o', adding: false);
            var ops = CountModeChanges(modes, 'o', adding: true);
            if (deops > 0) Add(ProtectionDetector.MassDeop, deops);
            if (ops > 0 && session.State.TryGetChannel(channel, out var state) && !state!.TryGetMember(actor, out _))
                Add(ProtectionDetector.ServerOp, ops);
        }
        return evidence;
    }

    private string? ProtectionExemption(
        IrcNetworkSession session,
        string? channel,
        string actor,
        IReadOnlyDictionary<string, string?> fields,
        ProtectionSettings settings,
        ProtectionDetector detector)
    {
        ChannelMemberState? member = null;
        if (channel is not null && session.State.TryGetChannel(channel, out var channelState))
        {
            channelState!.TryGetMember(actor, out member);
        }
        if (OperatorExemptionApplies(detector) && settings.ExemptOperators && member is not null &&
            HasOperatorPrivilege(session.Features, member))
            return "channel operator exemption";

        var username = fields.GetValueOrDefault("username") ?? member?.Username;
        var host = fields.GetValueOrDefault("host") ?? member?.Host;
        if (username is null || host is null || ProfileFor(session) is not { } profile) return null;
        var directory = _userAndChannelPolicy.GetDirectory(
            profile.Id,
            () => _userDirectoryStore.Load(profile.Id));
        var match = directory.Match($"{actor}!{username}@{host}", session.State.CaseMapping);
        if (match.Conflict || match.User is null) return null;
        var roles = match.User.EffectiveRoles(channel, session.State.CaseMapping);
        if (settings.ExemptProtectionExempt && roles.HasFlag(UserRole.ProtectionExempt))
            return $"{match.User.Handle} is protection-exempt";
        if (settings.ExemptProtected && roles.HasFlag(UserRole.Protected))
            return $"{match.User.Handle} is protected";
        return null;
    }

    private void PublishProtectionAudit(
        IrcNetworkSession session,
        string text,
        ProtectionDetector? detector,
        string? actor,
        string? channel,
        string? suppression)
    {
        OnSessionEvent(new SessionEvent(
            session.State.Id,
            ProtectionAuditBuffer(session).Id,
            SessionEventKind.Protection,
            TerminalTextSanitizer.Sanitize(text),
            DateTimeOffset.Now,
            new Dictionary<string, string?>
            {
                ["detector"] = detector?.ToString(),
                ["actor"] = actor,
                ["channel"] = channel,
                ["suppression"] = suppression
            }));
    }

    private static bool HasExcessiveCaps(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var letters = text.Where(char.IsLetter).ToArray();
        return letters.Length >= 10 && letters.Count(char.IsUpper) / (double)letters.Length >= 0.70;
    }

    internal static int CountModeChanges(string modes, char target, bool adding)
    {
        var state = true;
        var count = 0;
        foreach (var mode in modes)
        {
            if (mode == '+') state = true;
            else if (mode == '-') state = false;
            else if (mode == target && state == adding) count++;
        }
        return count;
    }

    internal static bool OperatorExemptionApplies(ProtectionDetector detector) =>
        detector is not (ProtectionDetector.MassKick or ProtectionDetector.MassDeop or ProtectionDetector.ServerOp);

    private void OnLogWriterError(string message) => _presenter.Result(message, success: false);


    private bool TryFriendlyChannelScope(
        IReadOnlyList<string> arguments,
        bool createUnknown,
        out ProtectionScope? scope,
        out string label,
        out bool created,
        out CommandResult failure)
    {
        scope = null;
        label = string.Empty;
        created = false;
        failure = CommandResult.Success();
        string channel;
        NetworkProfile? profile;
        IrcNetworkSession? session;

        if (arguments.Count == 0)
        {
            session = ActiveSession();
            channel = ActiveChannel() ?? string.Empty;
            if (session is null || channel.Length == 0)
            {
                failure = CommandResult.Failure(
                    "Use this in a channel window, or specify both network and channel: /cprot on EFnet #clircs");
                return false;
            }
            profile = EnsureProfileFor(session, out _);
        }
        else if (arguments.Count == 1 && IsChannelProtectionTarget(arguments[0]))
        {
            session = ActiveSession();
            if (session is null)
            {
                failure = CommandResult.Failure("Specify a network as well: /cprot on EFnet #clircs");
                return false;
            }
            profile = EnsureProfileFor(session, out _);
            channel = arguments[0];
        }
        else if (arguments.Count == 2)
        {
            if (!TryProtectionProfile(arguments[0], createUnknown, out profile, out session, out created, out failure))
                return false;
            channel = arguments[1];
        }
        else
        {
            failure = CommandResult.Failure("Channel protection needs an active channel or [network] [channel].");
            return false;
        }

        if (!IsChannelProtectionTarget(channel))
        {
            failure = CommandResult.Failure($"'{channel}' is not a channel name. Use * for the network-wide channel default.");
            return false;
        }
        if (channel == "*")
        {
            scope = new ProtectionScope(ProtectionScopeKind.Network, profile!.Id.ToString());
            label = $"all channels on {profile.DisplayName}";
            return true;
        }
        var normalized = channel.ToLowerInvariant();
        scope = new ProtectionScope(ProtectionScopeKind.Channel, profile!.Id.ToString(), normalized);
        label = $"{profile.DisplayName} {channel}";
        return true;
    }

    private bool TryFriendlyNetworkScope(
        IReadOnlyList<string> arguments,
        bool createUnknown,
        out ProtectionScope? scope,
        out string label,
        out bool created,
        out CommandResult failure)
    {
        scope = null;
        label = string.Empty;
        created = false;
        failure = CommandResult.Success();
        NetworkProfile? profile;
        if (arguments.Count == 0)
        {
            var session = ActiveSession();
            if (session is null)
            {
                failure = CommandResult.Failure("Connect to a network or name one, such as /pprot on EFnet.");
                return false;
            }
            profile = EnsureProfileFor(session, out _);
        }
        else if (arguments.Count == 1)
        {
            if (!TryProtectionProfile(arguments[0], createUnknown, out profile, out _, out created, out failure))
                return false;
        }
        else
        {
            failure = CommandResult.Failure("Personal protection accepts at most one network name.");
            return false;
        }
        scope = new ProtectionScope(ProtectionScopeKind.Network, profile!.Id.ToString());
        label = profile.DisplayName;
        return true;
    }

    private bool TryProtectionProfile(
        string name,
        bool createUnknown,
        out NetworkProfile? profile,
        out IrcNetworkSession? session,
        out bool created,
        out CommandResult failure)
    {
        created = false;
        failure = CommandResult.Success();
        profile = _profileStore.Find(name);
        session = null;
        if (profile is not null) return true;

        session = SessionsSnapshot().FirstOrDefault(candidate =>
            candidate.State.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            candidate.Features.NetworkName?.Equals(name, StringComparison.OrdinalIgnoreCase) == true ||
            ProfileFor(candidate)?.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase) == true);
        if (session is not null)
        {
            profile = EnsureProfileFor(session, out _);
            return true;
        }
        if (!createUnknown)
        {
            failure = CommandResult.Failure($"No network profile named '{name}' exists.");
            return false;
        }

        profile = new NetworkProfile(
            NetworkProfileId.New(),
            name,
            [],
            new IrcIdentity([_preferences.Nickname, _preferences.AlternateNickname], _preferences.Username, _preferences.RealName));
        _profileStore.Add(profile);
        created = true;
        return true;
    }

    private static bool IsChannelProtectionTarget(string value) =>
        value == "*" || value.Length > 1 && value[0] is '#' or '&' or '+' or '!';

    internal static ProtectionDetector? ParseFriendlyProtectionDetector(string value, bool personal)
    {
        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        var detector = normalized switch
        {
            "message" or "msg" or "privmsg" => ProtectionDetector.PrivateMessage,
            "notice" => ProtectionDetector.PrivateNotice,
            "ctcpchannel" => ProtectionDetector.ChannelCtcp,
            "ctcpuser" => ProtectionDetector.Ctcp,
            "masskick" => ProtectionDetector.MassKick,
            "massdeop" => ProtectionDetector.MassDeop,
            "serverop" or "servop" => ProtectionDetector.ServerOp,
            _ => ParseProtectionDetector(normalized)
        };
        if (detector is null) return null;
        return (personal ? PersonalProtectionDetectors : ChannelProtectionDetectors).Contains(detector.Value)
            ? detector
            : null;
    }

    private static PresentationBlock? FriendlyProtectionHelp(string requested, bool personal)
    {
        var detector = ParseFriendlyProtectionDetector(requested, personal);
        if (detector is null) return null;
        var command = personal ? "pprot" : "cprot";
        var scope = personal ? "[network]" : "[network] [channel]";
        var name = DetectorName(detector.Value);
        return new PresentationBlock("HELP:",
        [
            new("Usage", $"/{command} {name} <events> <within> {scope}"),
            new("Description", $"Configures how many {name} events within a duration trigger detection."),
            new("Time", "A bare number means seconds; suffixes s, m, and h are accepted."),
            new("Example", $"/{command} {name} 4 10s")
        ], TitleHighlight: $"/{command} {name}");
    }

    private static bool TryFriendlyProtectionDuration(string value, out TimeSpan duration)
    {
        if (int.TryParse(value, out var seconds) && seconds is >= 1 and <= 3600)
        {
            duration = TimeSpan.FromSeconds(seconds);
            return true;
        }
        return TryParseDuration(value, out duration);
    }

    internal static PresentationBlock FriendlyProtectionPresentation(
        string title,
        ProtectionSettings settings,
        IReadOnlyCollection<ProtectionDetector> detectors)
    {
        var personal = detectors.Contains(ProtectionDetector.PrivateMessage);
        var enabled = personal ? settings.PersonalEnabled : settings.ChannelEnabled;
        var fields = new List<PresentationField>
        {
            new("Protection", !enabled ? "off" : settings.MonitorOnly ? "monitor only" : "on")
        };

        if (personal)
        {
            fields.Add(new PresentationField(
                "Ignore time",
                FormatDuration(TimeSpan.FromSeconds(settings.PersonalIgnoreSeconds))));
        }
        else
        {
            fields.Add(new PresentationField("Action", ChannelActionName(settings.ChannelAction)));
            if (settings.ChannelAction == ChannelProtectionAction.KickBan)
            {
                fields.Add(new PresentationField("Ban time", settings.BanSeconds == 0
                    ? "permanent"
                    : FormatDuration(TimeSpan.FromSeconds(settings.BanSeconds))));
            }
        }

        return new PresentationBlock(
            title,
            fields,
            new PresentationTable(
                ["Detector", "State", "Events", "Within"],
                detectors.Select(detector =>
                {
                    var rule = settings.Rules[detector];
                    return (IReadOnlyList<string>)new[]
                    {
                        DetectorName(detector), rule.Enabled ? "on" : "off",
                        rule.Threshold.ToString(), $"{rule.WindowSeconds}s"
                    };
                }).ToArray()));
    }

    private EffectiveProtectionSettings EffectiveProtection(IrcNetworkSession? session, string? channel)
    {
        if (session is null) return _protectionStore.Effective(null, null);
        var profile = ProfileFor(session);
        var literalChannel = channel?.ToLowerInvariant();
        var foldedChannel = channel is null ? null : IrcCaseFold.Fold(channel, session.State.CaseMapping);
        return _protectionStore.Effective(profile?.Id.ToString(), literalChannel, foldedChannel);
    }

    private bool TryProtectionScope(
        IrcNetworkSession? session,
        IReadOnlyList<string> arguments,
        bool defaultChannel,
        out ProtectionScope? scope,
        out CommandResult failure)
    {
        scope = null;
        failure = CommandResult.Success();
        if (arguments.Count > 0 && arguments[0].Equals("--global", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Count != 1)
            {
                failure = CommandResult.Failure("--global does not take a channel name.");
                return false;
            }
            scope = new ProtectionScope(ProtectionScopeKind.Global);
            return true;
        }
        if (session is null)
        {
            if (arguments.Count == 0)
            {
                scope = new ProtectionScope(ProtectionScopeKind.Global);
                return true;
            }
            failure = CommandResult.Failure("Connect to a network or use --global.");
            return false;
        }

        var profile = EnsureProfileFor(session, out _);
        var networkId = profile.Id.ToString();
        if (arguments.Count > 0 && arguments[0].Equals("--network", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Count != 1)
            {
                failure = CommandResult.Failure("--network does not take a channel name.");
                return false;
            }
            scope = new ProtectionScope(ProtectionScopeKind.Network, networkId);
            return true;
        }

        var explicitChannel = arguments.Count > 0 && arguments[0].Equals("--channel", StringComparison.OrdinalIgnoreCase);
        if (arguments.Count > 0 && !explicitChannel)
        {
            failure = CommandResult.Failure("Protection scope must be --global, --network, or --channel [name].");
            return false;
        }
        if (arguments.Count > 2)
        {
            failure = CommandResult.Failure("--channel accepts at most one channel name.");
            return false;
        }
        var channel = explicitChannel && arguments.Count == 2 ? arguments[1] : ActiveChannel();
        if (explicitChannel || defaultChannel && channel is not null)
        {
            if (string.IsNullOrWhiteSpace(channel) || !session.Features.IsChannel(channel))
            {
                failure = CommandResult.Failure("A channel scope requires an active or explicitly named channel.");
                return false;
            }
            scope = new ProtectionScope(
                ProtectionScopeKind.Channel,
                networkId,
                channel.ToLowerInvariant());
            return true;
        }
        scope = new ProtectionScope(ProtectionScopeKind.Network, networkId);
        return true;
    }

    private void ChangeProtectionSetting(ProtectionScope scope, string key, string value)
    {
        var normalized = key.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (normalized is "exempt.operators" or "exempt.protected" or "exempt.protectionexempt")
        {
            if (!TryParseOnOff(value, out var enabled)) throw new ArgumentException("Exemption values must be on or off.");
            switch (normalized)
            {
                case "exempt.operators":
                    _protectionStore.SetExemptOperators(scope, enabled);
                    break;
                case "exempt.protected":
                    _protectionStore.SetExemptProtected(scope, enabled);
                    break;
                default:
                    _protectionStore.SetExemptProtectionExempt(scope, enabled);
                    break;
            }
            return;
        }

        var separator = normalized.LastIndexOf('.');
        if (separator <= 0 || separator == normalized.Length - 1)
        {
            throw new ArgumentException("Detector settings use <detector>.count, <detector>.window, or <detector>.enabled.");
        }
        var detector = ParseProtectionDetector(normalized[..separator])
            ?? throw new ArgumentException($"Unknown protection detector '{normalized[..separator]}'.");
        var property = normalized[(separator + 1)..];
        switch (property)
        {
            case "count" when int.TryParse(value, out var threshold):
                _protectionStore.SetRule(scope, detector, threshold: threshold);
                return;
            case "window" when TryParseDuration(value, out var duration) && duration.TotalSeconds <= 3600:
                _protectionStore.SetRule(scope, detector, windowSeconds: (int)Math.Ceiling(duration.TotalSeconds));
                return;
            case "enabled" when TryParseOnOff(value, out var enabled):
                _protectionStore.SetRule(scope, detector, enabled: enabled);
                return;
            case "count":
                throw new ArgumentException("Detector count must be a positive number.");
            case "window":
                throw new ArgumentException("Detector window must be between 1 second and 1 hour, such as 10s or 2m.");
            case "enabled":
                throw new ArgumentException("Detector enabled state must be on or off.");
            default:
                throw new ArgumentException($"Unknown detector property '{property}'.");
        }
    }

    private PresentationBlock ProtectionPresentation(
        string title,
        EffectiveProtectionSettings effective,
        bool includeRules,
        ProtectionDetector? selected = null)
    {
        var settings = effective.Settings;
        var fields = new List<PresentationField>
        {
            new("Source", effective.Source.DisplayName),
            new("Channel protection", settings.ChannelEnabled ? "on" : "off"),
            new("Personal protection", settings.PersonalEnabled ? "on" : "off"),
            new("Safety override", settings.MonitorOnly ? "monitor only" : "off"),
            new("Exempt operators", settings.ExemptOperators ? "yes" : "no"),
            new("Exempt protected users", settings.ExemptProtected ? "yes" : "no"),
            new("Exempt protection-exempt users", settings.ExemptProtectionExempt ? "yes" : "no")
        };
        fields.Add(new PresentationField("Channel action", ChannelActionName(settings.ChannelAction)));
        fields.Add(new PresentationField("Protection ban time", settings.BanSeconds == 0
            ? "permanent"
            : FormatDuration(TimeSpan.FromSeconds(settings.BanSeconds))));
        fields.Add(new PresentationField("Personal ignore time",
            FormatDuration(TimeSpan.FromSeconds(settings.PersonalIgnoreSeconds))));
        PresentationTable? table = null;
        if (includeRules)
        {
            var rules = settings.Rules
                .Where(entry => selected is null || entry.Key == selected)
                .OrderBy(entry => entry.Key)
                .Select(entry => (IReadOnlyList<string>)new[]
                {
                    DetectorName(entry.Key), entry.Value.Enabled ? "on" : "off",
                    entry.Value.Threshold.ToString(), $"{entry.Value.WindowSeconds}s"
                }).ToArray();
            table = new PresentationTable(["Detector", "Enabled", "Events", "Within"], rules);
        }
        return new PresentationBlock(title, fields, table);
    }

    private CommandResult ProtectionTest(IrcNetworkSession? session, IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 4 || ParseProtectionDetector(arguments[1]) is not { } detector ||
            !int.TryParse(arguments[3], out var count) || count is < 1 or > 1000)
        {
            return CommandResult.Failure("Usage: /protect test <detector> <actor> <count> [sample text]");
        }
        var effective = EffectiveProtection(session, ActiveChannel());
        var rule = effective.Settings.Rules[detector];
        var monitor = new ProtectionMonitor();
        ProtectionDetection? detection = null;
        var now = DateTimeOffset.Now;
        var networkId = session?.State.Id ?? NetworkSessionId.New();
        var text = arguments.Count > 4 ? string.Join(' ', arguments.Skip(4)) : "sample text";
        for (var index = 0; index < count; index++)
        {
            detection ??= monitor.Evaluate(new ProtectionEvidence(
                networkId, detector, arguments[2], ActiveChannel(), text,
                now.AddMilliseconds(index)), rule);
        }
        return CommandResult.Success(new PresentationBlock(
            "Protection test",
            [
                new PresentationField("Detector", DetectorName(detector)),
                new PresentationField("Actor", arguments[2]),
                new PresentationField("Evidence", count.ToString()),
                new PresentationField("Threshold", $"{rule.Threshold} in {rule.WindowSeconds}s"),
                new PresentationField("Result", detection is null ? "not triggered" : $"triggered at {detection.Count}")
            ],
            Summary: "Test mode never changes live counters or sends IRC actions."));
    }

    private BufferState ProtectionAuditBuffer(IrcNetworkSession session) =>
        session.State.GetOrCreateBuffer(BufferKind.Results, "=protection");

    private static ProtectionDetector? ParseProtectionDetector(string value)
    {
        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        if (normalized == "ctcpchannel") return ProtectionDetector.ChannelCtcp;
        if (normalized is "ctcpuser" or "ctcp") return ProtectionDetector.Ctcp;
        return Enum.TryParse<ProtectionDetector>(normalized, true, out var detector) ? detector : null;
    }

    internal static string DetectorName(ProtectionDetector detector) => detector switch
    {
        ProtectionDetector.MassKick => "mass.kick",
        ProtectionDetector.MassDeop => "mass.deop",
        ProtectionDetector.ServerOp => "servop",
        ProtectionDetector.PrivateMessage => "private message",
        ProtectionDetector.PrivateNotice => "private notice",
        ProtectionDetector.Ctcp => "ctcp.user",
        ProtectionDetector.ChannelCtcp => "ctcp.channel",
        _ => detector.ToString().ToLowerInvariant()
    };

    private static bool TryParseChannelProtectionAction(string value, out ChannelProtectionAction action)
    {
        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        action = normalized switch
        {
            "monitor" => ChannelProtectionAction.Monitor,
            "kick" => ChannelProtectionAction.Kick,
            "kickban" or "kb" => ChannelProtectionAction.KickBan,
            _ => default
        };
        return normalized is "monitor" or "kick" or "kickban" or "kb";
    }

    private static string ChannelActionName(ChannelProtectionAction action) => action switch
    {
        ChannelProtectionAction.Monitor => "monitor",
        ChannelProtectionAction.Kick => "kick",
        ChannelProtectionAction.KickBan => "kickban",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };


    private sealed record CloneGroup(string Host, ChannelMemberState[] Members);
}
