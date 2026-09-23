using System.Text;
using System.Threading.Channels;
using Clircs.State;
using Clircs.Identity;
using Clircs.Networking;

namespace Clircs.ConsoleClient;

internal sealed class EventLogWriter : IAsyncDisposable
{
    private readonly string _root;
    internal const int MaximumWriteBatchSize = 256;
    internal const int MaximumPendingEntries = 100_000;
    private readonly Channel<LogEntry> _queue;
    private readonly object _completionGate = new();
    private readonly Dictionary<NetworkSessionId, string> _sessionFolders = [];
    private readonly Task _worker;
    private bool _completed;

    public EventLogWriter(string root, int maximumPendingEntries = MaximumPendingEntries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPendingEntries, 1);
        _root = Path.GetFullPath(root);
        _queue = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(maximumPendingEntries)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _worker = Task.Run(WriteLoopAsync);
    }

    public string RootDirectory => _root;

    public event Action<string>? ErrorRaised;

    public ResourceQueueWriteResult Enqueue(
        string network,
        BufferKind kind,
        string target,
        DateTimeOffset timestamp,
        IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return ResourceQueueWriteResult.Accepted;
        lock (_completionGate)
        {
            if (_completed) return ResourceQueueWriteResult.Completed;
            return _queue.Writer.TryWrite(new LogEntry(network, kind, target, timestamp, lines))
                ? ResourceQueueWriteResult.Accepted
                : ResourceQueueWriteResult.CapacityExceeded;
        }
    }

    public ResourceQueueWriteResult EnqueueSession(
        IrcEndpoint endpoint,
        NetworkSessionId sessionId,
        BufferKind kind,
        string target,
        DateTimeOffset timestamp,
        IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return ResourceQueueWriteResult.Accepted;
        lock (_completionGate)
        {
            if (_completed) return ResourceQueueWriteResult.Completed;
            var folder = SessionFolderFor(endpoint, sessionId);
            return _queue.Writer.TryWrite(new LogEntry(
                string.Empty, kind, target, timestamp, lines, folder))
                ? ResourceQueueWriteResult.Accepted
                : ResourceQueueWriteResult.CapacityExceeded;
        }
    }

    private string SessionFolderFor(IrcEndpoint endpoint, NetworkSessionId sessionId)
    {
        if (_sessionFolders.TryGetValue(sessionId, out var folder))
            return folder;

        var baseName = SafeSegment(
            $"{endpoint.Host}_{endpoint.Port}{(endpoint.UseTls ? "_tls" : "")}");
        folder = baseName;
        for (var suffix = 2;
             _sessionFolders.Values.Contains(folder, StringComparer.OrdinalIgnoreCase);
             suffix++)
        {
            folder = $"{baseName}_{suffix}";
        }

        _sessionFolders.Add(sessionId, folder);
        return folder;
    }

    public void ReleaseSession(NetworkSessionId sessionId)
    {
        lock (_completionGate) _sessionFolders.Remove(sessionId);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_completionGate)
        {
            _completed = true;
            _queue.Writer.TryComplete();
        }
        await _worker.ConfigureAwait(false);
    }

    internal string PathFor(string network, BufferKind kind, string target, DateTimeOffset timestamp) =>
        PathForDirectory(Path.Combine(_root, SafeSegment(network)), kind, target, timestamp);

    internal string PathForSession(
        string folder,
        BufferKind kind,
        string target,
        DateTimeOffset timestamp) =>
        PathForDirectory(
            Path.Combine(_root, "session", folder), kind, target, timestamp);

    private static string PathForDirectory(
        string networkDirectory,
        BufferKind kind,
        string target,
        DateTimeOffset timestamp)
    {
        var targetDirectory = kind switch
        {
            BufferKind.Status => Path.Combine(networkDirectory, "status"),
            BufferKind.Query => Path.Combine(networkDirectory, "queries", SafeSegment(target)),
            BufferKind.Diagnostics => Path.Combine(networkDirectory, "debug"),
            _ => Path.Combine(networkDirectory, SafeSegment(target))
        };
        return Path.Combine(targetDirectory, timestamp.ToString("yyyy-MM-dd") + ".log");
    }

    private async Task WriteLoopAsync()
    {
        var batch = new List<LogEntry>(MaximumWriteBatchSize);
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (batch.Count < MaximumWriteBatchSize && _queue.Reader.TryRead(out var entry))
            {
                batch.Add(entry);
            }
            await WriteBatchAsync(batch).ConfigureAwait(false);
            batch.Clear();
        }
    }

    private async Task WriteBatchAsync(IReadOnlyList<LogEntry> entries)
    {
        foreach (var group in entries.GroupBy(entry =>
                     entry.SessionFolder is { } folder
                        ? PathForSession(folder, entry.Kind, entry.Target, entry.Timestamp)
                        : PathFor(entry.Network, entry.Kind, entry.Target, entry.Timestamp)))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(group.Key)!);
                var text = string.Concat(group.SelectMany(entry => entry.Lines.Select(line =>
                    $"[{entry.Timestamp:HH:mm:ss}] {line}{Environment.NewLine}")));
                await File.AppendAllTextAsync(group.Key, text, new UTF8Encoding(false)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ErrorRaised?.Invoke($"Logging failed: {exception.Message}");
            }
        }
    }

    private static string SafeSegment(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return "_";
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character =>
            invalid.Contains(character) ? '_' : character).ToArray()).TrimEnd('.', ' ');
        if (sanitized.Length == 0) return "_";
        if (sanitized is "." or "..") return "_" + sanitized.Replace('.', '_');
        var stem = sanitized.Split('.')[0];
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
            "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6",
            "LPT7", "LPT8", "LPT9"];
        return reserved.Contains(stem, StringComparer.OrdinalIgnoreCase) ? "_" + sanitized : sanitized;
    }

    private sealed record LogEntry(
        string Network,
        BufferKind Kind,
        string Target,
        DateTimeOffset Timestamp,
        IReadOnlyList<string> Lines,
        string? SessionFolder = null);
}
