using Clircs.Sessions;

namespace Clircs.ConsoleClient;

internal static class ViewportHistory
{
    public sealed record Slice<T>(T Item, int SkipRows, int TakeRows);
    public sealed record StoreResult(bool Stored, bool Replaced, SessionEvent? Previous);

    public static StoreResult StoreEvent(IList<SessionEvent> history, SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(sessionEvent);
        var replacementKey = sessionEvent.Fields?.GetValueOrDefault("history.replaceKey");
        var transientKey = sessionEvent.Fields?.GetValueOrDefault("history.transientKey");
        if (!string.IsNullOrWhiteSpace(transientKey) && ContainsFinalKey(history, transientKey))
        {
            return new StoreResult(false, false, null);
        }
        if (!string.IsNullOrWhiteSpace(replacementKey))
        {
            var index = FindLastTransientKey(history, replacementKey);
            if (index >= 0)
            {
                var previous = history[index];
                history[index] = sessionEvent;
                return new StoreResult(true, true, previous);
            }
        }

        history.Add(sessionEvent);
        return new StoreResult(true, false, null);
    }

    private static bool ContainsFinalKey(IList<SessionEvent> history, string key)
    {
        for (var index = history.Count - 1; index >= 0; index--)
        {
            if (string.Equals(
                    history[index].Fields?.GetValueOrDefault("history.finalKey"),
                    key,
                    StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static int FindLastTransientKey(IList<SessionEvent> history, string key)
    {
        for (var index = history.Count - 1; index >= 0; index--)
        {
            if (string.Equals(
                    history[index].Fields?.GetValueOrDefault("history.transientKey"),
                    key,
                    StringComparison.Ordinal)) return index;
        }
        return -1;
    }

    public static IReadOnlyList<Slice<T>> SelectRows<T>(
        IReadOnlyList<T> history,
        int offsetRows,
        int rowBudget,
        Func<T, int> measureRows)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(measureRows);
        rowBudget = Math.Max(1, rowBudget);
        offsetRows = Math.Max(0, offsetRows);

        // The overwhelmingly common live-output case is the bottom of the window.
        // Walk backward only far enough to fill the viewport instead of measuring and
        // allocating metadata for the complete scrollback on every incoming event.
        if (offsetRows == 0) return SelectBottomRows(history, rowBudget, measureRows);

        // Explicit scrollback needs the total row count so an oversized offset can be
        // clamped to a complete first page. This work happens on user navigation, not
        // for every line of live traffic.
        var measurements = history.Select(item => Math.Max(1, measureRows(item))).ToArray();
        var totalRows = measurements.Sum();
        offsetRows = Math.Clamp(offsetRows, 0, Math.Max(0, totalRows - rowBudget));
        var endRow = totalRows - offsetRows;
        var startRow = Math.Max(0, endRow - rowBudget);
        var slices = new List<Slice<T>>();
        var itemStart = 0;
        for (var index = 0; index < history.Count; index++)
        {
            var itemEnd = itemStart + measurements[index];
            var intersectionStart = Math.Max(startRow, itemStart);
            var intersectionEnd = Math.Min(endRow, itemEnd);
            if (intersectionStart < intersectionEnd)
            {
                slices.Add(new Slice<T>(
                    history[index],
                    intersectionStart - itemStart,
                    intersectionEnd - intersectionStart));
            }
            itemStart = itemEnd;
            if (itemStart >= endRow) break;
        }
        return slices;
    }

    private static IReadOnlyList<Slice<T>> SelectBottomRows<T>(
        IReadOnlyList<T> history,
        int rowBudget,
        Func<T, int> measureRows)
    {
        var slices = new List<Slice<T>>();
        var remainingBudget = rowBudget;
        for (var index = history.Count - 1; index >= 0 && remainingBudget > 0; index--)
        {
            var rows = Math.Max(1, measureRows(history[index]));
            var take = Math.Min(rows, remainingBudget);
            slices.Add(new Slice<T>(history[index], rows - take, take));
            remainingBudget -= take;
        }
        slices.Reverse();
        return slices;
    }
}
