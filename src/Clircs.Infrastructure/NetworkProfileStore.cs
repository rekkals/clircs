using System.Globalization;
using System.Text;
using System.Text.Json;
using Clircs.Identity;
using Clircs.Networking;

namespace Clircs.Infrastructure;

public sealed class NetworkProfileStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly DurableFileWriter _files;
    private List<NetworkProfile> _profiles = [];

    public NetworkProfileStore(string path, DurableFileWriter? files = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        _files = files ?? DurableFileWriter.Shared;
        Load();
    }

    public string Path => _path;

    public string? LoadError { get; private set; }

    public IReadOnlyList<NetworkProfile> Entries
    {
        get
        {
            lock (_gate)
            {
                return _profiles.OrderBy(profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
    }

    public NetworkProfile? Find(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            return _profiles.FirstOrDefault(profile =>
                profile.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public NetworkProfile? Find(NetworkProfileId id)
    {
        lock (_gate)
        {
            return _profiles.FirstOrDefault(profile => profile.Id == id);
        }
    }

    public void Add(NetworkProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_gate)
        {
            EnsureWritable();
            if (_profiles.Any(existing => existing.Id == profile.Id ||
                existing.DisplayName.Equals(profile.DisplayName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"A network profile named '{profile.DisplayName}' already exists.");
            }

            var candidate = new List<NetworkProfile>(_profiles) { profile };
            Save(candidate);
            _profiles = candidate;
        }
    }

    public void Replace(NetworkProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_gate)
        {
            EnsureWritable();
            var index = _profiles.FindIndex(existing => existing.Id == profile.Id);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Network profile '{profile.Id}' does not exist.");
            }

            if (_profiles.Any(existing => existing.Id != profile.Id &&
                existing.DisplayName.Equals(profile.DisplayName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"A network profile named '{profile.DisplayName}' already exists.");
            }

            var candidate = new List<NetworkProfile>(_profiles);
            candidate[index] = profile;
            Save(candidate);
            _profiles = candidate;
        }
    }

    public bool Remove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            EnsureWritable();
            var candidate = new List<NetworkProfile>(_profiles);
            var removed = candidate.RemoveAll(profile =>
                profile.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                Save(candidate);
                _profiles = candidate;
            }

            return removed;
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var profiles = new List<NetworkProfile>();
            Dictionary<string, string>? values = null;
            foreach (var sourceLine in File.ReadLines(_path, Encoding.UTF8))
            {
                var line = sourceLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                if (line.Equals("[[network]]", StringComparison.Ordinal))
                {
                    if (values is not null)
                    {
                        profiles.Add(ParseProfile(values));
                    }

                    values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    throw new InvalidDataException($"Invalid profile configuration line: {sourceLine}");
                }

                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                if (values is null)
                {
                    if (!key.Equals("version", StringComparison.OrdinalIgnoreCase) || value != "1")
                    {
                        throw new InvalidDataException("The profile configuration must begin with version = 1.");
                    }

                    continue;
                }

                if (!values.TryAdd(key, value))
                {
                    throw new InvalidDataException($"Duplicate network profile key '{key}'.");
                }
            }

            if (values is not null)
            {
                profiles.Add(ParseProfile(values));
            }

            if (profiles.GroupBy(profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1) ||
                profiles.GroupBy(profile => profile.Id).Any(group => group.Count() > 1))
            {
                throw new InvalidDataException("Network profile names and IDs must be unique.");
            }

            _profiles = profiles;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or ArgumentException or FormatException or OverflowException)
        {
            LoadError = $"Could not read network profiles from '{_path}': {exception.Message}";
            _profiles = [];
        }
    }

    private void Save(IReadOnlyList<NetworkProfile> profiles)
    {
        _files.WriteText(_path, Serialize(profiles), retainBackup: true, new UTF8Encoding(false));
    }

    private void EnsureWritable()
    {
        if (LoadError is not null)
        {
            throw new InvalidOperationException(
                $"{LoadError} The file was preserved and must be repaired or removed before profiles can be changed.");
        }
    }

    private static NetworkProfile ParseProfile(IReadOnlyDictionary<string, string> values)
    {
        var id = new NetworkProfileId(Guid.Parse(ParseString(Required(values, "id"))));
        var name = ParseString(Required(values, "name"));
        var networkName = values.TryGetValue("network_name", out var networkNameValue)
            ? ParseString(networkNameValue)
            : null;
        var hosts = ParseArray<string>(Required(values, "hosts"));
        var ports = ParseArray<int>(Required(values, "ports"));
        var tls = ParseArray<bool>(Required(values, "tls"));
        if (hosts.Length != ports.Length || hosts.Length != tls.Length)
        {
            throw new InvalidDataException("A network profile's hosts, ports, and tls arrays must have equal lengths.");
        }

        var endpoints = hosts.Select((host, index) => new IrcEndpoint(host, ports[index], tls[index]));
        var identity = new IrcIdentity(
            ParseArray<string>(Required(values, "nicknames")),
            ParseString(Required(values, "username")),
            ParseString(Required(values, "real_name")));
        var autojoin = values.TryGetValue("autojoin", out var autojoinValue)
            ? ParseArray<string>(autojoinValue)
            : [];
        var notify = values.TryGetValue("notify", out var notifyValue)
            ? ParseArray<string>(notifyValue)
            : [];
        var userModes = values.TryGetValue("user_modes", out var userModesValue)
            ? ParseString(userModesValue)
            : "+i";
        SaslProfileSettings? sasl = null;
        if (values.ContainsKey("sasl_mechanism") || values.ContainsKey("sasl_username") || values.ContainsKey("sasl_client_certificate"))
        {
            var mechanism = values.TryGetValue("sasl_mechanism", out var mechanismValue)
                ? ParseString(mechanismValue)
                : SaslMechanisms.Plain;
            var required = ParseBool(values, "sasl_required", true);
            if (mechanism.Equals(SaslMechanisms.Plain, StringComparison.OrdinalIgnoreCase))
            {
                sasl = new SaslProfileSettings(
                    ParseString(Required(values, "sasl_username")),
                    required);
            }
            else if (mechanism.Equals(SaslMechanisms.External, StringComparison.OrdinalIgnoreCase))
            {
                var authorizationIdentity = values.TryGetValue("sasl_authorization_identity", out var identityValue)
                    ? ParseString(identityValue)
                    : null;
                sasl = SaslProfileSettings.External(
                    ParseString(Required(values, "sasl_client_certificate")), authorizationIdentity, required);
            }
            else throw new InvalidDataException($"Unsupported SASL mechanism '{mechanism}'.");
        }
        var reconnectAttempts = ParseInt(values, "reconnect_max_attempts", 99);
        // Eight was the original hard-coded default and there was no client
        // command for changing it. Migrate those profiles to the new default.
        if (reconnectAttempts == 8) reconnectAttempts = 99;
        var reconnect = new ReconnectPolicy(
            reconnectAttempts,
            TimeSpan.FromSeconds(ParseInt(values, "reconnect_initial_seconds", 2)),
            TimeSpan.FromSeconds(ParseInt(values, "reconnect_max_seconds", 120)));
        return new NetworkProfile(id, name, endpoints, identity, autojoin, reconnect, networkName, notify, userModes, sasl);
    }

    private static string Serialize(IEnumerable<NetworkProfile> profiles)
    {
        var builder = new StringBuilder("# clircs network profiles. This file is safe to edit while clircs is closed.\nversion = 1\n");
        foreach (var profile in profiles.OrderBy(profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("\n[[network]]\n");
            Append(builder, "id", profile.Id.ToString());
            Append(builder, "name", profile.DisplayName);
            if (profile.NetworkName is not null)
            {
                Append(builder, "network_name", profile.NetworkName);
            }
            AppendArray(builder, "hosts", profile.Endpoints.Select(endpoint => endpoint.Host));
            AppendArray(builder, "ports", profile.Endpoints.Select(endpoint => endpoint.Port));
            AppendArray(builder, "tls", profile.Endpoints.Select(endpoint => endpoint.UseTls));
            AppendArray(builder, "nicknames", profile.Identity.Nicknames);
            Append(builder, "username", profile.Identity.Username);
            Append(builder, "real_name", profile.Identity.RealName);
            AppendArray(builder, "autojoin", profile.AutojoinChannels);
            AppendArray(builder, "notify", profile.NotifyNicknames);
            Append(builder, "user_modes", profile.UserModes.Length == 0 ? "none" : profile.UserModes);
            if (profile.Sasl is not null)
            {
                Append(builder, "sasl_mechanism", profile.Sasl.Mechanism);
                if (profile.Sasl.Mechanism == SaslMechanisms.Plain)
                    Append(builder, "sasl_username", profile.Sasl.Username!);
                else
                {
                    Append(builder, "sasl_client_certificate", profile.Sasl.ClientCertificatePath!);
                    if (!string.IsNullOrEmpty(profile.Sasl.AuthorizationIdentity))
                        Append(builder, "sasl_authorization_identity", profile.Sasl.AuthorizationIdentity);
                }
                builder.Append("sasl_required = ").Append(profile.Sasl.Required ? "true" : "false").Append('\n');
            }
            builder.Append("reconnect_max_attempts = ").Append(profile.Reconnect.MaximumAttempts).Append('\n');
            builder.Append("reconnect_initial_seconds = ").Append((int)profile.Reconnect.InitialDelay.TotalSeconds).Append('\n');
            builder.Append("reconnect_max_seconds = ").Append((int)profile.Reconnect.MaximumDelay.TotalSeconds).Append('\n');
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string key, string value) =>
        builder.Append(key).Append(" = ").Append(JsonSerializer.Serialize(value)).Append('\n');

    private static void AppendArray<T>(StringBuilder builder, string key, IEnumerable<T> values) =>
        builder.Append(key).Append(" = ").Append(JsonSerializer.Serialize(values)).Append('\n');

    private static string Required(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value)
            ? value
            : throw new InvalidDataException($"A network profile is missing '{key}'.");

    private static string ParseString(string value) =>
        JsonSerializer.Deserialize<string>(value) ?? throw new InvalidDataException("A TOML string cannot be null.");

    private static T[] ParseArray<T>(string value) =>
        JsonSerializer.Deserialize<T[]>(value) ?? throw new InvalidDataException("A TOML array cannot be null.");

    private static bool ParseBool(IReadOnlyDictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var value) ? bool.Parse(value) : fallback;

    private static int ParseInt(IReadOnlyDictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out var value)
            ? int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : fallback;
}
