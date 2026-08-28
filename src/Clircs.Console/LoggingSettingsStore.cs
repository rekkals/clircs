using System.Text.Json;
using Clircs.Identity;
using Clircs.Infrastructure;

namespace Clircs.ConsoleClient;

internal sealed record LoggingRule(
    NetworkProfileId NetworkId,
    string NetworkName,
    bool Enabled,
    IReadOnlyDictionary<string, bool> Targets);

internal sealed class LoggingSettingsStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };
    private readonly DurableFileWriter _files;
    private Dictionary<string, StoredNetworkRule> _rules = new(StringComparer.OrdinalIgnoreCase);

    public LoggingSettingsStore(string path, DurableFileWriter? files = null)
    {
        _path = System.IO.Path.GetFullPath(path);
        _files = files ?? DurableFileWriter.Shared;
        Load();
    }

    public string Path => _path;

    public string? LoadError { get; private set; }

    public bool IsEnabled(NetworkProfileId networkId, string target)
    {
        lock (_gate)
        {
            if (!_rules.TryGetValue(networkId.Value.ToString("D"), out var rule)) return false;
            return rule.Targets.TryGetValue(NormalizeTarget(target), out var enabled)
                ? enabled
                : rule.Enabled;
        }
    }

    public bool? TargetOverride(NetworkProfileId networkId, string target)
    {
        lock (_gate)
        {
            return _rules.TryGetValue(networkId.Value.ToString("D"), out var rule) &&
                   rule.Targets.TryGetValue(NormalizeTarget(target), out var enabled)
                ? enabled
                : null;
        }
    }

    public bool NetworkDefault(NetworkProfileId networkId)
    {
        lock (_gate)
        {
            return _rules.TryGetValue(networkId.Value.ToString("D"), out var rule) && rule.Enabled;
        }
    }

    public IReadOnlyList<LoggingRule> Entries
    {
        get
        {
            lock (_gate)
            {
                return _rules.Values
                    .Select(rule => new LoggingRule(
                        new NetworkProfileId(Guid.Parse(rule.NetworkId)),
                        rule.NetworkName,
                        rule.Enabled,
                        new Dictionary<string, bool>(rule.Targets, StringComparer.OrdinalIgnoreCase)))
                    .OrderBy(rule => rule.NetworkName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
    }

    public void SetNetwork(NetworkProfileId networkId, string networkName, bool enabled)
    {
        lock (_gate)
        {
            EnsureWritable();
            var key = networkId.Value.ToString("D");
            var candidate = CloneRules();
            candidate[key] = new StoredNetworkRule
            {
                NetworkId = key,
                NetworkName = networkName,
                Enabled = enabled,
                Targets = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            };
            Save(candidate);
            _rules = candidate;
        }
    }

    public void SetTarget(NetworkProfileId networkId, string networkName, string target, bool enabled)
    {
        lock (_gate)
        {
            EnsureWritable();
            var key = networkId.Value.ToString("D");
            var candidate = CloneRules();
            if (!candidate.TryGetValue(key, out var rule))
            {
                rule = new StoredNetworkRule
                {
                    NetworkId = key,
                    NetworkName = networkName,
                    Targets = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                };
                candidate.Add(key, rule);
            }
            rule.NetworkName = networkName;
            rule.Targets[NormalizeTarget(target)] = enabled;
            Save(candidate);
            _rules = candidate;
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredLoggingSettings>(
                File.ReadAllText(_path), _options)
                ?? throw new InvalidDataException("Logging settings are empty.");
            if (stored.SchemaVersion != 1)
            {
                throw new InvalidDataException($"Unsupported logging schema {stored.SchemaVersion}.");
            }
            _rules = (stored.Networks ?? [])
                .Where(rule => Guid.TryParse(rule.NetworkId, out _))
                .ToDictionary(
                    rule => rule.NetworkId,
                    rule =>
                    {
                        rule.Targets = new Dictionary<string, bool>(
                            rule.Targets ?? [], StringComparer.OrdinalIgnoreCase);
                        return rule;
                    },
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException
                or ArgumentException)
        {
            LoadError = $"Logging settings '{_path}' are invalid and were left untouched: {exception.Message}";
            _rules = new Dictionary<string, StoredNetworkRule>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save(IReadOnlyDictionary<string, StoredNetworkRule> rules)
    {
        var stored = new StoredLoggingSettings
        {
            SchemaVersion = 1,
            Networks = rules.Values.OrderBy(rule => rule.NetworkName, StringComparer.OrdinalIgnoreCase).ToList()
        };
        _files.WriteText(_path, JsonSerializer.Serialize(stored, _options));
    }

    private Dictionary<string, StoredNetworkRule> CloneRules() => _rules.ToDictionary(
        pair => pair.Key,
        pair => new StoredNetworkRule
        {
            NetworkId = pair.Value.NetworkId,
            NetworkName = pair.Value.NetworkName,
            Enabled = pair.Value.Enabled,
            Targets = new Dictionary<string, bool>(pair.Value.Targets, StringComparer.OrdinalIgnoreCase)
        },
        StringComparer.OrdinalIgnoreCase);

    private void EnsureWritable()
    {
        if (LoadError is not null) throw new InvalidOperationException(LoadError);
    }

    internal static string NormalizeTarget(string target) =>
        target.Trim().Equals("*", StringComparison.Ordinal) ? "status" : target.Trim();

    private sealed class StoredLoggingSettings
    {
        public int SchemaVersion { get; set; }
        public List<StoredNetworkRule> Networks { get; set; } = [];
    }

    private sealed class StoredNetworkRule
    {
        public string NetworkId { get; set; } = string.Empty;
        public string NetworkName { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public Dictionary<string, bool> Targets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
