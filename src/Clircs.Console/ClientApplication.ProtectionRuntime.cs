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
        var isPrivate = false;
        try
        {
            var fields = sessionEvent.Fields;
            if (fields is null) return;
            var channel = fields.GetValueOrDefault("channel");
            if (channel is null && session.State.TryGetBuffer(sessionEvent.BufferId, out var buffer) && buffer!.Kind == BufferKind.Channel)
            {
                channel = buffer.Name;
            }
            isPrivate = fields.GetValueOrDefault("private") == "true" ||
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

            var profileId = ProfileFor(session)?.Id.ToString();
            var literalChannel = channel?.ToLowerInvariant();
            var foldedChannel = channel is null
                ? null
                : IrcCaseFold.Fold(channel, session.State.CaseMapping);

            var channelSettings = isPrivate
                ? null
                : _protectionStore.EffectiveChannel(profileId, literalChannel, foldedChannel).Settings;
            var personalSettings = isPrivate
                ? _protectionStore.EffectivePersonal(profileId).Settings
                : null;

            var enabled = personalSettings?.Enabled ?? channelSettings!.Enabled;
            if (!enabled || !isPrivate && channel is null)
            {
                return;
            }

            var rules = personalSettings?.Rules ?? channelSettings!.Rules;
            var evidence = BuildProtectionEvidence(sessionEvent, session, actor, channel, isPrivate);
            foreach (var item in evidence)
            {
                var detection = _userAndChannelPolicy.Evaluate(item, rules[item.Detector]);
                if (detection is null) continue;
                var exemption = ProtectionExemption(
                    session,
                    channel,
                    actor,
                    fields,
                    channelSettings?.ExemptOperators ?? false,
                    personalSettings?.ExemptProtected ?? channelSettings!.ExemptProtected,
                    item.Detector);
                var location = channel ?? "private messages";
                var prefix =
                    $"{DetectorName(item.Detector)}: {actor} in {location} reached {detection.Count}/{detection.Rule.Threshold} " +
                    $"within {detection.Rule.WindowSeconds}s";
                if (exemption is not null)
                {
                    PublishProtectionAudit(session, $"{prefix}; SUPPRESSED - {exemption}.",
                        item.Detector, actor, channel, exemption, isPrivate);
                    continue;
                }
                var monitorOnly =
                    personalSettings?.Action == PersonalProtectionAction.Monitor ||
                    channelSettings?.Action == ChannelProtectionAction.Monitor;
                if (monitorOnly)
                {
                    PublishProtectionAudit(session, $"{prefix}; MONITOR - no IRC action sent.",
                        item.Detector, actor, channel, null, isPrivate);
                    continue;
                }
                if (isPrivate)
                {
                    var identity = ProtectionIdentityKey(session, actor, fields);
                    _userAndChannelPolicy.IgnorePersonally(
                        session.State.Id,
                        identity,
                        DateTimeOffset.UtcNow.AddSeconds(personalSettings!.IgnoreSeconds));
                    PublishProtectionAudit(session,
                        $"{prefix}; IGNORED locally for {FormatDuration(TimeSpan.FromSeconds(personalSettings!.IgnoreSeconds))}.",
                        item.Detector, actor, channel, null, isPrivate);
                    continue;
                }
                if (channel is null)
                {
                    PublishProtectionAudit(session, $"{prefix}; SUPPRESSED - no channel target was available.",
                        item.Detector, actor, channel, "missing channel", isPrivate);
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
                        item.Detector, actor, channel, "action already pending", isPrivate);
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
                        channelSettings!,
                        prefix));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            PublishProtectionAudit(session, $"Protection monitor error: {exception.Message}", null, null, null, "evaluation error", isPrivate);
        }
    }

    private bool IsIgnoredCommunication(SessionEvent sessionEvent)
    {
        if (sessionEvent.Kind == SessionEventKind.Protection ||
            FindSession(sessionEvent.NetworkSessionId) is not { } session ||
            sessionEvent.Fields is not { } fields)
        {
            return false;
        }

        var actor = fields.GetValueOrDefault("nick");
        if (string.IsNullOrWhiteSpace(actor))
        {
            return false;
        }

        var isPrivate = fields.GetValueOrDefault("private") == "true" ||
            session.State.TryGetBuffer(sessionEvent.BufferId, out var buffer) &&
            buffer!.Kind == BufferKind.Query;

        var permanentlyIgnored = IsPermanentIgnoreCandidate(sessionEvent) &&
            IsPermanentlyIgnored(
                session.State.Id,
                actor,
                fields.GetValueOrDefault("username"),
                fields.GetValueOrDefault("host"),
                session.State.CaseMapping);

        var temporarilyIgnored = isPrivate &&
            _userAndChannelPolicy.IsPersonallyIgnored(
                session.State.Id,
                ProtectionIdentityKey(session, actor, fields),
                DateTimeOffset.UtcNow);

        var ignored = permanentlyIgnored || temporarilyIgnored;
        if (ignored &&
            session.State.TryGetBuffer(sessionEvent.BufferId, out var ignoredBuffer) &&
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

            if (removeEmptyQuery)
            {
                _presenter.ForgetInputHistory(ignoredBuffer.Id);
            }
        }

        return ignored;
    }

    private static bool IsPermanentIgnoreCandidate(SessionEvent sessionEvent) =>
        sessionEvent.Kind is
            SessionEventKind.Message or
            SessionEventKind.Highlight or
            SessionEventKind.Notice or
            SessionEventKind.Action ||
        sessionEvent.Fields?.GetValueOrDefault("event") == "dcc.invalid";

    private bool IsPermanentlyIgnored(
        NetworkSessionId sessionId,
        string nickname,
        string? username,
        string? host,
        IrcCaseMapping mapping)
    {
        if (FindSession(sessionId) is not { } session)
            return false;

        var profile = ProfileFor(session);
        var directory = profile is null
            ? _userAndChannelPolicy.FindSessionIgnoreDirectory(sessionId)
            : _userAndChannelPolicy.GetDirectory(
                profile.Id,
                () => _userDirectoryStore.Load(profile.Id));

        return directory?.IsIgnored(nickname, username, host, mapping) == true;
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
        ChannelProtectionSettings settings,
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
                    detector, actor, channelName, "client is not a channel operator", false);
                return;
            }

            var targetNick = fields.GetValueOrDefault("newNick") ?? actor;
            var comparer = new IrcNameComparer(session.State.CaseMapping);
            if (comparer.Equals(targetNick, session.CurrentNickname) ||
                !channel.TryGetMember(targetNick, out var target))
            {
                PublishProtectionAudit(session,
                    $"{auditPrefix}; SUPPRESSED - {targetNick} is no longer in {channelName}.",
                    detector, actor, channelName, "target is no longer present", false);
                return;
            }

            string? banMask = null;
            if (settings.Action == ChannelProtectionAction.KickBan)
            {
                var username = target!.Username ?? fields.GetValueOrDefault("username");
                var host = target.Host ?? fields.GetValueOrDefault("host");
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(host))
                {
                    PublishProtectionAudit(session,
                        $"{auditPrefix}; SUPPRESSED - no synchronized address is available for {targetNick}.",
                        detector, actor, channelName, "missing synchronized address", false);
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
            var action = settings.Action == ChannelProtectionAction.KickBan
                ? $"KICKBAN sent to {targetNick} using {banMask}"
                : $"KICK sent to {targetNick}";
            PublishProtectionAudit(session, $"{auditPrefix}; {action}.",
                detector, actor, channelName, null, false);
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
                    detector, actor, channelName, "action failed", false);
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
        bool isPrivate)
    {
        var evidence = new List<ProtectionEvidence>();
        var fields = sessionEvent.Fields!;
        var text = fields.GetValueOrDefault("message");
        void Add(ProtectionDetector detector, int weight = 1, string? counterActor = null) =>
            evidence.Add(new ProtectionEvidence(
                session.State.Id, detector, counterActor ?? actor, channel, text, sessionEvent.ReceivedAt, weight));

        if (isPrivate)
        {
            if (fields.GetValueOrDefault("event") is "ctcp" or "dcc.request" or "dcc.invalid")
                Add(ProtectionDetector.Ctcp);
            else if (fields.GetValueOrDefault("event") == "invite") Add(ProtectionDetector.Invite);
            else if (sessionEvent.Kind == SessionEventKind.Notice) Add(ProtectionDetector.PrivateNotice);
            else if (sessionEvent.Kind is SessionEventKind.Message or SessionEventKind.Highlight or SessionEventKind.Action)
                Add(ProtectionDetector.PrivateMessage);
            return evidence;
        }
        if (channel is null) return evidence;

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
            if (deops > 0) Add(ProtectionDetector.MassDeop, deops);
        }
        return evidence;
    }

    private string? ProtectionExemption(
        IrcNetworkSession session,
        string? channel,
        string actor,
        IReadOnlyDictionary<string, string?> fields,
        bool exemptOperators,
        bool exemptProtected,
        ProtectionDetector detector)
    {
        ChannelMemberState? member = null;
        if (channel is not null && session.State.TryGetChannel(channel, out var channelState))
        {
            channelState!.TryGetMember(actor, out member);
        }
        if (OperatorExemptionApplies(detector) && exemptOperators && member is not null &&
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
        if (exemptProtected && roles.HasFlag(UserRole.Protected))
            return $"{match.User.Handle} is a friend";
        return null;
    }

    private void PublishProtectionAudit(
        IrcNetworkSession session,
        string text,
        ProtectionDetector? detector,
        string? actor,
        string? channel,
        string? suppression,
        bool personal)
    {
        OnSessionEvent(new SessionEvent(
            session.State.Id,
            ProtectionAuditBuffer(session, personal).Id,
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
        detector is not (ProtectionDetector.MassKick or ProtectionDetector.MassDeop);

    private void OnLogWriterError(string message) => _presenter.Result(message, success: false);


    private bool TryFriendlyChannelScope(
        IReadOnlyList<string> arguments,
        out ProtectionScope? scope,
        out string label,
        out CommandResult failure)
    {
        scope = null;
        label = string.Empty;
        failure = CommandResult.Success();
        string channel;
        NetworkProfile? profile;
        IrcNetworkSession? session;

        if (arguments.Count == 1 &&
            arguments[0].Equals("--global", StringComparison.OrdinalIgnoreCase))
        {
            scope = new ProtectionScope(ProtectionScopeKind.Global);
            label = "global defaults";
            return true;
        }
        else if (arguments.Count == 0)
        {
            session = ActiveSession();
            channel = ActiveChannel() ?? string.Empty;
            if (session is null || channel.Length == 0)
            {
                failure = CommandResult.Failure(
                    "Use this in a channel window, or specify both network and channel: /cprot on EFnet #clircs");
                return false;
            }
            profile = ProfileFor(session);
            if (profile is null)
            {
                failure = CommandResult.Failure(
                    "This connection has no saved network profile. Connect with /server <profile>, or specify an existing profile by name.");
                return false;
            }
        }
        else if (arguments.Count == 1 && IsChannelProtectionTarget(arguments[0]))
        {
            session = ActiveSession();
            if (session is null)
            {
                failure = CommandResult.Failure("Specify a network as well: /cprot on EFnet #clircs");
                return false;
            }
            profile = ProfileFor(session);
            if (profile is null)
            {
                failure = CommandResult.Failure(
                    "This connection has no saved network profile. Connect with /server <profile>, or specify an existing profile by name.");
                return false;
            }
            channel = arguments[0];
        }
        else if (arguments.Count == 2)
        {
            if (!TryProtectionProfile(arguments[0], out profile, out failure))
                return false;
            channel = arguments[1];
        }
        else
        {
            failure = CommandResult.Failure("Channel protection scope must be <channel>, <network> <channel>, <network> *, or --global.");
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
        out ProtectionScope? scope,
        out string label,
        out CommandResult failure)
    {
        scope = null;
        label = string.Empty;
        failure = CommandResult.Success();
        NetworkProfile? profile;
        if (arguments.Count == 1 &&
            arguments[0].Equals("--global", StringComparison.OrdinalIgnoreCase))
        {
            scope = new ProtectionScope(ProtectionScopeKind.Global);
            label = "global defaults";
            return true;
        }
        else if (arguments.Count == 0)
        {
            var session = ActiveSession();
            if (session is null)
            {
                failure = CommandResult.Failure("Connect to a network or name one, such as /pprot on EFnet.");
                return false;
            }
            profile = ProfileFor(session);
            if (profile is null)
            {
                failure = CommandResult.Failure(
                    "This connection has no saved network profile. Connect with /server <profile>, or specify an existing profile by name.");
                return false;
            }
        }
        else if (arguments.Count == 1)
        {
            if (!TryProtectionProfile(arguments[0], out profile, out failure))
                return false;
        }
        else
        {
            failure = CommandResult.Failure("Personal protection scope must be <network> or --global.");
            return false;
        }
        scope = new ProtectionScope(ProtectionScopeKind.Network, profile!.Id.ToString());
        label = profile.DisplayName;
        return true;
    }

    private bool TryProtectionProfile(
        string name,
        out NetworkProfile? profile,
        out CommandResult failure)
    {
        profile = _profileStore.Find(name);
        failure = profile is null
            ? CommandResult.Failure($"No network profile named '{name}' exists.")
            : CommandResult.Success();
        return profile is not null;
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
            "text" => ProtectionDetector.Text,
            "repeat" => ProtectionDetector.Repeat,
            "join" => ProtectionDetector.Join,
            "nick" => ProtectionDetector.Nick,
            "kick" => ProtectionDetector.MassKick,
            "deop" => ProtectionDetector.MassDeop,
            "caps" => ProtectionDetector.Caps,
            "controls" => ProtectionDetector.Controls,
            "ctcp" => personal
                ? ProtectionDetector.Ctcp
                : ProtectionDetector.ChannelCtcp,
            "message" or "msg" or "privmsg" => ProtectionDetector.PrivateMessage,
            "notice" => ProtectionDetector.PrivateNotice,
            "invite" => ProtectionDetector.Invite,
            _ => (ProtectionDetector?)null
        };
        if (detector is null) return null;
        return (personal ? PersonalProtectionDetectors : ChannelProtectionDetectors).Contains(detector.Value)
            ? detector
            : null;
    }

    private static PresentationBlock? FriendlyProtectionHelp(
        string requested,
        bool personal)
    {
        var detector = ParseFriendlyProtectionDetector(requested, personal);
        if (detector is null)
            return null;

        var command = personal ? "pprot" : "cprot";
        var name = DetectorName(detector.Value);
        var scopes = personal
            ? "<network>, --global"
            : "<channel>, <network> <channel>, <network> *, --global";
        var example = personal
            ? $"/{command} {name} 4 10s EFnet"
            : $"/{command} {name} 4 10s EFnet #clircs";

        return new PresentationBlock("HELP:",
        [
            new("Usage", $"/{command} {name} <events> <within> [scope]"),
            new("Disable/reset", $"/{command} {name} off|default [scope]"),
            new("Description", $"Configures how many {name} events within a duration trigger detection."),
            new("Time", "A bare number means seconds; suffixes s, m, and h are accepted."),
            new("Scopes", scopes),
            new("Example", example)
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

    internal static PresentationBlock ChannelProtectionPresentation(
        string title,
        ChannelProtectionSettings settings)
    {
        var fields = new List<PresentationField>
        {
            new("Protection", settings.Enabled ? "on" : "off"),
            new("Action", ChannelActionName(settings.Action))
        };

        if (settings.Action == ChannelProtectionAction.KickBan)
        {
            fields.Add(new PresentationField(
                "Ban time",
                settings.BanSeconds == 0
                    ? "permanent"
                    : FormatDuration(TimeSpan.FromSeconds(settings.BanSeconds))));
        }
        fields.Add(new PresentationField(
            "Exempt chanops",
            settings.ExemptOperators ? "yes" : "no"));
        fields.Add(new PresentationField(
            "Exempt friends",
            settings.ExemptProtected ? "yes" : "no"));
        fields.Add(new PresentationField(string.Empty, string.Empty));

        return new PresentationBlock(
            title,
            fields,
            ProtectionRuleTable(settings.Rules, ChannelProtectionDetectors));
    }

    internal static PresentationBlock PersonalProtectionPresentation(
        string title,
        PersonalProtectionSettings settings)
    {
        var fields = new List<PresentationField>
        {
            new("Protection", settings.Enabled ? "on" : "off"),
            new("Action", PersonalActionName(settings.Action))
        };

        if (settings.Action == PersonalProtectionAction.Ignore)
        {
            fields.Add(new PresentationField(
                "Ignore time",
                FormatDuration(TimeSpan.FromSeconds(settings.IgnoreSeconds))));
        }
        fields.Add(new PresentationField(
            "Exempt friends",
            settings.ExemptProtected ? "yes" : "no"));
        fields.Add(new PresentationField(string.Empty, string.Empty));

        return new PresentationBlock(
            title,
            fields,
            ProtectionRuleTable(settings.Rules, PersonalProtectionDetectors));
    }

    internal static PresentationBlock ProtectionCountersPresentation(
        string title,
        IReadOnlyList<ProtectionCounter> counters,
        DateTimeOffset now,
        bool personal)
    {
        var detectors = personal
            ? PersonalProtectionDetectors
            : ChannelProtectionDetectors;
        var relevant = counters
            .Where(counter => detectors.Contains(counter.Detector))
            .ToArray();

        if (relevant.Length == 0)
        {
            var kind = personal ? "personal" : "channel";
            return new PresentationBlock(
                title,
                Summary: $"No active {kind} protection counters.");
        }

        IReadOnlyList<IReadOnlyList<string>> rows = personal
            ? relevant.Select(counter => (IReadOnlyList<string>)
            [
                DetectorName(counter.Detector),
                counter.Actor,
                counter.Count.ToString(),
                FormatDuration(counter.ExpiresAt - now)
            ]).ToArray()
            : relevant.Select(counter => (IReadOnlyList<string>)
            [
                DetectorName(counter.Detector),
                counter.Actor,
                counter.Channel ?? "-",
                counter.Count.ToString(),
                FormatDuration(counter.ExpiresAt - now)
            ]).ToArray();

        return new PresentationBlock(
            title,
            Table: new PresentationTable(
                personal
                    ? ["Rule", "Actor", "Events", "Expires in"]
                    : ["Rule", "Actor", "Channel", "Events", "Expires in"],
                rows));
    }

    private static PresentationTable ProtectionRuleTable(
        IReadOnlyDictionary<ProtectionDetector, ProtectionRule> rules,
        IReadOnlyCollection<ProtectionDetector> detectors) =>
        new(
            ["Rule", "State", "Events", "Within"],
            detectors.Select(detector =>
            {
                var rule = rules[detector];
                return (IReadOnlyList<string>)new[]
                {
                    DetectorName(detector),
                    rule.Enabled ? "on" : "off",
                    rule.Threshold.ToString(),
                    $"{rule.WindowSeconds}s"
                };
            }).ToArray());

    private BufferState ProtectionAuditBuffer(
        IrcNetworkSession session,
        bool personal) =>
        session.State.GetOrCreateBuffer(
            BufferKind.Results,
            personal ? "=pprot" : "=cprot");

    internal static string DetectorName(ProtectionDetector detector) => detector switch
    {
        ProtectionDetector.MassKick => "kick",
        ProtectionDetector.MassDeop => "deop",
        ProtectionDetector.PrivateMessage => "message",
        ProtectionDetector.PrivateNotice => "notice",
        ProtectionDetector.Ctcp or ProtectionDetector.ChannelCtcp => "ctcp",
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

    private static string PersonalActionName(PersonalProtectionAction action) => action switch
    {
        PersonalProtectionAction.Ignore => "ignore",
        PersonalProtectionAction.Monitor => "monitor",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };


    private sealed record CloneGroup(string Host, ChannelMemberState[] Members);
}
