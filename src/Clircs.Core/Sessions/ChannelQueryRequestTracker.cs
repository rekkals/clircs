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
    private readonly object _gate = new();
    private readonly List<PendingRequest> _pending = [];

    private FollowUp? _followUp;

    public bool TryBegin(
        string channelName,
        ChannelQueryKind kind,
        out Guid requestId,
        bool awaitWrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);

        lock (_gate)
        {
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

            _pending.Add(new PendingRequest(
                requestId, channelName, kind, namesState, AwaitingWrite: awaitWrite));
            return true;
        }
    }

    public void RecordModeWrite(string channelName, Guid requestId, bool automatic)
    {
        lock (_gate)
        {
            if (automatic)
            {
                _pending.Add(new PendingRequest(
                    requestId, channelName, ChannelQueryKind.Mode, null, Automatic: true));
                return;
            }

            var index = _pending.FindIndex(request =>
                request.Id == requestId && request.AwaitingWrite);
            if (index < 0) return;

            var request = _pending[index];
            _pending.RemoveAt(index);
            _pending.Add(request with { AwaitingWrite = false });
        }
    }

    public ChannelState? FindNamesState(string channelName)
    {
        lock (_gate)
        {
            var index = FindIndex(channelName, ChannelQueryKind.Names);
            return index < 0 ? null : _pending[index].NamesState;
        }
    }

    public Guid? Complete(string channelName, ChannelQueryKind kind) =>
        CompleteRequest(channelName, kind).RequestId;

    public Guid? CompleteWithFollowUp(
        string channelName,
        ChannelQueryKind kind,
        string command) =>
        CompleteRequest(channelName, kind, command).RequestId;

    public (Guid? RequestId, bool Automatic) CompleteRequest(
        string channelName,
        ChannelQueryKind kind,
        string? followUpCommand = null)
    {
        lock (_gate)
        {
            var index = FindIndex(channelName, kind, forReply: true);
            var request = index >= 0 ? _pending[index] : null;
            if (request is not null)
            {
                _pending.RemoveAt(index);
            }

            var automatic = request?.Automatic == true;
            var requestId = automatic ? null : request?.Id;
            if (followUpCommand is not null)
            {
                _followUp = requestId is { } id
                    ? new FollowUp(id, channelName, followUpCommand)
                    : null;
            }

            return (requestId, automatic);
        }
    }

    public Guid? TakeFollowUp(string command, string? channelName)
    {
        lock (_gate)
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
    }

    public void Cancel(Guid requestId)
    {
        lock (_gate)
        {
            _pending.RemoveAll(request => request.Id == requestId);
            if (_followUp?.Id == requestId)
            {
                _followUp = null;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _pending.Clear();
            _followUp = null;
        }
    }

    private int FindIndex(string channelName, ChannelQueryKind kind, bool forReply = false)
    {
        var comparer = new IrcNameComparer(state.CaseMapping);
        return _pending.FindIndex(request =>
            request.Kind == kind &&
            (forReply ? !request.AwaitingWrite : !request.Automatic) &&
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
        ChannelState? NamesState,
        bool AwaitingWrite = false,
        bool Automatic = false);
}
