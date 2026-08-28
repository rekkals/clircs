namespace Clircs.ConsoleClient;

internal sealed record WrappedTerminalLine(string Leading, string Text);

internal static class TerminalWordWrapper
{
    public static IReadOnlyList<WrappedTerminalLine> Wrap(string leading, string text, int width)
        => Wrap(leading, string.Empty, text, width);

    public static IReadOnlyList<WrappedTerminalLine> Wrap(
        string leading,
        string continuationLeading,
        string text,
        int width)
    {
        leading ??= string.Empty;
        continuationLeading ??= string.Empty;
        text ??= string.Empty;
        var lineWidth = Math.Max(2, width - 1);
        var lines = new List<WrappedTerminalLine>();
        var logicalLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var first = true;
        foreach (var logicalLine in logicalLines)
        {
            var remaining = logicalLine;
            do
            {
                var lineLeading = first ? leading : continuationLeading;
                var available = Math.Max(1, lineWidth - lineLeading.Length);
                if (remaining.Length <= available)
                {
                    lines.Add(new WrappedTerminalLine(lineLeading, remaining));
                    first = false;
                    break;
                }

                var split = remaining.LastIndexOf(' ', available - 1, available);
                if (split <= 0) split = available;
                lines.Add(new WrappedTerminalLine(lineLeading, remaining[..split].TrimEnd()));
                remaining = remaining[split..].TrimStart();
                first = false;
            } while (remaining.Length > 0);
        }

        if (lines.Count == 0) lines.Add(new WrappedTerminalLine(leading, string.Empty));
        return lines;
    }
}
