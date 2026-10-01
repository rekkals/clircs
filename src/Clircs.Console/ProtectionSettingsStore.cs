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

internal sealed record EffectiveChannelProtectionSettings(
    ChannelProtectionSettings Settings,
    ProtectionScope Source);

internal sealed record EffectivePersonalProtectionSettings(
    PersonalProtectionSettings Settings,
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

    public EffectiveChannelProtectionSettings EffectiveChannel(
        string? networkId,
        string? channel,
        string? alternateChannel = null)
    {
        lock (_gate)
        {
            var settings = _document.ChannelGlobal.DeepCopy().Validate();
            var source = new ProtectionScope(ProtectionScopeKind.Global);

            if (networkId is not null &&
                _document.ChannelOverrides.TryGetValue(NetworkKey(networkId), out var networkOverride))
            {
                settings = networkOverride.Apply(settings).Validate();
                source = new ProtectionScope(ProtectionScopeKind.Network, networkId);
            }

            if (networkId is not null &&
                channel is not null &&
                TryChannelSettingsOverride(
                    networkId,
                    channel,
                    alternateChannel,
                    out var channelOverride))
            {
                settings = channelOverride.Apply(settings).Validate();
                source = new ProtectionScope(ProtectionScopeKind.Channel, networkId, channel);
            }

            return new EffectiveChannelProtectionSettings(settings, source);
        }
    }

    public EffectivePersonalProtectionSettings EffectivePersonal(string? networkId)
    {
        lock (_gate)
        {
            var settings = _document.PersonalGlobal.DeepCopy().Validate();
            var source = new ProtectionScope(ProtectionScopeKind.Global);

            if (networkId is not null &&
                _document.PersonalOverrides.TryGetValue(NetworkKey(networkId), out var networkOverride))
            {
                settings = networkOverride.Apply(settings).Validate();
                source = new ProtectionScope(ProtectionScopeKind.Network, networkId);
            }

            return new EffectivePersonalProtectionSettings(settings, source);
        }
    }

    public ChannelProtectionSettings ChannelSettingsFor(ProtectionScope scope) => scope.Kind switch
    {
        ProtectionScopeKind.Global => EffectiveChannel(null, null).Settings,
        ProtectionScopeKind.Network => EffectiveChannel(scope.NetworkId, null).Settings,
        ProtectionScopeKind.Channel => EffectiveChannel(scope.NetworkId, scope.Channel).Settings,
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };

    public PersonalProtectionSettings PersonalSettingsFor(ProtectionScope scope) => scope.Kind switch
    {
        ProtectionScopeKind.Global => EffectivePersonal(null).Settings,
        ProtectionScopeKind.Network => EffectivePersonal(scope.NetworkId).Settings,
        ProtectionScopeKind.Channel => throw new ArgumentException(
            "Personal protection does not support channel scope.",
            nameof(scope)),
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };

    public void SetChannelEnabled(ProtectionScope scope, bool enabled) =>
        ChangeChannel(
            scope,
            item => item.Enabled = enabled,
            settings => settings with { Enabled = enabled });

    public void SetPersonalEnabled(ProtectionScope scope, bool enabled) =>
        ChangePersonal(
            scope,
            item => item.Enabled = enabled,
            settings => settings with { Enabled = enabled });

    public void SetPersonalAction(ProtectionScope scope, PersonalProtectionAction action) =>
        ChangePersonal(
            scope,
            item => item.Action = action,
            settings => settings with { Action = action });

    public void SetChannelExemptOperators(ProtectionScope scope, bool enabled) =>
        ChangeChannel(
            scope,
            item => item.ExemptOperators = enabled,
            settings => settings with { ExemptOperators = enabled });

    public void SetChannelExemptProtected(ProtectionScope scope, bool enabled) =>
        ChangeChannel(
            scope,
            item => item.ExemptProtected = enabled,
            settings => settings with { ExemptProtected = enabled });

    public void SetPersonalExemptProtected(ProtectionScope scope, bool enabled) =>
        ChangePersonal(
            scope,
            item => item.ExemptProtected = enabled,
            settings => settings with { ExemptProtected = enabled });

    public void SetChannelAction(ProtectionScope scope, ChannelProtectionAction action) =>
        ChangeChannel(
            scope,
            item => item.Action = action,
            settings => settings with { Action = action });

    public void SetChannelBanSeconds(ProtectionScope scope, int seconds) =>
        ChangeChannel(
            scope,
            item => item.BanSeconds = seconds,
            settings => settings with { BanSeconds = seconds });

    public void SetPersonalIgnoreSeconds(ProtectionScope scope, int seconds) =>
        ChangePersonal(
            scope,
            item => item.IgnoreSeconds = seconds,
            settings => settings with { IgnoreSeconds = seconds });

    public void SetChannelRule(
        ProtectionScope scope,
        ProtectionDetector detector,
        bool? enabled = null,
        int? threshold = null,
        int? windowSeconds = null)
    {
        if (!ChannelProtectionSettings.Defaults().Rules.ContainsKey(detector))
            throw new ArgumentException($"{detector} is not a channel protection rule.", nameof(detector));

        ValidateRuleChange(threshold, windowSeconds);

        lock (_gate)
        {
            EnsureWritable();
            if (scope.Kind == ProtectionScopeKind.Global)
            {
                var current = _document.ChannelGlobal.Rules[detector];
                _document.ChannelGlobal.Rules[detector] = new ProtectionRule(
                    enabled ?? current.Enabled,
                    threshold ?? current.Threshold,
                    windowSeconds ?? current.WindowSeconds).Validate();
                _document.ChannelGlobal.Validate();
            }
            else
            {
                var item = ChannelOverrideFor(scope, create: true)!;
                ChangeRuleOverride(item.Rules, detector, enabled, threshold, windowSeconds);
            }

            Persist();
        }
    }

    public void SetPersonalRule(
        ProtectionScope scope,
        ProtectionDetector detector,
        bool? enabled = null,
        int? threshold = null,
        int? windowSeconds = null)
    {
        if (!PersonalProtectionSettings.Defaults().Rules.ContainsKey(detector))
            throw new ArgumentException($"{detector} is not a personal protection rule.", nameof(detector));
        if (scope.Kind == ProtectionScopeKind.Channel)
            throw new ArgumentException("Personal protection does not support channel scope.", nameof(scope));

        ValidateRuleChange(threshold, windowSeconds);

        lock (_gate)
        {
            EnsureWritable();
            if (scope.Kind == ProtectionScopeKind.Global)
            {
                var current = _document.PersonalGlobal.Rules[detector];
                _document.PersonalGlobal.Rules[detector] = new ProtectionRule(
                    enabled ?? current.Enabled,
                    threshold ?? current.Threshold,
                    windowSeconds ?? current.WindowSeconds).Validate();
                _document.PersonalGlobal.Validate();
            }
            else
            {
                var item = PersonalOverrideFor(scope, create: true)!;
                ChangeRuleOverride(item.Rules, detector, enabled, threshold, windowSeconds);
            }

            Persist();
        }
    }

    private static void ValidateRuleChange(int? threshold, int? windowSeconds)
    {
        if (threshold is not null && threshold is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(
                nameof(threshold),
                "Rule count must be from 1 through 1000.");
        if (windowSeconds is not null && windowSeconds is < 1 or > 3600)
            throw new ArgumentOutOfRangeException(
                nameof(windowSeconds),
                "Rule window must be from 1 through 3600 seconds.");
    }

    private static void ChangeRuleOverride(
        Dictionary<ProtectionDetector, ProtectionRuleOverride> rules,
        ProtectionDetector detector,
        bool? enabled,
        int? threshold,
        int? windowSeconds)
    {
        rules.TryGetValue(detector, out var current);
        rules[detector] = new ProtectionRuleOverride
        {
            Enabled = enabled ?? current?.Enabled,
            Threshold = threshold ?? current?.Threshold,
            WindowSeconds = windowSeconds ?? current?.WindowSeconds
        };
    }

    public bool ClearChannelRule(ProtectionScope scope, ProtectionDetector detector)
    {
        if (!ChannelProtectionSettings.Defaults().Rules.ContainsKey(detector))
            throw new ArgumentException($"{detector} is not a channel protection rule.", nameof(detector));

        lock (_gate)
        {
            EnsureWritable();
            if (scope.Kind == ProtectionScopeKind.Global)
            {
                _document.ChannelGlobal.Rules[detector] =
                    ChannelProtectionSettings.Defaults().Rules[detector];
                Persist();
                return true;
            }

            var item = ChannelOverrideFor(scope, create: false);
            var changed = item is not null && item.Rules.Remove(detector);
            if (changed)
            {
                RemoveChannelIfEmpty(scope, item!);
                Persist();
            }

            return changed;
        }
    }

    public bool ClearPersonalRule(ProtectionScope scope, ProtectionDetector detector)
    {
        if (!PersonalProtectionSettings.Defaults().Rules.ContainsKey(detector))
            throw new ArgumentException($"{detector} is not a personal protection rule.", nameof(detector));
        if (scope.Kind == ProtectionScopeKind.Channel)
            throw new ArgumentException("Personal protection does not support channel scope.", nameof(scope));

        lock (_gate)
        {
            EnsureWritable();
            if (scope.Kind == ProtectionScopeKind.Global)
            {
                _document.PersonalGlobal.Rules[detector] =
                    PersonalProtectionSettings.Defaults().Rules[detector];
                Persist();
                return true;
            }

            var item = PersonalOverrideFor(scope, create: false);
            var changed = item is not null && item.Rules.Remove(detector);
            if (changed)
            {
                RemovePersonalIfEmpty(scope, item!);
                Persist();
            }

            return changed;
        }
    }

    public bool ResetChannel(ProtectionScope scope)
    {
        lock (_gate)
        {
            EnsureWritable();
            var changed = scope.Kind == ProtectionScopeKind.Global
                ? ResetChannelGlobal()
                : _document.ChannelOverrides.Remove(Key(scope));
            if (changed)
                Persist();
            return changed;
        }
    }

    public bool ResetPersonal(ProtectionScope scope)
    {
        if (scope.Kind == ProtectionScopeKind.Channel)
            throw new ArgumentException("Personal protection does not support channel scope.", nameof(scope));

        lock (_gate)
        {
            EnsureWritable();
            var changed = scope.Kind == ProtectionScopeKind.Global
                ? ResetPersonalGlobal()
                : _document.PersonalOverrides.Remove(Key(scope));
            if (changed)
                Persist();
            return changed;
        }
    }

    private void ChangeChannel(
        ProtectionScope scope,
        Action<ChannelProtectionSettingsOverride> changeOverride,
        Func<ChannelProtectionSettings, ChannelProtectionSettings> changeGlobal)
    {
        lock (_gate)
        {
            EnsureWritable();
            if (scope.Kind == ProtectionScopeKind.Global)
            {
                _document.ChannelGlobal = changeGlobal(_document.ChannelGlobal).Validate();
            }
            else
            {
                changeOverride(ChannelOverrideFor(scope, create: true)!);
            }

            Persist();
        }
    }

    private void ChangePersonal(
        ProtectionScope scope,
        Action<PersonalProtectionSettingsOverride> changeOverride,
        Func<PersonalProtectionSettings, PersonalProtectionSettings> changeGlobal)
    {
        if (scope.Kind == ProtectionScopeKind.Channel)
            throw new ArgumentException("Personal protection does not support channel scope.", nameof(scope));

        lock (_gate)
        {
            EnsureWritable();
            if (scope.Kind == ProtectionScopeKind.Global)
            {
                _document.PersonalGlobal = changeGlobal(_document.PersonalGlobal).Validate();
            }
            else
            {
                changeOverride(PersonalOverrideFor(scope, create: true)!);
            }

            Persist();
        }
    }

    private ChannelProtectionSettingsOverride? ChannelOverrideFor(
        ProtectionScope scope,
        bool create)
    {
        var key = Key(scope);
        if (_document.ChannelOverrides.TryGetValue(key, out var item))
            return item;
        if (!create)
            return null;

        item = new ChannelProtectionSettingsOverride();
        _document.ChannelOverrides[key] = item;
        return item;
    }

    private PersonalProtectionSettingsOverride? PersonalOverrideFor(
        ProtectionScope scope,
        bool create)
    {
        if (scope.Kind != ProtectionScopeKind.Network || scope.NetworkId is null)
            throw new ArgumentException("Personal protection requires global or network scope.", nameof(scope));

        var key = NetworkKey(scope.NetworkId);
        if (_document.PersonalOverrides.TryGetValue(key, out var item))
            return item;
        if (!create)
            return null;

        item = new PersonalProtectionSettingsOverride();
        _document.PersonalOverrides[key] = item;
        return item;
    }

    private void RemoveChannelIfEmpty(
        ProtectionScope scope,
        ChannelProtectionSettingsOverride item)
    {
        if (item.IsEmpty)
            _document.ChannelOverrides.Remove(Key(scope));
    }

    private void RemovePersonalIfEmpty(
        ProtectionScope scope,
        PersonalProtectionSettingsOverride item)
    {
        if (item.IsEmpty)
            _document.PersonalOverrides.Remove(Key(scope));
    }

    private bool ResetChannelGlobal()
    {
        _document.ChannelGlobal = ChannelProtectionSettings.Defaults();
        return true;
    }

    private bool ResetPersonalGlobal()
    {
        _document.PersonalGlobal = PersonalProtectionSettings.Defaults();
        return true;
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var text = File.ReadAllText(_path, Encoding.UTF8);
            using var json = JsonDocument.Parse(text);
            var version = json.RootElement.TryGetProperty("Version", out var property)
                ? property.GetInt32()
                : 0;
            if (version != 6)
                throw new InvalidDataException($"Unsupported protection settings version {version}.");

            var loaded = JsonSerializer.Deserialize<ProtectionDocument>(text, JsonOptions)
                ?? throw new InvalidDataException("Protection settings are empty.");
            loaded.ChannelGlobal.Validate();
            loaded.PersonalGlobal.Validate();
            _document = loaded;
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

    private bool TryChannelSettingsOverride(
        string networkId,
        string channel,
        string? alternateChannel,
        out ChannelProtectionSettingsOverride item)
    {
        if (_document.ChannelOverrides.TryGetValue(ChannelKey(networkId, channel), out item!))
            return true;

        return alternateChannel is not null &&
               !alternateChannel.Equals(channel, StringComparison.Ordinal) &&
               _document.ChannelOverrides.TryGetValue(ChannelKey(networkId, alternateChannel), out item!);
    }

    private sealed class ProtectionDocument
    {
        public int Version { get; set; } = 6;

        public ChannelProtectionSettings ChannelGlobal { get; set; } =
            ChannelProtectionSettings.Defaults();

        public PersonalProtectionSettings PersonalGlobal { get; set; } =
            PersonalProtectionSettings.Defaults();

        public Dictionary<string, ChannelProtectionSettingsOverride> ChannelOverrides { get; set; } =
            new(StringComparer.Ordinal);

        public Dictionary<string, PersonalProtectionSettingsOverride> PersonalOverrides { get; set; } =
            new(StringComparer.Ordinal);
    }

    private sealed class ChannelProtectionSettingsOverride
    {
        public bool? Enabled { get; set; }
        public bool? ExemptOperators { get; set; }
        public bool? ExemptProtected { get; set; }
        public ChannelProtectionAction? Action { get; set; }
        public int? BanSeconds { get; set; }
        public Dictionary<ProtectionDetector, ProtectionRuleOverride> Rules { get; set; } = [];

        [JsonIgnore]
        public bool IsEmpty =>
            Enabled is null &&
            ExemptOperators is null &&
            ExemptProtected is null &&
            Action is null &&
            BanSeconds is null &&
            Rules.Count == 0;

        public ChannelProtectionSettings Apply(ChannelProtectionSettings basis)
        {
            var rules = basis.Rules.ToDictionary(entry => entry.Key, entry => entry.Value);
            foreach (var (detector, item) in Rules)
            {
                rules[detector] = item.Apply(rules[detector]);
            }

            return new ChannelProtectionSettings(
                Enabled ?? basis.Enabled,
                ExemptOperators ?? basis.ExemptOperators,
                ExemptProtected ?? basis.ExemptProtected,
                rules,
                Action ?? basis.Action,
                BanSeconds ?? basis.BanSeconds);
        }
    }

    private sealed class PersonalProtectionSettingsOverride
    {
        public bool? Enabled { get; set; }
        public bool? ExemptProtected { get; set; }
        public PersonalProtectionAction? Action { get; set; }
        public int? IgnoreSeconds { get; set; }
        public Dictionary<ProtectionDetector, ProtectionRuleOverride> Rules { get; set; } = [];

        [JsonIgnore]
        public bool IsEmpty =>
            Enabled is null &&
            ExemptProtected is null &&
            Action is null &&
            IgnoreSeconds is null &&
            Rules.Count == 0;

        public PersonalProtectionSettings Apply(PersonalProtectionSettings basis)
        {
            var rules = basis.Rules.ToDictionary(entry => entry.Key, entry => entry.Value);
            foreach (var (detector, item) in Rules)
            {
                rules[detector] = item.Apply(rules[detector]);
            }

            return new PersonalProtectionSettings(
                Enabled ?? basis.Enabled,
                ExemptProtected ?? basis.ExemptProtected,
                rules,
                Action ?? basis.Action,
                IgnoreSeconds ?? basis.IgnoreSeconds);
        }
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
