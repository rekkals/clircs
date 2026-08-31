using System.Globalization;
using Clircs.Commands;
using Clircs.Networking;
using Clircs.Sessions;
using Clircs.State;

namespace Clircs.ConsoleClient;

// Owns command names, aliases, help metadata, and handler registration.
internal sealed partial class ClientApplication
{
    private void RegisterCommands()
    {
        Register("help", ["?"], "Show a list of commands or help for [command].", HelpAsync);
        Register("about", [], "Display the clircs startup presentation.", AboutAsync);
        Register("config", [], "Show where clircs stores configuration and user data.", ConfigAsync);
        Register("backup", [], "Create or manage user-data backups.", BackupAsync);
        Register("logging", ["log"], "Start, stop, and manage channel and query logs.", LoggingAsync);
        Register("server", ["connect"], "Connect to an IRC server or saved network profile.", ServerAsync);
        Register("network", [], "List live connections or manage saved network profiles.", NetworkAsync);
        Register("disconnect", [], "Disconnect, but leave clircs running.", DisconnectAsync);
        Register("reconnect", [], "Reconnect the active offline session or cancel an automatic retry.", ReconnectAsync);
        Register("quit", ["exit"], "Disconnect and exit clircs.", QuitAsync);
        Register("nick", [], "Change your nickname, or set it before connecting.", NickAsync);
        Register("away", [], "Set yourself as away.", AwayAsync);
        Register("back", [], "Set yourself as back.", BackAsync);
        Register("awaylog", ["msglog"], "Enables or disables logging messages while away.", AwayLogAsync);
        Register("messages", ["mchk"], "Read or remove messages logged while away.", AwayMessagesAsync);
        Register("window", ["win"], "List windows or switch to window [number|name].", BufferAsync);
        Register("next", ["wn"], "Switch to the next window.", NextBufferAsync);
        Register("previous", ["wp"], "Switch to the previous window.", PreviousBufferAsync);
        Register("close", [], "Close the active window.", CloseBufferAsync);
        Register("clear", ["cls"], "Clear the visible terminal.", ClearAsync);
        Register("autojoin", ["ajoin", "aj"], "Manage autojoin channels for the active network profile.", AutojoinAsync);
        Register("rj", [], "Remove a channel from the active network profile's autojoin list.", AutojoinRemoveAliasAsync);
        Register("msg", ["m"], "Send a message to a nickname or channel.", MessageAsync);
        Register("notice", ["n"], "Send a notice to a nickname or channel.", NoticeAsync);
        Register("say", [], "Send text to the active channel/query.", SayCommandAsync);
        Register("me", [], "Send an action to the active channel/query.", MeAsync);
        Register("describe", [], "Send an action to a nickname or channel.", DescribeAsync);
        Register("ame", [], "Send an action to every joined channel on this network.", AllChannelActionAsync);
        Register("amsg", [], "Send a message to every joined channel on this network.", AllChannelMessageAsync);
        Register("query", ["q"], "Open a private query window with <nick>.", QueryAsync);
        Register("ctcp", [], "Send a CTCP request to <nick>.", CtcpAsync);
        Register("dcc", [], "Start and manage DCC chats, file transfers, and requests.", DccAsync);
        Register("xdcc", [], "Request file packs from XDCC bots.", XdccAsync);
        Register("ping", [], "Send a CTCP PING request.", PingAsync);
        Register("sv", [], "Send the current clircs version to the active window.", ShowVersionAsync);
        Register("time", [], "Show local time or query a [nickname].", TimeAsync);
        Register("raw", ["quote"], "Send raw input to the IRC server.", RawAsync);
        Register("join", ["j"], "Join a channel.", JoinAsync);
        Register("part", ["p"], "Part a channel.", PartAsync);
        Register("cycle", [], "Part and rejoin the active channel.", CycleAsync);
        Register("invite", ["i"], "Invite <nick> to [channel].", InviteAsync);
        Register("topic", ["t"], "Show or set a channel topic.", TopicAsync);
        Register("rt", [], "Set a configured default topic or a random quote.", RandomTopicAsync);
        Register("mode", ["cm"], "Show or change modes.", ModeAsync);
        Register("op", [], "Grant channel operator status.", OpAsync);
        Register("deop", ["dop"], "Remove channel operator status.", DeopAsync);
        Register("voice", ["v"], "Grant channel voice status.", VoiceAsync);
        Register("devoice", ["dv"], "Remove channel voice status.", DevoiceAsync);
        Register("kick", ["k"], "Kick a user from the active channel.", KickAsync);
        Register("ban", [], "Ban a <nick> or <hostmask> from the active channel.", BanAsync);
        Register("kickban", ["kb", "bk"], "Kick and ban a user from the active channel.", KickBanAsync);
        Register("tban", [], "Apply a ban and remove it after a <duration>.", TimedBanAsync);
        Register("mop", [], "Op every non-opped member in the active channel.", MassOpAsync);
        Register("mdop", [], "Deop every operator except yourself.", MassDeopAsync);
        Register("mv", [], "Voice every regular member.", MassVoiceAsync);
        Register("mdv", [], "Devoice every voiced member except yourself.", MassDevoiceAsync);
        Register("mmode", [], "Apply the given mode to multiple users.", MultiModeAsync);
        Register("banlist", [], "Check and display the channel ban list.", BanListAsync);
        Register("exceptlist", [], "Display channel ban exceptions.", ExceptListAsync);
        Register("invitelist", [], "Display channel invite exceptions.", InviteListAsync);
        Register("quietlist", [], "Display channel quiet masks.", QuietListAsync);
        Register("unban", [], "Remove a channel ban mask.", UnbanAsync);
        Register("clearbans", ["clban"], "Remove all channel bans.", ClearBansAsync);
        Register("appendtopic", ["at"], "Append text to the active channel topic.", AppendTopicAsync);
        Register("cleartopic", ["ct"], "Clear the active channel topic.", ClearTopicAsync);
        Register("adduser", [], "Add a user to the active network's user directory.", AddUserAsync);
        Register("addbot", [], "Add a bot to the active network's user directory.", AddBotAsync);
        Register("remuser", [], "Remove a user from the active network's user directory.", RemoveUserAsync);
        Register("addhost", [], "Add a hostmask to a user.", AddHostAsync);
        Register("remhost", [], "Remove a hostmask from a user.", RemoveHostAsync);
        Register("chattr", [], "Change global or channel user roles and permissions.", ChangeAttributesAsync);
        Register("addchan", [], "Add a channel to the user policy entry.", AddUserChannelAsync);
        Register("remchan", [], "Remove a channel from the user policy entry.", RemoveUserChannelAsync);
        Register("chinfo", [], "Set a global or channel-specific JOIN infoline for a user.", ChangeUserInfoAsync);
        Register("uwhois", [], "Show a user record or match a visible nickname.", UserWhoisAsync);
        Register("users", [], "List active network's user records.", UsersAsync);
        Register("usersum", [], "Summarize active network's user roles.", UserSummaryAsync);
        Register("notify", [], "Manage the active network's notify list.", NotifyAsync);
        Register("accept", [], "Manage the server-side accept list.", AcceptAsync);
        Register("cprot", [], "Configure channel protection.", ChannelProtectionAsync);
        Register("pprot", ["fprot"], "Configure personal protection.", PersonalProtectionAsync);
        Register("protect", [], "Inspect the combined protection engine and audit data.", ProtectAsync);
        Register("clones", [], "Find channel users sharing the same visible host.", ClonesAsync);
        Register("ufind", [], "Match visible channel members to user records.", UserFindAsync);
        Register("addban", [], "Add a persistent network policy ban.", AddPolicyBanAsync);
        Register("remban", [], "Remove a persistent network policy ban.", RemovePolicyBanAsync);
        Register("bans", [], "List persistent network policy bans.", PolicyBansAsync);
        Register("umop", [], "Op userlist members eligible for operator status.", UserMassOpAsync);
        Register("umdop", [], "Deop members not eligible for operator status.", UserMassDeopAsync);
        Register("umv", [], "Voice userlist members eligible for voice.", UserMassVoiceAsync);
        Register("umdv", [], "Devoice members not eligible for voice.", UserMassDevoiceAsync);
        Register("filterkick", ["fk"], "Kick members matching a hostmask.", FilterKickAsync);
        Register("filterkickban", ["fkb"], "Ban a hostmask and kick matching members.", FilterKickBanAsync);
        Register("findnickkick", ["fnk"], "Kick members whose nicknames match a wildcard.", FindNickKickAsync);
        Register("kicknonops", ["knop"], "Kick non-operators except protected users.", KickNonOperatorsAsync);
        Register("cop", [], "Op a nick in every eligible common channel.", CommonOpAsync);
        Register("cban", [], "Ban and deop a nick in every eligible common channel.", CommonBanAsync);
        Register("ckick", [], "Kick a nick from every eligible common channel.", CommonKickAsync);
        Register("ckb", [], "Kick-ban a nick in every eligible common channel.", CommonKickBanAsync);
        Register("massinvite", ["mi"], "Invite active-channel members to another joined channel.", MassInviteAsync);
        Register("inviteall", ["ia"], "Invite one nick to every eligible joined channel.", InviteAllAsync);
        Register("wall", ["on", "wl"], "Notice operators in the active channel.", OperatorWallAsync);
        Register("wallmsg", ["wallm", "wm"], "Message operators in the active channel.", OperatorWallMessageAsync);
        Register("voicenotice", ["vnotice", "vn", "vwall", "wallv"], "Notice voiced users and operators.", VoiceNoticeAsync);
        Register("voicemsg", ["vmsg"], "Message voiced users and operators.", VoiceMessageAsync);
        Register("nonopnotice", ["nnotice", "nn", "nwall", "walln"], "Notice non-operators.", NonOperatorNoticeAsync);
        Register("nonopmsg", ["nmsg"], "Message non-operators.", NonOperatorMessageAsync);
        Register("userwall", ["uwall"], "Notice non-bot operators.", UserWallAsync);
        Register("names", [], "List users in the active channel.", NamesAsync);
        Register("who", [], "Performs a WHO on the active window or specified [channel|nickname|mask].", WhoAsync);
        Register("whois", ["wi", "w"], "Performs a WHOIS on <nickname>.", WhoisAsync);
        Register("iwhois", ["wii"], "Performs WHOIS on <nickname> with idle and sign-on information.", IdleWhoisAsync);
        Register("whowas", ["ww"], "Performs WHOWAS on <nickname>.", WhowasAsync);
        Register("motd", [], "Request the server MOTD.", MotdAsync);
        Register("links", [], "Request server links.", LinksAsync);
        Register("list", [], "Show publicly listed channels on the server.", ListAsync);
        Register("dns", [], "Resolve a hostname or IP address.", DnsAsync);
        Register("nickserv", [], "Send a command to NickServ.", ServiceAsync);
        Register("chanserv", [], "Send a command to ChanServ.", ServiceAsync);
        Register("memoserv", [], "Send a command to MemoServ.", ServiceAsync);
        Register("operserv", [], "Send a command to OperServ.", ServiceAsync);
        Register("hostserv", [], "Send a command to HostServ.", ServiceAsync);
        Register("botserv", [], "Send a command to BotServ.", ServiceAsync);
        Register("limitserv", [], "Send a command to LimitServ.", ServiceAsync);
        Register("set", [], "Inspect or change client settings.", SetAsync);
        Register("theme", [], "List, select, or reload themes.", ThemeAsync);
        Register("tls", [], "List or revoke remembered TLS certificate pins.", TlsAsync);
        Register("script", [], "Manage third party scripts.", ScriptAsync);
        Register("debug", [], "Open raw IRC traffic for the active network.", DebugAsync);
    }

    private void Register(string name, string[] aliases, string summary, CommandHandler handler)
    {
        // A summary may embed usage beginning with "/<command>"; otherwise an
        // explicit usage override, or the bare command name, supplies the Usage field.
        var syntaxStart = summary.IndexOf($"/{name}", StringComparison.OrdinalIgnoreCase);
        var usage = syntaxStart >= 0
            ? summary[syntaxStart..].Trim().TrimEnd('.')
            : HelpUsageOverrides.GetValueOrDefault(name, $"/{name}");
        var description = syntaxStart <= 0
            ? FormatLocalCommandResult(summary)
            : FormatLocalCommandResult(summary[..syntaxStart].Trim().TrimEnd(':'));
        Func<string, PresentationBlock?>? topicHelp = name switch
        {
            "set" => SettingHelp,
            "dcc" => DccHelp,
            "protect" => ProtectionHelp,
            "cprot" => requested => FriendlyProtectionHelp(requested, personal: false),
            "pprot" => requested => FriendlyProtectionHelp(requested, personal: true),
            _ => null
        };
        _commands.Register(new CommandDefinition(
            name,
            aliases,
            name == "dcc" ? string.Empty : usage,
            description,
            handler,
            // /protect remains callable for advanced use but is intentionally omitted
            // from the general command list.
            visibleInHelp: name != "protect",
            ExtendedHelpFields(name),
            topicHelp));
    }

    private static IReadOnlyList<PresentationField> ExtendedHelpFields(string command) => command.ToLowerInvariant() switch
    {
        "server" =>
        [
            new("--tls", "Encrypt the connection with TLS."),
            new("--new", "Open a new network session instead of replacing active."),
            new("--password", "Prompt securely for a server password or complete bouncer login string."),
            new("Example", "/server irc.example.net 6697 --tls")
        ],
        "network" =>
        [
            new("SASL status", "/network sasl <profile>"),
            new("Enable PLAIN", "/network sasl <profile> [plain] <account> [required|optional]"),
            new("Enable EXTERNAL", "/network sasl <profile> external <certificate.pfx> [required|optional]"),
            new("Disable SASL", "/network sasl <profile> off"),
            new("Security", "SASL requires TLS; passwords are encrypted with Windows DPAPI")
        ],
        "dcc" =>
        [
            new("Chat", "/dcc chat <nick> [--passive]"),
            new("Secure chat", "/dcc schat <nick> [--passive]"),
            new("Send", "/dcc send <nick> <file> [--passive]"),
            new("Secure send", "/dcc ssend <nick> <file> [--passive]"),
            new("Requests", "/dcc list or /dcc show <id>"),
            new("Incoming", "/dcc accept <id> or /dcc reject <id>"),
            new("Resume", "/dcc resume <id>"),
            new("Cancel", "/dcc cancel <id>")
        ],
        "xdcc" =>
        [
            new("Download", "/xdcc get <bot> <pack>"),
            new("Secure download", "/xdcc sget <bot> <pack>"),
            new("Incoming", "Use /dcc accept <id> or /dcc reject <id> when the bot offers the file"),
            new("Security", "Secure requests never fall back to plaintext")
        ],
        "set" =>
        [
            new("Identity", "nickname, altnick, username, realname"),
            new("Messages", "awaymsg, kickmsg, quitmsg, topicmsg"),
            new("Behavior", "banmask, clonedetect, highlight, joininfo, kickrejoin"),
            new("Network", "usermodes, network.reconnect, kill.reconnect"),
            new("DCC", "dcc.address, dcc.ports, dcc.downloads"),
            new("Hostmasks", "hostmasks, hostmasks.join, hostmasks.part, hostmasks.quit"),
            new("Routing", "who.output, whois.output, whowas.output, ctcp.output, notice.output, invite.output, links.output, list.output, dns.output, messageguard")
        ],
        "protect" =>
        [
            new("Operations", "status, settings, show, channel, personal, monitor, audit, counters, test, reset"),
            new("Scopes", "--global, --network, --channel [name]"),
            new("Channel", "text, repeat, join, nick, mass.kick, mass.deop, caps, controls, ctcp.channel, servop"),
            new("Personal", "privateMessage, privateNotice, ctcp.user, invite"),
            new("Rule fields", "<detector>.enabled, <detector>.count, <detector>.window"),
            new("Exemptions", "exempt.operators, exempt.protected, exempt.protectionExempt"),
            new("Friendly setup", "Use /cprot for channels and /pprot for personal protection.")
        ],
        "cprot" =>
        [
            new("Enable", "/cprot on|off [network] [channel]"),
            new("Tune", "/cprot <detector> <count> <seconds> [network] [channel]"),
            new("Other", "/cprot <detector> off|default [network] [channel]"),
            new("Action", "/cprot action <monitor|kick|kickban> [network] [channel]"),
            new("Ban time", "/cprot bantime <duration|permanent> [network] [channel]"),
            new("Detectors", "text, repeat, join, nick, mass.kick, mass.deop, caps, controls, ctcp.channel, servop"),
            new("Network default", "Use * as the channel: /cprot on EFnet *"),
            new("Example", "/cprot text 10 5 EFnet #clircs")
        ],
        "pprot" =>
        [
            new("Enable", "/pprot on|off [network]"),
            new("Tune", "/pprot <detector> <count> <seconds> [network]"),
            new("Other", "/pprot <detector> off|default [network]"),
            new("Ignore time", "/pprot ignoretime <duration> [network]"),
            new("Detectors", "message, notice, ctcp.user, invite"),
            new("Example", "/pprot message 6 5 EFnet")
        ],
        _ => []
    };

    // DCC operations have substantially different syntax, so each operation
    // receives its own topic help instead of one misleading top-level usage line.
    private static PresentationBlock? DccHelp(string requested)
    {
        var type = requested.TrimStart('/').ToLowerInvariant();
        return type switch
        {
            "chat" => DccTypeHelp(
                "chat",
                "/dcc chat <nick> [--passive]",
                "Start an ordinary plaintext DCC chat",
                "With --passive, the other client listens and clircs connects back"),
            "schat" => DccTypeHelp(
                "schat",
                "/dcc schat <nick> [--passive]",
                "Start a DCC chat protected by TLS",
                "SCHAT never falls back to plaintext; --passive reverses which client listens"),
            "send" => DccTypeHelp(
                "send",
                "/dcc send <nick> <file> [--passive]",
                "Send a file over ordinary plaintext DCC",
                "With --passive, the receiving client listens and clircs connects back"),
            "ssend" => DccTypeHelp(
                "ssend",
                "/dcc ssend <nick> <file> [--passive]",
                "Send a file over DCC protected by TLS",
                "SSEND never falls back to plaintext; --passive reverses which client listens"),
            "list" => DccTypeHelp("list", "/dcc list", "List active DCC requests and connections", null),
            "show" => DccTypeHelp("show", "/dcc show <id>", "Show one DCC request or connection", null),
            "accept" => DccTypeHelp("accept", "/dcc accept <id>", "Accept an incoming DCC request", null),
            "reject" => DccTypeHelp("reject", "/dcc reject <id>", "Reject an incoming DCC request", null),
            "resume" => DccTypeHelp("resume", "/dcc resume <id>", "Resume an incomplete incoming file transfer", null),
            "cancel" => DccTypeHelp("cancel", "/dcc cancel <id>", "Cancel an outgoing request or active DCC connection", null),
            _ => null
        };
    }

    private static PresentationBlock DccTypeHelp(string type, string usage, string description, string? detail)
    {
        var fields = new List<PresentationField>
        {
            new("Usage", usage),
            new("Description", description)
        };
        if (detail is not null) fields.Add(new PresentationField("Details", detail));
        return new PresentationBlock("HELP:", fields, TitleHighlight: $"/dcc {type}");
    }

    internal PresentationBlock? SettingHelp(string requested)
    {
        var setting = CanonicalSettingName(requested);
        var detail = setting switch
        {
            "nickname" => ("<nickname>", "your current default", "Primary nickname for new direct connections.", "/set nickname rekkals"),
            "altnick" => ("<nickname>", "primary nickname plus _", "Fallback nickname used if the primary nickname is unavailable.", "/set altnick rekkals_"),
            "username" => ("<username>", "your Windows user name", "Username sent during IRC registration.", "/set username rekkals"),
            "realname" => ("<name>", "clircs user", "Real name text sent during IRC registration.", "/set realname Example User"),
            "awaymsg" => ("<message>", "away", "Message used by /away when no message is supplied.", "/set awaymsg out to lunch"),
            "kickmsg" => ("<message|random>", "random", "Default kick reason; random selects a quote when no reason is supplied.", "/set kickmsg lewser"),
            "quitmsg" => ("<message|random>", "random", "Default quit reason; random selects a quote when no reason is supplied.", "/set quitmsg random"),
            "topicmsg" => ("<message|random>", "random", "Default topic used by /rt; random selects a quote.", "/set topicmsg random"),
            "banmask" => ("<host|userhost|nick-userhost|wildcard-host>", "host", "Mask style used when a nickname must be converted into a ban.", "/set banmask host"),
            "clonedetect" => ("<on|off>", "on", "Detects channel users sharing one visible host; it never takes protection action.", "/set clonedetect on"),
            "highlight" => ("<on|off>", "on", "Highlights mentions of your nickname and mirrors them into the active window.", "/set highlight on"),
            "joininfo" => ("<on|off>", "off", "Shows matching userlist infolines when someone joins.", "/set joininfo on"),
            "kickrejoin" => ("<on|off>", "off", "Automatically rejoins a channel after you are kicked.", "/set kickrejoin on"),
            "usermodes" => ("<modes|none>", "+i", "User modes applied after registration on the active logical network.", "/set usermodes +iw"),
            "network.reconnect" => ("<on|off>", "on", "Reconnects after an unrequested network or server disconnect.", "/set network.reconnect on"),
            "kill.reconnect" => ("<on|off>", "on", "Reconnects after the server kills your current IRC connection.", "/set kill.reconnect on"),
            "dcc.address" => ("<auto|IP|hostname>", "auto", "Public IPv6 or IPv4 address advertised for outgoing active DCC connections.", "/set dcc.address 2603:8081:3000:48b3::20"),
            "dcc.ports" => ("<random|port|first-last>", "random", "Listening port or range used by outgoing active DCC connections; fixed ports must be forwarded through NAT.", "/set dcc.ports 50000-50009"),
            "dcc.downloads" => ("<folder>", DefaultDccDownloadDirectory(_dataDirectory), "Folder used for received DCC files. Existing files are never overwritten.", "/set dcc.downloads %USERPROFILE%\\Downloads"),
            _ when setting.StartsWith("hostmasks", StringComparison.Ordinal) =>
                ("<userhost|host|off>", "userhost", "Controls hostmask detail on join, part, or quit messages.", $"/set {setting} host"),
            "notice.output" =>
                ("<active|status|window>", "active", "Selects the window used for ordinary incoming notices.", "/set notice.output window"),
            "list.output" =>
                ("<active|status|dedicated>", "dedicated", "Selects the window used for channel-list results.", "/set list.output dedicated"),
            _ when setting.EndsWith(".output", StringComparison.Ordinal) || setting == "messageguard" =>
                ("<active|status|dedicated>", setting == "links.output" ? "status" : "active", "Selects the window used for this family of server output.", $"/set {setting} active"),
            _ => default
        };
        if (detail == default) return null;
        return new PresentationBlock("HELP:",
        [
            new("Usage", $"/set {setting} {detail.Item1}"),
            new("Currently", CurrentSettingValue(setting) ?? detail.Item2),
            new("Description", detail.Item3),
            new("Example", detail.Item4)
        ], TitleHighlight: $"/set {setting}");
    }

    private string? CurrentSettingValue(string setting) => setting switch
    {
        "nickname" => _preferences.Nickname,
        "altnick" => _preferences.AlternateNickname,
        "username" => _preferences.Username,
        "realname" => _preferences.RealName,
        "awaymsg" => _preferences.AwayMessage,
        "kickmsg" => _preferences.DefaultKickMessage ?? "random",
        "quitmsg" => _preferences.DefaultQuitMessage ?? "random",
        "topicmsg" => _preferences.DefaultTopicMessage ?? "random",
        "banmask" => BanmaskFormatter.Name(_preferences.BanmaskStyle),
        "clonedetect" => _preferences.CloneDetection ? "on" : "off",
        "highlight" => _preferences.HighlightNickname ? "on" : "off",
        "joininfo" => _preferences.AnnounceUserInfoOnJoin ? "on" : "off",
        "kickrejoin" => _preferences.AutoRejoinOnKick ? "on" : "off",
        "usermodes" => CurrentUserModes(),
        "network.reconnect" => _preferences.NetworkReconnect ? "on" : "off",
        "kill.reconnect" => _preferences.KillReconnect ? "on" : "off",
        "dcc.address" => _preferences.DccAddress,
        "dcc.ports" => _preferences.DccPorts.ToString(),
        "dcc.downloads" => _preferences.DccDownloads,
        "hostmasks" => _preferences.JoinHostmasks == _preferences.PartHostmasks && _preferences.PartHostmasks == _preferences.QuitHostmasks
            ? FormatHostmaskVisibility(_preferences.JoinHostmasks) : "mixed",
        "hostmasks.join" => FormatHostmaskVisibility(_preferences.JoinHostmasks),
        "hostmasks.part" => FormatHostmaskVisibility(_preferences.PartHostmasks),
        "hostmasks.quit" => FormatHostmaskVisibility(_preferences.QuitHostmasks),
        "messageguard" => FormatOutputDestination(_outputRouting.DestinationFor("messageguard")),
        _ when setting.EndsWith(".output", StringComparison.Ordinal) &&
            _outputRouting.TryGetDestination(setting[..^".output".Length], out var destination) =>
                FormatOutputDestination(destination, setting == "notice.output"),
        _ => null
    };

    private string CurrentUserModes()
    {
        var session = ActiveSession();
        if (session is null) return "+i";
        var profile = ProfileFor(session);
        if (profile is null) return "+i";
        return profile.UserModes.Length == 0 ? "none" : profile.UserModes;
    }

    internal static PresentationBlock? ProtectionHelp(string requested)
    {
        var topic = requested.TrimStart('/');
        var normalized = topic.ToLowerInvariant();
        var separator = normalized.LastIndexOf('.');
        if (normalized is "exempt.operators" or "exempt.protected" or "exempt.protectionexempt")
        {
            return new PresentationBlock("HELP:",
            [
                new("Usage", $"/protect set {topic} <on|off> [scope]"),
                new("Default", "on"),
                new("Description", "Excludes the selected trusted user class from protection detections."),
                new("Scopes", "--global, --network, --channel [name]"),
                new("Example", $"/protect set {topic} on --channel")
            ], TitleHighlight: $"/protect {topic}");
        }
        if (separator > 0 && ParseProtectionDetector(normalized[..separator]) is { } detector &&
            normalized[(separator + 1)..] is "enabled" or "count" or "window")
        {
            var field = normalized[(separator + 1)..];
            var value = field == "enabled" ? "<on|off>" : "<number>";
            var description = field switch
            {
                "enabled" => $"Enables or disables the {DetectorName(detector)} detector.",
                "count" => $"Number of {DetectorName(detector)} events required to trigger detection.",
                _ => $"Rolling time window, in seconds, for the {DetectorName(detector)} detector."
            };
            return new PresentationBlock("HELP:",
            [
                new("Usage", $"/protect set {topic} {value} [scope]"),
                new("Default", "set by the selected preset"),
                new("Description", description),
                new("Scopes", "--global, --network, --channel [name]"),
                new("Example", $"/protect set {topic} {(field == "enabled" ? "on" : field == "count" ? "6" : "10")}")
            ], TitleHighlight: $"/protect {topic}");
        }

        if (ParseProtectionDetector(normalized) is { } selected)
        {
            return new PresentationBlock("HELP:",
            [
                new("Usage", $"/protect show {topic}"),
                new("Description", $"Inspect the {DetectorName(selected)} detector and its enabled, count, and window fields."),
                new("Settings", $"{topic}.enabled, {topic}.count, {topic}.window"),
                new("Example", $"/protect set {topic}.enabled on")
            ], TitleHighlight: $"/protect {topic}");
        }

        var operation = normalized switch
        {
            "status" => ("/protect status", "Show effective protection state for the current context.", "/protect status"),
            "settings" or "show" => ("/protect show [detector]", "Show effective detector settings.", "/protect show text"),
            "channel" => ("/protect channel <on|off> [scope]", "Enable or disable channel-event protection.", "/protect channel on --channel"),
            "personal" => ("/protect personal <on|off> [scope]", "Enable or disable private-message protection.", "/protect personal on --network"),
            "monitor" => ("/protect monitor <on|off> [scope]", "Turns the audit-only safety override on or off.", "/protect monitor on --network"),
            "set" => ("/protect set <setting> <value> [scope]", "Change one detector or exemption setting.", "/protect set text.count 6 --channel"),
            "reset" => ("/protect reset [scope]", "Remove the selected scope override.", "/protect reset --channel"),
            "audit" => ("/protect audit", "Open the active network's protection audit window.", "/protect audit"),
            "counters" => ("/protect counters", "Show currently active protection counters.", "/protect counters"),
            "test" => ("/protect test <detector> <actor> <count> [channel]", "Test a detector without affecting live users.", "/protect test text Alice 6 #clircs"),
            _ => default
        };
        if (operation == default) return null;
        return new PresentationBlock("HELP:",
        [
            new("Usage", operation.Item1),
            new("Description", operation.Item2),
            new("Scopes", "--global, --network, --channel [name]"),
            new("Example", operation.Item3)
        ], TitleHighlight: $"/protect {topic}");
    }

    private ValueTask<CommandResult> HelpAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (input.Arguments.Count > 0)
        {
            var requestedCommand = input.Arguments[0].TrimStart('/');
            if (!_commands.TryResolve(requestedCommand, out var command))
            {
                return ValueTask.FromResult(CommandResult.Failure($"Unknown command: /{requestedCommand}"));
            }

            if (input.Arguments.Count > 1)
            {
                var detail = command.TopicHelp?.Invoke(input.Arguments[1]);
                if (detail is not null) return ValueTask.FromResult(CommandResult.Success(detail));
                if (command.TopicHelp is not null)
                {
                    return ValueTask.FromResult(CommandResult.Failure(
                        $"Unknown /{command.Name} help topic: {input.Arguments[1]}. Use /help {command.Name} for the available topics."));
                }
            }

            var fields = new List<PresentationField>();
            if (command.Usage.Length > 0)
            {
                fields.Add(new PresentationField("Usage", command.Usage));
            }
            if (command.Aliases.Count > 0)
            {
                fields.Add(new PresentationField("Aliases", string.Join(", ", command.Aliases.Select(alias => $"/{alias}"))));
            }
            fields.Add(new PresentationField("Description", command.Description));
            fields.AddRange(command.ExtendedHelp);
            return ValueTask.FromResult(CommandResult.Success(new PresentationBlock(
                "HELP:",
                fields,
                TitleHighlight: $"/{command.Name}")));
        }

        var commands = _commands.Definitions
            .Where(command => command.VisibleInHelp)
            .OrderBy(command => command.Name)
            .Select(command => $"/{command.Name}")
            .ToArray();
        return ValueTask.FromResult(CommandResult.Success(new PresentationBlock(
            "Commands",
            Summary: "Use /help <command> for details.",
            Grid: commands)));
    }

    private ValueTask<CommandResult> AboutAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (input.Arguments.Count != 0)
        {
            return ValueTask.FromResult(CommandResult.Failure("Usage: /about"));
        }
        var session = ActiveSession();
        var buffer = ActiveBuffer();
        if (session is null || buffer is null)
        {
            _presenter.About();
        }
        else
        {
            OnSessionEvent(StartupEvent(session, buffer));
        }
        return ValueTask.FromResult(CommandResult.Success());
    }

    private ValueTask<CommandResult> ConfigAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        if (input.Arguments.Count > 1 ||
            input.Arguments.Count == 1 && !input.Arguments[0].Equals("path", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(CommandResult.Failure("Usage: /config [path]"));
        }
        return ValueTask.FromResult(CommandResult.Success(new PresentationBlock(
            "Configuration",
            [
                new("Data", _dataDirectory),
                new("Settings", Path.Combine(_dataDirectory, "appearance.json")),
                new("Networks", _profileStore.Path),
                new("Protection", Path.Combine(_dataDirectory, "protection.json")),
                new("Users", Path.Combine(_dataDirectory, "users")),
                new("Themes", Path.Combine(_dataDirectory, "themes")),
                new("Scripts", Path.Combine(_dataDirectory, "scripts")),
                new("Backups", _backupManager.BackupDirectory),
                new("Logging", _loggingStore.Path),
                new("Logs", _logWriter.RootDirectory),
                new("DCC downloads", _preferences.DccDownloads)
            ],
            Summary: "Changes are saved immediately. Close clircs before manually editing these files.")));
    }

    private async ValueTask<CommandResult> BackupAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        if (input.Arguments.Count > 1)
        {
            return CommandResult.Failure("Usage: /backup [create|list|path]");
        }
        var operation = input.Arguments.Count == 0 ? "create" : input.Arguments[0].ToLowerInvariant();
        try
        {
            switch (operation)
            {
                case "create":
                    var path = await Task.Run(_backupManager.Create, cancellationToken);
                    return CommandResult.Success($"Backup created: {path}");
                case "path":
                    return CommandResult.Success($"Backup directory: {_backupManager.BackupDirectory}");
                case "list":
                    var backups = _backupManager.List();
                    if (backups.Count == 0) return CommandResult.Success("No clircs backups found.");
                    return CommandResult.Success(new PresentationBlock(
                        "Backups",
                        Table: new PresentationTable(
                            ["File", "Created", "Size"],
                            backups.Select(file => (IReadOnlyList<string>)new[]
                            {
                                file.Name,
                                file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                                FormatFileSize(file.Length)
                            }).ToArray(),
                            new HashSet<int> { 0 }),
                        Summary: _backupManager.BackupDirectory));
                default:
                    return CommandResult.Failure("Usage: /backup [create|list|path]");
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return CommandResult.Failure($"Backup failed: {exception.Message}");
        }
    }

    internal static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KB";
        return $"{bytes / (1024d * 1024d):0.0} MB";
    }

    private ValueTask<CommandResult> LoggingAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        if (input.Arguments.Count == 0)
        {
            return ValueTask.FromResult(LoggingStatus());
        }

        var operation = input.Arguments[0].ToLowerInvariant();
        if (operation == "path" && input.Arguments.Count == 1)
        {
            return ValueTask.FromResult(CommandResult.Success($"Log directory: {_logWriter.RootDirectory}"));
        }
        if (operation == "list" && input.Arguments.Count == 1)
        {
            var rules = _loggingStore.Entries;
            if (rules.Count == 0) return ValueTask.FromResult(CommandResult.Success("No logging rules configured."));
            var rows = new List<IReadOnlyList<string>>();
            foreach (var rule in rules)
            {
                rows.Add([rule.NetworkName, rule.Enabled ? "on" : "off", "(network)", ""]);
                rows.AddRange(rule.Targets
                    .OrderBy(target => target.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(target => (IReadOnlyList<string>)new[]
                    {
                        rule.NetworkName, rule.Enabled ? "on" : "off", target.Key, target.Value ? "on" : "off"
                    }));
            }
            return ValueTask.FromResult(CommandResult.Success(new PresentationBlock(
                "Logging Rules",
                Table: new PresentationTable(["Network", "Network state", "Target", "Target state"], rows))));
        }
        if (operation == "status" && input.Arguments.Count == 1)
        {
            return ValueTask.FromResult(LoggingStatus());
        }
        if (operation is not ("on" or "off") || input.Arguments.Count > 3)
        {
            return ValueTask.FromResult(CommandResult.Failure(
                "Usage: /logging <on|off|status|list|path> [network] [target]"));
        }

        var enabled = operation == "on";
        NetworkProfile? profile;
        string? target = null;
        if (input.Arguments.Count == 1)
        {
            var activeSession = ActiveSession();
            var activeBuffer = ActiveBuffer();
            if (activeSession is null || activeBuffer is null)
            {
                return ValueTask.FromResult(CommandResult.Failure("There is no active IRC window."));
            }
            if (!CanLog(activeBuffer))
            {
                return ValueTask.FromResult(CommandResult.Failure(
                    "Logging applies to status, channel, query, DCC CHAT, and debug windows."));
            }
            profile = EnsureProfileFor(activeSession, out _);
            target = LoggingTarget(activeBuffer);
        }
        // With one operand, prefer a matching saved network; otherwise treat the
        // operand as a target on the active network.
        else if (input.Arguments.Count == 2)
        {
            profile = _profileStore.Find(input.Arguments[1]);
            if (profile is null)
            {
                var activeSession = ActiveSession();
                if (activeSession is null)
                {
                    return ValueTask.FromResult(CommandResult.Failure(
                        $"No saved network profile named '{input.Arguments[1]}' exists."));
                }
                profile = EnsureProfileFor(activeSession, out _);
                target = LoggingSettingsStore.NormalizeTarget(input.Arguments[1]);
            }
        }
        else
        {
            profile = _profileStore.Find(input.Arguments[1]);
            if (profile is null)
            {
                return ValueTask.FromResult(CommandResult.Failure(
                    $"No saved network profile named '{input.Arguments[1]}' exists."));
            }
            target = LoggingSettingsStore.NormalizeTarget(input.Arguments[2]);
        }

        try
        {
            if (target is null)
            {
                _loggingStore.SetNetwork(profile.Id, profile.DisplayName, enabled);
                return ValueTask.FromResult(CommandResult.Success(
                    $"Logging is now {(enabled ? "on" : "off")} for network {profile.DisplayName}."));
            }
            _loggingStore.SetTarget(profile.Id, profile.DisplayName, target, enabled);
            return ValueTask.FromResult(CommandResult.Success(
                $"Logging is now {(enabled ? "on" : "off")} for {profile.DisplayName}/{target}."));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ValueTask.FromResult(CommandResult.Failure($"Logging settings were not changed: {exception.Message}"));
        }
    }

    private CommandResult LoggingStatus()
    {
        var session = ActiveSession();
        var buffer = ActiveBuffer();
        if (session is null || buffer is null || !CanLog(buffer))
        {
            return CommandResult.Failure("There is no active loggable window.");
        }
        var profile = ProfileFor(session);
        var target = LoggingTarget(buffer);
        var networkDefault = profile is not null && _loggingStore.NetworkDefault(profile.Id);
        var targetOverride = profile is null ? null : _loggingStore.TargetOverride(profile.Id, target);
        return CommandResult.Success(LoggingStatusPresentation(
            profile?.DisplayName ?? session.State.DisplayName,
            buffer,
            networkDefault,
            targetOverride));
    }

    internal static PresentationBlock LoggingStatusPresentation(
        string network,
        BufferState buffer,
        bool networkEnabled,
        bool? windowOverride)
    {
        var windowLabel = buffer.Kind switch
        {
            BufferKind.Channel => "Channel",
            BufferKind.Query => "Query",
            BufferKind.Status => "Status",
            BufferKind.Diagnostics => "Debug",
            BufferKind.DccChat => "DCC",
            BufferKind.DccTransfer => "DCC",
            _ => "Window"
        };
        var effective = windowOverride ?? networkEnabled;
        var overrideText = windowOverride is not null && windowOverride.Value != networkEnabled
            ? $" ({windowLabel.ToLowerInvariant()} override)"
            : string.Empty;
        return new PresentationBlock(
            $"Logging: {network}/{LoggingTarget(buffer)}",
            [
                new("Network", networkEnabled ? "on" : "off"),
                new(windowLabel, (effective ? "on" : "off") + overrideText)
            ]);
    }

    private static bool CanLog(BufferState buffer) =>
        buffer.Kind is BufferKind.Status or BufferKind.Channel or BufferKind.Query or BufferKind.DccChat or BufferKind.Diagnostics;

    private static string LoggingTarget(BufferState buffer) =>
        buffer.Kind == BufferKind.Status ? "status" : buffer.Name;

}
