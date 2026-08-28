namespace Clircs.Sessions;

public sealed record PresentationField
{
    public PresentationField(string label, string value, IrcFormattedText? formattedValue = null)
    {
        Label = label;
        FormattedValue = formattedValue ?? IrcTextFormatting.Parse(value);
        Value = FormattedValue.PlainText;
    }

    public string Label { get; init; }

    public string Value { get; init; }

    public IrcFormattedText FormattedValue { get; init; }
}

public sealed record PresentationTable
{
    public const int UnboundedWidth = int.MaxValue;

    public PresentationTable(
        IReadOnlyList<string> Columns,
        IReadOnlyList<IReadOnlyList<string>> Rows,
        IReadOnlySet<int>? PreserveColumns = null,
        bool KeepAllColumns = false,
        IReadOnlyList<int>? MaximumWidths = null,
        IReadOnlyList<IReadOnlyList<IrcFormattedText?>>? FormattedRows = null)
    {
        this.Columns = Columns;
        this.PreserveColumns = PreserveColumns;
        this.KeepAllColumns = KeepAllColumns;
        this.MaximumWidths = MaximumWidths;
        this.FormattedRows = FormattedRows ?? Rows
            .Select(row => (IReadOnlyList<IrcFormattedText?>)row
                .Select(value => (IrcFormattedText?)IrcTextFormatting.Parse(value)).ToArray())
            .ToArray();
        this.Rows = Rows.Select((row, rowIndex) => (IReadOnlyList<string>)row
            .Select((value, columnIndex) =>
                rowIndex < this.FormattedRows.Count && columnIndex < this.FormattedRows[rowIndex].Count
                    ? this.FormattedRows[rowIndex][columnIndex]?.PlainText ?? value
                    : IrcTextFormatting.ToPlainText(value))
            .ToArray()).ToArray();
    }

    public IReadOnlyList<string> Columns { get; init; }

    public IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }

    public IReadOnlySet<int>? PreserveColumns { get; init; }

    public bool KeepAllColumns { get; init; }

    public IReadOnlyList<int>? MaximumWidths { get; init; }

    public IReadOnlyList<IReadOnlyList<IrcFormattedText?>> FormattedRows { get; init; }
}

public sealed record PresentationBlock(
    string Title,
    IReadOnlyList<PresentationField>? Fields = null,
    PresentationTable? Table = null,
    string? Summary = null,
    IReadOnlyList<string>? Grid = null,
    bool BracketGridCells = false,
    string? TitleHighlight = null,
    int? GridColumns = null,
    int? GridColumnWidth = null,
    int? FieldLabelWidth = null);
