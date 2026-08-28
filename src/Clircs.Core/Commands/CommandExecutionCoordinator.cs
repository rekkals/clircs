namespace Clircs.Commands;

/// <summary>
/// Owns the single command execution lane and the context of the command
/// currently running in that lane.
/// </summary>
public sealed class CommandExecutionCoordinator
{
    private readonly CommandRegistry _registry;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly AsyncLocal<CommandContext?> _currentContext = new();

    public CommandExecutionCoordinator(CommandRegistry registry) =>
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public CommandContext? CurrentContext => _currentContext.Value;

    public async ValueTask<CommandResult> ExecuteAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);

        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var previousContext = _currentContext.Value;
        _currentContext.Value = context;
        try
        {
            return await _registry.ExecuteAsync(context, input, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _currentContext.Value = previousContext;
            _executionGate.Release();
        }
    }

    public async ValueTask<CommandResult> ExecuteAsync(
        CommandContext context,
        Func<CancellationToken, ValueTask<CommandResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);

        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var previousContext = _currentContext.Value;
        _currentContext.Value = context;
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _currentContext.Value = previousContext;
            _executionGate.Release();
        }
    }
}
