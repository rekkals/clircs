using System.Text;
using System.Text.Json;
using Clircs.Identity;

namespace Clircs.Infrastructure;

public sealed class NetworkCredentialStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly DurableFileWriter _files;
    private Dictionary<string, string> _passwords = new(StringComparer.OrdinalIgnoreCase);

    public NetworkCredentialStore(string path, DurableFileWriter? files = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _files = files ?? DurableFileWriter.Shared;
        Load();
    }

    public string? LoadError { get; private set; }

    public bool HasSaslSecret(NetworkProfileId profileId) =>
        HasSecret(profileId, serverPassword: false);

    public bool HasServerPassword(NetworkProfileId profileId) =>
        HasSecret(profileId, serverPassword: true);

    public string? GetSaslSecret(NetworkProfileId profileId) =>
        GetSecret(profileId, serverPassword: false);

    public string? GetServerPassword(NetworkProfileId profileId) =>
        GetSecret(profileId, serverPassword: true);

    public void SetSaslSecret(NetworkProfileId profileId, string secret, bool allowEmpty = false) =>
        SetSecret(profileId, secret, serverPassword: false, allowEmpty: allowEmpty);

    public void SetServerPassword(NetworkProfileId profileId, string password) =>
        SetSecret(profileId, password, serverPassword: true, allowEmpty: false);

    public bool RemoveSaslSecret(NetworkProfileId profileId) =>
        RemoveSecrets(SecretKey(profileId, serverPassword: false));

    public bool RemoveServerPassword(NetworkProfileId profileId) =>
        RemoveSecrets(SecretKey(profileId, serverPassword: true));

    public bool Remove(NetworkProfileId profileId) =>
        RemoveSecrets(
            SecretKey(profileId, serverPassword: false),
            SecretKey(profileId, serverPassword: true));

    private bool HasSecret(NetworkProfileId profileId, bool serverPassword)
    {
        lock (_gate) return _passwords.ContainsKey(SecretKey(profileId, serverPassword));
    }

    private string? GetSecret(NetworkProfileId profileId, bool serverPassword)
    {
        lock (_gate)
        {
            EnsureReadable();
            if (!_passwords.TryGetValue(SecretKey(profileId, serverPassword), out var encrypted))
                return null;

            try
            {
                var plain = WindowsDataProtection.Unprotect(
                    Convert.FromBase64String(encrypted),
                    Entropy(profileId, serverPassword));
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception exception) when (
                exception is FormatException or ArgumentException or InvalidOperationException)
            {
                var label = serverPassword ? "server password" : "SASL secret";
                throw new InvalidDataException(
                    $"The saved {label} for profile {profileId} could not be decrypted.", exception);
            }
        }
    }

    private void SetSecret(
        NetworkProfileId profileId,
        string secret,
        bool serverPassword,
        bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if ((!allowEmpty && secret.Length < 1) ||
            secret.Length > 16_384 ||
            secret.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            var label = serverPassword ? "server password" : "SASL secret";
            throw new ArgumentException($"The {label} is invalid.", nameof(secret));
        }

        lock (_gate)
        {
            EnsureReadable();
            var candidate = new Dictionary<string, string>(_passwords, StringComparer.OrdinalIgnoreCase)
            {
                [SecretKey(profileId, serverPassword)] =
                    Convert.ToBase64String(WindowsDataProtection.Protect(
                        Encoding.UTF8.GetBytes(secret),
                        Entropy(profileId, serverPassword)))
            };
            Save(candidate);
            _passwords = candidate;
        }
    }

    private bool RemoveSecrets(params string[] keys)
    {
        lock (_gate)
        {
            EnsureReadable();
            var candidate = new Dictionary<string, string>(_passwords, StringComparer.OrdinalIgnoreCase);
            var removed = false;
            foreach (var key in keys)
            {
                removed |= candidate.Remove(key);
            }
            if (!removed) return false;

            Save(candidate);
            _passwords = candidate;
            return true;
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));
            _passwords = loaded is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadError = $"Could not read encrypted network credentials from '{_path}': {exception.Message}";
            _passwords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save(IReadOnlyDictionary<string, string> passwords) =>
        _files.WriteText(
            _path,
            JsonSerializer.Serialize(passwords, new JsonSerializerOptions { WriteIndented = true }),
            retainBackup: true,
            new UTF8Encoding(false));

    private void EnsureReadable()
    {
        if (LoadError is not null)
        {
            throw new InvalidOperationException($"{LoadError} The file was preserved and must be repaired or removed.");
        }
    }

    private static string SecretKey(NetworkProfileId profileId, bool serverPassword) =>
        serverPassword ? $"{profileId}:server" : profileId.ToString();

    private static byte[] Entropy(NetworkProfileId profileId, bool serverPassword) =>
        Encoding.UTF8.GetBytes(serverPassword
            ? $"clircs-network-server:{profileId}"
            : $"clircs-network-sasl:{profileId}");
}
