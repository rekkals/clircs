using Clircs.Identity;

namespace Clircs.State;

public enum BufferKind
{
    Status,
    Channel,
    Query,
    Results,
    Diagnostics,
    DccChat,
    DccTransfer
}

public sealed record BufferState(BufferId Id, NetworkSessionId NetworkSessionId, BufferKind Kind, string Name);
