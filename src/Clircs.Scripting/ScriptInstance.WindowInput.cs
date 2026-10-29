using Clircs.Commands;
using Clircs.Identity;
using Jint;
using Jint.Native;

namespace Clircs.Scripting;

internal sealed partial class ScriptInstance
{
    private const int MaximumWindowInputHandlers = 64;
    private readonly Dictionary<BufferId, WindowInputBinding> _windowInputs = [];

    private void WindowOnInput(JsValue windowId, JsValue handler)
    {
        Demand(ScriptPermission.Commands);
        Demand(ScriptPermission.Output);

        if (!windowId.IsString() ||
            !Guid.TryParse(windowId.AsString(), out var id) ||
            id == Guid.Empty)
        {
            throw new ArgumentException(
                "windowId must be a valid window ID returned by clircs.");
        }

        if (!handler.IsNull() && !handler.IsCallable())
        {
            throw new ArgumentException(
                "clircs.windows.onInput requires a function or null.");
        }

        var bufferId = new BufferId(id);
        lock (_resourceGate)
        {
            EnsureRunning();
            PruneWindowInputsUnsafe();

            if (!handler.IsNull() &&
                !_windowInputs.ContainsKey(bufferId) &&
                _windowInputs.Count >= MaximumWindowInputHandlers)
            {
                throw new InvalidOperationException(
                    $"Scripts cannot register more than {MaximumWindowInputHandlers} window input handlers.");
            }
        }

        if (handler.IsNull())
        {
            _services.Windows.OnInput(_script.Manifest.Id, bufferId, null);
            WindowInputBinding? previous;
            lock (_resourceGate)
            {
                _windowInputs.Remove(bufferId, out previous);
            }
            previous?.Registration?.Dispose();
            return;
        }

        var binding = new WindowInputBinding(handler);
        var registration = _services.Windows.OnInput(
            _script.Manifest.Id,
            bufferId,
            (context, text, cancellationToken) =>
                InvokeWindowInputAsync(
                    bufferId, binding, context, text, cancellationToken))
            ?? throw new InvalidOperationException(
                "The window host did not register the input handler.");
        binding.Registration = registration;

        WindowInputBinding? replaced;
        try
        {
            lock (_resourceGate)
            {
                EnsureRunning();
                _windowInputs.TryGetValue(bufferId, out replaced);
                _windowInputs[bufferId] = binding;
            }
        }
        catch
        {
            registration.Dispose();
            throw;
        }

        replaced?.Registration?.Dispose();
    }

    private async ValueTask<CommandResult> InvokeWindowInputAsync(
        BufferId bufferId,
        WindowInputBinding binding,
        CommandContext context,
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(text);

        if (_disposed || Faulted)
        {
            return CommandResult.Failure(
                $"Script '{_script.Manifest.Id}' is not running.");
        }

        await _execution.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (context.BufferId != bufferId)
            {
                return CommandResult.Failure(
                    "The script window input context does not match its window.");
            }

            lock (_resourceGate)
            {
                if (_disposed || Faulted ||
                    !_windowInputs.TryGetValue(bufferId, out var current) ||
                    !ReferenceEquals(current, binding) ||
                    binding.Registration is not { IsActive: true })
                {
                    return CommandResult.Failure(
                        "This script window no longer has an active input handler.");
                }
            }

            _currentContext = context;
            ResetConstraints();
            var payload = CreatePayload(new
            {
                text,
                networkId = context.NetworkSessionId?.Value.ToString(),
                bufferId = bufferId.Value.ToString()
            });
            var result = _engine.Invoke(binding.Handler, payload);
            _consecutiveFailures = 0;

            if (result.IsUndefined() || result.IsNull())
            {
                return CommandResult.Success();
            }

            if (result.IsBoolean() && !result.AsBoolean())
            {
                return CommandResult.Failure(
                    $"Script '{_script.Manifest.Id}' window input handler failed.");
            }

            return CommandResult.Success(result.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RecordFailure($"window input {bufferId.Value}", exception);
            return CommandResult.Failure(
                $"Script '{_script.Manifest.Id}' failed: {exception.Message}");
        }
        finally
        {
            _currentContext = new CommandContext(null, null);
            _execution.Release();
        }
    }

    private void PruneWindowInputsUnsafe()
    {
        var inactive = _windowInputs
            .Where(pair => pair.Value.Registration?.IsActive != true)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var bufferId in inactive)
        {
            if (_windowInputs.Remove(bufferId, out var binding))
            {
                binding.Registration?.Dispose();
            }
        }
    }

    private void ReleaseWindowInputs()
    {
        WindowInputBinding[] bindings;
        lock (_resourceGate)
        {
            bindings = _windowInputs.Values.ToArray();
            _windowInputs.Clear();
        }

        foreach (var binding in bindings)
        {
            binding.Registration?.Dispose();
        }
    }

    private sealed class WindowInputBinding(JsValue handler)
    {
        public JsValue Handler { get; } = handler;

        public IScriptWindowInputRegistration? Registration { get; set; }
    }
}
