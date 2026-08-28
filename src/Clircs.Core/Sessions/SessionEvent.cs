using Clircs.Identity;

namespace Clircs.Sessions;

public enum SessionEventKind
{
    Status,
    Server,
    Message,
    Highlight,
    Notice,
    Action,
    Join,
    Part,
    Nick,
    Topic,
    ChannelInfo,
    ChannelSync,
    MessageGuard,
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
    IrcFormattedText? FormattedContent = null);
