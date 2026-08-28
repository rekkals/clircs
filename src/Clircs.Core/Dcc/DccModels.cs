using Clircs.Identity;

namespace Clircs.Dcc;

public enum DccRequestType
{
    Chat,
    Send
}

public enum DccRequestState
{
    Pending,
    Connecting,
    Connected,
    Rejected,
    Cancelled,
    Expired,
    Invalidated,
    Closed,
    Completed,
    Failed
}

public enum DccRequestDirection
{
    Incoming,
    Outgoing
}

public sealed record DccOffer(
    DccRequestType Type,
    string? Filename,
    string Address,
    int Port,
    long? Size,
    string? PassiveToken,
    string RawPayload,
    bool IsSecure = false)
{
    public bool HasPassiveToken => !string.IsNullOrWhiteSpace(PassiveToken);
    public bool IsPassiveRequest => Port == 0 && HasPassiveToken;
    public bool IsPassiveResponse => Port > 0 && HasPassiveToken;
    public bool IsPassive => IsPassiveRequest;
}

public sealed record DccRequest(
    int Id,
    NetworkSessionId NetworkSessionId,
    string Network,
    string Sender,
    DccOffer Offer,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DccRequestState State,
    string? StateReason = null,
    DccRequestDirection Direction = DccRequestDirection.Incoming);
