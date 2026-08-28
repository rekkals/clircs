using System.Collections.Concurrent;
using System.Diagnostics;

namespace Clircs.Networking;

internal sealed class IrcOutboundScheduler : IAsyncDisposable
{
    internal const int DefaultQueueLimit = 512;
    internal const int DefaultCriticalQueueLimit = 32;
    private const int DefaultBurstTokens = 4;
    private static readonly TimeSpan DefaultTokenInterval = TimeSpan.FromMilliseconds(750);

    private readonly IIrcTransport _transport;
    private readonly ConcurrentQueue<OutboundItem>[] _queues;
    private readonly object _lifecycleGate = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _writerTask;
    private readonly int _queueLimit;
    private readonly int _criticalQueueLimit;
    private readonly int _burstTokens;
    private readonly TimeSpan _tokenInterval;
    private double _availableTokens;
    private long _lastRefillTimestamp;
    private int _pending;
    private int _criticalPending;
    private bool _accepting = true;
    private int _disposed;

    public IrcOutboundScheduler(
        IIrcTransport transport,
        int queueLimit = DefaultQueueLimit,
        int criticalQueueLimit = DefaultCriticalQueueLimit,
        int burstTokens = DefaultBurstTokens,
        TimeSpan? tokenInterval = null)
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
            .Select(_ => new ConcurrentQueue<OutboundItem>())
            .ToArray();
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public async ValueTask EnqueueAsync(
        ReadOnlyMemory<byte> bytes,
        IrcOutboundPriority priority,
        CancellationToken cancellationToken)
    {
        var item = new OutboundItem(bytes.ToArray());
        lock (_lifecycleGate)
        {
            if (!_accepting)
            {
                throw new InvalidOperationException("The IRC outbound scheduler is stopped.");
            }

            if (priority == IrcOutboundPriority.Critical)
            {
                if (_criticalPending >= _criticalQueueLimit)
                {
                    throw new InvalidOperationException("The critical IRC send queue is full");
                }
                _criticalPending++;
            }
            else
            {
                if (_pending >= _queueLimit)
                {
                    throw new InvalidOperationException("The IRC send queue is full; excess output was not sent");
                }
                _pending++;
            }

            _queues[(int)priority].Enqueue(item with { Priority = priority });
            _available.Release();
        }
        await item.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_lifecycleGate)
        {
            _accepting = false;
        }
        _stopping.Cancel();
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
                await _available.WaitAsync(_stopping.Token).ConfigureAwait(false);
                if (!TryDequeue(out var item))
                {
                    continue;
                }

                try
                {
                    if (item.Priority != IrcOutboundPriority.Critical)
                    {
                        await WaitForSendBudgetAsync(item.Bytes.Length, _stopping.Token).ConfigureAwait(false);
                    }
                    await _transport.WriteAsync(item.Bytes, _stopping.Token).ConfigureAwait(false);
                    item.Completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    item.Completion.TrySetException(exception);
                    throw;
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

    private bool TryDequeue(out OutboundItem item)
    {
        foreach (var queue in _queues)
        {
            if (queue.TryDequeue(out item!))
            {
                if (item.Priority == IrcOutboundPriority.Critical) _criticalPending--;
                else _pending--;
                return true;
            }
        }

        item = null!;
        return false;
    }

    private void FailPending(Exception exception)
    {
        foreach (var queue in _queues)
        {
            while (queue.TryDequeue(out var item))
            {
                if (item.Priority == IrcOutboundPriority.Critical) _criticalPending--;
                else _pending--;
                item.Completion.TrySetException(exception);
            }
        }
    }

    private async Task WaitForSendBudgetAsync(int byteCount, CancellationToken cancellationToken)
    {
        var cost = Math.Min(_burstTokens, Math.Max(1, (int)Math.Ceiling(byteCount / 120d)));
        while (true)
        {
            RefillTokens();
            if (_availableTokens >= cost)
            {
                _availableTokens -= cost;
                return;
            }

            var missing = cost - _availableTokens;
            var delay = TimeSpan.FromTicks((long)Math.Ceiling(missing * _tokenInterval.Ticks));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
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
    }
}
