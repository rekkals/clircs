using System.Text.Json;
using Clircs.Infrastructure;

namespace Clircs.Scripting;

internal sealed class ScriptLoadStateStore
{
    private readonly string _path;
    private readonly HashSet<string> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly DurableFileWriter _files;
    private bool _preservedDamagedFile;

    public ScriptLoadStateStore(string path, DurableFileWriter? files = null)
    {
        _path = Path.GetFullPath(path);
        _files = files ?? DurableFileWriter.Shared;
        Load();
    }

    public string? LoadError { get; private set; }

    public IReadOnlyList<string> DesiredLoaded =>
        _loaded.Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public bool SetLoaded(string scriptId, bool loaded)
    {
        var candidate = _loaded.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = loaded ? candidate.Add(scriptId) : candidate.Remove(scriptId);
        if (changed)
        {
            Save(candidate);
            _loaded.Clear();
            _loaded.UnionWith(candidate);
        }
        return changed;
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path))
                ?? throw new InvalidDataException("The file is empty.");
            if (state.SchemaVersion != 1)
            {
                throw new InvalidDataException($"Unsupported schema version {state.SchemaVersion}.");
            }
            if (state.Loaded.Length > 1_000 ||
                state.Loaded.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 64))
            {
                throw new InvalidDataException("The loaded-script list is invalid.");
            }

            foreach (var id in state.Loaded)
            {
                _loaded.Add(id.Trim().ToLowerInvariant());
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            LoadError = $"Script load state was not loaded: {exception.Message}";
            _loaded.Clear();
        }
    }

    private void Save(IReadOnlySet<string> loaded)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        PreserveDamagedFile();
        var state = new State
        {
            Loaded = loaded.Order(StringComparer.OrdinalIgnoreCase).ToArray()
        };
        _files.WriteText(_path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void PreserveDamagedFile()
    {
        if (_preservedDamagedFile || LoadError is null || !File.Exists(_path))
        {
            return;
        }

        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var preserved = Path.Combine(
            Path.GetDirectoryName(_path)!,
            $"{Path.GetFileNameWithoutExtension(_path)}.invalid-{timestamp}{Path.GetExtension(_path)}");
        for (var suffix = 2; File.Exists(preserved); suffix++)
        {
            preserved = Path.Combine(
                Path.GetDirectoryName(_path)!,
                $"{Path.GetFileNameWithoutExtension(_path)}.invalid-{timestamp}-{suffix}{Path.GetExtension(_path)}");
        }
        File.Move(_path, preserved);
        _preservedDamagedFile = true;
    }

    private sealed class State
    {
        public int SchemaVersion { get; set; } = 1;

        public string[] Loaded { get; set; } = [];
    }
}
