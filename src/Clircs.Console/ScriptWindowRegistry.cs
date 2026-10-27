using Clircs.Identity;
using Clircs.State;

namespace Clircs.ConsoleClient;

/// Owns script-window identities and connection scopes.
/// Numbering, history, activity, and scrolling remain in WindowStateRegistry.

internal sealed class ScriptWindowRegistry
{
    public const int MaximumWindowsPerScript = 64;

    private readonly object _gate = new();
    private readonly NetworkSessionId _localSessionId = NetworkSessionId.New();
    private readonly Dictionary<BufferId, ScriptWindow> _windows = [];

    public BufferState Open(
        string scriptId,
        string name,
        NetworkSessionState? session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 64 || name.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character != '_' &&
                character != '-'))
        {
            throw new ArgumentException(
                "Script window names must contain 1-64 ASCII letters, digits, underscores, or hyphens.",
                nameof(name));
        }

        var sessionId = session?.Id;
        lock (_gate)
        {
            var existing = _windows.Values.FirstOrDefault(window =>
                window.SessionId == sessionId &&
                window.ScriptId.Equals(scriptId, StringComparison.OrdinalIgnoreCase) &&
                window.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing.Buffer;

            var count = _windows.Values.Count(window =>
                window.ScriptId.Equals(scriptId, StringComparison.OrdinalIgnoreCase));
            if (count >= MaximumWindowsPerScript)
            {
                throw new InvalidOperationException(
                    $"Script '{scriptId}' cannot have more than {MaximumWindowsPerScript} open windows.");
            }

            var displayName = $"={scriptId}.{name}";
            BufferState buffer;
            if (session is not null)
            {
                if (session.TryGetBuffer(displayName, out _))
                {
                    throw new InvalidOperationException(
                        $"Window '{displayName}' already exists.");
                }

                buffer = session.GetOrCreateBuffer(BufferKind.Script, displayName);
            }
            else
            {
                buffer = new BufferState(
                    BufferId.New(),
                    _localSessionId,
                    BufferKind.Script,
                    displayName);
            }

            _windows.Add(buffer.Id, new ScriptWindow(
                scriptId, name, sessionId, buffer));
            return buffer;
        }
    }

    public BufferState? Find(BufferId bufferId)
    {
        lock (_gate) return _windows.GetValueOrDefault(bufferId)?.Buffer;
    }

    public BufferState? FindLocal(BufferId bufferId)
    {
        lock (_gate)
        {
            var window = _windows.GetValueOrDefault(bufferId);
            return window is { SessionId: null } ? window.Buffer : null;
        }
    }

    public BufferState? Find(string name, NetworkSessionId? sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            return _windows.Values.FirstOrDefault(window =>
                window.SessionId == sessionId &&
                window.Buffer.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Buffer;
        }
    }

    public BufferState[] LocalBuffersSnapshot()
    {
        lock (_gate)
        {
            return _windows.Values
                .Where(window => window.SessionId is null)
                .Select(window => window.Buffer)
                .ToArray();
        }
    }

    public bool Remove(BufferId bufferId)
    {
        lock (_gate) return _windows.Remove(bufferId);
    }

    public void RemoveSession(NetworkSessionId sessionId)
    {
        lock (_gate)
        {
            var ids = _windows.Values
                .Where(window => window.SessionId == sessionId)
                .Select(window => window.Buffer.Id)
                .ToArray();
            foreach (var id in ids) _windows.Remove(id);
        }
    }

    private sealed record ScriptWindow(
        string ScriptId,
        string Name,
        NetworkSessionId? SessionId,
        BufferState Buffer);
}
