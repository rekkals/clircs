namespace Clircs.Networking;

public enum IrcWireDirection
{
    Received,
    Sent
}

public sealed record IrcWireLine(
    IrcWireDirection Direction,
    string Line,
    DateTimeOffset Timestamp);
