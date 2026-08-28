using Clircs.Sessions;

namespace Clircs.ConsoleClient;

internal sealed record BufferHeaderItem(string Text, int Priority, int MinimumWidth);

internal sealed record BufferHeaderModel(
    string? Primary,
    IReadOnlyList<BufferHeaderItem> Auxiliary,
    IrcFormattedText? FormattedPrimary = null)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Primary) && Auxiliary.Count == 0;
}

internal static class BufferHeaderComposer
{
    public static string? Compose(BufferHeaderModel model, int width, string separator = " | ")
    {
        width = Math.Max(1, width);
        var primary = Clean(model.Primary);
        var items = model.Auxiliary
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .OrderByDescending(item => item.Priority)
            .Select(item => item with { Text = Clean(item.Text)! })
            .ToList();
        if (primary is null && items.Count == 0)
        {
            return null;
        }

        separator = string.IsNullOrEmpty(separator) ? " | " : separator;
        var auxiliary = FitAuxiliary(items, width, primary is not null, separator);
        if (primary is null)
        {
            return Truncate(auxiliary, width);
        }
        if (string.IsNullOrEmpty(auxiliary))
        {
            return Truncate(primary, width);
        }
        if (primary.Length + separator.Length + auxiliary.Length <= width)
        {
            return primary + separator + auxiliary;
        }

        var itemMinimum = Math.Min(
            width / 2,
            Math.Max(8, items.FirstOrDefault()?.MinimumWidth ?? 24));
        var auxiliaryWidth = Math.Min(auxiliary.Length, Math.Max(itemMinimum, width / 3));
        auxiliaryWidth = Math.Min(auxiliaryWidth, Math.Max(0, width - separator.Length - 8));
        var primaryWidth = Math.Max(0, width - separator.Length - auxiliaryWidth);
        return Truncate(primary, primaryWidth) + separator + Truncate(auxiliary, auxiliaryWidth);
    }

    private static string FitAuxiliary(
        List<BufferHeaderItem> items,
        int width,
        bool hasPrimary,
        string separator)
    {
        while (items.Count > 1)
        {
            var joined = string.Join(separator, items.Select(item => item.Text));
            var reserved = hasPrimary ? Math.Max(8, width / 3) : 0;
            if (joined.Length + reserved <= width)
            {
                return joined;
            }
            items.RemoveAt(items.Count - 1);
        }
        return items.Count == 0 ? string.Empty : items[0].Text;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : TerminalTextSanitizer.Sanitize(value).Trim();

    internal static string Truncate(string value, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }
        if (value.Length <= width)
        {
            return value;
        }
        return width <= 3 ? value[..width] : value[..(width - 3)] + "...";
    }
}
