namespace Clircs.Sessions;

public sealed record ChannelModeBatch(string ModeString, IReadOnlyList<string> Targets);

public static class ChannelModeBatcher
{
    public static IReadOnlyList<ChannelModeBatch> Create(
        char mode,
        bool adding,
        IEnumerable<string> targets,
        int modesPerCommand)
    {
        if (!char.IsAsciiLetter(mode))
        {
            throw new ArgumentException("A channel member mode must be one ASCII letter.", nameof(mode));
        }

        if (modesPerCommand < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(modesPerCommand));
        }

        ArgumentNullException.ThrowIfNull(targets);
        var normalized = targets
            .Select(target => target?.Trim() ?? string.Empty)
            .Where(target => target.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Any(target => target.IndexOfAny([' ', '\r', '\n', '\0']) >= 0))
        {
            throw new ArgumentException("Mode targets must be individual IRC nickname tokens.", nameof(targets));
        }

        return normalized
            .Chunk(modesPerCommand)
            .Select(chunk => new ChannelModeBatch(
                $"{(adding ? '+' : '-')}{new string(mode, chunk.Length)}",
                chunk))
            .ToArray();
    }
}
