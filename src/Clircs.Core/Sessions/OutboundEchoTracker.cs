using Clircs.Protocol;

namespace Clircs.Sessions;

internal sealed class OutboundEchoTracker
{
    private const int MaximumPending = 128;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly List<PendingEcho> _pending = [];
    private readonly TimeProvider _timeProvider;

    public OutboundEchoTracker(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Guid Track(string command, string target, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(text);
        var id = Guid.NewGuid();

        lock (_gate)
        {
            RemoveExpiredUnsafe();
            _pending.Add(new PendingEcho(
                id,
                command.ToUpperInvariant(),
                target,
                text,
                _timeProvider.GetUtcNow()));
            if (_pending.Count > MaximumPending)
            {
                _pending.RemoveRange(0, _pending.Count - MaximumPending);
            }
        }

        return id;
    }

    public void Cancel(Guid id)
    {
        lock (_gate)
        {
            _pending.RemoveAll(candidate => candidate.Id == id);
        }
    }

    public bool TryConsume(IrcMessage message, string currentNickname, IrcCaseMapping caseMapping)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentNickname);
        if (message.Parameters.Count < 2 || message.Prefix is null)
        {
            return false;
        }

        var comparer = new IrcNameComparer(caseMapping);
        if (!comparer.Equals(IrcSessionProcessor.NickFromPrefix(message.Prefix), currentNickname))
        {
            return false;
        }

        lock (_gate)
        {
            RemoveExpiredUnsafe();
            var index = _pending.FindIndex(candidate =>
                candidate.Command == message.Command &&
                comparer.Equals(candidate.Target, message.Parameters[0]) &&
                string.Equals(candidate.Text, message.Parameters[1], StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }

            _pending.RemoveAt(index);
            return true;
        }
    }

    private void RemoveExpiredUnsafe()
    {
        var cutoff = _timeProvider.GetUtcNow() - MaximumAge;
        _pending.RemoveAll(candidate => candidate.SentAt < cutoff);
    }

    private sealed record PendingEcho(
        Guid Id,
        string Command,
        string Target,
        string Text,
        DateTimeOffset SentAt);
}
