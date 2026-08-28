namespace Clircs.Networking;

public enum IrcConnectionState
{
    Disconnected,
    Connecting,
    Registering,
    Online,
    Disconnecting,
    Failed
}

public enum IrcOutboundPriority
{
    Critical,
    Interactive,
    Control,
    Automation,
    Bulk
}
