using System.Text.Json;
using System.Text.RegularExpressions;
using Clircs.Sessions;

namespace Clircs.Scripting;

public sealed partial class ScriptManager : IAsyncDisposable
{
    private const int ErrorLimit = 100;
    private readonly string _scriptsDirectory;
    private readonly string? _installedScriptsDirectory;
    private readonly string _storageDirectory;
    private readonly string _secretDirectory;
    private readonly ScriptHostServices _services;
    private readonly ScriptPermissionStore _permissionStore;
    private readonly ScriptLoadStateStore _loadStateStore;
    private readonly Dictionary<string, ScriptInstance> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _loadedGate = new();
    private readonly List<ScriptError> _errors = [];
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    public ScriptManager(string dataDirectory, ScriptHostServices services, string? installedScriptsDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _services = services ?? throw new ArgumentNullException(nameof(services));
        var root = Path.GetFullPath(dataDirectory);
        _scriptsDirectory = Path.Combine(root, "scripts");
        _installedScriptsDirectory = string.IsNullOrWhiteSpace(installedScriptsDirectory)
            ? null
            : Path.GetFullPath(installedScriptsDirectory);
        _storageDirectory = Path.Combine(root, "script-data");
        _secretDirectory = Path.Combine(root, "script-secrets");
        Directory.CreateDirectory(_scriptsDirectory);
        Directory.CreateDirectory(_storageDirectory);
        Directory.CreateDirectory(_secretDirectory);
        _permissionStore = new ScriptPermissionStore(Path.Combine(root, "script-permissions.json"));
        _loadStateStore = new ScriptLoadStateStore(Path.Combine(root, "script-load-state.json"));
    }

    public string ScriptsDirectory => _scriptsDirectory;

    public IReadOnlyList<ScriptError> Errors
    {
        get
        {
            lock (_errors)
            {
                return _errors.ToArray();
            }
        }
    }

    public IReadOnlyList<ScriptInfo> List()
    {
        var discovered = Discover();
        Dictionary<string, ScriptInstance> loaded;
        lock (_loadedGate)
        {
            loaded = new Dictionary<string, ScriptInstance>(_loaded, StringComparer.OrdinalIgnoreCase);
        }

        return discovered.Keys
            .Concat(loaded.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(scriptId =>
            {
                if (loaded.TryGetValue(scriptId, out var instance))
                {
                    return instance.Describe(discovered.ContainsKey(scriptId));
                }

                var script = discovered[scriptId];
                var requested = ParsePermissions(script.Manifest);
                var granted = _permissionStore.PreviewGranted(script.Manifest.Id, requested);
                return new ScriptInfo(
                    script.Manifest.Id,
                    script.Manifest.Name,
                    script.Manifest.Version,
                    "unloaded",
                    requested.Order().ToArray(),
                    granted.Order().ToArray(),
                    null);
            })
            .OrderBy(script => script.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async ValueTask<ScriptInfo> LoadAsync(string scriptId, CancellationToken cancellationToken = default)
    {
        scriptId = NormalizeId(scriptId);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = LoadCore(scriptId);
            _loadStateStore.SetLoaded(scriptId, loaded: true);
            return loaded;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask<bool> UnloadAsync(string scriptId, CancellationToken cancellationToken = default)
    {
        scriptId = NormalizeId(scriptId);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var unloaded = await UnloadCoreAsync(scriptId).ConfigureAwait(false);
            var forgotten = _loadStateStore.SetLoaded(scriptId, loaded: false);
            return unloaded || forgotten;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask<ScriptInfo> ReloadAsync(string scriptId, CancellationToken cancellationToken = default)
    {
        scriptId = NormalizeId(scriptId);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _loadStateStore.SetLoaded(scriptId, loaded: true);
            await UnloadCoreAsync(scriptId).ConfigureAwait(false);
            return LoadCore(scriptId);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask<IReadOnlyList<string>> RestoreLoadedAsync(CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        if (_loadStateStore.LoadError is not null)
        {
            failures.Add(_loadStateStore.LoadError);
        }
        if (_permissionStore.LoadError is not null)
        {
            failures.Add(_permissionStore.LoadError + " Requested IRC and local-network permissions were denied");
        }

        foreach (var scriptId in _loadStateStore.DesiredLoaded)
        {
            try
            {
                await LoadAsync(scriptId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or
                InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                failures.Add($"Script '{scriptId}' was not restored: {exception.Message}");
            }
        }

        return failures;
    }

    public async ValueTask SetPermissionAsync(
        string scriptId,
        ScriptPermission permission,
        bool granted,
        CancellationToken cancellationToken = default)
    {
        scriptId = NormalizeId(scriptId);
        var discovered = Discover();
        if (!discovered.TryGetValue(scriptId, out var script))
        {
            throw new FileNotFoundException($"Script '{scriptId}' was not found.");
        }

        var requested = ParsePermissions(script.Manifest);
        if (!requested.Contains(permission))
        {
            throw new InvalidOperationException($"Script '{scriptId}' does not request '{permission.ToString().ToLowerInvariant()}'.");
        }

        _ = _permissionStore.GetOrCreateGranted(scriptId, requested);
        _permissionStore.Set(scriptId, permission, granted);
        bool isLoaded;
        lock (_loadedGate)
        {
            isLoaded = _loaded.ContainsKey(scriptId);
        }

        if (isLoaded)
        {
            await ReloadAsync(scriptId, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Publish(SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        ScriptInstance[] instances;
        lock (_loadedGate)
        {
            instances = _loaded.Values.ToArray();
        }

        foreach (var instance in instances)
        {
            instance.Publish(sessionEvent);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            ScriptInstance[] instances;
            lock (_loadedGate)
            {
                instances = _loaded.Values.ToArray();
                _loaded.Clear();
            }

            foreach (var instance in instances)
            {
                await instance.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycle.Release();
            _lifecycle.Dispose();
        }
    }

    private Dictionary<string, DiscoveredScript> Discover()
    {
        var scripts = new Dictionary<string, DiscoveredScript>(StringComparer.OrdinalIgnoreCase);
        var roots = new[] { _scriptsDirectory, _installedScriptsDirectory }
            .Where(root => root is not null && Directory.Exists(root))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            foreach (var directory in Directory.EnumerateDirectories(root!))
            {
                var manifestPath = Path.Combine(directory, "clircs-script.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                try
                {
                    var manifest = JsonSerializer.Deserialize<ScriptManifest>(
                        File.ReadAllText(manifestPath),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? throw new InvalidDataException("Manifest is empty.");
                    ValidateManifest(manifest);
                    scripts.TryAdd(manifest.Id, new DiscoveredScript(directory, manifest));
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
                {
                    RecordError(new ScriptError(DateTimeOffset.UtcNow, Path.GetFileName(directory), "manifest", exception.Message));
                }
            }
        }

        return scripts;
    }

    private ScriptInfo LoadCore(string scriptId)
    {
        lock (_loadedGate)
        {
            if (_loaded.ContainsKey(scriptId))
            {
                throw new InvalidOperationException($"Script '{scriptId}' is already loaded.");
            }
        }

        var discovered = Discover();
        if (!discovered.TryGetValue(scriptId, out var script))
        {
            throw new FileNotFoundException($"Script '{scriptId}' was not found in '{_scriptsDirectory}'.");
        }

        var requested = ParsePermissions(script.Manifest);
        var granted = _permissionStore.GetOrCreateGranted(scriptId, requested);
        ScriptInstance instance;
        try
        {
            instance = new ScriptInstance(
                script,
                requested,
                granted,
                _services,
                _storageDirectory,
                _secretDirectory,
                RecordError);
        }
        catch (Exception exception)
        {
            RecordError(new ScriptError(DateTimeOffset.UtcNow, scriptId, "load", exception.Message));
            throw new InvalidOperationException($"Could not load script '{scriptId}': {exception.Message}", exception);
        }

        lock (_loadedGate)
        {
            _loaded.Add(scriptId, instance);
        }
        return new ScriptInfo(
            scriptId,
            script.Manifest.Name,
            script.Manifest.Version,
            "loaded",
            requested.Order().ToArray(),
            granted.Order().ToArray(),
            null);
    }

    private async ValueTask<bool> UnloadCoreAsync(string scriptId)
    {
        ScriptInstance? instance;
        lock (_loadedGate)
        {
            if (!_loaded.Remove(scriptId, out instance))
            {
                return false;
            }
        }

        await instance.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    private static void ValidateManifest(ScriptManifest manifest)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidDataException("Only script manifest schema version 1 is supported.");
        }

        manifest.Id = NormalizeId(manifest.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.Entry);
        if (manifest.Name.Length > 100 || manifest.Version.Length > 40 || manifest.Entry.Length > 200)
        {
            throw new InvalidDataException("Script manifest contains an overlong name, version, or entry point.");
        }

        _ = ParsePermissions(manifest);
    }

    private static HashSet<ScriptPermission> ParsePermissions(ScriptManifest manifest)
    {
        var permissions = new HashSet<ScriptPermission>();
        foreach (var value in manifest.Permissions ?? [])
        {
            if (!Enum.TryParse<ScriptPermission>(value, true, out var permission))
            {
                throw new InvalidDataException($"Unknown script permission '{value}'.");
            }

            permissions.Add(permission);
        }

        return permissions;
    }

    private static string NormalizeId(string scriptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        scriptId = scriptId.Trim().ToLowerInvariant();
        if (!ScriptIdPattern().IsMatch(scriptId))
        {
            throw new InvalidDataException("Script ids must contain 1-64 lowercase letters, digits, dots, underscores, or hyphens.");
        }

        return scriptId;
    }

    private void RecordError(ScriptError error)
    {
        lock (_errors)
        {
            _errors.Add(error);
            if (_errors.Count > ErrorLimit)
            {
                _errors.RemoveRange(0, _errors.Count - ErrorLimit);
            }
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptIdPattern();
}
