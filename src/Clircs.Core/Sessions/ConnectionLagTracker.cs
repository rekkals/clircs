using System.Diagnostics;
using Clircs.Protocol;

namespace Clircs.Sessions;

internal sealed class ConnectionLagTracker
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private string? _pendingToken;
    private long _pendingStartedAt;
    private long? _lastProbeStartedAt;
    private TimeSpan? _roundTripTime;
    private TimeSpan? _unansweredFor;

    public string? TryBeginProbe(long timestamp)
    {
        lock (_gate)
        {
            if (_pendingToken is not null)
            {
                var elapsed = Elapsed(_pendingStartedAt, timestamp);
                if (elapsed < ProbeTimeout)
                {
                    return null;
                }

                // Retain the unanswered delay while a replacement probe runs.
                _unansweredFor = elapsed;
            }

            if (_lastProbeStartedAt is { } lastStartedAt &&
                Elapsed(lastStartedAt, timestamp) < ProbeInterval)
            {
                return null;
            }

            _pendingToken = $"clircs-lag-{Guid.NewGuid():N}";
            _pendingStartedAt = timestamp;
            _lastProbeStartedAt = timestamp;
            return _pendingToken;
        }
    }

    public bool TryAcceptPong(IrcMessage message, long timestamp)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_gate)
        {
            if (_pendingToken is null ||
                message.Command != "PONG" ||
                message.Parameters.Count == 0 ||
                !string.Equals(
                    message.Parameters[^1],
                    _pendingToken,
                    StringComparison.Ordinal))
            {
                return false;
            }

            _roundTripTime = Elapsed(_pendingStartedAt, timestamp);
            _pendingToken = null;
            _unansweredFor = null;
            return true;
        }
    }

    public void CancelProbe(string token)
    {
        lock (_gate)
        {
            if (string.Equals(_pendingToken, token, StringComparison.Ordinal))
            {
                _pendingToken = null;
            }
        }
    }

    public ConnectionLagSnapshot Snapshot(long timestamp)
    {
        lock (_gate)
        {
            var unansweredFor = _unansweredFor;
            if (_pendingToken is not null)
            {
                var elapsed = Elapsed(_pendingStartedAt, timestamp);
                if (unansweredFor is null || elapsed > unansweredFor.Value)
                {
                    unansweredFor = elapsed;
                }
            }

            return new ConnectionLagSnapshot(_roundTripTime, unansweredFor);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _pendingToken = null;
            _pendingStartedAt = 0;
            _lastProbeStartedAt = null;
            _roundTripTime = null;
            _unansweredFor = null;
        }
    }

    private static TimeSpan Elapsed(long startedAt, long timestamp) =>
        timestamp <= startedAt
            ? TimeSpan.Zero
            : Stopwatch.GetElapsedTime(startedAt, timestamp);
}

public readonly record struct ConnectionLagSnapshot(
    TimeSpan? RoundTripTime,
    TimeSpan? UnansweredFor);
