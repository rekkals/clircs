using Clircs.Commands;

namespace Clircs.ConsoleClient;

internal static class ScriptCommandRequestPolicy
{
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "nick",
        "away",
        "back",

        "msg",
        "notice",
        "say",
        "me",
        "describe",
        "ame",
        "amsg",
        "ctcp",
        "ping",
        "sv",
        "time",

        "join",
        "part",
        "cycle",
        "invite",
        "topic",
        "rt",
        "mode",
        "op",
        "deop",
        "voice",
        "devoice",
        "kick",
        "ban",
        "kickban",
        "tban",
        "mop",
        "mdop",
        "mv",
        "mdv",
        "mmode",
        "banlist",
        "exceptlist",
        "invitelist",
        "quietlist",
        "unban",
        "clearbans",
        "appendtopic",
        "cleartopic",

        "accept",
        "umop",
        "umdop",
        "umv",
        "umdv",
        "filterkick",
        "filterkickban",
        "findnickkick",
        "kicknonops",
        "cop",
        "cban",
        "ckick",
        "ckb",
        "massinvite",
        "inviteall",
        "wall",
        "wallmsg",
        "voicenotice",
        "voicemsg",
        "nonopnotice",
        "nonopmsg",
        "userwall",

        "names",
        "who",
        "whois",
        "iwhois",
        "whowas",
        "motd",
        "links",
        "list",

        "nickserv",
        "chanserv",
        "memoserv",
        "operserv",
        "hostserv",
        "botserv",
        "limitserv"
    };

    public static bool IsAllowed(CommandRegistry commands, CommandInput command)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(command);

        return commands.TryResolve(command.Name, out var definition) &&
            AllowedCommands.Contains(definition.Name);
    }
}
