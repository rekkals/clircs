using System.Globalization;
using Clircs.Sessions;

namespace Clircs.ConsoleClient;

internal static class LagMeterFormatter
{
    private static readonly TimeSpan WarningThreshold = TimeSpan.FromSeconds(10);

    public static string Format(ConnectionLagSnapshot lag)
    {
        var elapsed = lag.RoundTripTime;
        if (lag.UnansweredFor is { } waiting &&
            (elapsed is null || waiting > elapsed.Value))
        {
            elapsed = waiting;
        }

        if (elapsed is not { } delay)
        {
            return "???";
        }

        if (delay >= WarningThreshold)
        {
            return "10+";
        }

        if (lag.RoundTripTime is null)
        {
            return "???";
        }

        var seconds = Math.Clamp(
            Math.Round(delay.TotalSeconds, 1, MidpointRounding.AwayFromZero),
            0.1,
            9.9);
        return seconds.ToString("0.0", CultureInfo.InvariantCulture);
    }
}
