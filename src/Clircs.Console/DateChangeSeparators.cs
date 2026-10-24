using System.Globalization;
using Clircs.Identity;
using Clircs.Sessions;

namespace Clircs.ConsoleClient;

internal static class DateChangeSeparators
{
    public static bool IsSeparator(SessionEvent sessionEvent) =>
        sessionEvent.Fields?.GetValueOrDefault("event") == "datechange";

    public static bool IsVisible(
        SessionEvent sessionEvent,
        DateTimeOffset? previousTimestamp,
        bool enabled = true) =>
        !IsSeparator(sessionEvent) ||
        (enabled && (previousTimestamp is not { } previous ||
                 previous.Date != sessionEvent.Timestamp.Date));

    public static SessionEvent Create(
        NetworkSessionId networkSessionId,
        BufferId bufferId,
        DateTimeOffset timestamp)
    {
        // The midnight timestamp is for display; retain the supplied time for aging.
        var midnight = new DateTimeOffset(timestamp.Date, TimeSpan.Zero);
        var date = midnight.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new SessionEvent(
            networkSessionId,
            bufferId,
            SessionEventKind.Server,
            $"Date: {date}",
            midnight,
            new Dictionary<string, string?>
            {
                ["clientResult"] = "true",
                ["event"] = "datechange"
            })
        {
            ReceivedAt = timestamp
        };
    }

    public static SessionEvent? Before(
        SessionEvent sessionEvent,
        DateTimeOffset? previousTimestamp)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        if (IsSeparator(sessionEvent) ||
            previousTimestamp is not { } previous ||
            previous.Date == sessionEvent.Timestamp.Date)
        {
            return null;
        }

        return Create(
            sessionEvent.NetworkSessionId,
            sessionEvent.BufferId,
            sessionEvent.Timestamp);
    }

    public static IReadOnlyList<SessionEvent> ForHistory(
        IReadOnlyList<SessionEvent> history,
        DateTimeOffset? previousTimestamp = null,
        bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (!enabled)
        {
            return history.Any(IsSeparator)
                ? history.Where(item => !IsSeparator(item)).ToArray()
                : history;
        }

        var output = new List<SessionEvent>(history.Count);
        foreach (var sessionEvent in history)
        {
            if (Before(sessionEvent, previousTimestamp) is { } separator)
            {
                output.Add(separator);
            }
            if (IsVisible(sessionEvent, previousTimestamp))
            {
                output.Add(sessionEvent);
            }
            previousTimestamp = sessionEvent.Timestamp;
        }
        return output;
    }
}
