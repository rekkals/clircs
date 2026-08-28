using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Clircs.Protection;
using Clircs.Infrastructure;

namespace Clircs.ConsoleClient;

internal enum ProtectionScopeKind
{
    Global,
    Network,
    Channel
}

internal sealed record ProtectionScope(
    ProtectionScopeKind Kind,
    string? NetworkId = null,
    string? Channel = null)
{
    public string DisplayName => Kind switch
    {
        ProtectionScopeKind.Global => "global",
        ProtectionScopeKind.Network => "network",
        ProtectionScopeKind.Channel => $"channel {Channel}",
        _ => throw new ArgumentOutOfRangeException()
    };
}

internal sealed record EffectiveProtectionSettings(
    ProtectionSettings Settings,
    ProtectionScope Source);

internal sealed class ProtectionSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly DurableFileWriter _files;
    private ProtectionDocument _document = new();
    private ProtectionDocument _persistedDocument = new();

    public ProtectionSettingsStore(string path, DurableFileWriter? files = null)
    {
        _path = Path.GetFullPath(path);
        _files = files ?? DurableFileWriter.Shared;
        Load();
        _persistedDocument = Clone(_document);
    }

    public string? LoadError { get; private set; }

    public EffectiveProtectionSettings Effective(string? networkId, string? channel, string? alternateChannel = null)
    {
        lock (_gate)
        {
            var settings = _document.Global.DeepCopy().Validate();
            var source = new ProtectionScope(ProtectionScopeKind.Global);
            if (networkId is not null &&
                _document.Overrides.TryGetValue(NetworkKey(networkId), out var networkOverride))
            {
                settings = networkOverride.Apply(settings);
                source = new ProtectionScope(ProtectionScopeKind.Network, networkId);
            }
            if (networkId is not null && channel is not null &&
                TryChannelOverride(networkId, channel, alternateChannel, out var channelOverride))
            {
                settings = channelOverride.Apply(settings);
                source = new ProtectionScope(ProtectionScopeKind.Channel, networkId, channel);
            }
            return new EffectiveProtectionSettings(settings.Validate(), source);
        }
    }

    public ProtectionSettings SettingsFor(ProtectionScope scope) => scope.Kind switch
    {
        ProtectionScopeKind.Global => Effective(null, null).Settings,
        ProtectionScopeKind.Network => Effective(scope.NetworkId, null).Settings,
        ProtectionScopeKind.Channel => Effective(scope.NetworkId, scope.Channel).Settings,
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };

    public void SetChannelEnabled(ProtectionScope scope, bool enabled) =>
        Change(scope, item => item.ChannelEnabled = enabled,
            settings => settings with { ChannelEnabled = enabled });

    public void SetPersonalEnabled(ProtectionScope scope, bool enabled) =>
        Change(scope, item => item.PersonalEnabled = enabled,
            settings => settings with { PersonalEnabled = enabled });

    public void SetMonitorOnly(ProtectionScope scope, bool enabled) =>
        Change(scope, item => item.MonitorOnly = enabled,
            settings => settings with { MonitorOnly = enabled });

    public void SetExemptOperators(ProtectionScope scope, bool enabled) =>
        Change(scope, item => item.ExemptOperators = enabled,
            settings => settings with { ExemptOperators = enabled });

    public void SetExemptProtected(ProtectionScope scope, bool enabled) =>
        Change(scope, item => item.ExemptProtected = enabled,
            settings => settings with { ExemptProtected = enabled });

    public void SetExemptProtectionExempt(ProtectionScope scope, bool enabled) =>
        Change(scope, item => item.ExemptProtectionExempt = enabled,
            settings => settings with { ExemptProtectionExempt = enabled });

    public void SetChannelAction(ProtectionScope scope, ChannelProtectionAction action) =>
        Change(scope, item => item.ChannelAction = action,
            settings => settings with { ChannelAction = action });

    public void SetBanSeconds(ProtectionScope scope, int seconds) =>
        Change(scope, item => item.BanSeconds = seconds,
            settings => settings with { BanSeconds = seconds });

    public void SetPersonalIgnoreSeconds(ProtectionScope scope, int seconds) =>
        Change(scope, item => item.PersonalIgnoreSeconds = seconds,
            settings => settings with { PersonalIgnoreSeconds = seconds });

    public void SetRule(
        ProtectionScope scope,
        ProtectionDetector detector,
        bool? enabled = null,
        int? threshold = null,
        int? windowSeconds = null)
    {
        if (threshold is not null && threshold is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(threshold), "Detector count must be from 1 through 1000.");
        if (windowSeconds is not null && windowSeconds is < 1 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(windowSeconds), "Detector window must be from 1 through 3600 seconds.");

        lock (_gate)
        {
            EnsureWritable();
            if (scope.Kind == ProtectionScopeKind.Global)
            {
                var current = _document.Global.Rules[detector];
                _document.Global.Rules[detector] = new ProtectionRule(
                    enabled ?? current.Enabled,
                    threshold ?? current.Threshold,
                    windowSeconds ?? current.WindowSeconds).Validate();
            }
            else
            {
                var item = OverrideFor(scope, create: true)!;
                item.Rules.TryGetValue(detector, out var current);
                item.Rules[detector] = new ProtectionRuleOverride
                {
                    Enabled = enabled ?? current?.Enabled,
                    Threshold = threshold ?? current?.Threshold,
                    WindowSeconds = windowSeconds ?? current?.WindowSeconds
                };
            }
            Persist();
        }
    }

    public bool ClearRule(ProtectionScope scope, ProtectionDetector detector)
    {
        lock (_gate)
        {
            EnsureWritable();
            if (scope.Kind == ProtectionScopeKind.Global)
            {
                _document.Global.Rules[detector] = ProtectionSettings.Defaults().Rules[detector];
                Persist();
                return true;
            }
            var item = OverrideFor(scope, create: false);
            var changed = item is not null && item.Rules.Remove(detector);
            if (changed)
            {
                RemoveIfEmpty(scope, item!);
                Persist();
            }
            return changed;
        }
    }

    public bool Reset(ProtectionScope scope)
    {
        lock (_gate)
        {
            EnsureWritable();
            var changed = scope.Kind == ProtectionScopeKind.Global
                ? ResetGlobal()
                : _document.Overrides.Remove(Key(scope));
            if (changed) Persist();
            return changed;
        }
    }

    private void Change(
        ProtectionScope scope,
        Action<ProtectionSettingsOverride> changeOverride,
        Func<ProtectionSettings, ProtectionSettings> changeGlobal)
    {
        lock (_gate)
        {
            EnsureWritable();
            if (scope.Kind == ProtectionScopeKind.Global)
                _document.Global = changeGlobal(_document.Global).Validate();
            else
                changeOverride(OverrideFor(scope, create: true)!);
            Persist();
        }
    }

    private ProtectionSettingsOverride? OverrideFor(ProtectionScope scope, bool create)
    {
        var key = Key(scope);
        if (_document.Overrides.TryGetValue(key, out var item)) return item;
        if (!create) return null;
        item = new ProtectionSettingsOverride();
        _document.Overrides[key] = item;
        return item;
    }

    private void RemoveIfEmpty(ProtectionScope scope, ProtectionSettingsOverride item)
    {
        if (item.IsEmpty) _document.Overrides.Remove(Key(scope));
    }

    private bool ResetGlobal()
    {
        _document.Global = ProtectionSettings.Defaults();
        return true;
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var text = File.ReadAllText(_path, Encoding.UTF8);
            using var json = JsonDocument.Parse(text);
            var version = json.RootElement.TryGetProperty("Version", out var property) ? property.GetInt32() : 1;
            if (version == 1)
            {
                var legacy = JsonSerializer.Deserialize<LegacyProtectionDocument>(text, JsonOptions)
                    ?? throw new InvalidDataException("Protection settings are empty.");
                EnsureDetectors(legacy.Global).Validate();
                _document = new ProtectionDocument { Global = legacy.Global.DeepCopy() with { MonitorOnly = false } };
                foreach (var (key, settings) in legacy.Scopes)
                {
                    EnsureDetectors(settings).Validate();
                    _document.Overrides[key] = ProtectionSettingsOverride.From(settings with { MonitorOnly = false });
                }
                Persist();
                return;
            }
            if (version is not (2 or 3 or 4)) throw new InvalidDataException($"Unsupported protection settings version {version}.");
            var loaded = JsonSerializer.Deserialize<ProtectionDocument>(text, JsonOptions)
                ?? throw new InvalidDataException("Protection settings are empty.");
            EnsureDetectors(loaded.Global).Validate();
            if (version < 4)
            {
                loaded.Global = loaded.Global with { MonitorOnly = false };
                foreach (var item in loaded.Overrides.Values)
                {
                    item.MonitorOnly = null;
                }
            }
            loaded.Version = 4;
            _document = loaded;
            if (version < 4) Persist();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or ArgumentException)
        {
            LoadError = $"Could not read protection settings from '{_path}': {exception.Message}";
            _document = new ProtectionDocument();
        }
    }

    private void Persist()
    {
        try
        {
            _files.WriteText(
                _path,
                JsonSerializer.Serialize(_document, JsonOptions),
                retainBackup: true,
                encoding: new UTF8Encoding(false));
            _persistedDocument = Clone(_document);
        }
        catch
        {
            _document = Clone(_persistedDocument);
            throw;
        }
    }

    private static ProtectionDocument Clone(ProtectionDocument document) =>
        JsonSerializer.Deserialize<ProtectionDocument>(JsonSerializer.Serialize(document, JsonOptions), JsonOptions)
        ?? throw new InvalidDataException("Protection settings could not be copied");

    private void EnsureWritable()
    {
        if (LoadError is not null)
            throw new InvalidOperationException($"{LoadError} Repair or remove the file before changing protection settings.");
    }

    private static string Key(ProtectionScope scope) => scope.Kind switch
    {
        ProtectionScopeKind.Network when scope.NetworkId is not null => NetworkKey(scope.NetworkId),
        ProtectionScopeKind.Channel when scope.NetworkId is not null && scope.Channel is not null =>
            ChannelKey(scope.NetworkId, scope.Channel),
        _ => throw new ArgumentException("The protection scope is incomplete.", nameof(scope))
    };

    private static string NetworkKey(string networkId) => $"network:{networkId}";

    private static string ChannelKey(string networkId, string channel) =>
        $"channel:{networkId}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(channel.ToLowerInvariant()))}";

    private bool TryChannelOverride(
        string networkId,
        string channel,
        string? alternateChannel,
        out ProtectionSettingsOverride item)
    {
        if (_document.Overrides.TryGetValue(ChannelKey(networkId, channel), out item!)) return true;
        return alternateChannel is not null &&
               !alternateChannel.Equals(channel, StringComparison.Ordinal) &&
               _document.Overrides.TryGetValue(ChannelKey(networkId, alternateChannel), out item!);
    }

    private static ProtectionSettings EnsureDetectors(ProtectionSettings settings)
    {
        var defaults = ProtectionSettings.Defaults().Rules;
        foreach (var detector in Enum.GetValues<ProtectionDetector>())
        {
            if (!settings.Rules.ContainsKey(detector)) settings.Rules[detector] = defaults[detector];
        }
        return settings;
    }

    private sealed class ProtectionDocument
    {
        public int Version { get; set; } = 4;
        public ProtectionSettings Global { get; set; } = ProtectionSettings.Defaults();
        public Dictionary<string, ProtectionSettingsOverride> Overrides { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class LegacyProtectionDocument
    {
        public ProtectionSettings Global { get; set; } = ProtectionSettings.Defaults();
        public Dictionary<string, ProtectionSettings> Scopes { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class ProtectionSettingsOverride
    {
        public bool? ChannelEnabled { get; set; }
        public bool? PersonalEnabled { get; set; }
        public bool? MonitorOnly { get; set; }
        public bool? ExemptOperators { get; set; }
        public bool? ExemptProtected { get; set; }
        public bool? ExemptProtectionExempt { get; set; }
        public ChannelProtectionAction? ChannelAction { get; set; }
        public int? BanSeconds { get; set; }
        public int? PersonalIgnoreSeconds { get; set; }
        public Dictionary<ProtectionDetector, ProtectionRuleOverride> Rules { get; set; } = [];

        [JsonIgnore]
        public bool IsEmpty =>
            ChannelEnabled is null && PersonalEnabled is null && MonitorOnly is null &&
            ExemptOperators is null && ExemptProtected is null && ExemptProtectionExempt is null &&
            ChannelAction is null && BanSeconds is null && PersonalIgnoreSeconds is null &&
            Rules.Count == 0;

        public ProtectionSettings Apply(ProtectionSettings basis)
        {
            var rules = basis.Rules.ToDictionary(entry => entry.Key, entry => entry.Value);
            foreach (var (detector, item) in Rules)
            {
                var current = rules[detector];
                rules[detector] = item.Apply(current);
            }
            return new ProtectionSettings(
                ChannelEnabled ?? basis.ChannelEnabled,
                PersonalEnabled ?? basis.PersonalEnabled,
                MonitorOnly ?? basis.MonitorOnly,
                ExemptOperators ?? basis.ExemptOperators,
                ExemptProtected ?? basis.ExemptProtected,
                ExemptProtectionExempt ?? basis.ExemptProtectionExempt,
                rules,
                ChannelAction ?? basis.ChannelAction,
                BanSeconds ?? basis.BanSeconds,
                PersonalIgnoreSeconds ?? basis.PersonalIgnoreSeconds);
        }

        public static ProtectionSettingsOverride From(ProtectionSettings settings) => new()
        {
            ChannelEnabled = settings.ChannelEnabled,
            PersonalEnabled = settings.PersonalEnabled,
            MonitorOnly = settings.MonitorOnly,
            ExemptOperators = settings.ExemptOperators,
            ExemptProtected = settings.ExemptProtected,
            ExemptProtectionExempt = settings.ExemptProtectionExempt,
            ChannelAction = settings.ChannelAction,
            BanSeconds = settings.BanSeconds,
            PersonalIgnoreSeconds = settings.PersonalIgnoreSeconds,
            Rules = settings.Rules.ToDictionary(
                entry => entry.Key,
                entry => new ProtectionRuleOverride
                {
                    Enabled = entry.Value.Enabled,
                    Threshold = entry.Value.Threshold,
                    WindowSeconds = entry.Value.WindowSeconds
                })
        };
    }

    private sealed class ProtectionRuleOverride
    {
        public bool? Enabled { get; set; }
        public int? Threshold { get; set; }
        public int? WindowSeconds { get; set; }

        public ProtectionRule Apply(ProtectionRule basis) => new(
            Enabled ?? basis.Enabled,
            Threshold ?? basis.Threshold,
            WindowSeconds ?? basis.WindowSeconds);
    }
}
