using System.Threading.Channels;

namespace Clircs.ConsoleClient;

// IRC sessions must be able to return to socket reads without waiting for terminal
// presentation. This pump preserves event order while moving application delivery to
// one owned consumer. Drain barriers let session shutdown finish already-accepted work
// before its buffers and routing state are removed.
internal sealed class InboundSessionEventPump<T> : IAsyncDisposable
{
    internal const int MaximumBatchSize = 128;
    internal const int MaximumPendingItems = 100_000;
    private readonly Channel<WorkItem> _queue;
    private readonly Action<IReadOnlyList<T>> _handler;
    private readonly Action<Exception> _failureHandler;
    private readonly object _completionGate = new();
    private readonly Task _worker;
    private bool _completed;

    public InboundSessionEventPump(
        Action<IReadOnlyList<T>> handler,
        Action<Exception> failureHandler,
        int maximumPendingItems = MaximumPendingItems)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(failureHandler);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPendingItems, 1);
        _handler = handler;
        _failureHandler = failureHandler;
        _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(maximumPendingItems)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _worker = Task.Run(DeliverAsync);
    }

    public ResourceQueueWriteResult Enqueue(T item)
    {
        lock (_completionGate)
        {
            if (_completed) return ResourceQueueWriteResult.Completed;
            return _queue.Writer.TryWrite(new Delivery(item))
                ? ResourceQueueWriteResult.Accepted
                : ResourceQueueWriteResult.CapacityExceeded;
        }
    }

    public Task DrainAsync()
    {
        Barrier barrier;
        lock (_completionGate)
        {
            if (_completed) return _worker;
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            barrier = new Barrier(reached);
        }
        return ReachBarrierAsync(barrier);
    }

    public async Task CompleteAsync()
    {
        lock (_completionGate)
        {
            if (!_completed)
            {
                _completed = true;
                _queue.Writer.TryComplete();
            }
        }
        await _worker.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await CompleteAsync().ConfigureAwait(false);

    private async Task ReachBarrierAsync(Barrier barrier)
    {
        try
        {
            // A drain request is lifecycle work, not inbound traffic. If the bounded
            // queue is full, wait for room instead of mistaking capacity pressure for
            // completion and leaving shutdown waiting on the lifetime worker.
            await _queue.Writer.WriteAsync(barrier).ConfigureAwait(false);
            await barrier.Reached.Task.ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            await _worker.ConfigureAwait(false);
        }
    }

    private async Task DeliverAsync()
    {
        var batch = new List<T>(MaximumBatchSize);
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (_queue.Reader.TryRead(out var work))
            {
                if (work is Barrier barrier)
                {
                    DeliverBatch(batch);
                    barrier.Reached.TrySetResult();
                    continue;
                }

                batch.Add(((Delivery)work).Item);
                if (batch.Count >= MaximumBatchSize)
                {
                    DeliverBatch(batch);
                }
            }
            DeliverBatch(batch);
        }
    }

    private void DeliverBatch(List<T> batch)
    {
        if (batch.Count == 0) return;
        var delivery = batch.ToArray();
        batch.Clear();
        try
        {
            _handler(delivery);
        }
        catch (Exception exception)
        {
            try
            {
                _failureHandler(exception);
            }
            catch
            {
                // Failure reporting must not terminate event delivery. The original
                // exception remains available to the reporter when it is invoked.
            }
        }
    }

    private abstract record WorkItem;
    private sealed record Delivery(T Item) : WorkItem;
    private sealed record Barrier(TaskCompletionSource Reached) : WorkItem;
}
