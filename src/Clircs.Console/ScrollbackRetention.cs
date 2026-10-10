using Clircs.Sessions;

namespace Clircs.ConsoleClient;

// Keep enough scrollback to be useful, while bounding busy windows separately so
// clircs doesn't eventually start operating like it has a hangover.
internal static class ScrollbackRetention
{
    internal const int MinimumEntries = 500;
    internal const int MaximumEntries = 5_000;
    internal const int MaximumTotalEntries = 100_000;
    internal const int EmergencyMaximumEntries = 250_000;
    internal const int EmergencyMaximumTotalEntries = 500_000;

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
}
