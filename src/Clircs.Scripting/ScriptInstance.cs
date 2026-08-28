using System.Text.Json;
using Clircs.Commands;
using Clircs.Sessions;
using Jint;
using Jint.Native;

namespace Clircs.Scripting;

internal sealed class ScriptInstance : IAsyncDisposable
{
    private const int MaximumCommands = 64;
    private const int MaximumEventHandlers = 256;
    private const int MaximumHeaders = 32;
    private const int MaximumPendingCallbacks = 512;
    private const int MaximumTimers = 256;
    private const int MaximumWebSockets = 16;
    private const string Bootstrap = """
        globalThis.clircs = Object.freeze({
          print: text => __clirc_print(String(text)),
          on: (eventName, handler) => __clirc_on(String(eventName), handler),
          registerCommand: (name, aliases, summary, handler) =>
            __clirc_register_command(String(name), Array.isArray(aliases) ? aliases.join(',') : String(aliases || ''), String(summary || ''), handler),
          run: commandLine => __clirc_run(String(commandLine)),
          setTimeout: (handler, milliseconds) => __clirc_set_timeout(handler, Number(milliseconds)),
          clearTimeout: timerId => __clirc_clear_timeout(Number(timerId)),
          permissions: Object.freeze({
            has: permission => __clirc_has_permission(String(permission))
          }),
          local: Object.freeze({
            websocket: Object.freeze({
              connect: (address, handler) => __clirc_websocket_connect(String(address), handler),
              send: (socketId, text) => __clirc_websocket_send(Number(socketId), String(text)),
              close: socketId => __clirc_websocket_close(Number(socketId))
            })
          }),
          ui: Object.freeze({
            setHeader: (id, text, options) =>
              __clirc_ui_set_header(String(id), String(text), JSON.stringify(options || {})),
            clearHeader: id => __clirc_ui_clear_header(String(id))
          }),
          secrets: Object.freeze({
            get: key => __clirc_secret_get(String(key)),
            set: (key, value) => __clirc_secret_set(String(key), String(value)),
            prompt: (key, label) => __clirc_secret_prompt(String(key), String(label || "Secret")),
            remove: key => __clirc_secret_remove(String(key))
          }),
          storage: Object.freeze({
            get: (key, fallbackValue) => __clirc_storage_get(String(key), fallbackValue === undefined ? null : String(fallbackValue)),
            set: (key, value) => __clirc_storage_set(String(key), String(value)),
            remove: key => __clirc_storage_remove(String(key))
          })
        });
        """;

    private readonly DiscoveredScript _script;
    private readonly HashSet<ScriptPermission> _requestedPermissions;
    private readonly HashSet<ScriptPermission> _permissions;
    private readonly ScriptHostServices _services;
    private readonly ScriptStorage _storage;
    private readonly ScriptSecretStorage _secrets;
    private readonly Action<ScriptError> _recordError;
    private readonly Engine _engine;
    private readonly SemaphoreSlim _execution = new(1, 1);
    private readonly Dictionary<string, List<JsValue>> _eventHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JsValue> _commandHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _commandRegistrations = [];
    private readonly Dictionary<int, Timer> _timers = [];
    private readonly Dictionary<int, ScriptLocalWebSocket> _webSockets = [];
    private readonly HashSet<string> _headerIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _resourceGate = new();
    private readonly object _eventQueueGate = new();
    private Task _eventTail = Task.CompletedTask;
    private CommandContext _currentContext = new(null, null);
    private int _nextTimerId;
    private int _nextWebSocketId;
    private int _eventHandlerCount;
    private int _pendingCallbacks;
    private int _consecutiveFailures;
    private volatile bool _disposed;
    private volatile bool _faulted;
    private bool _queueOverloadRecorded;

    public ScriptInstance(
        DiscoveredScript script,
        HashSet<ScriptPermission> requestedPermissions,
        HashSet<ScriptPermission> permissions,
        ScriptHostServices services,
        string storageDirectory,
        string secretDirectory,
        Action<ScriptError> recordError)
    {
        _script = script;
        _requestedPermissions = requestedPermissions;
        _permissions = permissions;
        _services = services;
        _storage = new ScriptStorage(storageDirectory, script.Manifest.Id);
        _secrets = new ScriptSecretStorage(secretDirectory, script.Manifest.Id);
        _recordError = recordError;
        _engine = new Engine(options =>
        {
            options.Strict();
            options.LimitRecursion(64);
            options.MaxStatements(25_000);
            options.TimeoutInterval(TimeSpan.FromMilliseconds(250));
            options.LimitMemory(16 * 1024 * 1024);
            options.Constraints.MaxArraySize = 100_000;
        });

        try
        {
            BindHostFunctions();
            _engine.Execute(Bootstrap, "clircs-bootstrap.js");
            var entryPath = ResolveEntryPath(script);
            _engine.Execute(File.ReadAllText(entryPath), entryPath);
        }
        catch
        {
            ReleaseOwnedResourcesAsync(waitForSockets: true).AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    public bool Faulted => _faulted;

    public string? LastError { get; private set; }

    public ScriptInfo Describe(bool sourceAvailable) => new(
        _script.Manifest.Id,
        _script.Manifest.Name,
        _script.Manifest.Version,
        Faulted ? "faulted" : sourceAvailable ? "loaded" : "loaded (source missing)",
        _requestedPermissions.Order().ToArray(),
        _permissions.Order().ToArray(),
        LastError);

    public void Publish(SessionEvent sessionEvent)
    {
        if (_disposed || Faulted || !_permissions.Contains(ScriptPermission.Events))
        {
            return;
        }

        QueueWork($"event {sessionEvent.Kind.ToString().ToLowerInvariant()}", () => InvokeEventAsync(sessionEvent));
    }

    public async ValueTask<CommandResult> InvokeCommandAsync(
        string commandName,
        CommandContext context,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (_disposed || Faulted)
        {
            return CommandResult.Failure($"Script '{_script.Manifest.Id}' is not running.");
        }

        await _execution.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_commandHandlers.TryGetValue(commandName, out var handler))
            {
                return CommandResult.Failure($"Script command '/{commandName}' is no longer registered.");
            }

            _currentContext = context;
            ResetConstraints();
            var payload = CreatePayload(new
            {
                name = commandName,
                args = arguments,
                networkId = context.NetworkSessionId?.Value.ToString(),
                bufferId = context.BufferId?.Value.ToString()
            });
            var result = _engine.Invoke(handler, payload);
            _consecutiveFailures = 0;
            if (result.IsUndefined() || result.IsNull())
            {
                return CommandResult.Success();
            }

            if (result.IsBoolean() && !result.AsBoolean())
            {
                return CommandResult.Failure($"Script command '/{commandName}' failed.");
            }

            return CommandResult.Success(result.ToString());
        }
        catch (Exception exception)
        {
            RecordFailure($"command /{commandName}", exception);
            return CommandResult.Failure($"Script '{_script.Manifest.Id}' failed: {exception.Message}");
        }
        finally
        {
            _currentContext = new CommandContext(null, null);
            _execution.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await ReleaseOwnedResourcesAsync(waitForSockets: true).ConfigureAwait(false);
        Task eventTail;
        lock (_eventQueueGate)
        {
            eventTail = _eventTail;
        }

        await eventTail.ConfigureAwait(false);
        await _execution.WaitAsync().ConfigureAwait(false);
        try
        {
            // Resource registration is rejected once disposal starts. Taking
            // the execution gate here guarantees no JavaScript is still using
            // the engine when disposal completes.
        }
        finally
        {
            _execution.Release();
        }
    }

    private static string ResolveEntryPath(DiscoveredScript script)
    {
        var directory = Path.GetFullPath(script.Directory) + Path.DirectorySeparatorChar;
        var entryPath = Path.GetFullPath(Path.Combine(directory, script.Manifest.Entry));
        if (!entryPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Script entry point must remain inside its script directory.");
        }

        var info = new FileInfo(entryPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Script entry point was not found.", entryPath);
        }

        if (info.Length > 256 * 1024)
        {
            throw new InvalidDataException("Script entry points cannot exceed 256 KB.");
        }

        return entryPath;
    }

    private void BindHostFunctions()
    {
        _engine.SetValue("__clirc_print", new Action<string>(Print));
        _engine.SetValue("__clirc_on", new Action<string, JsValue>(RegisterEvent));
        _engine.SetValue("__clirc_register_command", new Action<string, string, string, JsValue>(RegisterCommand));
        _engine.SetValue("__clirc_run", new Action<string>(QueueCommand));
        _engine.SetValue("__clirc_set_timeout", new Func<JsValue, double, int>(SetTimeout));
        _engine.SetValue("__clirc_clear_timeout", new Action<double>(ClearTimeout));
        _engine.SetValue("__clirc_storage_get", new Func<string, string?, string?>(StorageGet));
        _engine.SetValue("__clirc_storage_set", new Action<string, string>(StorageSet));
        _engine.SetValue("__clirc_storage_remove", new Func<string, bool>(StorageRemove));
        _engine.SetValue("__clirc_websocket_connect", new Func<string, JsValue, int>(WebSocketConnect));
        _engine.SetValue("__clirc_websocket_send", new Action<double, string>(WebSocketSend));
        _engine.SetValue("__clirc_websocket_close", new Action<double>(WebSocketClose));
        _engine.SetValue("__clirc_ui_set_header", new Action<string, string, string>(SetHeader));
        _engine.SetValue("__clirc_ui_clear_header", new Action<string>(ClearHeader));
        _engine.SetValue("__clirc_secret_get", new Func<string, string?>(SecretGet));
        _engine.SetValue("__clirc_secret_set", new Action<string, string>(SecretSet));
        _engine.SetValue("__clirc_secret_prompt", new Func<string, string, bool>(SecretPrompt));
        _engine.SetValue("__clirc_secret_remove", new Func<string, bool>(SecretRemove));
        _engine.SetValue("__clirc_has_permission", new Func<string, bool>(HasPermission));
    }

    private void Print(string text)
    {
        Demand(ScriptPermission.Output);
        if (text.Length > 4096)
        {
            throw new InvalidOperationException("Script output cannot exceed 4096 characters per call.");
        }

        _services.Print(_script.Manifest.Id, text);
    }

    private void RegisterEvent(string eventName, JsValue handler)
    {
        Demand(ScriptPermission.Events);
        if (!handler.IsCallable())
        {
            throw new InvalidOperationException("clircs.on requires a function.");
        }

        eventName = eventName.Trim().ToLowerInvariant();
        if (eventName != "*" && !Enum.TryParse<SessionEventKind>(eventName, true, out _))
        {
            throw new InvalidOperationException($"Unknown clircs event '{eventName}'.");
        }

        if (!_eventHandlers.TryGetValue(eventName, out var handlers))
        {
            handlers = [];
            _eventHandlers.Add(eventName, handlers);
        }

        if (_eventHandlerCount >= MaximumEventHandlers)
        {
            throw new InvalidOperationException($"Scripts cannot register more than {MaximumEventHandlers} event handlers.");
        }

        handlers.Add(handler);
        _eventHandlerCount++;
    }

    private void RegisterCommand(string name, string aliases, string summary, JsValue handler)
    {
        Demand(ScriptPermission.Commands);
        if (!handler.IsCallable())
        {
            throw new InvalidOperationException("clircs.registerCommand requires a function.");
        }

        name = CommandLineParser.NormalizeName(name);
        var parsedAliases = aliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CommandLineParser.NormalizeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (_commandHandlers.ContainsKey(name))
        {
            throw new InvalidOperationException($"Script command '/{name}' is already registered by this script.");
        }

        lock (_resourceGate)
        {
            EnsureRunning();
            if (_commandRegistrations.Count >= MaximumCommands)
            {
                throw new InvalidOperationException($"Scripts cannot register more than {MaximumCommands} commands.");
            }
            _commandHandlers.Add(name, handler);
        }
        try
        {
            var registration = new ScriptCommandRegistration(
                _script.Manifest.Id,
                name,
                parsedAliases,
                string.IsNullOrWhiteSpace(summary) ? $"Command supplied by {_script.Manifest.Name}." : summary,
                (context, arguments, cancellationToken) => InvokeCommandAsync(name, context, arguments, cancellationToken));
            var ownedRegistration = _services.RegisterCommand(registration);
            lock (_resourceGate)
            {
                if (_disposed || Faulted)
                {
                    ownedRegistration.Dispose();
                    throw new InvalidOperationException($"Script '{_script.Manifest.Id}' is not running.");
                }
                _commandRegistrations.Add(ownedRegistration);
            }
        }
        catch
        {
            lock (_resourceGate)
            {
                _commandHandlers.Remove(name);
            }
            throw;
        }
    }

    private void QueueCommand(string commandLine)
    {
        Demand(ScriptPermission.Irc);
        if (!commandLine.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("clircs.run accepts slash commands only.");
        }

        _services.QueueCommand(_script.Manifest.Id, _currentContext, commandLine);
    }

    private int SetTimeout(JsValue handler, double milliseconds)
    {
        Demand(ScriptPermission.Timers);
        if (!handler.IsCallable())
        {
            throw new InvalidOperationException("clircs.setTimeout requires a function.");
        }

        if (!double.IsFinite(milliseconds) || milliseconds is < 10 or > 86_400_000)
        {
            throw new InvalidOperationException("Timer delay must be between 10 milliseconds and 24 hours.");
        }

        var timerId = Interlocked.Increment(ref _nextTimerId);
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await InvokeTimerAsync(timerId, handler).ConfigureAwait(false);
                }
                finally
                {
                    timer?.Dispose();
                }
            });
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        lock (_resourceGate)
        {
            EnsureRunning();
            if (_timers.Count >= MaximumTimers)
            {
                timer.Dispose();
                throw new InvalidOperationException($"Scripts cannot have more than {MaximumTimers} active timers.");
            }
            _timers.Add(timerId, timer);
        }
        try
        {
            timer.Change(TimeSpan.FromMilliseconds(milliseconds), Timeout.InfiniteTimeSpan);
        }
        catch
        {
            lock (_resourceGate)
            {
                _timers.Remove(timerId);
            }
            timer.Dispose();
            throw;
        }
        return timerId;
    }

    private void ClearTimeout(double timerId)
    {
        Demand(ScriptPermission.Timers);
        var id = checked((int)timerId);
        Timer? timer;
        lock (_resourceGate)
        {
            _timers.Remove(id, out timer);
        }
        if (timer is not null)
        {
            timer.Dispose();
        }
    }

    private string? StorageGet(string key, string? fallback)
    {
        Demand(ScriptPermission.Storage);
        return _storage.Get(key, fallback);
    }

    private void StorageSet(string key, string value)
    {
        Demand(ScriptPermission.Storage);
        _storage.Set(key, value);
    }

    private bool StorageRemove(string key)
    {
        Demand(ScriptPermission.Storage);
        return _storage.Remove(key);
    }

    private int WebSocketConnect(string address, JsValue handler)
    {
        Demand(ScriptPermission.LocalNetwork);
        if (!handler.IsCallable())
        {
            throw new InvalidOperationException("clircs.local.websocket.connect requires an event handler.");
        }

        var id = Interlocked.Increment(ref _nextWebSocketId);
        var socket = new ScriptLocalWebSocket(
            socketEvent => QueueCallback($"local WebSocket {id}", handler, socketEvent));
        lock (_resourceGate)
        {
            EnsureRunning();
            if (_webSockets.Count >= MaximumWebSockets)
            {
                socket.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw new InvalidOperationException($"Scripts cannot have more than {MaximumWebSockets} local WebSockets open.");
            }
            _webSockets.Add(id, socket);
        }
        try
        {
            socket.Start(address);
            _ = RetireWebSocketWhenCompletedAsync(id, socket);
        }
        catch
        {
            lock (_resourceGate)
            {
                _webSockets.Remove(id);
            }
            socket.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
        return id;
    }

    private void WebSocketSend(double socketId, string text)
    {
        Demand(ScriptPermission.LocalNetwork);
        var id = checked((int)socketId);
        ScriptLocalWebSocket? socket;
        lock (_resourceGate)
        {
            _webSockets.TryGetValue(id, out socket);
        }
        if (socket is null)
        {
            throw new InvalidOperationException($"Local WebSocket {id} was not found.");
        }

        _ = socket.SendAsync(text).ContinueWith(
            task =>
            {
                if (task.Exception is { } exception)
                {
                    RecordExternalFailure($"local WebSocket {id} send", exception.GetBaseException());
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void WebSocketClose(double socketId)
    {
        Demand(ScriptPermission.LocalNetwork);
        var id = checked((int)socketId);
        ScriptLocalWebSocket? socket;
        lock (_resourceGate)
        {
            _webSockets.Remove(id, out socket);
        }
        if (socket is not null)
        {
            _ = socket.DisposeAsync().AsTask();
        }
    }

    private void SetHeader(string id, string text, string optionsJson)
    {
        Demand(ScriptPermission.Ui);
        id = NormalizeHeaderId(id);
        if (text.Length > 4096)
        {
            throw new InvalidOperationException("Script header text cannot exceed 4,096 characters.");
        }

        using var document = JsonDocument.Parse(optionsJson);
        var root = document.RootElement;
        var scope = root.TryGetProperty("scope", out var scopeElement)
            ? scopeElement.GetString()?.Trim().ToLowerInvariant()
            : null;
        string? bufferId;
        if (scope == "all")
        {
            bufferId = null;
        }
        else if (root.TryGetProperty("bufferId", out var bufferElement))
        {
            bufferId = bufferElement.GetString();
        }
        else
        {
            bufferId = _currentContext.BufferId?.Value.ToString();
        }

        var priority = root.TryGetProperty("priority", out var priorityElement)
            ? Math.Clamp(priorityElement.GetInt32(), -1000, 1000)
            : 0;
        var minimumWidth = root.TryGetProperty("minimumWidth", out var widthElement)
            ? Math.Clamp(widthElement.GetInt32(), 8, 200)
            : 24;
        var added = false;
        lock (_resourceGate)
        {
            EnsureRunning();
            if (!_headerIds.Contains(id) && _headerIds.Count >= MaximumHeaders)
            {
                throw new InvalidOperationException($"Scripts cannot contribute more than {MaximumHeaders} headers.");
            }
            added = _headerIds.Add(id);
        }
        try
        {
            _services.SetHeader(
                _script.Manifest.Id,
                new ScriptHeaderContribution(id, text, bufferId, priority, minimumWidth));
        }
        catch
        {
            if (added)
            {
                lock (_resourceGate)
                {
                    _headerIds.Remove(id);
                }
            }
            throw;
        }
    }

    private void ClearHeader(string id)
    {
        Demand(ScriptPermission.Ui);
        id = NormalizeHeaderId(id);
        lock (_resourceGate)
        {
            _headerIds.Remove(id);
        }
        _services.ClearHeader(_script.Manifest.Id, id);
    }

    private string? SecretGet(string key)
    {
        Demand(ScriptPermission.Secrets);
        return _secrets.Get(key);
    }

    private void SecretSet(string key, string value)
    {
        Demand(ScriptPermission.Secrets);
        _secrets.Set(key, value);
    }

    private bool SecretPrompt(string key, string label)
    {
        Demand(ScriptPermission.Secrets);
        var value = _services.ReadSecret(_script.Manifest.Id, label);
        // Terminal input is controlled by a human, not by JavaScript. Do not
        // charge time spent at the masked prompt against the script's 250 ms
        // execution budget.
        _engine.Constraints.Reset();
        if (value is null)
        {
            return false;
        }

        _secrets.Set(key, value);
        return true;
    }

    private bool SecretRemove(string key)
    {
        Demand(ScriptPermission.Secrets);
        return _secrets.Remove(key);
    }

    private bool HasPermission(string permission) =>
        Enum.TryParse<ScriptPermission>(permission, true, out var parsed) && _permissions.Contains(parsed);

    private static string NormalizeHeaderId(string id)
    {
        id = id.Trim().ToLowerInvariant();
        if (id.Length is < 1 or > 64 ||
            id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
        {
            throw new InvalidOperationException("Script header ids contain 1-64 letters, digits, dots, underscores, or hyphens.");
        }

        return id;
    }

    private void QueueCallback(string operation, JsValue handler, object payload)
    {
        if (_disposed || Faulted)
        {
            return;
        }

        QueueWork(operation, () => InvokeCallbackAsync(operation, handler, payload));
    }

    private void QueueWork(string operation, Func<Task> callback)
    {
        var overloaded = false;
        lock (_eventQueueGate)
        {
            if (_disposed || Faulted)
            {
                return;
            }

            if (_pendingCallbacks >= MaximumPendingCallbacks)
            {
                overloaded = true;
            }
            else
            {
                _pendingCallbacks++;
                _eventTail = _eventTail
                    .ContinueWith(
                        async _ =>
                        {
                            try
                            {
                                await callback().ConfigureAwait(false);
                            }
                            finally
                            {
                                lock (_eventQueueGate)
                                {
                                    _pendingCallbacks--;
                                }
                            }
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default)
                    .Unwrap();
            }
        }

        if (overloaded)
        {
            FaultForQueueOverload(operation);
        }
    }

    private async Task InvokeCallbackAsync(string operation, JsValue handler, object payload)
    {
        await _execution.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || Faulted)
            {
                return;
            }

            ResetConstraints();
            _engine.Invoke(handler, CreatePayload(payload));
            _consecutiveFailures = 0;
        }
        catch (Exception exception)
        {
            RecordFailure(operation, exception);
        }
        finally
        {
            _execution.Release();
        }
    }

    private void RecordExternalFailure(string operation, Exception exception)
    {
        LastError = exception.Message;
        _recordError(new ScriptError(DateTimeOffset.UtcNow, _script.Manifest.Id, operation, exception.Message));
    }

    private async Task InvokeEventAsync(SessionEvent sessionEvent)
    {
        var eventName = sessionEvent.Kind.ToString().ToLowerInvariant();
        var handlers = (_eventHandlers.TryGetValue(eventName, out var typed) ? typed : [])
            .Concat(_eventHandlers.TryGetValue("*", out var all) ? all : [])
            .ToArray();
        if (handlers.Length == 0)
        {
            return;
        }

        await _execution.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || Faulted)
            {
                return;
            }

            _currentContext = new CommandContext(sessionEvent.NetworkSessionId, sessionEvent.BufferId);
            foreach (var handler in handlers)
            {
                ResetConstraints();
                var payload = CreatePayload(new
                {
                    kind = eventName,
                    text = sessionEvent.Text,
                    networkId = sessionEvent.NetworkSessionId.Value.ToString(),
                    bufferId = sessionEvent.BufferId.Value.ToString(),
                    timestamp = sessionEvent.Timestamp.ToString("O"),
                    fields = sessionEvent.Fields
                });
                _engine.Invoke(handler, payload);
            }

            _consecutiveFailures = 0;
        }
        catch (Exception exception)
        {
            RecordFailure($"event {eventName}", exception);
        }
        finally
        {
            _currentContext = new CommandContext(null, null);
            _execution.Release();
        }
    }

    private async Task InvokeTimerAsync(int timerId, JsValue handler)
    {
        await _execution.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_resourceGate)
            {
                _timers.Remove(timerId);
            }
            if (_disposed || Faulted)
            {
                return;
            }

            ResetConstraints();
            _engine.Invoke(handler);
            _consecutiveFailures = 0;
        }
        catch (Exception exception)
        {
            RecordFailure($"timer {timerId}", exception);
        }
        finally
        {
            _execution.Release();
        }
    }

    private JsValue CreatePayload(object value)
    {
        _engine.SetValue("__clirc_payload_json", JsonSerializer.Serialize(value));
        return _engine.Evaluate("JSON.parse(__clirc_payload_json)");
    }

    private void ResetConstraints() => _engine.Constraints.Reset();

    private void Demand(ScriptPermission permission)
    {
        if (!_permissions.Contains(permission))
        {
            throw new UnauthorizedAccessException($"Script permission '{permission.ToString().ToLowerInvariant()}' is not granted.");
        }
    }

    private void RecordFailure(string operation, Exception exception)
    {
        LastError = exception.Message;
        _recordError(new ScriptError(DateTimeOffset.UtcNow, _script.Manifest.Id, operation, exception.Message));
        _consecutiveFailures++;
        var typeName = exception.GetType().Name;
        if (_consecutiveFailures >= 3 ||
            typeName.Contains("Memory", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("Statements", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("Recursion", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("Canceled", StringComparison.OrdinalIgnoreCase))
        {
            _faulted = true;
            _ = ReleaseOwnedResourcesAsync(waitForSockets: false);
        }
    }

    private void FaultForQueueOverload(string operation)
    {
        lock (_eventQueueGate)
        {
            if (_queueOverloadRecorded || _disposed || Faulted)
            {
                return;
            }
            _queueOverloadRecorded = true;
            _faulted = true;
        }

        const string message = "The script callback queue exceeded its 512-item limit.";
        LastError = message;
        _recordError(new ScriptError(DateTimeOffset.UtcNow, _script.Manifest.Id, operation, message));
        _ = ReleaseOwnedResourcesAsync(waitForSockets: false);
    }

    private async ValueTask ReleaseOwnedResourcesAsync(bool waitForSockets)
    {
        Timer[] timers;
        ScriptLocalWebSocket[] sockets;
        IDisposable[] registrations;
        lock (_resourceGate)
        {
            timers = _timers.Values.ToArray();
            _timers.Clear();
            sockets = _webSockets.Values.ToArray();
            _webSockets.Clear();
            registrations = _commandRegistrations.ToArray();
            _commandRegistrations.Clear();
            _commandHandlers.Clear();
            _headerIds.Clear();
        }

        foreach (var timer in timers)
        {
            timer.Dispose();
        }
        foreach (var registration in registrations)
        {
            registration.Dispose();
        }
        _services.ClearHeaders(_script.Manifest.Id);
        foreach (var socket in sockets)
        {
            var disposal = socket.DisposeAsync();
            if (waitForSockets)
            {
                await disposal.ConfigureAwait(false);
            }
        }
    }

    private async Task RetireWebSocketWhenCompletedAsync(int id, ScriptLocalWebSocket socket)
    {
        await socket.Completion.ConfigureAwait(false);
        var owned = false;
        lock (_resourceGate)
        {
            if (_webSockets.TryGetValue(id, out var current) && ReferenceEquals(current, socket))
            {
                _webSockets.Remove(id);
                owned = true;
            }
        }
        if (owned)
        {
            await socket.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void EnsureRunning()
    {
        if (_disposed || Faulted)
        {
            throw new InvalidOperationException($"Script '{_script.Manifest.Id}' is not running.");
        }
    }
}
