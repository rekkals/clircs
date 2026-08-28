namespace Clircs.ConsoleClient;

// Owns fire-and-forget work associated with one application lifetime. Session
// runtimes use one tracker each, and the application shell has another for
// work that is not tied to a connected network.
internal sealed class SessionWorkTracker : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<Task> _tasks = [];
    private readonly TaskCompletionSource _drained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime;
    private bool _stopping;

    public SessionWorkTracker(CancellationToken applicationToken = default) =>
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);

    public CancellationToken Token => _lifetime.Token;

    public bool TryStart(
        string operation,
        Func<Task> start,
        Action<string, Exception> reportFailure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(reportFailure);

        var reservation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_stopping) return false;
            _tasks.Add(reservation.Task);
        }

        // A Func<Task> begins executing synchronously when invoked. Starting it
        // on the caller here allowed DNS and socket setup to borrow the input
        // thread until their first incomplete await. Schedule the whole
        // operation so accepted background work is background work from its
        // first instruction.
        var task = Task.Run(async () =>
        {
            var started = start() ?? throw new InvalidOperationException("Session work returned no task.");
            await started.ConfigureAwait(false);
        });

        _ = ObserveAsync(task, reservation.Task, operation, reportFailure);
        return true;
    }

    public Task StopAndWaitAsync()
    {
        Task drained;
        lock (_gate)
        {
            _stopping = true;
            if (_tasks.Count == 0) _drained.TrySetResult();
            drained = _drained.Task;
        }
        _lifetime.Cancel();
        return drained;
    }

    public void Dispose() => _lifetime.Dispose();

    private async Task ObserveAsync(
        Task task,
        Task reservation,
        string operation,
        Action<string, Exception> reportFailure)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Cancellation is an ordinary part of session shutdown.
        }
        catch (Exception exception)
        {
            reportFailure(operation, exception);
        }
        finally
        {
            Retire(reservation);
        }
    }

    private void Retire(Task reservation)
    {
        lock (_gate)
        {
            _tasks.Remove(reservation);
            if (_stopping && _tasks.Count == 0) _drained.TrySetResult();
        }
    }
}
