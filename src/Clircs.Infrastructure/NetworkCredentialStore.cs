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

    public bool HasSaslSecret(NetworkProfileId profileId)
    {
        lock (_gate) return _passwords.ContainsKey(profileId.ToString());
    }

    public string? GetSaslSecret(NetworkProfileId profileId)
    {
        lock (_gate)
        {
            EnsureReadable();
            if (!_passwords.TryGetValue(profileId.ToString(), out var encrypted)) return null;
            try
            {
                var plain = WindowsDataProtection.Unprotect(
                    Convert.FromBase64String(encrypted),
                    Entropy(profileId));
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception exception) when (exception is FormatException or InvalidOperationException)
            {
                throw new InvalidDataException(
                    $"The saved SASL secret for profile {profileId} could not be decrypted.", exception);
            }
        }
    }

    public void SetSaslSecret(NetworkProfileId profileId, string secret, bool allowEmpty = false)
    {
        if ((!allowEmpty && secret.Length < 1) || secret.Length > 16_384 || secret.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("The SASL secret is invalid.", nameof(secret));
        }
        lock (_gate)
        {
            EnsureReadable();
            var candidate = new Dictionary<string, string>(_passwords, StringComparer.OrdinalIgnoreCase)
            {
                [profileId.ToString()] = Convert.ToBase64String(WindowsDataProtection.Protect(
                    Encoding.UTF8.GetBytes(secret),
                    Entropy(profileId)))
            };
            Save(candidate);
            _passwords = candidate;
        }
    }

    public bool Remove(NetworkProfileId profileId)
    {
        lock (_gate)
        {
            EnsureReadable();
            var candidate = new Dictionary<string, string>(_passwords, StringComparer.OrdinalIgnoreCase);
            if (!candidate.Remove(profileId.ToString())) return false;
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

    private static byte[] Entropy(NetworkProfileId profileId) =>
        Encoding.UTF8.GetBytes($"clircs-network-sasl:{profileId}");
}
