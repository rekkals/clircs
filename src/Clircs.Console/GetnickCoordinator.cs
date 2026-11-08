using Clircs.Protocol;
namespace Clircs.ConsoleClient;

internal readonly record struct GetnickSnapshot(
    string? Target,
    bool Paused,
    long Generation,
    string? WatchTarget);

internal sealed record GetnickClaim(
    string Nickname,
    long Generation,
    CancellationToken CancellationToken);

internal sealed record GetnickManualChange(string Nickname);

// Owns nickname-recovery state for one connection. Claims remain pending
// until the server confirms or rejects them.
internal sealed class GetnickCoordinator(IEqualityComparer<string> comparer)
{
    private readonly object _gate = new();
    private readonly List<GetnickManualChange> _manualChanges = [];
    private IEqualityComparer<string> _comparer = comparer;
    private string? _target;
    private string? _currentNickname;
    private int? _nicknameLengthLimit;
    private bool _paused;
    private long _generation;
    private long? _lastAttemptAt;
    private GetnickClaim? _claim;
    private CancellationTokenSource? _claimCancellation;

    private string? WatchTarget =>
        _target is not null &&
        _currentNickname is not null &&
        !_paused &&
        _manualChanges.Count == 0 &&
        !_comparer.Equals(_target, _currentNickname) &&
        (_nicknameLengthLimit is not { } limit ||
            IrcTextEncoding.Encode(_target).Length <= limit)
            ? _target
            : null;

    public GetnickSnapshot Snapshot()
    {
        lock (_gate)
            return new GetnickSnapshot(_target, _paused, _generation, WatchTarget);
    }

    public void Configure(
        string? target,
        string? currentNickname,
        int? nicknameLengthLimit = null)
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            _target = target;
            _currentNickname = currentNickname;
            _nicknameLengthLimit = nicknameLengthLimit;
            _paused = false;
            _generation++;
            _lastAttemptAt = null;
            _manualChanges.Clear();
            cancellation = ClearClaim();
        }
        Cancel(cancellation);
    }

    public bool UpdateNicknameLength(int? nicknameLengthLimit)
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_nicknameLengthLimit == nicknameLengthLimit) return false;

            _nicknameLengthLimit = nicknameLengthLimit;
            _generation++;
            cancellation = ClearClaim();
        }
        Cancel(cancellation);
        return true;
    }

    public GetnickManualChange? BeginManualChange(string nickname)
    {
        lock (_gate)
        {
            if (_target is null || _currentNickname is null ||
                !_comparer.Equals(_currentNickname, _target) ||
                _comparer.Equals(nickname, _target))
            {
                return null;
            }

            var change = new GetnickManualChange(nickname);
            _manualChanges.Add(change);
            return change;
        }
    }

    public void CancelManualChange(GetnickManualChange? change)
    {
        if (change is null) return;
        lock (_gate)
            _manualChanges.RemoveAll(pending => ReferenceEquals(pending, change));
    }

    public void NicknameChanged(string nickname, bool forced)
    {
        CancellationTokenSource? cancellation = null;
        lock (_gate)
        {
            _currentNickname = nickname;
            var index = _manualChanges.FindIndex(
                pending => _comparer.Equals(pending.Nickname, nickname));
            if (index >= 0)
            {
                _manualChanges.RemoveAt(index);
                if (!forced) _paused = true;
            }

            if (_target is not null && _comparer.Equals(_target, nickname))
            {
                _paused = false;
                _lastAttemptAt = null;
                cancellation = ClearClaim();
            }
            else if (_paused)
            {
                cancellation = ClearClaim();
            }
        }
        Cancel(cancellation);
    }

    public void NicknameRejected(string? nickname)
    {
        CancellationTokenSource? cancellation = null;
        lock (_gate)
        {
            var index = _manualChanges.FindIndex(
                pending => nickname is null ||
                    _comparer.Equals(pending.Nickname, nickname));
            if (index >= 0) _manualChanges.RemoveAt(index);

            if (_claim is not null &&
                (nickname is null || _comparer.Equals(_claim.Nickname, nickname)))
            {
                cancellation = ClearClaim();
            }
        }
        Cancel(cancellation);
    }

    // timestamp uses Environment.TickCount64. Failed claims follow the
    // existing 60-second availability refresh instead of retrying rapidly.
    public bool TryBeginClaim(
        string nickname,
        long generation,
        long timestamp,
        out GetnickClaim claim)
    {
        lock (_gate)
        {
            if (generation != _generation ||
                WatchTarget is not { } target ||
                !_comparer.Equals(target, nickname) ||
                _claim is not null ||
                (_lastAttemptAt is { } previous && timestamp - previous < 60_000))
            {
                claim = null!;
                return false;
            }

            _claimCancellation = new CancellationTokenSource();
            claim = new GetnickClaim(target, _generation, _claimCancellation.Token);
            _claim = claim;
            _lastAttemptAt = timestamp;
            return true;
        }
    }

    public bool IsCurrentClaim(GetnickClaim claim)
    {
        lock (_gate)
            return ReferenceEquals(_claim, claim) &&
                claim.Generation == _generation &&
                WatchTarget is { } target &&
                _comparer.Equals(target, claim.Nickname);
    }

    public void AbandonClaim(GetnickClaim claim)
    {
        CancellationTokenSource? cancellation = null;
        lock (_gate)
        {
            if (ReferenceEquals(_claim, claim))
                cancellation = ClearClaim();
        }
        Cancel(cancellation);
    }

    public void Reindex(IEqualityComparer<string> comparer)
    {
        lock (_gate) _comparer = comparer;
    }

    public void Reset() => Configure(null, null);

    private CancellationTokenSource? ClearClaim()
    {
        var cancellation = _claimCancellation;
        _claimCancellation = null;
        _claim = null;
        return cancellation;
    }

    private static void Cancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }
}
