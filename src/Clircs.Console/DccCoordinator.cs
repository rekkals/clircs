using Clircs.Dcc;
using Clircs.Identity;
using Clircs.State;
using Clircs.Transport;
using System.Net.Sockets;

namespace Clircs.ConsoleClient;

/// <summary>
/// Owns the application-side lifecycle of DCC requests and their live resources.
/// Command parsing and presentation remain in <see cref="ClientApplication"/>;
/// sockets, timers, transfers, and background operations belong here.
/// </summary>
internal sealed class DccCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<int, DccRuntime> _runtimes = [];

    internal DccRequestRegistry Requests { get; } = new();

    internal bool TryBeginResume(int requestId, PendingDccResume pending)
    {
        lock (_gate)
        {
            var runtime = RuntimeForUnsafe(requestId);
            if (runtime.PendingResume is not null) return false;
            runtime.PendingResume = pending;
            return true;
        }
    }

    internal PendingDccResume? PendingResume(int requestId)
    {
        lock (_gate) return _runtimes.GetValueOrDefault(requestId)?.PendingResume;
    }

    internal void ClearPendingResume(int requestId)
    {
        lock (_gate)
        {
            if (_runtimes.TryGetValue(requestId, out var runtime)) runtime.PendingResume = null;
        }
    }

    internal bool TakePendingResume(int requestId, PendingDccResume expected)
    {
        lock (_gate)
        {
            if (_runtimes.GetValueOrDefault(requestId)?.PendingResume != expected) return false;
            _runtimes[requestId].PendingResume = null;
            return true;
        }
    }

    internal void SetTransfer(int requestId, ActiveDccTransfer transfer)
    {
        lock (_gate) RuntimeForUnsafe(requestId).Transfer = transfer;
    }

    internal bool ClearTransfer(int requestId, ActiveDccTransfer transfer)
    {
        lock (_gate)
        {
            if (_runtimes.GetValueOrDefault(requestId)?.Transfer != transfer) return false;
            _runtimes[requestId].Transfer = null;
            return true;
        }
    }

    internal (ActiveDccTransfer? Transfer, OutgoingDccSend? Send) TransferHandles(int requestId)
    {
        lock (_gate)
        {
            var runtime = _runtimes.GetValueOrDefault(requestId);
            return (runtime?.Transfer, runtime?.OutgoingSend);
        }
    }

    internal void SetOutgoingSend(int requestId, OutgoingDccSend send)
    {
        lock (_gate) RuntimeForUnsafe(requestId).OutgoingSend = send;
    }

    internal OutgoingDccSend? OutgoingSend(int requestId)
    {
        lock (_gate) return _runtimes.GetValueOrDefault(requestId)?.OutgoingSend;
    }

    internal bool ClearOutgoingSend(int requestId, OutgoingDccSend send)
    {
        lock (_gate)
        {
            if (_runtimes.GetValueOrDefault(requestId)?.OutgoingSend != send) return false;
            _runtimes[requestId].OutgoingSend = null;
            return true;
        }
    }

    internal OutgoingDccSend? TakeUnstartedPassiveSend(int requestId)
    {
        lock (_gate)
        {
            if (_runtimes.GetValueOrDefault(requestId)?.OutgoingSend is not { Listener: null } send) return null;
            _runtimes[requestId].OutgoingSend = null;
            return send;
        }
    }

    internal void SetChatListener(int requestId, PendingDccChat listener)
    {
        lock (_gate) RuntimeForUnsafe(requestId).ChatListener = listener;
    }

    internal PendingDccChat? TakeChatListener(int requestId)
    {
        lock (_gate)
        {
            var listener = _runtimes.GetValueOrDefault(requestId)?.ChatListener;
            if (listener is not null) _runtimes[requestId].ChatListener = null;
            return listener;
        }
    }

    internal bool RemoveChatListener(int requestId, PendingDccChat listener)
    {
        lock (_gate)
        {
            if (_runtimes.GetValueOrDefault(requestId)?.ChatListener != listener) return false;
            _runtimes[requestId].ChatListener = null;
            return true;
        }
    }

    internal PendingDccConnection BeginChatConnection(int requestId, CancellationTokenSource lifetime)
    {
        lock (_gate)
        {
            var runtime = RuntimeForUnsafe(requestId);
            if (runtime.ChatConnection is not null)
                throw new InvalidOperationException($"DCC request #{requestId} is already connecting");
            return runtime.ChatConnection = new PendingDccConnection(lifetime);
        }
    }

    internal PendingDccConnection? ChatConnection(int requestId)
    {
        lock (_gate) return _runtimes.GetValueOrDefault(requestId)?.ChatConnection;
    }

    internal bool RemoveChatConnection(int requestId, PendingDccConnection connection)
    {
        lock (_gate)
        {
            if (_runtimes.GetValueOrDefault(requestId)?.ChatConnection != connection) return false;
            _runtimes[requestId].ChatConnection = null;
            return true;
        }
    }

    internal void SetChat(int requestId, ActiveDccChat chat)
    {
        lock (_gate) RuntimeForUnsafe(requestId).Chat = chat;
    }

    internal ActiveDccChat? TakeChat(int requestId)
    {
        lock (_gate)
        {
            var chat = _runtimes.GetValueOrDefault(requestId)?.Chat;
            if (chat is not null) _runtimes[requestId].Chat = null;
            return chat;
        }
    }

    internal (int RequestId, ActiveDccChat Chat)? ChatForBuffer(BufferId bufferId)
    {
        lock (_gate)
        {
            var runtime = _runtimes.Values.FirstOrDefault(candidate => candidate.ChatBufferId == bufferId);
            return runtime?.Chat is { } chat ? (runtime.RequestId, chat) : null;
        }
    }

    internal int? RequestIdForChatBuffer(BufferId bufferId)
    {
        lock (_gate)
            return _runtimes.Values.FirstOrDefault(candidate => candidate.ChatBufferId == bufferId)?.RequestId;
    }

    internal BufferId? ChatBufferId(int requestId)
    {
        lock (_gate) return _runtimes.GetValueOrDefault(requestId)?.ChatBufferId;
    }

    internal void SetChatBuffer(int requestId, BufferId bufferId)
    {
        lock (_gate) RuntimeForUnsafe(requestId).ChatBufferId = bufferId;
    }

    internal bool ClearChatBuffer(int requestId, BufferId bufferId)
    {
        lock (_gate)
        {
            if (_runtimes.GetValueOrDefault(requestId)?.ChatBufferId != bufferId) return false;
            _runtimes[requestId].ChatBufferId = null;
            return true;
        }
    }

    internal void ClearChatBuffer(BufferId bufferId)
    {
        lock (_gate)
        {
            foreach (var runtime in _runtimes.Values.Where(candidate => candidate.ChatBufferId == bufferId))
                runtime.ChatBufferId = null;
        }
    }

    internal void SetNotificationBuffer(int requestId, BufferId bufferId)
    {
        lock (_gate) RuntimeForUnsafe(requestId).NotificationBufferId = bufferId;
    }

    internal BufferId? NotificationBufferId(int requestId)
    {
        lock (_gate) return _runtimes.GetValueOrDefault(requestId)?.NotificationBufferId;
    }

    internal void ClearNotificationBuffer(int requestId)
    {
        lock (_gate)
        {
            if (_runtimes.TryGetValue(requestId, out var runtime)) runtime.NotificationBufferId = null;
        }
    }

    internal void SetExpiration(int requestId, CancellationTokenSource expiration)
    {
        lock (_gate) RuntimeForUnsafe(requestId).ExpirationTimer = expiration;
    }

    internal CancellationTokenSource? TakeExpiration(int requestId, CancellationTokenSource? expected = null)
    {
        lock (_gate)
        {
            var expiration = _runtimes.GetValueOrDefault(requestId)?.ExpirationTimer;
            if (expiration is null || expected is not null && expiration != expected) return null;
            _runtimes[requestId].ExpirationTimer = null;
            return expiration;
        }
    }

    internal void TrackTask(int requestId, Task task)
    {
        lock (_gate)
        {
            RuntimeForUnsafe(requestId).Tasks.Add(task);
        }
        _ = task.ContinueWith(
            completed =>
            {
                lock (_gate)
                {
                    if (_runtimes.TryGetValue(requestId, out var runtime))
                        runtime.Tasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal async Task AwaitTasksAsync(IEnumerable<int> requestIds)
    {
        Task[] tasks;
        var ids = requestIds.ToHashSet();
        lock (_gate)
        {
            tasks = _runtimes.Values
                .Where(runtime => ids.Contains(runtime.RequestId))
                .SelectMany(runtime => runtime.Tasks)
                .Where(task => !task.IsCompleted)
                .Distinct()
                .ToArray();
        }
        if (tasks.Length == 0) return;
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or
            SocketException or ObjectDisposedException)
        {
            // The owning request has already published its terminal state.
        }
    }

    internal void PruneTerminalRuntimes()
    {
        var validIds = Requests.Snapshot().Select(request => request.Id).ToHashSet();
        lock (_gate)
        {
            foreach (var runtime in _runtimes.Values.Where(runtime => !validIds.Contains(runtime.RequestId)).ToArray())
            {
                CancelLifetime(runtime.ExpirationTimer);
                CancelLifetime(runtime.ChatListener?.Lifetime);
                CancelLifetime(runtime.ChatConnection?.Lifetime);
                CancelLifetime(runtime.Chat?.Lifetime);
                CancelLifetime(runtime.Transfer?.Lifetime);
                CancelLifetime(runtime.OutgoingSend?.Lifetime);
                _runtimes.Remove(runtime.RequestId);
            }
        }
    }

    internal static void CancelLifetime(CancellationTokenSource? lifetime)
    {
        if (lifetime is null) return;
        try
        {
            lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Another terminal path already released the same lifetime.
        }
    }

    private DccRuntime RuntimeForUnsafe(int requestId)
    {
        if (_runtimes.TryGetValue(requestId, out var runtime)) return runtime;
        runtime = new DccRuntime(requestId);
        _runtimes.Add(requestId, runtime);
        return runtime;
    }
}

internal sealed class DccRuntime(int requestId)
{
    internal int RequestId { get; } = requestId;
    internal HashSet<Task> Tasks { get; } = [];
    internal CancellationTokenSource? ExpirationTimer { get; set; }
    internal PendingDccChat? ChatListener { get; set; }
    internal PendingDccConnection? ChatConnection { get; set; }
    internal ActiveDccChat? Chat { get; set; }
    internal ActiveDccTransfer? Transfer { get; set; }
    internal OutgoingDccSend? OutgoingSend { get; set; }
    internal PendingDccResume? PendingResume { get; set; }
    internal BufferId? ChatBufferId { get; set; }
    internal BufferId? NotificationBufferId { get; set; }
}

internal sealed record PendingDccChat(DccChatListener Listener, CancellationTokenSource Lifetime);

internal sealed record PendingDccConnection(CancellationTokenSource Lifetime);

internal sealed record ActiveDccChat(
    int RequestId,
    DccChatTransport Transport,
    BufferState Buffer,
    CancellationTokenSource Lifetime);

internal sealed record ActiveDccTransfer(
    int RequestId,
    DccDownloadTarget Target,
    CancellationTokenSource Lifetime,
    DccFileReceiveListener? Listener = null,
    long InitialOffset = 0);

internal sealed class OutgoingDccSend(
    int requestId,
    string filePath,
    long expectedBytes,
    DateTime lastWriteTimeUtc,
    DccFileSendListener? listener,
    CancellationTokenSource lifetime)
{
    private long _resumeOffset;

    internal int RequestId { get; } = requestId;
    internal string FilePath { get; } = filePath;
    internal long ExpectedBytes { get; } = expectedBytes;
    internal DateTime LastWriteTimeUtc { get; } = lastWriteTimeUtc;
    internal DccFileSendListener? Listener { get; } = listener;
    internal CancellationTokenSource Lifetime { get; } = lifetime;
    internal long ResumeOffset => Interlocked.Read(ref _resumeOffset);

    internal bool TrySetResumeOffset(long offset) =>
        offset > 0 && offset < ExpectedBytes &&
        Interlocked.CompareExchange(ref _resumeOffset, offset, 0) == 0;
}

internal sealed record PendingDccResume(DccDownloadTarget Target, long Position);
