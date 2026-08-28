using Clircs.Protocol;
using Clircs.State;

namespace Clircs.Sessions;

internal sealed class SessionEventBuilder(NetworkSessionState state)
{
    public SessionEvent Status(
        SessionEventKind kind,
        string text,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string?>? fields = null,
        PresentationBlock? presentation = null) =>
        Create(state.StatusBuffer, kind, text, now, fields, presentation);

    public SessionEvent Create(
        BufferState buffer,
        SessionEventKind kind,
        string text,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string?>? fields = null,
        PresentationBlock? presentation = null,
        IrcFormattedText? formattedContent = null) =>
        new(state.Id, buffer.Id, kind, TerminalTextSanitizer.Sanitize(text), now, SanitizeFields(fields),
            SanitizePresentation(presentation), formattedContent);

    public static IReadOnlyDictionary<string, string?> Fields(params (string Name, string? Value)[] values) =>
        values.Where(value => value.Value is not null)
            .ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);

    internal static PresentationBlock? SanitizePresentation(PresentationBlock? presentation)
    {
        if (presentation is null) return null;
        return presentation with
        {
            Title = TerminalTextSanitizer.Sanitize(presentation.Title),
            Fields = presentation.Fields?.Select(field => new PresentationField(
                TerminalTextSanitizer.Sanitize(field.Label),
                TerminalTextSanitizer.Sanitize(field.Value),
                field.FormattedValue)).ToArray(),
            Table = presentation.Table is null ? null : new PresentationTable(
                presentation.Table.Columns.Select(TerminalTextSanitizer.Sanitize).ToArray(),
                presentation.Table.Rows.Select(row => (IReadOnlyList<string>)row
                    .Select(TerminalTextSanitizer.Sanitize).ToArray()).ToArray(),
                presentation.Table.PreserveColumns,
                presentation.Table.KeepAllColumns,
                presentation.Table.MaximumWidths,
                presentation.Table.FormattedRows),
            Summary = presentation.Summary is null ? null : TerminalTextSanitizer.Sanitize(presentation.Summary),
            Grid = presentation.Grid?.Select(TerminalTextSanitizer.Sanitize).ToArray(),
            TitleHighlight = presentation.TitleHighlight is null
                ? null
                : TerminalTextSanitizer.Sanitize(presentation.TitleHighlight)
        };
    }

    private static IReadOnlyDictionary<string, string?>? SanitizeFields(
        IReadOnlyDictionary<string, string?>? fields) =>
        fields?.ToDictionary(
            entry => entry.Key,
            entry => entry.Value is null ? null : TerminalTextSanitizer.Sanitize(entry.Value),
            StringComparer.Ordinal);
}
