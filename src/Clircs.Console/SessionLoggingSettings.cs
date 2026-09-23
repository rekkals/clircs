using Clircs.Identity;

namespace Clircs.ConsoleClient;

internal sealed class SessionLoggingSettings
{
    private readonly object _gate = new();
    private readonly Dictionary<NetworkSessionId, HashSet<string>> _enabledTargets = [];

    public bool IsEnabled(NetworkSessionId sessionId, string target)
    {
        lock (_gate)
            return _enabledTargets.TryGetValue(sessionId, out var targets) &&
                targets.Contains(LoggingSettingsStore.NormalizeTarget(target));
    }

    public void Set(NetworkSessionId sessionId, string target, bool enabled)
    {
        lock (_gate)
        {
            if (!_enabledTargets.TryGetValue(sessionId, out var targets))
            {
                if (!enabled) return;
                targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _enabledTargets.Add(sessionId, targets);
            }

            target = LoggingSettingsStore.NormalizeTarget(target);
            if (enabled) targets.Add(target);
            else if (targets.Remove(target) && targets.Count == 0)
                _enabledTargets.Remove(sessionId);
        }
    }

    public void ClearSession(NetworkSessionId sessionId)
    {
        lock (_gate) _enabledTargets.Remove(sessionId);
    }
}
