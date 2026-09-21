using System.Globalization;

namespace Clircs.Protocol;

public static class IrcServerTime
{
    private const string TimestampFormat =
        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static bool TryParse(
        string? value,
        out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (value is null || value.Length != 24)
        {
            return false;
        }

        var leapSecond = value.AsSpan(17, 2).SequenceEqual("60");
        var normalized = leapSecond
            ? $"{value[..17]}59{value[19..]}"
            : value;

        if (!DateTimeOffset.TryParseExact(
                normalized,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal |
                DateTimeStyles.AdjustToUniversal,
                out timestamp))
        {
            return false;
        }

        if (leapSecond)
        {
            timestamp = timestamp.AddSeconds(1);
        }

        return true;
    }
}
