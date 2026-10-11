using Clircs.Protocol;
using Clircs.State;

namespace Clircs.Sessions;

internal enum ChannelQueryKind
{
    Names,
    Topic,
    Mode
}

internal sealed class ChannelQueryRequestTracker(NetworkSessionState state)
{
    private readonly List<PendingRequest> _pending = [];

    private FollowUp? _followUp;

    public bool TryBegin(
        string channelName,
        ChannelQueryKind kind,
        out Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);

        if (FindIndex(channelName, kind) >= 0)
        {
            requestId = default;
            return false;
        }

        requestId = Guid.NewGuid();
        var namesState = kind == ChannelQueryKind.Names &&
                         !state.TryGetChannel(channelName, out _)
            ? new ChannelState(channelName, state.CaseMapping)
            : null;

        _pending.Add(new PendingRequest(requestId, channelName, kind, namesState));
        return true;
    }

    public ChannelState? FindNamesState(string channelName)
    {
        var index = FindIndex(channelName, ChannelQueryKind.Names);
        return index < 0 ? null : _pending[index].NamesState;
    }

    public Guid? Complete(string channelName, ChannelQueryKind kind)
    {
        var index = FindIndex(channelName, kind);
        if (index < 0)
        {
            return null;
        }

        var requestId = _pending[index].Id;
        _pending.RemoveAt(index);
        return requestId;
    }

    public Guid? CompleteWithFollowUp(
        string channelName,
        ChannelQueryKind kind,
        string command)
    {
        var requestId = Complete(channelName, kind);
        _followUp = requestId is { } id
            ? new FollowUp(id, channelName, command)
            : null;
        return requestId;
    }

    public Guid? TakeFollowUp(string command, string? channelName)
    {
        var followUp = _followUp;
        _followUp = null;

        if (followUp is null ||
            !command.Equals(
                followUp.Command,
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(channelName))
        {
            return null;
        }

        var comparer = new IrcNameComparer(state.CaseMapping);
        return comparer.Equals(followUp.ChannelName, channelName)
            ? followUp.Id
            : null;
    }

    public void Cancel(Guid requestId)
    {
        _pending.RemoveAll(request => request.Id == requestId);
        if (_followUp?.Id == requestId)
        {
            _followUp = null;
        }
    }

    public void Reset()
    {
        _pending.Clear();
        _followUp = null;
    }

    private int FindIndex(string channelName, ChannelQueryKind kind)
    {
        var comparer = new IrcNameComparer(state.CaseMapping);
        return _pending.FindIndex(request =>
            request.Kind == kind &&
            comparer.Equals(request.ChannelName, channelName));
    }

    private sealed record FollowUp(
        Guid Id,
        string ChannelName,
        string Command);

    private sealed record PendingRequest(
        Guid Id,
        string ChannelName,
        ChannelQueryKind Kind,
        ChannelState? NamesState);
}
