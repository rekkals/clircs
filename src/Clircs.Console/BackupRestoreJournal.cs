using System.Text.Json;
using System.Text.Json.Serialization;
using Clircs.Infrastructure;

namespace Clircs.ConsoleClient;

internal enum BackupRestorePhase
{
    SavingOriginals,
    Installing,
    RestoringOriginals,
    Committed,
    RolledBack
}

internal sealed record BackupRestoreState(
    string WorkspaceName,
    BackupRestorePhase Phase);

internal sealed class BackupRestoreJournal
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter(allowIntegerValues: false)
        }
    };

    private readonly DurableFileWriter _files;

    public BackupRestoreJournal(
        string dataDirectory,
        DurableFileWriter? files = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        DataDirectory = System.IO.Path.TrimEndingDirectorySeparator(
            System.IO.Path.GetFullPath(dataDirectory));
        BackupDirectory = System.IO.Path.Combine(DataDirectory, "backups");
        Path = System.IO.Path.Combine(BackupDirectory, ".clircs-restore.json");
        _files = files ?? DurableFileWriter.Shared;
    }

    public string DataDirectory { get; }

    public string BackupDirectory { get; }

    public string Path { get; }

    public BackupRestoreState? Load()
    {
        StoredState? stored;

        try
        {
            stored = JsonSerializer.Deserialize<StoredState>(
                File.ReadAllText(Path), JsonOptions);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The restore recovery information is invalid.", exception);
        }

        if (stored is null ||
            stored.SchemaVersion != CurrentSchemaVersion ||
            stored.WorkspaceName is null ||
            stored.Phase is null)
        {
            throw new InvalidDataException(
                "The restore recovery information is incomplete or uses an unsupported schema.");
        }

        var state = new BackupRestoreState(
            stored.WorkspaceName, stored.Phase.Value);
        ValidateState(state);
        return state;
    }

    public void Save(BackupRestoreState state, DataDirectoryLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureExclusiveFor(DataDirectory);
        ValidateState(state);

        var stored = new StoredState
        {
            SchemaVersion = CurrentSchemaVersion,
            WorkspaceName = state.WorkspaceName,
            Phase = state.Phase
        };

        _files.WriteText(
            Path,
            JsonSerializer.Serialize(stored, JsonOptions),
            retainBackup: false);
    }

    public void Delete(DataDirectoryLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureExclusiveFor(DataDirectory);
        File.Delete(Path);
    }

    public string WorkspaceFor(BackupRestoreState state)
    {
        ValidateState(state);
        return System.IO.Path.Combine(BackupDirectory, state.WorkspaceName);
    }

    private static void ValidateState(BackupRestoreState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!Enum.IsDefined(state.Phase))
        {
            throw new InvalidDataException(
                "The restore recovery information contains an unknown phase.");
        }

        var name = state.WorkspaceName;
        var prefix = PreparedBackupRestore.WorkspacePrefix;
        var suffix = PreparedBackupRestore.WorkspaceSuffix;

        if (string.IsNullOrEmpty(name) ||
            name.Length != prefix.Length + 32 + suffix.Length ||
            !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(suffix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name.Substring(prefix.Length, 32), "N", out _))
        {
            throw new InvalidDataException(
                "The restore recovery information contains an invalid workspace name.");
        }
    }

    private sealed class StoredState
    {
        public int SchemaVersion { get; set; }

        public string? WorkspaceName { get; set; }

        public BackupRestorePhase? Phase { get; set; }
    }
}
