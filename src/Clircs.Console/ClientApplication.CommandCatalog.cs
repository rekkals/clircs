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
        Register("help", ["?"], "Show the command list or help for a command.", HelpAsync);
        Register("about", [], "Show clircs startup information.", AboutAsync);
        Register("config", [], "Show where clircs stores configuration and user data.", ConfigAsync);
        Register("backup", [], "Create or restore backups.", BackupAsync);
        Register("logging", ["log"], "Manage channel and query logging.", LoggingAsync);
        Register("server", ["connect"], "Connect to an IRC server or saved network profile.", ServerAsync);
        Register("network", ["net"], "Show network status, list connections, or manage saved network profiles.", NetworkAsync);
        Register("disconnect", [], "Disconnect, but leave clircs running.", DisconnectAsync);
        Register("reconnect", [], "Reconnect to the server.", ReconnectAsync);
        Register("quit", ["exit"], "Disconnect and exit clircs.", QuitAsync);
        Register("nick", [], "Change your nickname, or set it before connecting.", NickAsync);
        Register("away", [], "Mark yourself as away.", AwayAsync);
        Register("back", [], "Mark yourself as back.", BackAsync);
        Register("messages", ["inbox"], "Manage messages logged while away.", AwayMessagesAsync);
        Register("window", ["win"], "List windows or switch to another window.", BufferAsync);
        Register("next", ["wn"], "Switch to the next window.", NextBufferAsync);
        Register("previous", ["wp"], "Switch to the previous window.", PreviousBufferAsync);
        Register("close", [], "Close the active window.", CloseBufferAsync);
        Register("clear", [], "Clear the visible window or activity.", ClearAsync);
        Register("autojoin", ["ajoin"], "Manage autojoin channels for the active network profile.", AutojoinAsync);
        Register("aj", [], "Add a channel to the active network profile's autojoin list.", AutojoinAddAliasAsync);
        Register("rj", [], "Remove a channel from the active network profile's autojoin list.", AutojoinRemoveAliasAsync);
        Register("msg", ["m"], "Send a message to a nickname or channel.", MessageAsync);
        Register("notice", ["n"], "Send a notice to a nickname or channel.", NoticeAsync);
        Register("wallops", [], "Send a WALLOPS message.", WallopsAsync);
        Register("say", [], "Send text to the active channel, query, or DCC chat.", SayCommandAsync);
        Register("stop", [], "Stop queued chat in the current channel or query.", StopAsync);
        Register("me", [], "Send an action to the active channel, query, or DCC chat.", MeAsync);
        Register("describe", [], "Send an action to a nickname or channel.", DescribeAsync);
        Register("ame", [], "Send an action to every joined channel on this network.", AllChannelActionAsync);
        Register("amsg", [], "Send a message to every joined channel on this network.", AllChannelMessageAsync);
        Register("query", ["q"], "Open a private query window.", QueryAsync);
        Register("ctcp", [], "Send a CTCP request to a nickname.", CtcpAsync);
        Register("dcc", [], "Start and manage DCC chats, file transfers, and requests.", DccAsync);
        Register("xdcc", [], "List and request file packs from XDCC bots.", XdccAsync);
        Register("sv", [], "Send the clircs version to the active channel or query.", ShowVersionAsync);
        Register("time", ["date"], "Show the local date and time.", TimeAsync);
        Register("uptime", [], "Show how long clircs has been running.", UptimeAsync);
        Register("raw", ["quote"], "Send an IRC command directly to the server.", RawAsync, argumentMode: CommandArgumentMode.Text);
        Register("oper", [], "Authenticate as an IRC operator.", OperAsync);
        Register("join", ["j"], "Join one or more channels (separate multiple channels and keys with commas).", JoinAsync);
        Register("part", ["p"], "Part a channel.", PartAsync);
        Register("cycle", [], "Part and rejoin a channel.", CycleAsync);
        Register("invite", ["i"], "Invite a nickname to a channel.", InviteAsync);
        Register("topic", ["t"], "Show or set a channel topic.", TopicAsync);
        Register("rt", [], "Set the topic to a random quote.", RandomTopicAsync);
        Register("mode", [], "Show or change modes.", ModeAsync);
        Register("op", [], "Grant channel operator status.", OpAsync);
        Register("deop", ["dop"], "Remove channel operator status.", DeopAsync);
        Register("voice", ["v"], "Grant channel voice status.", VoiceAsync);
        Register("devoice", ["dv"], "Remove channel voice status.", DevoiceAsync);
        Register("kick", ["k"], "Kick a user from a channel.", KickAsync);
        Register("ban", [], "Set a ban in the active channel.", BanAsync);
        Register("kickban", ["kb", "bk"], "Kick and ban a user from the active channel.", KickBanAsync);
        Register("tban", [], "Set a temporary ban in the active channel.", TimedBanAsync);
        Register("mop", [], "Op all non-operators in the active channel.", MassOpAsync);
        Register("mdop", [], "Deop every operator except yourself in the active channel.", MassDeopAsync);
        Register("mv", [], "Voice every regular member in the active channel.", MassVoiceAsync);
        Register("mdv", [], "Devoice every voiced member except yourself in the active channel.", MassDevoiceAsync);
        Register("mmode", [], "Apply the given mode to multiple users.", MultiModeAsync);
        Register("banlist", [], "Request the current ban list for a joined channel.", BanListAsync);
        Register("exceptlist", [], "Display channel ban exceptions.", ExceptListAsync);
        Register("invitelist", [], "Display channel invite exceptions.", InviteListAsync);
        Register("quietlist", [], "Display channel quiet masks.", QuietListAsync);
        Register("unban", [], "Remove a channel ban mask.", UnbanAsync);
        Register("clearbans", [], "Remove all bans from the active channel.", ClearBansAsync);
        Register("appendtopic", ["at"], "Append text to the active channel topic.", AppendTopicAsync);
        Register("cleartopic", ["ct"], "Clear the active channel topic.", ClearTopicAsync);
        Register("adduser", [], "Add a user to the active network's user directory.", AddUserAsync);
        Register("addbot", [], "Add a bot to the active network's user directory.", AddBotAsync);
        Register("remuser", [], "Remove a user from the active network's user directory.", RemoveUserAsync);
        Register("addhost", [], "Add a hostmask to a user.", AddHostAsync);
        Register("remhost", [], "Remove a hostmask from a user.", RemoveHostAsync);
        Register("chattr", [], "Change userlist flags.", ChangeAttributesAsync);
        Register("addchan", [], "Add a channel to a user record.", AddUserChannelAsync);
        Register("remchan", [], "Remove a channel from a user record.", RemoveUserChannelAsync);
        Register("chinfo", [], "Set a global or channel-specific JOIN infoline for a user.", ChangeUserInfoAsync);
        Register("uwhois", [], "Show a user record or match a nickname.", UserWhoisAsync);
        Register("users", [], "List saved user records for the active network profile.", UsersAsync);
        Register("usersum", [], "Summarize userlist flags for the active network.", UserSummaryAsync);
        Register("ignore", [], "List or add entries ignored on the active network.", IgnoreAsync);
        Register("unignore", [], "Remove an exact ignore entry from the active network.", UnignoreAsync);
        Register("notify", [], "Manage the active network's notify list.", NotifyAsync);
        Register("accept", [], "List or add entries on the server-side accept list.", AcceptAsync);
        Register("unaccept", [], "Remove entries from the server-side accept list.", AcceptAsync);
        Register("cprot", [], "Configure channel protection.", ChannelProtectionAsync);
        Register("pprot", [], "Configure personal protection.", PersonalProtectionAsync);
        Register("clones", [], "Find channel users sharing the same host.", ClonesAsync);
        Register("ufind", [], "Show which channel members match saved user records.", UserFindAsync);
        Register("addban", [], "Add a network policy ban.", AddPolicyBanAsync);
        Register("remban", [], "Remove a saved policy ban.", RemovePolicyBanAsync);
        Register("bans", [], "List saved network policy bans.", PolicyBansAsync);
        Register("umop", [], "Op eligible users in the active channel.", UserMassOpAsync);
        Register("umdop", [], "Deop non-eligible users in the active channel.", UserMassDeopAsync);
        Register("umv", [], "Voice eligible users in the active channel.", UserMassVoiceAsync);
        Register("umdv", [], "Devoice non-eligible users in the active channel.", UserMassDevoiceAsync);
        Register("filterkick", ["fk"], "Kick active-channel members matching a hostmask, except friends.", FilterKickAsync);
        Register("filterkickban", ["fkb"], "Ban a hostmask and kick matching non-friends from the active channel.", FilterKickBanAsync);
        Register("findnickkick", ["fnk"], "Kick active-channel members whose nicknames match a wildcard, except friends.", FindNickKickAsync);
        Register("kicknonops", ["knop"], "Kick non-operators from the active channel, except friends.", KickNonOperatorsAsync);
        Register("cop", [], "Op a nickname in eligible common channels.", CommonOpAsync);
        Register("cban", [], "Ban and deop a nickname in eligible common channels.", CommonBanAsync);
        Register("ckick", [], "Kick a nickname from eligible common channels.", CommonKickAsync);
        Register("ckb", [], "Ban and kick a nickname in eligible common channels.", CommonKickBanAsync);
        Register("massinvite", ["mi"], "Invite members of the active channel to another joined channel.", MassInviteAsync);
        Register("inviteall", ["ia"], "Invite a nickname to every eligible channel you've joined.", InviteAllAsync);
        Register("wall", ["onotice"], "Notice operators in the active channel.", OperatorWallAsync);
        Register("wallmsg", ["wmsg"], "Message operators in the active channel.", OperatorWallMessageAsync);
        Register("voicenotice", ["vnotice"], "Notice voiced users and operators in the active channel.", VoiceNoticeAsync);
        Register("voicemsg", ["vmsg"], "Message voiced users and operators in the active channel.", VoiceMessageAsync);
        Register("nonopnotice", ["nnotice"], "Notice non-operators in the active channel.", NonOperatorNoticeAsync);
        Register("nonopmsg", ["nmsg"], "Message non-operators in the active channel.", NonOperatorMessageAsync);
        Register("userwall", ["uwall"], "Notice active-channel operators who are not marked as bots.", UserWallAsync);
        Register("names", [], "Request a channel's nickname list.", NamesAsync);
        Register("who", [], "Request WHO information for a channel, nickname, or mask.", WhoAsync);
        Register("ircops", [], "List IRC operators in a channel.", IrcOpsAsync);
        Register("whois", ["wi", "w"], "Request WHOIS information for a nickname.", WhoisAsync);
        Register("iwhois", ["wii"], "Request WHOIS information, including idle and sign-on times.", IdleWhoisAsync);
        Register("whowas", ["ww"], "Request nickname history.", WhowasAsync);
        Register("motd", [], "Request a server's message of the day.", MotdAsync);
        Register("links", [], "Request server links.", LinksAsync);
        Register("stats", [], "Request server statistics.", StatsAsync);
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
        Register("theme", [], "Show, list, load, or reload themes.", ThemeAsync);
        Register("tls", [], "Show connection TLS status or manage saved certificate pins.", TlsAsync);
        Register("script", [], "Load and manage scripts.", ScriptAsync);
        Register("debug", [], "Open a raw IRC traffic window for the active network.", DebugAsync);
    }

    private void Register(
        string name,
        string[] aliases,
        string summary,
        CommandHandler handler,
        CommandArgumentMode argumentMode = CommandArgumentMode.Tokenized)
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
            "messages" => MessagesHelp,
            "network" => NetworkHelp,
            "autojoin" => AutojoinHelp,
            "backup" => BackupHelp,
            "logging" => LoggingHelp,
            "tls" => TlsHelp,
            "xdcc" => XdccHelp,
            "notify" => NotifyHelp,
            "script" => ScriptHelp,
            "cprot" => requested => FriendlyProtectionHelp(requested, personal: false),
            "pprot" => requested => FriendlyProtectionHelp(requested, personal: true),
            _ => null
        };
        _commands.Register(new CommandDefinition(
            name,
            aliases,
            usage,
            description,
            handler,
            visibleInHelp: true,
            ExtendedHelpFields(name),
            topicHelp,
            argumentMode));
    }

    private static IReadOnlyList<PresentationField> ExtendedHelpFields(string command) => command.ToLowerInvariant() switch
    {
        "autojoin" =>
        [
            new("View list", "/autojoin list"),
            new("Add channel", "/autojoin add [channel]"),
            new("Remove channel", "/autojoin remove [channel]"),
            new("Join channels", "/autojoin run"),
            new("Clear list", "/autojoin clear")
        ],
        "backup" =>
        [
            new("Create backup", "/backup create"),
            new("View list", "/backup list"),
            new("Backup folder", "/backup path"),
            new("Restore backup", "/backup restore <file>")
        ],
        "tls" =>
        [
            new("TLS status", "/tls"),
            new("View certificates", "/tls list"),
            new("Forget certificate", "/tls forget <host> <port>")
        ],
        "logging" =>
        [
            new("Status", "/logging [status]"),
            new("Enable logging", "/logging on [profile|target] [target]"),
            new("Disable logging", "/logging off [profile|target] [target]"),
            new("View rules", "/logging list"),
            new("Log folder", "/logging path")
        ],
        "server" =>
        [
            new("Direct", "/server <host> [port] [--tls] [--new] [--password]"),
            new("Profile", "/server <profile> [number] [--new] [--password]"),
            new("--tls", "Use TLS for a direct server connection."),
            new("--new", "Open a new connection alongside the current one."),
            new("--password", "Prompt for a server or bouncer password.")
        ],
        "reconnect" =>
        [
            new("cancel", "Cancel a pending automatic reconnect."),
            new("tolerate", "Allow improperly tagged CAP messages during this reconnect.")
        ],
        "messages" =>
        [
            new("View list", "/messages list [nick]"),
            new("Read messages", "/messages read <nick> <number>"),
            new("Delete messages", "/messages delete <nick> <number|all>"),
            new("Clear inbox", "/messages clear"),
            new("Inbox window", "/messages window")
        ],
        "network" =>
        [
            new("Connections", "/network list"),
            new("Profiles", "/network profiles"),
            new("Status", "/network status [name|number]"),
            new("Create profile", "/network add <name> <host> [port] [--tls]"),
            new("Add server", "/network server add <profile> <host> [port] [--tls]"),
            new("Remove server", "/network server remove <profile> <number>"),
            new("Username", "/network username [profile]"),
            new("Set username", "/network username <profile> <username>"),
            new("Use global username", "/network username <profile> global"),
            new("User modes", "/network usermodes [profile]"),
            new("Set user modes", "/network usermodes <profile> <modes>"),
            new("Use global user modes", "/network usermodes <profile> global"),
            new("Getnick", "/network getnick <profile> [nick|off]"),
            new("Remove profile", "/network remove <profile>"),
            new("Password status", "/network password <profile>"),
            new("Save password", "/network password <profile> set"),
            new("Clear password", "/network password <profile> clear"),
            new("SASL status", "/network sasl <profile>"),
            new("Enable PLAIN", "/network sasl <profile> [plain] <account> [required|optional]"),
            new("Enable EXTERNAL", "/network sasl <profile> external <certificate.pfx> [required|optional]"),
            new("Disable SASL", "/network sasl <profile> off")
        ],
        "chattr" =>
        [
            new("Flags", "Use + to add and - to remove. Separate changes with commas."),
            new("b", "Bot: excluded from /userwall"),
            new("o", "Op: eligible for /umop"),
            new("a", "Auto-op: requires the o flag"),
            new("v", "Voice: eligible for /umv and auto-voice"),
            new("f", "Friend: exempt from automatic deop, kick on join, and from " +
                "protection rules configured to exempt friends"),
            new("d", "Deop: automatically remove channel operator status"),
            new("k", "Kick: kick on join"),
            new("Example", "/chattr alice +oa,-v #clircs")
        ],
        "ignore" =>
        [
            new("Wildcards", "Hostmask entries support * and ?.")
        ],
        "unignore" =>
        [
            new("Matching", "Match the stored entry exactly, including wildcard characters.")
        ],
        "dcc" =>
        [
            new("Chat", "/dcc chat <nick> [--passive]"),
            new("Secure chat", "/dcc schat <nick> [--passive]"),
            new("Send", "/dcc send <nick> <file> [--passive]"),
            new("Secure send", "/dcc ssend <nick> <file> [--passive]"),
            new("View list", "/dcc list"),
            new("Show request", "/dcc show <id>"),
            new("Accept request", "/dcc accept <id>"),
            new("Reject request", "/dcc reject <id>"),
            new("Resume", "/dcc resume <id>"),
            new("Cancel", "/dcc cancel <id>")
        ],
        "xdcc" =>
        [
            new("List", "/xdcc list <bot>"),
            new("Download", "/xdcc get <bot> <pack>"),
            new("Secure download", "/xdcc sget <bot> <pack>"),
            new("Auto-accept", "/set xdcc.auto on"),
            new("Pending", "/xdcc pending"),
            new("Cancel waiting", "/xdcc cancel <bot>")
        ],
        "notify" =>
        [
            new("View list", "/notify list"),
            new("Add nicknames", "/notify add <nick...>"),
            new("Remove nicknames", "/notify remove <nick...>")
        ],
        "script" =>
        [
            new("View list", "/script list"),
            new("Load script", "/script load <id>"),
            new("Unload script", "/script unload <id>"),
            new("Reload script", "/script reload <id>"),
            new("View errors", "/script errors"),
            new("Permissions", "/script permissions [id]"),
            new("Set permission", "/script permissions <id> <permission> <on|off>")
        ],
        "set" =>
        [
            new("Identity", "nickname, altnick, username, realname"),
            new("Messages", "awaymsg, kickmsg, quitmsg"),
            new("Behavior", "banmask, showhosts, clonedetect, datechange, highlight, awaylog, joininfo, kickrejoin, startup"),
            new("On join", "onjoin.users, onjoin.topic, onjoin.created, onjoin.modes, onjoin.sync"),
            new("Status bar", "status.lagmeter, status.tls, status.bouncer, status.chanmodes, status.usermodes, status.totals"),
            new("Network", "usermodes, reconnect.network, reconnect.kill, reconnect.limit, floodcontrol"),
            new("DCC", "dcc.address, dcc.ports, dcc.downloads, xdcc.auto"),
            new("Routing", "who.output, whois.output, whowas.output, names.output, ctcp.output, notice.output, snotice.output, notify.output, wallops.output, invite.output, links.output, list.output, dns.output, callerid.output")
        ],
        "cprot" =>
        [
            new("Status", "/cprot status [scope]"),
            new("Enable", "/cprot on [scope]"),
            new("Disable", "/cprot off [scope]"),
            new("Set rule", "/cprot <rule> <events> <within> [scope]"),
            new("Disable/reset", "/cprot <rule> <off|default> [scope]"),
            new("Action", "/cprot action <monitor|kick|kickban> [scope]"),
            new("Ban time", "/cprot bantime <duration|permanent> [scope]"),
            new("Exemptions", "/cprot exempt <chanops|friends> <on|off> [scope]"),
            new("Reset", "/cprot reset [scope]"),
            new("Audit window", "/cprot audit"),
            new("Counters", "/cprot counters"),
            new("Rules", "text, repeat, join, nick, kick, deop, caps, controls, ctcp"),
            new("Scopes", "<channel>, <network> <channel>, <network> *, --global")
        ],
        "pprot" =>
        [
            new("Status", "/pprot status [scope]"),
            new("Enable", "/pprot on [scope]"),
            new("Disable", "/pprot off [scope]"),
            new("Set rule", "/pprot <rule> <events> <within> [scope]"),
            new("Disable/reset", "/pprot <rule> <off|default> [scope]"),
            new("Action", "/pprot action <monitor|ignore> [scope]"),
            new("Ignore time", "/pprot ignoretime <duration> [scope]"),
            new("Exemption", "/pprot exempt friends <on|off> [scope]"),
            new("Reset", "/pprot reset [scope]"),
            new("Audit window", "/pprot audit"),
            new("Counters", "/pprot counters"),
            new("Rules", "message, notice, ctcp, invite"),
            new("Scopes", "<network>, --global")
        ],
        "debug" =>
        [
            new("Warning", "\u000304Using /debug can expose passwords and other secrets.\u000f")
        ],
        _ => []
    };

    private static PresentationBlock? ScriptHelp(string requested)
    {
        var topic = requested.TrimStart('/').ToLowerInvariant();
        var detail = topic switch
        {
            "list" => (
                "/script list",
                "List installed scripts"),
            "load" => (
                "/script load <id>",
                "Load a script"),
            "unload" => (
                "/script unload <id>",
                "Unload a script"),
            "reload" => (
                "/script reload <id>",
                "Reload a script"),
            "errors" => (
                "/script errors",
                "Show recent script errors"),
            "permissions" => (
                "/script permissions [id]",
                "Show or change script permissions"),
            _ => default
        };
        if (detail == default) return null;

        var fields = new List<PresentationField>
        {
            new("Usage", detail.Item1),
            new("Description", detail.Item2)
        };
        if (topic == "permissions")
        {
            fields.Add(new(
                "Set permission",
                "/script permissions <id> <permission> <on|off>"));
        }
        if (topic is "load" or "unload" or "reload" or "permissions")
        {
            fields.Add(new("ID", "Script ID from /script list"));
        }

        return new PresentationBlock(
            "HELP:", fields, TitleHighlight: $"/script {topic}");
    }

    private static PresentationBlock? NotifyHelp(string requested)
    {
        var topic = requested.TrimStart('/').ToLowerInvariant();
        var detail = topic switch
        {
            "list" => (
                "/notify list",
                "Show notify nicknames and their online status"),
            "add" => (
                "/notify add <nick...>",
                "Add nicknames to the notify list"),
            "remove" => (
                "/notify remove <nick...>",
                "Remove nicknames from the notify list"),
            _ => default
        };
        if (detail == default) return null;

        return new PresentationBlock(
            "HELP:",
            [
                new("Usage", detail.Item1),
                new("Description", detail.Item2)
            ],
            TitleHighlight: $"/notify {topic}");
    }

    private static PresentationBlock? XdccHelp(string requested)
    {
        var topic = requested.TrimStart('/').ToLowerInvariant();
        var detail = topic switch
        {
            "list" => (
                "/xdcc list <bot>",
                "Request a bot's pack list"),
            "get" => (
                "/xdcc get <bot> <pack>",
                "Request a file pack"),
            "sget" => (
                "/xdcc sget <bot> <pack>",
                "Request a file pack using secure DCC"),
            "pending" => (
                "/xdcc pending",
                "List pending XDCC auto-accept requests"),
            "cancel" => (
                "/xdcc cancel <bot>",
                "Request queue removal and clear pending auto-accept requests"),
            _ => default
        };
        if (detail == default) return null;

        return new PresentationBlock(
            "HELP:",
            [
                new("Usage", detail.Item1),
                new("Description", detail.Item2)
            ],
            TitleHighlight: $"/xdcc {topic}");
    }

    private static PresentationBlock? TlsHelp(string requested)
    {
        var topic = requested.TrimStart('/').ToLowerInvariant();
        var detail = topic switch
        {
            "list" => (
                "/tls list",
                "List saved certificate pins"),
            "forget" => (
                "/tls forget <host> <port>",
                "Forget a saved certificate pin"),
            _ => default
        };
        if (detail == default) return null;

        return new PresentationBlock(
            "HELP:",
            [
                new("Usage", detail.Item1),
                new("Description", detail.Item2)
            ],
            TitleHighlight: $"/tls {topic}");
    }

    private static PresentationBlock? LoggingHelp(string requested)
    {
        var topic = requested.TrimStart('/').ToLowerInvariant();
        var detail = topic switch
        {
            "on" => (
                "/logging on [target]",
                "Enable logging"),
            "off" => (
                "/logging off [target]",
                "Disable logging"),
            "status" => (
                "/logging status",
                "Show logging settings for the current window"),
            "list" => (
                "/logging list",
                "List saved logging rules"),
            "path" => (
                "/logging path",
                "Show the log folder"),
            _ => default
        };
        if (detail == default) return null;

        var fields = new List<PresentationField>
        {
            new("Usage", detail.Item1),
            new("Description", detail.Item2)
        };
        if (topic is "on" or "off")
        {
            fields.Add(new(
                "Profile",
                $"/logging {topic} <profile> [target]"));
            fields.Add(new(
                "Target",
                "Window name (use status for the Status window)"));
        }

        return new PresentationBlock(
            "HELP:", fields, TitleHighlight: $"/logging {topic}");
    }

    private static PresentationBlock? BackupHelp(string requested)
    {
        var topic = requested.TrimStart('/').ToLowerInvariant();
        var detail = topic switch
        {
            "create" => (
                "/backup create",
                "Create a backup"),
            "list" => (
                "/backup list",
                "List available backups"),
            "path" => (
                "/backup path",
                "Show the backup folder"),
            "restore" => (
                "/backup restore <file>",
                "Restore a backup"),
            _ => default
        };
        if (detail == default) return null;

        var fields = new List<PresentationField>
        {
            new("Usage", detail.Item1),
            new("Description", detail.Item2)
        };
        if (topic == "restore")
        {
            fields.Add(new(
                "File",
                "Use a filename from /backup list or a quoted full path"));
        }

        return new PresentationBlock(
            "HELP:", fields, TitleHighlight: $"/backup {topic}");
    }

    private static PresentationBlock? AutojoinHelp(string requested)
    {
        var topic = requested.TrimStart('/').ToLowerInvariant();
        var detail = topic switch
        {
            "list" => (
                "/autojoin list",
                "List autojoin channels"),
            "add" => (
                "/autojoin add [channel]",
                "Add a channel to autojoin and join it if connected"),
            "remove" => (
                "/autojoin remove [channel]",
                "Remove a channel from autojoin"),
            "run" => (
                "/autojoin run",
                "Join the channels in the autojoin list"),
            "clear" => (
                "/autojoin clear",
                "Clear the autojoin list"),
            _ => default
        };
        if (detail == default) return null;

        return new PresentationBlock(
            "HELP:",
            [
                new("Usage", detail.Item1),
                new("Description", detail.Item2)
            ],
            TitleHighlight: $"/autojoin {topic}");
    }

    private static PresentationBlock? MessagesHelp(string requested)
    {
        var topic = requested.TrimStart('/').ToLowerInvariant();
        var detail = topic switch
        {
            "list" => (
                "/messages list [nick]",
                "List senders or preview a sender's messages"),
            "read" => (
                "/messages read <nick> <number>",
                "Read one saved message"),
            "delete" => (
                "/messages delete <nick> <number|all>",
                "Delete one message or all from a sender"),
            "clear" => (
                "/messages clear",
                "Clear all messages from the inbox"),
            "window" => (
                "/messages window",
                "Open a dedicated window for managing messages"),
            _ => default
        };
        if (detail == default) return null;

        var fields = new List<PresentationField>
        {
            new("Usage", detail.Item1),
            new("Description", detail.Item2)
        };
        if (topic is "read" or "delete")
        {
            fields.Add(new("Number", "Message number from /messages list <nick>"));
        }

        return new PresentationBlock(
            "HELP:", fields, TitleHighlight: $"/messages {topic}");
    }

    private static PresentationBlock? NetworkHelp(string requested)
    {
        var topic = requested.TrimStart('/').ToLowerInvariant();
        var detail = topic switch
        {
            "list" => (
                "/network list",
                "List network connections"),
            "profiles" => (
                "/network profiles",
                "List saved network profiles"),
            "status" => (
                "/network status [name|number]",
                "Show network or profile status"),
            "add" => (
                "/network add <name> <host> [port] [--tls]",
                "Create a network profile"),
            "remove" => (
                "/network remove <profile>",
                "Remove a saved network profile"),
            "server" => (
                "/network server <add|remove> [arguments]",
                "Add or remove servers from a profile"),
            "username" => (
                "/network username [profile [username]]",
                "Show or change a profile's username"),
            "usermodes" => (
                "/network usermodes [profile [modes]]",
                "Show or change a profile's user modes"),
            "getnick" => (
                "/network getnick <profile> [nick]",
                "Show or change automatic nickname recovery"),
            "password" => (
                "/network password <profile> [set|clear]",
                "Show, save, or clear a server or bouncer password"),
            "sasl" => (
                "/network sasl <profile> [arguments]",
                "Show or configure SASL authentication"),
            _ => default
        };
        if (detail == default) return null;

        var fields = new List<PresentationField>
        {
            new("Usage", detail.Item1),
            new("Description", detail.Item2)
        };

        switch (topic)
        {
            case "status":
                fields.Add(new("Name", "Connection, network, or saved profile name"));
                fields.Add(new("Number", "Connection number from /network list"));
                break;

            case "add":
                fields.Add(new("--tls", "Use TLS for this server"));
                break;

            case "server":
                fields.Add(new("Add server",
                    "/network server add <profile> <host> [port] [--tls]"));
                fields.Add(new("Remove server",
                    "/network server remove <profile> <number>"));
                fields.Add(new("Number", "Server number from /network profiles"));
                fields.Add(new("--tls", "Use TLS for the added server"));
                break;

            case "username":
                fields.Add(new("Use global", "/network username <profile> global"));
                break;

            case "usermodes":
                fields.Add(new("Use global", "/network usermodes <profile> global"));
                break;

            case "getnick":
                fields.Add(new("Disable", "/network getnick <profile> off"));
                break;

            case "password":
                fields.Add(new("set", "Prompt for the password"));
                break;

            case "sasl":
                fields.Add(new("Status", "/network sasl <profile>"));
                fields.Add(new("PLAIN",
                    "/network sasl <profile> [plain] <account> [required|optional]"));
                fields.Add(new("EXTERNAL",
                    "/network sasl <profile> external <certificate.pfx> [required|optional]"));
                fields.Add(new("Disable", "/network sasl <profile> off"));
                fields.Add(new("required", "Disconnect if authentication fails (default)"));
                fields.Add(new("optional", "Continue unidentified if authentication fails"));
                fields.Add(new("Password", "Passwords are entered through a prompt"));
                fields.Add(new("TLS", "All profile servers must use TLS to enable SASL"));
                break;
        }

        return new PresentationBlock(
            "HELP:", fields, TitleHighlight: $"/network {topic}");
    }

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
                "Start a plaintext DCC chat",
                "Ask the target to listen for the connection."),
            "schat" => DccTypeHelp(
                "schat",
                "/dcc schat <nick> [--passive]",
                "Start a TLS-encrypted DCC chat",
                "Ask the target to listen for the connection."),
            "send" => DccTypeHelp(
                "send",
                "/dcc send <nick> <file> [--passive]",
                "Send a file using plaintext DCC",
                "Ask the target to listen for the connection."),
            "ssend" => DccTypeHelp(
                "ssend",
                "/dcc ssend <nick> <file> [--passive]",
                "Send a file using TLS-encrypted DCC",
                "Ask the target to listen for the connection."),
            "list" => DccTypeHelp("list", "/dcc list", "List active DCC requests and connections", null),
            "show" => DccTypeHelp("show", "/dcc show <id>", "Show one DCC request or connection", null),
            "accept" => DccTypeHelp("accept", "/dcc accept <id>", "Accept an incoming DCC request", null),
            "reject" => DccTypeHelp("reject", "/dcc reject <id>", "Reject an incoming DCC request", null),
            "resume" => DccTypeHelp("resume", "/dcc resume <id>", "Resume an incomplete incoming file transfer", null),
            "cancel" => DccTypeHelp(
                "cancel",
                "/dcc cancel <id>",
                "Cancel an outgoing request, file transfer, or active DCC chat",
                null),
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
        if (detail is not null) fields.Add(new PresentationField("--passive", detail));
        if (type is "show" or "accept" or "reject" or "resume" or "cancel")
        {
            fields.Add(new PresentationField("ID", "DCC ID from /dcc list"));
        }
        return new PresentationBlock("HELP:", fields, TitleHighlight: $"/dcc {type}");
    }

    internal PresentationBlock? SettingHelp(string requested)
    {
        var setting = requested.ToLowerInvariant();
        var detail = setting switch
        {
            "nickname" => ("[nick]", "your current default", "Primary nickname used when connecting.", string.Empty),
            "altnick" => ("[nick]", "primary nickname plus _", "Fallback nickname used if the primary nickname is unavailable.", string.Empty),
            "username" => ("[username]", "your Windows user name", "Username sent during IRC registration.", string.Empty),
            "realname" => ("[name]", "default settings enthusiast", "Real name text sent during IRC registration.", string.Empty),
            "awaymsg" => ("[message|random|default]", "default", "Message displayed when marking yourself as away.", string.Empty),
            "kickmsg" => ("[message|random|default]", "default", "Reason used when kicking a user from a channel.", string.Empty),
            "quitmsg" => ("[message|random|default]", "default", "Quit reason displayed when exiting clircs.", string.Empty),
            "banmask" => ("[nick|host|userhost|full|wildcard]", "host", "Hostmask style used when banning someone.", string.Empty),
            "clonedetect" => ("[on|off]", "on", "Detects channel users sharing the same host.", string.Empty),
            "datechange" => ("[on|off]", "on", "Shows date changes in window output.", string.Empty),
            "startup" => ("[on|off]", "on", "Shows the startup presentation at launch and on new connections.", string.Empty),
            "highlight" => ("[on|off]", "on", "Highlights mentions of your nickname and mirrors them into the active window.", string.Empty),
            "awaylog" => ("[on|off]", "off", "Records incoming private messages and actions while you are away.", string.Empty),
            "status.lagmeter" => ("[on|off]", "on", "Shows connection response time in the status bar.", string.Empty),
            "status.tls" => ("[on|off]", "on", "Shows TLS indicators for server and bouncer connections.", string.Empty),
            "status.bouncer" => ("[on|off]", "on", "Shows bouncer identification beside the server name.", string.Empty),
            "status.chanmodes" => ("[on|off]", "on", "Shows channel modes beside the channel name.", string.Empty),
            "status.usermodes" => ("[on|off]", "on", "Shows your user modes beside your nickname.", string.Empty),
            "status.totals" => ("[on|off]", "on", "Shows channel member totals and rank counts.", string.Empty),
            "joininfo" => ("[on|off]", "off", "Shows matching userlist infolines when someone joins.", string.Empty),
            "onjoin.users" => ("[on|off]", "on", "Shows the user list when you join a channel.", string.Empty),
            "onjoin.topic" => ("[on|off]", "on", "Shows topic information when you join a channel.", string.Empty),
            "onjoin.created" => ("[on|off]", "on", "Shows the channel's creation time when you join.", string.Empty),
            "onjoin.modes" => ("[on|off]", "off", "Shows channel modes when you join.", string.Empty),
            "onjoin.sync" => ("[on|off]", "on", "Shows how long channel join synchronization took.", string.Empty),
            "kickrejoin" => ("[on|off]", "off", "Automatically rejoins a channel after you are kicked.", string.Empty),
            "usermodes" => ("[modes|none]", "+i", "User modes applied automatically after registration.", string.Empty),
            "reconnect.network" => ("[on|off]", "on", "Automatically reconnects after an unexpected disconnect.", string.Empty),
            "reconnect.kill" => ("[on|off]", "off", "Automatically reconnects after receiving a KILL.", string.Empty),
            "reconnect.limit" => ("[number]", "100", "Maximum automatic reconnect attempts per connection (set to 0 to disable).", string.Empty),
            "floodcontrol" => ("[on|off]", "on", "Paces outgoing IRC traffic to reduce flood disconnects.", string.Empty),
            "dcc.address" => ("[auto|IP|hostname]", "auto", "Public IPv6 or IPv4 address advertised for outgoing active DCC connections.", string.Empty),
            "dcc.ports" => ("[random|port|first-last]", "random", "Listening port or range used for outgoing active DCC connections.", string.Empty),
            "dcc.downloads" => ("[folder]", DefaultDccDownloadDirectory(_dataDirectory), "Folder used for received DCC files.", "/set dcc.downloads %USERPROFILE%\\Downloads"),
            "xdcc.auto" => ("[on|off]", "off", "Automatically accepts files requested through /xdcc.", string.Empty),
            "showhosts" => ("[on|off]", "on", "Shows user@host on joins, parts, and quits.", string.Empty),
            "notice.output" =>
                ("[active|status|window]", "active", "Selects the window used for notices from users.", string.Empty),
            "snotice.output" =>
                ("[active|status|window]", "status", "Selects the window used for server notices.", string.Empty),
            "notify.output" =>
                ("[active|status|window]", "status", "Selects the window used for notify online/offline announcements.", string.Empty),
            "wallops.output" =>
                ("[active|status|window]", "active", "Selects the window used for incoming WALLOPS messages.", string.Empty),
            "callerid.output" =>
                ("[active|status|window]", "active", "Selects the window used for caller-ID notifications.", string.Empty),
            "list.output" =>
                ("[active|status|window]", "window", "Selects the window used for channel-list results.", string.Empty),
            "who.output" =>
                ("[active|status|window]", "active", "Selects the window used for /who and /ircops results.", string.Empty),
            "whois.output" =>
                ("[active|status|window]", "active", "Selects the window used for WHOIS results.", string.Empty),
            "whowas.output" =>
                ("[active|status|window]", "active", "Selects the window used for WHOWAS results.", string.Empty),
            "names.output" =>
                ("[active|status|window]", "active", "Selects the window used for /names results.", string.Empty),
            "ctcp.output" =>
                ("[active|status|window]", "active", "Selects the window used for CTCP requests and replies.", string.Empty),
            "invite.output" =>
                ("[active|status|window]", "active", "Selects the window used for channel invitations and confirmations.", string.Empty),
            "links.output" =>
                ("[active|status|window]", "status", "Selects the window used for server-link listings.", string.Empty),
            "dns.output" =>
                ("[active|status|window]", "active", "Selects the window used for DNS lookup results.", string.Empty),
            _ => default
        };
        if (detail == default) return null;
        var fields = new List<PresentationField>
        {
            new("Usage", $"/set {setting} {detail.Item1}"),
            new("Currently", CurrentSettingValue(setting) ?? detail.Item2),
            new("Description", detail.Item3)
        };

        if (detail.Item4.Length > 0)
        {
            fields.Add(new("Example", detail.Item4));
        }

        return new PresentationBlock("HELP:", fields, TitleHighlight: $"/set {setting}");
    }

    private string? CurrentSettingValue(string setting) => setting switch
    {
        "nickname" => _preferences.Nickname,
        "altnick" => _preferences.AlternateNickname,
        "username" => _preferences.Username,
        "realname" => _preferences.RealName,
        "awaymsg" => _preferences.AwayMessage,
        "kickmsg" => _preferences.DefaultKickMessage ?? "random",
        "quitmsg" => _preferences.DefaultQuitMessage ?? "default",
        "banmask" => BanmaskFormatter.Name(_preferences.BanmaskStyle),
        "clonedetect" => _preferences.CloneDetection ? "on" : "off",
        "datechange" => _preferences.DateChange ? "on" : "off",
        "startup" => _preferences.Startup ? "on" : "off",
        "highlight" => _preferences.HighlightNickname ? "on" : "off",
        "awaylog" => _preferences.AwayLogging ? "on" : "off",
        "status.lagmeter" => _preferences.LagMeter ? "on" : "off",
        "status.tls" => _preferences.StatusTls ? "on" : "off",
        "status.bouncer" => _preferences.StatusBouncer ? "on" : "off",
        "status.chanmodes" => _preferences.StatusChannelModes ? "on" : "off",
        "status.usermodes" => _preferences.StatusUserModes ? "on" : "off",
        "status.totals" => _preferences.StatusTotals ? "on" : "off",
        "joininfo" => _preferences.AnnounceUserInfoOnJoin ? "on" : "off",
        "onjoin.users" => _preferences.OnJoinUsers ? "on" : "off",
        "onjoin.topic" => _preferences.OnJoinTopic ? "on" : "off",
        "onjoin.created" => _preferences.OnJoinCreated ? "on" : "off",
        "onjoin.modes" => _preferences.OnJoinModes ? "on" : "off",
        "onjoin.sync" => _preferences.OnJoinSync ? "on" : "off",
        "kickrejoin" => _preferences.AutoRejoinOnKick ? "on" : "off",
        "usermodes" => _preferences.UserModes.Length == 0 ? "not set" : _preferences.UserModes,
        "reconnect.network" => _preferences.NetworkReconnect ? "on" : "off",
        "reconnect.kill" => _preferences.KillReconnect ? "on" : "off",
        "reconnect.limit" => _preferences.ReconnectLimit.ToString(CultureInfo.InvariantCulture),
        "floodcontrol" => _preferences.FloodControl ? "on" : "off",
        "dcc.address" => _preferences.DccAddress,
        "dcc.ports" => _preferences.DccPorts.ToString(),
        "dcc.downloads" => _preferences.DccDownloads,
        "xdcc.auto" => _preferences.XdccAuto ? "on" : "off",
        "showhosts" => _preferences.ShowHosts ? "on" : "off",
        _ when setting.EndsWith(".output", StringComparison.Ordinal) &&
            _outputRouting.TryGetDestination(setting[..^".output".Length], out var destination) =>
                FormatOutputDestination(destination),
        _ => null
    };

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
                Summary: command.Name is "dcc" or "messages" or "network" or "autojoin"
                    or "backup" or "logging" or "tls" or "xdcc" or "notify" or "script"
                    or "cprot" or "pprot"
                    ? $"Use /help {command.Name} <command> for details."
                    : null,
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
        if (input.Arguments.Count != 0)
        {
            return ValueTask.FromResult(CommandResult.Failure("Usage: /config"));
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
            ])));
    }

    private async ValueTask<CommandResult> BackupAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        const string usage = "Usage: /backup <command> [arguments]";

        if (input.Arguments.Count == 0)
        {
            return CommandResult.Failure(usage);
        }

        var operation = input.Arguments[0].ToLowerInvariant();

        if (operation == "restore"
            ? input.Arguments.Count != 2
            : input.Arguments.Count > 1)
        {
            return CommandResult.Failure(usage);
        }

        try
        {
            switch (operation)
            {
                case "create":
                    await _logWriter.FlushAsync(cancellationToken);
                    var path = await Task.Run(_backupManager.Create, cancellationToken);
                    return CommandResult.Success($"Backup created: {path}");

                case "path":
                    return CommandResult.Success(
                        $"Backup directory: {_backupManager.BackupDirectory}");

                case "list":
                    var backups = _backupManager.List();
                    if (backups.Count == 0)
                    {
                        return CommandResult.Success("No clircs backups found.");
                    }

                    return CommandResult.Success(new PresentationBlock(
                        "Backups",
                        Table: new PresentationTable(
                            ["File", "Created", "Size"],
                            backups.Select(file => (IReadOnlyList<string>)new[]
                            {
                                file.Name,
                                file.LastWriteTime.ToString(
                                    "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                                FormatFileSize(file.Length)
                            }).ToArray(),
                            new HashSet<int> { 0 })));

                case "restore":
                    return await RestoreBackupAsync(
                        context, input.Arguments[1], cancellationToken);

                default:
                    return CommandResult.Failure(usage);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidDataException or ArgumentException or NotSupportedException)
        {
            var action = operation == "restore" ? "Restore" : "Backup";
            return CommandResult.Failure($"{action} failed: {exception.Message}");
        }
    }

    private async ValueTask<CommandResult> RestoreBackupAsync(
        CommandContext context,
        string requestedPath,
        CancellationToken cancellationToken)
    {
        PreparedBackupRestore? prepared = null;

        try
        {
            var archivePath = _backupManager.ResolveRestorePath(requestedPath);
            prepared = await Task.Run(
                () => PreparedBackupRestore.Create(
                    _dataDirectory, archivePath, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            DisplayCommandResult(
                CommandResult.Success(new PresentationBlock(
                    "Restore Backup",
                    [
                        new("Archive", prepared.ArchivePath),
                        new("Destination", _dataDirectory)
                    ])),
                context);
            DisplayCommandResult(
                CommandResult.Success(
                    "Existing backups will be kept."),
                context);
            DisplayCommandResult(
                CommandResult.Success(
                    "After confirmation, clircs will disconnect, restore the backup, " +
                    "and exit. Restart clircs to complete the process."),
                context);

            DisplayCommandWarning(
                "WARNING: This replaces all current user data, including " +
                "settings, profiles, scripts, and logs.",
                context);
            DisplayCommandWarning(
                "WARNING: Files added since this backup will be deleted. " +
                "Nothing will be merged, and overwritten data is unrecoverable.",
                context);

            var response = _presenter.ReadPrompt("Restore this backup? [y/N] ");
            if (!IsBackupRestoreConfirmed(response))
            {
                return CommandResult.Success("Restore canceled.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            _pendingRestore = prepared;
            prepared = null;
            _exitRequested = true;

            return CommandResult.Success("Closing clircs to restore the backup.");
        }
        finally
        {
            prepared?.Dispose();
        }
    }

    internal static bool IsBackupRestoreConfirmed(string? response)
    {
        var choice = response?.Trim();
        return string.Equals(choice, "y", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(choice, "yes", StringComparison.OrdinalIgnoreCase);
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
                rows.Add([rule.NetworkName, "Profile default", rule.Enabled ? "on" : "off"]);
                rows.AddRange(rule.Targets
                    .OrderBy(target => target.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(target => (IReadOnlyList<string>)new[]
                    {
                        rule.NetworkName, target.Key, target.Value ? "on" : "off"
                    }));
            }
            return ValueTask.FromResult(CommandResult.Success(new PresentationBlock(
                "Logging Rules",
                Table: new PresentationTable(["Profile", "Target", "Status"], rows))));
        }
        if (operation == "status" && input.Arguments.Count == 1)
        {
            return ValueTask.FromResult(LoggingStatus());
        }
        if (operation is not ("on" or "off") || input.Arguments.Count > 3)
        {
            return ValueTask.FromResult(CommandResult.Failure(
                "Usage: /logging [on|off|status|list|path] [network] [target]"));
        }

        var enabled = operation == "on";
        NetworkProfile? profile;
        IrcNetworkSession? session = null;
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
            session = activeSession;
            profile = ProfileFor(activeSession);
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
                session = activeSession;
                profile = ProfileFor(activeSession);
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

        if (profile is null)
        {
            if (session is null || target is null)
                return ValueTask.FromResult(CommandResult.Failure("There is no active loggable window."));

            _sessionLogging.Set(session.State.Id, target, enabled);
            return ValueTask.FromResult(CommandResult.Success(
                $"Logging is now {(enabled ? "on" : "off")} for {target} in this session."));
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
        if (profile is null)
        {
            return CommandResult.Success(new PresentationBlock(
                $"Logging: session/{target}",
                [
                    new("Scope", "current session only"),
                    new("Window", _sessionLogging.IsEnabled(session.State.Id, target) ? "on" : "off")
                ]));
        }
        var networkDefault = _loggingStore.NetworkDefault(profile.Id);
        var targetOverride = _loggingStore.TargetOverride(profile.Id, target);
        return CommandResult.Success(LoggingStatusPresentation(
            profile.DisplayName,
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
