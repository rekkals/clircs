using Clircs.Protocol;

namespace Clircs.Sessions;

internal sealed class NetworkQueryResponseProcessor(SessionEventBuilder events)
{
    private readonly List<LinkResultRow> _linkResults = [];
    private readonly List<ListResultRow> _listResults = [];

    public void Reset()
    {
        _linkResults.Clear();
        _listResults.Clear();
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

    private sealed record LinkResultRow(string Server, int Hops, string Description);

    private sealed record ListResultRow(string Channel, int Users, string Topic);
}
