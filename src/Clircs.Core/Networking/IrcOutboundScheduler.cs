using Clircs.Identity;
using System.Diagnostics;

namespace Clircs.Networking;

internal sealed class IrcOutboundScheduler : IAsyncDisposable
{
    internal const int DefaultQueueLimit = 512;
    internal const int DefaultCriticalQueueLimit = 32;
    private const int DefaultBurstTokens = 4;
    private static readonly TimeSpan DefaultTokenInterval = TimeSpan.FromMilliseconds(750);

    private readonly IIrcTransport _transport;
    private readonly Func<bool>? _floodControlEnabled;
    private readonly List<OutboundItem>[] _queues;
    private readonly object _lifecycleGate = new();
    private readonly SemaphoreSlim _available = new(0, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _writerTask;
    private readonly int _queueLimit;
    private readonly int _criticalQueueLimit;
    private readonly int _burstTokens;
    private readonly TimeSpan _tokenInterval;
    private double _availableTokens;
    private long _lastRefillTimestamp;
    private BufferId? _lastChatBufferId;
    private bool _accepting = true;
    private int _disposed;

    public IrcOutboundScheduler(
        IIrcTransport transport,
        int queueLimit = DefaultQueueLimit,
        int criticalQueueLimit = DefaultCriticalQueueLimit,
        int burstTokens = DefaultBurstTokens,
        TimeSpan? tokenInterval = null,
        Func<bool>? floodControlEnabled = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(criticalQueueLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(burstTokens);
        _transport = transport;
        _queueLimit = queueLimit;
        _criticalQueueLimit = criticalQueueLimit;
        _burstTokens = burstTokens;
        _tokenInterval = tokenInterval ?? DefaultTokenInterval;
        if (_tokenInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(tokenInterval));
        _availableTokens = burstTokens;
        _lastRefillTimestamp = Stopwatch.GetTimestamp();
        _queues = Enumerable.Range(0, Enum.GetValues<IrcOutboundPriority>().Length)
            .Select(_ => new List<OutboundItem>())
            .ToArray();
        _floodControlEnabled = floodControlEnabled;
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public async ValueTask EnqueueAsync(
        ReadOnlyMemory<byte> bytes,
        IrcOutboundPriority priority,
        CancellationToken cancellationToken,
        bool cancelBeforeSend = false,
        Action? beforeWrite = null,
        Action? afterWrite = null)
    {
        var completion = Enqueue(bytes, priority, cancellationToken, cancelBeforeSend, beforeWrite, afterWrite);
        var wait = cancelBeforeSend
            ? completion
            : completion.WaitAsync(cancellationToken);
        await wait.ConfigureAwait(false);
    }

    internal Task Enqueue(
        ReadOnlyMemory<byte> bytes,
        IrcOutboundPriority priority,
        CancellationToken cancellationToken,
        bool cancelBeforeSend = false,
        Action? beforeWrite = null,
        Action? afterWrite = null,
        BufferId? chatBufferId = null)
    {
        if (cancelBeforeSend) cancellationToken.ThrowIfCancellationRequested();

        var item = new OutboundItem(bytes.ToArray())
        {
            Priority = priority,
            BeforeSendCancellation = cancelBeforeSend ? cancellationToken : default,
            BeforeWrite = beforeWrite,
            AfterWrite = afterWrite,
            ChatBufferId = chatBufferId
        };
        lock (_lifecycleGate)
        {
            if (!_accepting)
            {
                throw new InvalidOperationException("The IRC outbound scheduler is stopped.");
            }

            var queue = _queues[(int)priority];
            if (priority == IrcOutboundPriority.Critical)
            {
                if (queue.Count >= _criticalQueueLimit)
                {
                    throw new InvalidOperationException("The critical IRC send queue is full");
                }
            }
            else
            {
                var pending = _queues.Sum(candidate => candidate.Count) -
                    _queues[(int)IrcOutboundPriority.Critical].Count;
                if (pending >= _queueLimit)
                {
                    throw new InvalidOperationException("The IRC send queue is full; excess output was not sent");
                }
            }

            queue.Add(item);
            WakeWriter();
        }

        return item.Completion.Task;
    }

    internal bool CancelQueuedChat(BufferId bufferId)
    {
        lock (_lifecycleGate)
        {
            if (!_accepting)
            {
                return false;
            }

            var queue = _queues[(int)IrcOutboundPriority.Interactive];
            var canceled = false;
            for (var index = queue.Count - 1; index >= 0; index--)
            {
                var item = queue[index];
                if (item.ChatBufferId != bufferId)
                {
                    continue;
                }

                queue.RemoveAt(index);
                item.Completion.TrySetCanceled();
                canceled = true;
            }

            if (canceled)
            {
                WakeWriter();
            }

            return canceled;
        }
    }

    internal void FloodControlChanged()
    {
        lock (_lifecycleGate)
        {
            if (_accepting)
            {
                WakeWriter();
            }
        }
    }

    // Call while holding _lifecycleGate.
    private void WakeWriter()
    {
        if (_available.CurrentCount == 0)
        {
            _available.Release();
        }
    }

    internal void StopWriting()
    {
        lock (_lifecycleGate)
        {
            _accepting = false;
        }

        _stopping.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopWriting();
        try
        {
            await _writerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        FailPending(new OperationCanceledException("The IRC outbound scheduler stopped."));
        _available.Dispose();
        _stopping.Dispose();
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            while (true)
            {
                var (item, floodControlEnabled) = await TakeNextAsync().ConfigureAwait(false);

                var writeStarted = false;
                try
                {
                    item.BeforeSendCancellation.ThrowIfCancellationRequested();
                    if (!floodControlEnabled && item.Priority is not (IrcOutboundPriority.Critical or IrcOutboundPriority.Interactive))
                    {
                        using var budgetCancellation = item.BeforeSendCancellation.CanBeCanceled
                            ? CancellationTokenSource.CreateLinkedTokenSource(
                                _stopping.Token, item.BeforeSendCancellation)
                            : null;

                        await WaitForSendBudgetAsync(
                            item.Bytes.Length,
                            budgetCancellation?.Token ?? _stopping.Token).ConfigureAwait(false);
                    }

                    item.BeforeSendCancellation.ThrowIfCancellationRequested();

                    // Once writing starts, finish the line even if its request is canceled.
                    writeStarted = true;
                    item.BeforeWrite?.Invoke();
                    await _transport.WriteAsync(item.Bytes, _stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    !writeStarted &&
                    item.BeforeSendCancellation.IsCancellationRequested &&
                    !_stopping.IsCancellationRequested)
                {
                    item.Completion.TrySetCanceled(item.BeforeSendCancellation);
                    continue;
                }
                catch (Exception exception)
                {
                    item.Completion.TrySetException(exception);
                    throw;
                }
                try
                {
                    item.AfterWrite?.Invoke();
                    item.Completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    item.Completion.TrySetException(exception);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (_lifecycleGate)
            {
                _accepting = false;
            }
            FailPending(exception);
        }
    }

    private async ValueTask<(OutboundItem Item, bool FloodControlEnabled)> TakeNextAsync()
    {
        var stopping = _stopping.Token;

        while (true)
        {
            stopping.ThrowIfCancellationRequested();
            var delay = Timeout.InfiniteTimeSpan;
            var lineCancellation = CancellationToken.None;

            lock (_lifecycleGate)
            {
                var floodControlEnabled = _floodControlEnabled?.Invoke() == true;
                foreach (var queue in _queues)
                {
                    if (queue.Count == 0)
                    {
                        continue;
                    }

                    var index = floodControlEnabled && queue[0].Priority == IrcOutboundPriority.Interactive
                        ? NextInteractiveIndex(queue)
                        : 0;
                    var item = queue[index];
                    var destination = item.ChatBufferId;
                    var canceled = item.BeforeSendCancellation.IsCancellationRequested;

                    if (floodControlEnabled && item.Priority != IrcOutboundPriority.Critical && !canceled)
                    {
                        RefillTokens();
                        var cost = SendCost(item.Bytes.Length);
                        if (_availableTokens < cost)
                        {
                            delay = BudgetDelay(cost);
                            lineCancellation = item.BeforeSendCancellation;
                            break;
                        }

                        _availableTokens -= cost;
                    }

                    queue.RemoveAt(index);
                    if (floodControlEnabled && destination.HasValue && !canceled)
                    {
                        _lastChatBufferId = destination;
                        var remaining = queue.FindAll(candidate => candidate.ChatBufferId == destination);
                        queue.RemoveAll(candidate => candidate.ChatBufferId == destination);
                        queue.AddRange(remaining);
                    }

                    return (item, floodControlEnabled);
                }
            }

            using var waitCancellation = lineCancellation.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(stopping, lineCancellation)
                : null;

            try
            {
                await _available.WaitAsync(delay, waitCancellation?.Token ?? stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                lineCancellation.IsCancellationRequested && !stopping.IsCancellationRequested)
            {
                // Recheck the queue so the canceled entry can be retired.
            }
        }
    }

    private int NextInteractiveIndex(List<OutboundItem> queue)
    {
        var command = queue.FindIndex(item => item.ChatBufferId is null);
        if (command >= 0)
        {
            return command;
        }

        var otherDestination = queue.FindIndex(item => item.ChatBufferId != _lastChatBufferId);
        return otherDestination >= 0 ? otherDestination : 0;
    }

    private void FailPending(Exception exception)
    {
        lock (_lifecycleGate)
        {
            foreach (var queue in _queues)
            {
                foreach (var item in queue)
                {
                    item.Completion.TrySetException(exception);
                }

                queue.Clear();
            }
        }
    }

    private int SendCost(int byteCount) =>
        Math.Min(_burstTokens, Math.Max(1, (int)Math.Ceiling(byteCount / 120d)));

    private TimeSpan BudgetDelay(int cost) =>
        TimeSpan.FromTicks((long)Math.Ceiling((cost - _availableTokens) * _tokenInterval.Ticks));

    private async Task WaitForSendBudgetAsync(int byteCount, CancellationToken cancellationToken)
    {
        var cost = SendCost(byteCount);
        while (true)
        {
            RefillTokens();
            if (_availableTokens >= cost)
            {
                _availableTokens -= cost;
                return;
            }

            await Task.Delay(BudgetDelay(cost), cancellationToken).ConfigureAwait(false);
        }
    }

    private void RefillTokens()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastRefillTimestamp, now);
        _lastRefillTimestamp = now;
        _availableTokens = Math.Min(
            _burstTokens,
            _availableTokens + elapsed.TotalSeconds / _tokenInterval.TotalSeconds);
    }

    private sealed record OutboundItem(byte[] Bytes)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IrcOutboundPriority Priority { get; init; }

        public CancellationToken BeforeSendCancellation { get; init; }

        public Action? BeforeWrite { get; init; }

        public Action? AfterWrite { get; init; }

        public BufferId? ChatBufferId { get; init; }
    }
}
