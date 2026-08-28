namespace Clircs.Protection;

public sealed class ProtectionMonitor
{
    private readonly object _gate = new();
    private readonly Dictionary<CounterKey, Queue<DateTimeOffset>> _windows = [];
    private readonly Dictionary<CounterKey, DateTimeOffset> _cooldowns = [];
    private readonly Dictionary<CounterKey, int> _windowSeconds = [];
    private DateTimeOffset _nextPruneAt = DateTimeOffset.MinValue;

    public ProtectionDetection? Evaluate(ProtectionEvidence evidence, ProtectionRule rule)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(rule);
        rule.Validate();
        if (!rule.Enabled || evidence.Weight < 1) return null;

        var discriminator = evidence.Detector == ProtectionDetector.Repeat
            ? NormalizeRepeatText(evidence.Text)
            : string.Empty;
        if (evidence.Detector == ProtectionDetector.Repeat && discriminator.Length == 0) return null;
        var key = new CounterKey(
            evidence.NetworkSessionId,
            evidence.Detector,
            evidence.Channel ?? string.Empty,
            evidence.Actor,
            discriminator);
        lock (_gate)
        {
            if (evidence.Timestamp >= _nextPruneAt)
            {
                PruneExpiredUnsafe(evidence.Timestamp);
                _nextPruneAt = evidence.Timestamp.AddSeconds(1);
            }
            var cutoff = evidence.Timestamp.AddSeconds(-rule.WindowSeconds);
            if (!_windows.TryGetValue(key, out var window))
            {
                window = new Queue<DateTimeOffset>();
                _windows.Add(key, window);
            }
            _windowSeconds[key] = rule.WindowSeconds;
            while (window.Count > 0 && window.Peek() < cutoff) window.Dequeue();
            for (var index = 0; index < evidence.Weight; index++) window.Enqueue(evidence.Timestamp);

            if (window.Count < rule.Threshold) return null;
            if (_cooldowns.TryGetValue(key, out var cooldown) && cooldown > evidence.Timestamp) return null;
            _cooldowns[key] = evidence.Timestamp.AddSeconds(rule.WindowSeconds);
            return new ProtectionDetection(
                evidence,
                window.Count,
                rule,
                evidence.Timestamp >= window.Peek() ? evidence.Timestamp - window.Peek() : TimeSpan.Zero);
        }
    }

    public IReadOnlyList<ProtectionCounter> Counters(DateTimeOffset now)
    {
        lock (_gate)
        {
            PruneExpiredUnsafe(now);
            var counters = new List<ProtectionCounter>();
            foreach (var (key, window) in _windows)
            {
                var seconds = _windowSeconds.GetValueOrDefault(key, 3600);
                counters.Add(new ProtectionCounter(
                    key.Detector,
                    key.Actor,
                    key.Channel.Length == 0 ? null : key.Channel,
                    window.Count,
                    window.Last().AddSeconds(seconds)));
            }
            return counters
                .OrderBy(counter => counter.Detector)
                .ThenBy(counter => counter.Channel)
                .ThenBy(counter => counter.Actor)
                .ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _windows.Clear();
            _cooldowns.Clear();
            _windowSeconds.Clear();
            _nextPruneAt = DateTimeOffset.MinValue;
        }
    }

    public void Clear(Clircs.Identity.NetworkSessionId network)
    {
        lock (_gate)
        {
            foreach (var key in _windows.Keys.Where(key => key.Network == network).ToArray())
            {
                RemoveUnsafe(key);
            }
        }
    }

    private void PruneExpiredUnsafe(DateTimeOffset now)
    {
        foreach (var (key, window) in _windows.ToArray())
        {
            var seconds = _windowSeconds.GetValueOrDefault(key, 3600);
            while (window.Count > 0 && window.Peek().AddSeconds(seconds) < now) window.Dequeue();
            if (window.Count == 0) RemoveUnsafe(key);
        }
        foreach (var (key, expiresAt) in _cooldowns.ToArray())
        {
            if (expiresAt <= now && !_windows.ContainsKey(key)) _cooldowns.Remove(key);
        }
    }

    private void RemoveUnsafe(CounterKey key)
    {
        _windows.Remove(key);
        _cooldowns.Remove(key);
        _windowSeconds.Remove(key);
    }

    private static string NormalizeRepeatText(string? text) => string.IsNullOrWhiteSpace(text)
        ? string.Empty
        : string.Join(' ', text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private readonly record struct CounterKey(
        Clircs.Identity.NetworkSessionId Network,
        ProtectionDetector Detector,
        string Channel,
        string Actor,
        string Discriminator);
}
