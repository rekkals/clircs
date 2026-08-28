namespace Clircs.State;

public sealed record ChannelListEntry(
    char Mode,
    string Mask,
    string? SetBy = null,
    DateTimeOffset? SetAt = null);
