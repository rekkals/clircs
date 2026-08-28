using System.Text;
using System.Text.Json;
using Clircs.Infrastructure;

namespace Clircs.Scripting;

internal sealed class ScriptSecretStorage
{
    private readonly string _path;
    private readonly string _scriptId;
    private readonly DurableFileWriter _files;
    private Dictionary<string, string> _values;

    public ScriptSecretStorage(string directory, string scriptId, DurableFileWriter? files = null)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, $"{scriptId}.json");
        _scriptId = scriptId;
        _files = files ?? DurableFileWriter.Shared;
        _values = Load();
    }

    public string? Get(string key)
    {
        ValidateKey(key);
        if (!_values.TryGetValue(key, out var encrypted))
        {
            return null;
        }

        return Encoding.UTF8.GetString(WindowsDataProtection.Unprotect(Convert.FromBase64String(encrypted), Entropy()));
    }

    public void Set(string key, string value)
    {
        ValidateKey(key);
        if (value.Length > 16_384)
        {
            throw new InvalidOperationException("Script secrets cannot exceed 16,384 characters.");
        }

        var candidate = new Dictionary<string, string>(_values, StringComparer.Ordinal)
        {
            [key] = Convert.ToBase64String(WindowsDataProtection.Protect(Encoding.UTF8.GetBytes(value), Entropy()))
        };
        Save(candidate);
        _values = candidate;
    }

    public bool Remove(string key)
    {
        ValidateKey(key);
        var candidate = new Dictionary<string, string>(_values, StringComparer.Ordinal);
        if (!candidate.Remove(key))
        {
            return false;
        }

        Save(candidate);
        _values = candidate;
        return true;
    }

    private byte[] Entropy() => Encoding.UTF8.GetBytes($"clircs-script:{_scriptId}");

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path))
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Script secret file '{_path}' is invalid.", exception);
        }
    }

    private void Save(IReadOnlyDictionary<string, string> values)
    {
        _files.WriteText(_path, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
        {
            throw new InvalidOperationException("Script secret keys must contain 1-128 characters.");
        }
    }

}
