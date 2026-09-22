using Clircs.Protocol;

namespace Clircs.Sessions;

internal sealed class NetworkQueryResponseProcessor(SessionEventBuilder events)
{
    private readonly List<LinkResultRow> _linkResults = [];
    private readonly List<ListResultRow> _listResults = [];
    private readonly List<StatsOperatorRow> _statsOperatorResults = [];
    private string? _statsOperatorSummary;

    public void Reset()
    {
        _linkResults.Clear();
        _listResults.Clear();
        _statsOperatorResults.Clear();
        _statsOperatorSummary = null;
    }

    public bool TryProcess(
        IrcMessage message,
        DateTimeOffset now,
        out IReadOnlyList<SessionEvent> sessionEvents)
    {
        SessionEvent? result = null;
        switch (message.Command)
        {
            case "364":
                CollectLink(message);
                break;
            case "365":
                result = CompleteLinks(now);
                break;
            case "321":
                _listResults.Clear();
                break;
            case "322":
                CollectListRow(message);
                break;
            case "323":
                result = CompleteList(now);
                break;
            case "249" when IsStatsOperatorReply(message):
                CollectStatsOperator(message);
                break;
            case "219" when IsStatsOperatorReply(message):
                result = CompleteStatsOperators(now);
                break;
            default:
                sessionEvents = [];
                return false;
        }

        sessionEvents = result is null ? [] : [result];
        return true;
    }

    private void CollectLink(IrcMessage message)
    {
        if (message.Parameters.Count < 4) return;
        var details = message.Parameters[3];
        var separator = details.IndexOf(' ');
        var hopsText = separator < 0 ? details : details[..separator];
        var hops = int.TryParse(hopsText, out var parsedHops) ? Math.Clamp(parsedHops, 0, 32) : 0;
        var description = separator < 0 ? string.Empty : details[(separator + 1)..];
        _linkResults.Add(new LinkResultRow(message.Parameters[1], hops, description));
    }

    private SessionEvent CompleteLinks(DateTimeOffset now)
    {
        var rows = _linkResults
            .Select((row, index) => (row, index))
            .OrderBy(entry => entry.row.Hops == 0 ? 0 : 1)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.row)
            .ToArray();
        _linkResults.Clear();
        return events.Status(
            SessionEventKind.Server,
            $"LINKS: {rows.Length} server(s)",
            now,
            SessionEventBuilder.Fields(
                ("outputFamily", "links"), ("numeric", "365"), ("outputEnd", "true")),
            new PresentationBlock(
                "Server links",
                Table: new PresentationTable(
                    ["Server", "Description"],
                    rows.Select(row => (IReadOnlyList<string>)new[]
                    {
                        $"{new string(' ', row.Hops * 2)}{row.Server} ({row.Hops})", row.Description
                    }).ToArray()),
                Summary: rows.Length == 0 ? "No server links were returned." : $"{rows.Length} server(s)"));
    }

    private void CollectListRow(IrcMessage message)
    {
        if (message.Parameters.Count < 4) return;
        _listResults.Add(new ListResultRow(
            message.Parameters[1],
            int.TryParse(message.Parameters[2], out var users) ? users : 0,
            message.Parameters[3]));
    }

    private SessionEvent CompleteList(DateTimeOffset now)
    {
        var rows = _listResults.ToArray();
        _listResults.Clear();
        return events.Status(
            SessionEventKind.Server,
            $"LIST: {rows.Length} channel(s)",
            now,
            SessionEventBuilder.Fields(
                ("outputFamily", "list"), ("numeric", "323"),
                ("outputEnd", "true"), ("routeConfigured", "true")),
            new PresentationBlock(
                "Channels",
                Table: new PresentationTable(
                    ["Channel", "Users", "Topic"],
                    rows.Select(row => (IReadOnlyList<string>)new[]
                    {
                        row.Channel, row.Users.ToString(), row.Topic
                    }).ToArray(),
                    KeepAllColumns: true,
                    MaximumWidths: [30, 8, 80]),
                Summary: rows.Length == 0 ? "No channels matched." : $"{rows.Length} channel(s)"));
    }

    private static bool IsStatsOperatorReply(IrcMessage message) =>
        message.Parameters.Count >= 2 &&
        string.Equals(message.Parameters[1], "p", StringComparison.Ordinal);

    private void CollectStatsOperator(IrcMessage message)
    {
        if (message.Parameters.Count < 3) return;

        var text = message.Parameters[^1];
        if (TryParseStatsOperator(text, out var row))
        {
            _statsOperatorResults.Add(row);
        }
        else if (text.EndsWith(" OPER(s)", StringComparison.OrdinalIgnoreCase))
        {
            _statsOperatorSummary = text;
        }
        else
        {
            // Preserve unfamiliar server output instead of getting rid of it.
            _statsOperatorResults.Add(new StatsOperatorRow(string.Empty, text, string.Empty, string.Empty));
        }
    }

    private SessionEvent CompleteStatsOperators(DateTimeOffset now)
    {
        var rows = _statsOperatorResults.ToArray();
        var summary = _statsOperatorSummary ??
            (rows.Length == 0 ? "No visible IRC operators." : $"{rows.Length} operator(s)");

        _statsOperatorResults.Clear();
        _statsOperatorSummary = null;

        return events.Status(
            SessionEventKind.Server,
            $"STATS p: {summary}",
            now,
            SessionEventBuilder.Fields(
                ("outputFamily", "stats"),
                ("statsSelector", "p"),
                ("numeric", "219"),
                ("outputEnd", "true")),
            new PresentationBlock(
                "IRC operators",
                Table: new PresentationTable(
                    ["Nick", "Address", "Role", "Idle"],
                    rows.Select(row => (IReadOnlyList<string>)new[]
                    {
                        row.Nick, row.Address, row.Role, row.Idle
                    }).ToArray(),
                    KeepAllColumns: true,
                    MaximumWidths: [24, PresentationTable.UnboundedWidth, 8, 14]),
                Summary: summary));
    }

    private static bool TryParseStatsOperator(string text, out StatsOperatorRow row)
    {
        row = default!;

        if (!text.StartsWith("[", StringComparison.Ordinal))
        {
            return false;
        }

        var roleEnd = text.IndexOf(']');
        if (roleEnd <= 1)
        {
            return false;
        }

        var addressStart = text.IndexOf(" (", roleEnd + 1, StringComparison.Ordinal);
        const string idleMarker = ") Idle: ";
        var idleStart = addressStart < 0
            ? -1
            : text.IndexOf(idleMarker, addressStart + 2, StringComparison.OrdinalIgnoreCase);

        if (addressStart < 0 || idleStart < 0)
        {
            return false;
        }

        var roleCode = text[1..roleEnd];
        var nick = text[(roleEnd + 1)..addressStart].Trim();
        var address = text[(addressStart + 2)..idleStart].Trim();
        var idle = text[(idleStart + idleMarker.Length)..].Trim();

        if (nick.Length == 0 ||
            address.Length == 0 ||
            !long.TryParse(idle, out var idleSeconds) ||
            idleSeconds < 0)
        {
            return false;
        }

        var role = roleCode switch
        {
            "A" => "Admin",
            "O" => "Oper",
            _ => $"[{roleCode}]"
        };

        row = new StatsOperatorRow(role, nick, address, FormatElapsed(idleSeconds));
        return true;
    }

    private static string FormatElapsed(long seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        var parts = new List<string>();
        if (span.Days > 0) parts.Add($"{span.Days}d");
        if (span.Hours > 0) parts.Add($"{span.Hours}h");
        if (span.Minutes > 0) parts.Add($"{span.Minutes}m");
        parts.Add($"{span.Seconds}s");
        return string.Join(' ', parts);
    }

    private sealed record LinkResultRow(string Server, int Hops, string Description);

    private sealed record ListResultRow(string Channel, int Users, string Topic);

    private sealed record StatsOperatorRow(
        string Role,
        string Nick,
        string Address,
        string Idle);
}
