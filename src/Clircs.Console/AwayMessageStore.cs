using System.Text.Json;
using Clircs.Infrastructure;

namespace Clircs.ConsoleClient;

internal sealed record AwayMessageEntry(
    Guid Id,
    string NetworkKey,
    string NetworkName,
    string Nickname,
    string? Username,
    string? Host,
    string Type,
    string Text,
    DateTimeOffset ReceivedAt,
    bool Read);

internal sealed class AwayMessageStore
{
    private const int MaximumEntries = 5000;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };
    private readonly DurableFileWriter _files;
    private List<AwayMessageEntry> _entries = [];

    public AwayMessageStore(string path, DurableFileWriter? files = null)
    {
        _path = Path.GetFullPath(path);
        _files = files ?? DurableFileWriter.Shared;
        Load();
    }

    public string? LoadError { get; private set; }

    public IReadOnlyList<AwayMessageEntry> ForNetwork(string networkKey)
    {
        lock (_gate)
        {
            return _entries
                .Where(entry => entry.NetworkKey.Equals(networkKey, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.ReceivedAt)
                .ToArray();
        }
    }

    public void Add(AwayMessageEntry entry)
    {
        lock (_gate)
        {
            EnsureWritable();
            var candidate = new List<AwayMessageEntry>(_entries) { entry };
            if (candidate.Count > MaximumEntries)
            {
                candidate.RemoveRange(0, candidate.Count - MaximumEntries);
            }
            SaveUnsafe(candidate);
            _entries = candidate;
        }
    }

    public int MarkRead(string networkKey, string nickname)
    {
        lock (_gate)
        {
            EnsureWritable();
            var candidate = new List<AwayMessageEntry>(_entries);
            var changed = 0;
            for (var index = 0; index < candidate.Count; index++)
            {
                var entry = candidate[index];
                if (!entry.NetworkKey.Equals(networkKey, StringComparison.OrdinalIgnoreCase) ||
                    !entry.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase) ||
                    entry.Read) continue;
                candidate[index] = entry with { Read = true };
                changed++;
            }
            if (changed > 0)
            {
                SaveUnsafe(candidate);
                _entries = candidate;
            }
            return changed;
        }
    }

    public int Delete(string networkKey, string? nickname = null)
    {
        lock (_gate)
        {
            EnsureWritable();
            var candidate = new List<AwayMessageEntry>(_entries);
            var removed = candidate.RemoveAll(entry =>
                entry.NetworkKey.Equals(networkKey, StringComparison.OrdinalIgnoreCase) &&
                (nickname is null || entry.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase)));
            if (removed > 0)
            {
                SaveUnsafe(candidate);
                _entries = candidate;
            }
            return removed;
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredAwayMessages>(File.ReadAllText(_path), _options)
                ?? throw new InvalidDataException("Away-message data is empty.");
            if (stored.SchemaVersion != 1)
            {
                throw new InvalidDataException($"Unsupported away-message schema {stored.SchemaVersion}.");
            }
            _entries = stored.Messages
                .Where(entry => !string.IsNullOrWhiteSpace(entry.NetworkKey) &&
                    !string.IsNullOrWhiteSpace(entry.Nickname))
                .TakeLast(MaximumEntries)
                .ToList();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            LoadError = $"Away-message data '{_path}' is invalid and was left untouched: {exception.Message}";
            _entries = [];
        }
    }

    private void SaveUnsafe(IReadOnlyList<AwayMessageEntry> entries)
    {
        _files.WriteText(_path, JsonSerializer.Serialize(new StoredAwayMessages
        {
            SchemaVersion = 1,
            Messages = entries.ToList()
        }, _options));
    }

    private void EnsureWritable()
    {
        if (LoadError is not null) throw new InvalidOperationException(LoadError);
    }

    private sealed class StoredAwayMessages
    {
        public int SchemaVersion { get; set; }
        public List<AwayMessageEntry> Messages { get; set; } = [];
    }
}
