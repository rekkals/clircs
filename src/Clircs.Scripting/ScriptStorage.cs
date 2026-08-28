using System.Text.Json;
using Clircs.Infrastructure;

namespace Clircs.Scripting;

internal sealed class ScriptStorage
{
    private const int MaxKeyLength = 128;
    private const int MaxValueLength = 64 * 1024;
    private const int MaxFileLength = 1024 * 1024;
    private readonly string _path;
    private readonly DurableFileWriter _files;
    private Dictionary<string, string> _values;

    public ScriptStorage(string directory, string scriptId, DurableFileWriter? files = null)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, $"{scriptId}.json");
        _files = files ?? DurableFileWriter.Shared;
        _values = Load();
    }

    public string? Get(string key, string? fallback = null)
    {
        ValidateKey(key);
        return _values.TryGetValue(key, out var value) ? value : fallback;
    }

    public void Set(string key, string value)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaxValueLength)
        {
            throw new InvalidOperationException($"Script storage values cannot exceed {MaxValueLength} characters.");
        }

        var candidate = new Dictionary<string, string>(_values, StringComparer.Ordinal) { [key] = value };
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

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var info = new FileInfo(_path);
        if (info.Length > MaxFileLength)
        {
            throw new InvalidDataException($"Script storage file '{_path}' is too large.");
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path))
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void Save(IReadOnlyDictionary<string, string> values)
    {
        var json = JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true });
        if (json.Length > MaxFileLength)
        {
            throw new InvalidOperationException("Script storage has reached its one-megabyte limit.");
        }

        _files.WriteText(_path, json);
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > MaxKeyLength)
        {
            throw new InvalidOperationException($"Script storage keys cannot exceed {MaxKeyLength} characters.");
        }
    }
}
