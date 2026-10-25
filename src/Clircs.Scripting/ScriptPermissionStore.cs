using System.Text.Json;
using Clircs.Infrastructure;

namespace Clircs.Scripting;

internal sealed class ScriptPermissionStore
{
    private readonly string _path;
    private readonly DurableFileWriter _files;
    private Dictionary<string, PermissionState> _permissions;
    private bool _preservedDamagedFile;

    public ScriptPermissionStore(string path, DurableFileWriter? files = null)
    {
        _path = Path.GetFullPath(path);
        _files = files ?? DurableFileWriter.Shared;
        _permissions = Load();
    }

    public string? LoadError { get; private set; }

    public HashSet<ScriptPermission> PreviewGranted(
        string scriptId,
        IEnumerable<ScriptPermission> requested)
    {
        var requestedSet = requested.ToHashSet();
        return _permissions.TryGetValue(scriptId, out var stored)
            ? stored.Granted.Where(requestedSet.Contains).ToHashSet()
            : [];
    }

    public HashSet<ScriptPermission> Unreviewed(
        string scriptId,
        IEnumerable<ScriptPermission> requested)
    {
        var unreviewed = requested.ToHashSet();
        if (_permissions.TryGetValue(scriptId, out var stored))
        {
            unreviewed.ExceptWith(stored.Reviewed);
        }

        return unreviewed;
    }

    public void Approve(string scriptId, IEnumerable<ScriptPermission> requested)
    {
        var requestedSet = requested.ToHashSet();
        var candidate = ClonePermissions();
        if (!candidate.TryGetValue(scriptId, out var values))
        {
            values = new PermissionState([], []);
            candidate.Add(scriptId, values);
        }

        var unreviewed = requestedSet.Except(values.Reviewed).ToHashSet();
        values.Granted.UnionWith(unreviewed);
        values.Reviewed.UnionWith(requestedSet);

        Save(candidate);
        _permissions = candidate;
    }

    public void Set(string scriptId, ScriptPermission permission, bool granted)
    {
        var candidate = ClonePermissions();
        if (!candidate.TryGetValue(scriptId, out var values))
        {
            values = new PermissionState([], []);
            candidate.Add(scriptId, values);
        }

        values.Reviewed.Add(permission);
        if (granted)
        {
            values.Granted.Add(permission);
        }
        else
        {
            values.Granted.Remove(permission);
        }

        Save(candidate);
        _permissions = candidate;
    }

    private Dictionary<string, PermissionState> Load()
    {
        var permissions = new Dictionary<string, PermissionState>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_path))
        {
            return permissions;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The script permissions must be an object.");
            }

            foreach (var entry in document.RootElement.EnumerateObject())
            {
                PermissionState state;
                if (entry.Value.ValueKind == JsonValueKind.Array)
                {
                    // Older files stored grants without permission-review history.
                    state = new PermissionState([], ReadPermissions(entry.Value));
                }
                else if (entry.Value.ValueKind == JsonValueKind.Object &&
                    entry.Value.TryGetProperty("Reviewed", out var reviewedElement) &&
                    entry.Value.TryGetProperty("Granted", out var grantedElement))
                {
                    var reviewed = ReadPermissions(reviewedElement);
                    var granted = ReadPermissions(grantedElement);
                    state = new PermissionState(reviewed, granted);
                }
                else
                {
                    throw new InvalidDataException($"Invalid permissions for script '{entry.Name}'.");
                }

                if (!permissions.TryAdd(entry.Name, state))
                {
                    throw new InvalidDataException($"Duplicate permissions for script '{entry.Name}'.");
                }
            }

            return permissions;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LoadError = $"Script permissions were not loaded: {exception.Message}";
            permissions.Clear();
            return permissions;
        }
    }

    private static HashSet<ScriptPermission> ReadPermissions(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("A script permission list must be an array.");
        }

        var permissions = new HashSet<ScriptPermission>();
        foreach (var value in element.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Script permission names must be strings.");
            }

            if (Enum.TryParse<ScriptPermission>(value.GetString(), true, out var permission) &&
                Enum.IsDefined(permission))
            {
                permissions.Add(permission);
            }
        }

        return permissions;
    }

    private void Save(IReadOnlyDictionary<string, PermissionState> permissions)
    {
        PreserveDamagedFile();
        var stored = permissions.ToDictionary(
            pair => pair.Key,
            pair => new
            {
                Reviewed = FormatPermissions(pair.Value.Reviewed),
                Granted = FormatPermissions(pair.Value.Granted)
            },
            StringComparer.OrdinalIgnoreCase);
        _files.WriteText(_path, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
    }

    private Dictionary<string, PermissionState> ClonePermissions() => _permissions.ToDictionary(
        pair => pair.Key,
        pair => new PermissionState(pair.Value.Reviewed.ToHashSet(), pair.Value.Granted.ToHashSet()),
        StringComparer.OrdinalIgnoreCase);

    private static string[] FormatPermissions(IEnumerable<ScriptPermission> permissions) =>
        permissions.Select(value => value.ToString().ToLowerInvariant()).Order().ToArray();

    private void PreserveDamagedFile()
    {
        if (_preservedDamagedFile || LoadError is null || !File.Exists(_path)) return;
        var preserved = _path + $".invalid-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        for (var suffix = 2; File.Exists(preserved); suffix++) preserved = _path + $".invalid-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{suffix}";
        File.Move(_path, preserved);
        _preservedDamagedFile = true;
    }

    private sealed record PermissionState(
        HashSet<ScriptPermission> Reviewed,
        HashSet<ScriptPermission> Granted);
}
