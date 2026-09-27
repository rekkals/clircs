using System.Security.Cryptography;
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
        if (value.Length == 0) return ",";

        var bytes = Encoding.UTF8.GetBytes(value);
        var trailingStart = bytes.Length;
        while (trailingStart > 0 &&
               (bytes[trailingStart - 1] == (byte)'.' ||
                bytes[trailingStart - 1] == (byte)' '))
        {
            trailingStart--;
        }

        const string forbidden = "<>:\"/\\|?*,";
        var result = new StringBuilder();

        for (var index = 0; index < bytes.Length; index++)
        {
            var current = bytes[index];
            var escape = current < 0x20 ||
                         current >= 0x7F ||
                         forbidden.Contains((char)current) ||
                         (index >= trailingStart &&
                          (current == (byte)'.' || current == (byte)' '));

            if (escape)
            {
                result.Append(',');
                result.Append(current.ToString("X2"));
            }
            else
            {
                result.Append((char)current);
            }
        }

        var segment = result.ToString();
        var stem = segment.Split('.')[0];
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
            "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6",
            "LPT7", "LPT8", "LPT9"];

        if (reserved.Contains(stem, StringComparer.OrdinalIgnoreCase))
            segment = "," + bytes[0].ToString("X2") + segment[1..];

        const int maximumLength = 120;
        if (segment.Length <= maximumLength) return segment;

        var suffix = ",H" + Convert.ToHexString(SHA256.HashData(bytes))[..32];
        var prefixLength = maximumLength - suffix.Length;

        // Don't cut through a ,HH escape.
        while (prefixLength > 0 &&
               (segment[prefixLength - 1] == ',' ||
                (prefixLength > 1 && segment[prefixLength - 2] == ',')))
        {
            prefixLength--;
        }

        return segment[..prefixLength] + suffix;
    }

    private sealed record LogEntry(
        string Network,
        BufferKind Kind,
        string Target,
        DateTimeOffset Timestamp,
        IReadOnlyList<string> Lines,
        string? SessionFolder = null);
}
