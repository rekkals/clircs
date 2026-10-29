using Clircs.Commands;
using Clircs.Identity;
using Clircs.Scripting;
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

    public ScriptWindowInfo[] ListOwned(string scriptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        lock (_gate)
        {
            return _windows.Values
                .Where(window => window.ScriptId.Equals(
                    scriptId, StringComparison.OrdinalIgnoreCase))
                .Select(window => new ScriptWindowInfo(
                    window.Buffer.Id,
                    window.Name,
                    window.SessionId))
                .ToArray();
        }
    }

    public IScriptWindowInputRegistration? SetInputHandler(
        string scriptId,
        BufferId bufferId,
        ScriptWindowInputHandler? handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        lock (_gate)
        {
            if (!_windows.TryGetValue(bufferId, out var window))
            {
                throw new InvalidOperationException(
                    "The requested script window no longer exists.");
            }

            if (!window.ScriptId.Equals(scriptId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Scripts can handle input only in windows they own.");
            }

            var registration = handler is null
                ? null
                : new InputRegistration(
                    this,
                    bufferId,
                    new CommandContext(window.SessionId, bufferId),
                    handler);

            window.Input?.Deactivate();
            window.Input = registration;
            return registration;
        }
    }

    public ScriptWindowInputHandler? FindInputHandler(CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate)
        {
            if (context.BufferId is not { } bufferId ||
                !_windows.TryGetValue(bufferId, out var window) ||
                window.SessionId != context.NetworkSessionId ||
                window.Input is not { IsActive: true } registration)
            {
                return null;
            }

            return registration.InvokeAsync;
        }
    }

    public bool Remove(BufferId bufferId)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(bufferId, out var window)) return false;
            window.Input?.Deactivate();
            return _windows.Remove(bufferId);
        }
    }

    public void RemoveSession(NetworkSessionId sessionId)
    {
        lock (_gate)
        {
            var ids = _windows.Values
                .Where(window => window.SessionId == sessionId)
                .Select(window => window.Buffer.Id)
                .ToArray();
            foreach (var id in ids)
            {
                _windows[id].Input?.Deactivate();
                _windows.Remove(id);
            }
        }
    }

    private void RemoveInputRegistration(
        BufferId bufferId,
        InputRegistration registration)
    {
        lock (_gate)
        {
            if (_windows.TryGetValue(bufferId, out var window) &&
                ReferenceEquals(window.Input, registration))
            {
                window.Input = null;
            }
        }
    }

    private sealed record ScriptWindow(
        string ScriptId,
        string Name,
        NetworkSessionId? SessionId,
        BufferState Buffer)
    {
        public InputRegistration? Input { get; set; }
    }

    private sealed class InputRegistration(
        ScriptWindowRegistry owner,
        BufferId bufferId,
        CommandContext context,
        ScriptWindowInputHandler handler) : IScriptWindowInputRegistration
    {
        private ScriptWindowRegistry? _owner = owner;
        private ScriptWindowInputHandler? _handler = handler;

        public bool IsActive => Volatile.Read(ref _handler) is not null;

        public ValueTask<CommandResult> InvokeAsync(
            CommandContext requestedContext,
            string text,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(requestedContext);
            ArgumentNullException.ThrowIfNull(text);
            cancellationToken.ThrowIfCancellationRequested();

            var currentHandler = Volatile.Read(ref _handler);
            if (currentHandler is null)
            {
                return ValueTask.FromResult(CommandResult.Failure(
                    "This script window no longer has an active input handler."));
            }

            if (requestedContext != context)
            {
                return ValueTask.FromResult(CommandResult.Failure(
                    "The script window input context does not match its window."));
            }

            return currentHandler(context, text, cancellationToken);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _handler, null);
            Interlocked.Exchange(ref _owner, null)?
                .RemoveInputRegistration(bufferId, this);
        }

        public void Deactivate()
        {
            Interlocked.Exchange(ref _handler, null);
            Interlocked.Exchange(ref _owner, null);
        }
    }
}
