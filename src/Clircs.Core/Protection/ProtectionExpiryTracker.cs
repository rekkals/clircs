using Clircs.Identity;

namespace Clircs.Protection;

public sealed class ProtectionExpiryTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<(NetworkSessionId Network, string Key), DateTimeOffset> _entries = [];

    public void Set(NetworkSessionId network, string key, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            _entries[(network, key)] = expiresAt;
        }
    }

    public bool Contains(NetworkSessionId network, string key, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            PruneExpiredUnsafe(now);
            if (!_entries.TryGetValue((network, key), out var expiresAt))
            {
                return false;
            }
            if (expiresAt > now)
            {
                return true;
            }
            _entries.Remove((network, key));
            return false;
        }
    }

    public bool TryReserve(NetworkSessionId network, string key, DateTimeOffset now, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (expiresAt <= now) throw new ArgumentOutOfRangeException(nameof(expiresAt));
        lock (_gate)
        {
            PruneExpiredUnsafe(now);
            if (_entries.TryGetValue((network, key), out var current) && current > now) return false;
            _entries[(network, key)] = expiresAt;
            return true;
        }
    }

    public void Clear(NetworkSessionId network)
    {
        lock (_gate)
        {
            foreach (var key in _entries.Keys.Where(entry => entry.Network == network).ToArray())
            {
                _entries.Remove(key);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private void PruneExpiredUnsafe(DateTimeOffset now)
    {
        foreach (var (key, expiresAt) in _entries.ToArray())
        {
            if (expiresAt <= now) _entries.Remove(key);
        }
    }
}
