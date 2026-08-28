using System.Text.Json;
using Clircs.Infrastructure;

namespace Clircs.Scripting;

internal sealed class ScriptPermissionStore
{
    private readonly string _path;
    private readonly DurableFileWriter _files;
    private Dictionary<string, HashSet<ScriptPermission>> _grants;
    private bool _preservedDamagedFile;

    public ScriptPermissionStore(string path, DurableFileWriter? files = null)
    {
        _path = Path.GetFullPath(path);
        _files = files ?? DurableFileWriter.Shared;
        _grants = Load();
    }

    public string? LoadError { get; private set; }

    public HashSet<ScriptPermission> PreviewGranted(string scriptId, IEnumerable<ScriptPermission> requested)
    {
        var requestedSet = requested.ToHashSet();
        if (!_grants.TryGetValue(scriptId, out var stored))
        {
            return DefaultGrants(requestedSet);
        }

        return stored.Where(requestedSet.Contains).ToHashSet();
    }

    public HashSet<ScriptPermission> GetOrCreateGranted(
        string scriptId,
        IEnumerable<ScriptPermission> requested)
    {
        var requestedSet = requested.ToHashSet();
        if (_grants.TryGetValue(scriptId, out var stored))
        {
            return stored.Where(requestedSet.Contains).ToHashSet();
        }

        var granted = DefaultGrants(requestedSet);
        var candidate = CloneGrants();
        candidate[scriptId] = granted;
        Save(candidate);
        _grants = candidate;
        return granted.ToHashSet();
    }

    public void Set(string scriptId, ScriptPermission permission, bool granted)
    {
        var candidate = CloneGrants();
        if (!candidate.TryGetValue(scriptId, out var values))
        {
            values = [];
            candidate.Add(scriptId, values);
        }

        if (granted)
        {
            values.Add(permission);
        }
        else
        {
            values.Remove(permission);
        }

        Save(candidate);
        _grants = candidate;
    }

    private Dictionary<string, HashSet<ScriptPermission>> Load()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, HashSet<ScriptPermission>>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(_path)) ?? [];
            return stored.ToDictionary(
                pair => pair.Key,
                pair => pair.Value
                    .Select(value => Enum.TryParse<ScriptPermission>(value, true, out var permission) ? permission : (ScriptPermission?)null)
                    .Where(value => value.HasValue)
                    .Select(value => value!.Value)
                    .ToHashSet(),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LoadError = $"Script permissions were not loaded: {exception.Message}";
            return new Dictionary<string, HashSet<ScriptPermission>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save(IReadOnlyDictionary<string, HashSet<ScriptPermission>> grants)
    {
        PreserveDamagedFile();
        var stored = grants.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Select(value => value.ToString().ToLowerInvariant()).Order().ToArray(),
            StringComparer.OrdinalIgnoreCase);
        _files.WriteText(_path, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
    }

    private Dictionary<string, HashSet<ScriptPermission>> CloneGrants() => _grants.ToDictionary(
        pair => pair.Key,
        pair => pair.Value.ToHashSet(),
        StringComparer.OrdinalIgnoreCase);

    private static HashSet<ScriptPermission> DefaultGrants(IEnumerable<ScriptPermission> requested)
    {
        var granted = requested.ToHashSet();
        granted.Remove(ScriptPermission.Irc);
        granted.Remove(ScriptPermission.LocalNetwork);
        return granted;
    }

    private void PreserveDamagedFile()
    {
        if (_preservedDamagedFile || LoadError is null || !File.Exists(_path)) return;
        var preserved = _path + $".invalid-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        for (var suffix = 2; File.Exists(preserved); suffix++) preserved = _path + $".invalid-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{suffix}";
        File.Move(_path, preserved);
        _preservedDamagedFile = true;
    }
}
