namespace Clircs.Sessions;

public enum SessionDisconnectKind
{
    Intentional,
    Accidental,
    Killed
}

public sealed record SessionDisconnectInfo(
    SessionDisconnectKind Kind,
    string Message,
    Exception? Exception = null,
    string? Actor = null,
    string? Reason = null,
    bool RetryRecommended = true,
    bool AnnounceToBuffers = true);
