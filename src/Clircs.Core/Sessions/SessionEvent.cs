using Clircs.Identity;

namespace Clircs.Sessions;

public enum SessionEventKind
{
    Status,
    Server,
    Message,
    Notice,
    Action,
    Join,
    Part,
    Nick,
    Topic,
    ChannelInfo,
    ChannelSync,
    CallerId,
    Mode,
    Protection,
    Error,
    Diagnostic
}

public sealed record SessionEvent(
    NetworkSessionId NetworkSessionId,
    BufferId BufferId,
    SessionEventKind Kind,
    string Text,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, string?>? Fields = null,
    PresentationBlock? Presentation = null,
    IrcFormattedText? FormattedContent = null)
{
    public DateTimeOffset ReceivedAt { get; init; } = Timestamp;

    public bool IsHighlighted { get; init; }

    public IReadOnlyList<SessionEventChannel>? AffectedChannels { get; init; }

    public ScriptEventDelivery? ScriptDelivery { get; init; }
}

public sealed record SessionEventChannel(string Channel, BufferId BufferId);

public sealed record ScriptEventDelivery(SessionEvent? Observation)
{
    public static ScriptEventDelivery DisplayOnly { get; } = new(Observation: null);
}
