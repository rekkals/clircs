namespace Clircs.Users;

public sealed record PolicyBan
{
    public PolicyBan(Guid id, string mask, IEnumerable<string> channels, string? reason = null, DateTimeOffset? createdAt = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A policy ban ID cannot be empty.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(mask);
        mask = mask.Trim();
        if (!mask.Contains('!') || !mask.Contains('@') || mask.IndexOfAny([' ', '\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("Policy bans must use a nick!user@host mask without whitespace.", nameof(mask));
        }

        var normalizedChannels = channels.Select(channel => channel.Trim())
            .Where(channel => channel.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedChannels.Length == 0 || normalizedChannels.Any(channel => channel.IndexOfAny([' ', '\r', '\n', '\0']) >= 0))
        {
            throw new ArgumentException("A policy ban requires one or more channel names (or *).", nameof(channels));
        }

        Id = id;
        Mask = mask;
        Channels = normalizedChannels;
        Reason = reason?.Trim() ?? string.Empty;
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
    }

    public Guid Id { get; }

    public string ShortId => Id.ToString("N")[..8];

    public string Mask { get; }

    public IReadOnlyList<string> Channels { get; }

    public string Reason { get; }

    public DateTimeOffset CreatedAt { get; }
}
