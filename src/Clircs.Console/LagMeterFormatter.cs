using System.Globalization;
using Clircs.Sessions;

namespace Clircs.ConsoleClient;

internal enum LagMeterSeverity
{
    Normal,
    Warning,
    Severe
}

internal readonly record struct LagMeterDisplay(
    string Text,
    LagMeterSeverity Severity);

internal static class LagMeterFormatter
{
    private static readonly TimeSpan WarningThreshold = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SevereThreshold = TimeSpan.FromSeconds(10);

    public static LagMeterDisplay Format(ConnectionLagSnapshot lag)
    {
        var elapsed = lag.RoundTripTime;
        if (lag.UnansweredFor is { } waiting &&
            (elapsed is null || waiting > elapsed.Value))
        {
            elapsed = waiting;
        }

        if (elapsed is not { } delay)
        {
            return new LagMeterDisplay("???", LagMeterSeverity.Normal);
        }

        if (delay >= SevereThreshold)
        {
            return new LagMeterDisplay("10+", LagMeterSeverity.Severe);
        }

        var severity = delay >= WarningThreshold
            ? LagMeterSeverity.Warning
            : LagMeterSeverity.Normal;

        if (lag.RoundTripTime is null)
        {
            return new LagMeterDisplay("???", severity);
        }

        if (delay < TimeSpan.FromMilliseconds(100))
        {
            return new LagMeterDisplay("<.1", severity);
        }

        var seconds = Math.Clamp(
            Math.Round(delay.TotalSeconds, 1, MidpointRounding.AwayFromZero),
            0.1,
            9.9);

        return new LagMeterDisplay(
            seconds.ToString("0.0", CultureInfo.InvariantCulture),
            severity);
    }
}
