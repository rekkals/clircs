using Clircs.Sessions;

namespace Clircs.ConsoleClient;

internal static class TranscriptFormatter
{
    public static IReadOnlyList<string> FormatLines(
        SessionEvent sessionEvent,
        HostmaskVisibility joinHostmasks = HostmaskVisibility.UserHost,
        HostmaskVisibility partHostmasks = HostmaskVisibility.UserHost,
        HostmaskVisibility quitHostmasks = HostmaskVisibility.UserHost)
    {
        var semantics = SessionEventPresentation.From(sessionEvent);
        if (semantics.Subtype == SessionEventSubtype.Startup || semantics.IsTransientHistory)
        {
            return [];
        }

        if (sessionEvent.Presentation is { } block)
        {
            return Flatten(block);
        }

        var text = sessionEvent.Kind switch
        {
            SessionEventKind.Join when semantics.Nick is not null =>
                $"--> {semantics.Nick}{Identity(semantics, joinHostmasks)} joined {semantics.Channel}",
            SessionEventKind.Part when semantics.Nick is not null && semantics.Subtype == SessionEventSubtype.Quit =>
                $"<-- {semantics.Nick}{Identity(semantics, quitHostmasks)} quit{Reason(semantics.Reason)}",
            SessionEventKind.Part when semantics.Nick is not null && semantics.Subtype != SessionEventSubtype.Kick =>
                $"<-- {semantics.Nick}{Identity(semantics, partHostmasks)} left {semantics.Channel}{Reason(semantics.Reason)}",
            SessionEventKind.Message or SessionEventKind.Highlight
                when semantics.Nick is not null && semantics.Message is not null =>
                $"<{semantics.NickPrefix}{semantics.Nick}> {semantics.Message}",
            _ => sessionEvent.Text
        };
        text = TerminalTextSanitizer.Sanitize(text).TrimEnd();
        return text.Length == 0 ? [] : [text];
    }

    private static IReadOnlyList<string> Flatten(PresentationBlock block)
    {
        var lines = new List<string> { block.Title };
        if (block.Fields is { Count: > 0 })
        {
            var width = block.Fields.Max(field => field.Label.Length);
            lines.AddRange(block.Fields.Select(field => $"{field.Label.PadRight(width)}  {field.Value}"));
        }
        if (block.Grid is { Count: > 0 }) lines.AddRange(block.Grid);
        if (block.Table is { } table)
        {
            lines.Add(string.Join("  ", table.Columns));
            lines.AddRange(table.Rows.Select(row => string.Join("  ", row)));
        }
        if (!string.IsNullOrWhiteSpace(block.Summary)) lines.Add(block.Summary);
        return lines.Select(line => TerminalTextSanitizer.Sanitize(line).TrimEnd())
            .Where(line => line.Length > 0)
            .ToArray();
    }

    private static string Identity(SessionEventPresentation semantics, HostmaskVisibility visibility)
    {
        var value = visibility switch
        {
            HostmaskVisibility.Full or HostmaskVisibility.UserHost
                when semantics.Username is not null && semantics.Host is not null =>
                $"{semantics.Username}@{semantics.Host}",
            HostmaskVisibility.Host when semantics.Host is not null => semantics.Host,
            _ => null
        };
        return value is null ? string.Empty : $" [{value}]";
    }

    private static string Reason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? string.Empty : $" ({reason})";
}
