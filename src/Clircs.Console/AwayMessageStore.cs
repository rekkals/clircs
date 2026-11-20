using System.Text.Json;
using Clircs.Infrastructure;

namespace Clircs.ConsoleClient;

internal sealed record AwayMessageEntry(
    Guid Id,
    string InboxKey,
    string InboxName,
    string Nickname,
    string? Username,
    string? Host,
    string Type,
    string Text,
    DateTimeOffset ReceivedAt,
    bool Read)
{
    public long Number { get; init; }
}

internal sealed class AwayMessageStore
{
    private const int MaximumEntries = 5000;
    private const int CurrentSchemaVersion = 2;
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };
    private readonly DurableFileWriter _files;
    private List<AwayMessageEntry> _entries = [];
    private List<AwayMessageSequence> _sequences = [];

    public AwayMessageStore(string? path = null, DurableFileWriter? files = null)
    {
        _path = path is null ? null : Path.GetFullPath(path);
        _files = files ?? DurableFileWriter.Shared;
        Load();
    }

    public string? LoadError { get; private set; }

    public IReadOnlyList<AwayMessageEntry> ForInbox(string inboxKey)
    {
        lock (_gate)
        {
            return _entries
                .Where(entry => entry.InboxKey.Equals(inboxKey, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.ReceivedAt)
                .ToArray();
        }
    }

    public void Add(AwayMessageEntry entry)
    {
        lock (_gate)
        {
            EnsureWritable();

            var sequences = new List<AwayMessageSequence>(_sequences);
            var sequenceIndex = sequences.FindIndex(sequence =>
                sequence.InboxKey.Equals(entry.InboxKey, StringComparison.OrdinalIgnoreCase) &&
                sequence.Nickname.Equals(entry.Nickname, StringComparison.OrdinalIgnoreCase));
            var lastNumber = sequenceIndex < 0 ? 0 : sequences[sequenceIndex].LastNumber;
            entry = entry with { Number = checked(lastNumber + 1) };

            var sequence = new AwayMessageSequence(
                entry.InboxKey, entry.Nickname, entry.Number);
            if (sequenceIndex < 0)
            {
                sequences.Add(sequence);
            }
            else
            {
                sequences[sequenceIndex] = sequence;
            }

            var candidate = new List<AwayMessageEntry>(_entries) { entry };
            if (candidate.Count > MaximumEntries)
            {
                candidate.RemoveRange(0, candidate.Count - MaximumEntries);
            }

            sequences = RetainSequences(candidate, sequences);
            SaveUnsafe(candidate, sequences);
            _entries = candidate;
            _sequences = sequences;
        }
    }

    public AwayMessageEntry? Read(string inboxKey, string nickname, long number)
    {
        lock (_gate)
        {
            EnsureWritable();

            var index = _entries.FindIndex(entry =>
                entry.InboxKey.Equals(inboxKey, StringComparison.OrdinalIgnoreCase) &&
                entry.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase) &&
                entry.Number == number);
            if (index < 0) return null;

            var entry = _entries[index];
            if (entry.Read) return entry;

            entry = entry with { Read = true };
            var candidate = new List<AwayMessageEntry>(_entries);
            candidate[index] = entry;

            SaveUnsafe(candidate, _sequences);
            _entries = candidate;
            return entry;
        }
    }

    public int Delete(
        string inboxKey,
        string? nickname = null,
        long? number = null)
    {
        if (number is not null && nickname is null)
        {
            throw new ArgumentException(
                "A nickname is required when deleting a numbered message.",
                nameof(nickname));
        }

        lock (_gate)
        {
            EnsureWritable();

            var candidate = new List<AwayMessageEntry>(_entries);
            var removed = candidate.RemoveAll(entry =>
                entry.InboxKey.Equals(inboxKey, StringComparison.OrdinalIgnoreCase) &&
                (nickname is null ||
                    entry.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase)) &&
                (number is null || entry.Number == number.Value));
            if (removed > 0)
            {
                var sequences = RetainSequences(candidate, _sequences);
                SaveUnsafe(candidate, sequences);
                _entries = candidate;
                _sequences = sequences;
            }
            return removed;
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredAwayMessages>(File.ReadAllText(_path), _options)
                ?? throw new InvalidDataException("Away-message data is empty.");
            // Old network-based messages are deliberately not migrated. Sorry. :(
            if (stored.SchemaVersion == 1) return;

            if (stored.SchemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported away-message schema {stored.SchemaVersion}.");
            }
            _entries = stored.Messages
                .Where(entry => entry.Number > 0 &&
                    !string.IsNullOrWhiteSpace(entry.InboxKey) &&
                    !string.IsNullOrWhiteSpace(entry.Nickname))
                .TakeLast(MaximumEntries)
                .ToList();
            _sequences = RetainSequences(_entries, stored.Sequences);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            LoadError = $"Away-message data '{_path}' is invalid and was left untouched: {exception.Message}";
            _entries = [];
            _sequences = [];
        }
    }

    private void SaveUnsafe(
        IReadOnlyList<AwayMessageEntry> entries,
        IReadOnlyList<AwayMessageSequence> sequences)
    {
        if (_path is null) return;

        _files.WriteText(_path, JsonSerializer.Serialize(new StoredAwayMessages
        {
            SchemaVersion = CurrentSchemaVersion,
            Messages = entries.ToList(),
            Sequences = sequences.ToList()
        }, _options));
    }

    private static List<AwayMessageSequence> RetainSequences(
        IReadOnlyList<AwayMessageEntry> entries,
        IReadOnlyList<AwayMessageSequence> sequences)
    {
        var senders = entries
            .GroupBy(entry => entry.InboxKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.Nickname)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        return sequences.Where(sequence =>
            senders.TryGetValue(sequence.InboxKey, out var nicknames) &&
            nicknames.Contains(sequence.Nickname)).ToList();
    }

    private void EnsureWritable()
    {
        if (LoadError is not null) throw new InvalidOperationException(LoadError);
    }

    private sealed record AwayMessageSequence(
        string InboxKey,
        string Nickname,
        long LastNumber);

    private sealed class StoredAwayMessages
    {
        public int SchemaVersion { get; set; }
        public List<AwayMessageEntry> Messages { get; set; } = [];
        public List<AwayMessageSequence> Sequences { get; set; } = [];
    }
}
