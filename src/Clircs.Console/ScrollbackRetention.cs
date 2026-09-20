using Clircs.Sessions;

namespace Clircs.ConsoleClient;

// We'll keep quieter window history for up to a day, while bounding busier windows
// separately, so regular scrollback doesn't cause clircs to start operating like it
// has a hangover.
internal static class ScrollbackRetention
{
    internal const int MinimumEntries = 500;
    internal const int MaximumEntries = 5_000;
    internal const int MaximumTotalEntries = 100_000;
    internal const int EmergencyMaximumEntries = 250_000;
    internal const int EmergencyMaximumTotalEntries = 500_000;
    internal static readonly TimeSpan RetentionTime = TimeSpan.FromDays(1);

    public static int Trim(List<SessionEvent> history, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(history);
        var maximumRemoval = history.Count - MinimumEntries;
        if (maximumRemoval <= 0) return 0;

        var cutoff = now - RetentionTime;
        var remove = 0;
        while (remove < maximumRemoval && history[remove].Timestamp < cutoff)
        {
            remove++;
        }
        if (remove > 0) history.RemoveRange(0, remove);
        return remove;
    }

    public static int Trim(WindowEventHistory history, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(history);
        var remove = ExpiredEntryCount(history, now);
        if (remove > 0) history.RemoveFirst(remove);
        return remove;
    }

    public static int EnforceRetentionLimit(
        WindowEventHistory history,
        int maximumEntries = MaximumEntries)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, MinimumEntries);
        var remove = history.Count - maximumEntries;
        if (remove <= 0) return 0;
        history.RemoveFirst(remove);
        return remove;
    }

    public static bool EnforceEmergencyLimit(
        List<SessionEvent> history,
        int maximumEntries = EmergencyMaximumEntries)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        var remove = history.Count - maximumEntries;
        if (remove <= 0) return false;
        history.RemoveRange(0, remove);
        return true;
    }

    public static bool EnforceEmergencyLimit(
        WindowEventHistory history,
        int maximumEntries = EmergencyMaximumEntries)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        var remove = history.Count - maximumEntries;
        if (remove <= 0) return false;
        history.RemoveFirst(remove);
        return true;
    }

    private static int ExpiredEntryCount(IReadOnlyList<SessionEvent> history, DateTimeOffset now)
    {
        var maximumRemoval = history.Count - MinimumEntries;
        if (maximumRemoval <= 0) return 0;
        var cutoff = now - RetentionTime;
        var remove = 0;
        while (remove < maximumRemoval && history[remove].Timestamp < cutoff) remove++;
        return remove;
    }
}
