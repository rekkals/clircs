using Clircs.Infrastructure;

namespace Clircs.ConsoleClient;

internal sealed class BackupRestoreTransaction
{
    private const string PreviousDirectoryName = "previous";

    private readonly BackupRestoreJournal _journal;
    private readonly Action<BackupRestorePhase, string>? _afterMove;

    public BackupRestoreTransaction(
        string dataDirectory,
        DurableFileWriter? files = null,
        Action<BackupRestorePhase, string>? afterMove = null)
    {
        _journal = new BackupRestoreJournal(dataDirectory, files);
        _afterMove = afterMove;
    }

    public void Apply(
        PreparedBackupRestore prepared,
        DataDirectoryLease lease)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureExclusiveFor(_journal.DataDirectory);

        if (!string.Equals(
            prepared.DataDirectory,
            _journal.DataDirectory,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The prepared backup belongs to a different data directory.");
        }

        EnsureSafeDataDirectory();

        if (_journal.Load() is not null)
        {
            throw new IOException(
                "An earlier restore must finish recovery before another restore can begin.");
        }

        var state = new BackupRestoreState(
            Path.GetFileName(prepared.WorkspaceDirectory),
            BackupRestorePhase.SavingOriginals);
        var workspace = _journal.WorkspaceFor(state);

        if (!string.Equals(
            workspace,
            prepared.WorkspaceDirectory,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The prepared backup has an unexpected workspace location.");
        }

        RequireDirectory(workspace);
        RequireDirectory(prepared.StagingDirectory);

        var previous = Path.Combine(workspace, PreviousDirectoryName);
        if (Directory.Exists(previous) || File.Exists(previous))
        {
            throw new IOException(
                "The restore workspace already contains rollback data.");
        }

        var originals = Entries(
            _journal.DataDirectory, excludeBackups: true);
        var staged = Entries(prepared.StagingDirectory);

        Directory.CreateDirectory(previous);
        RequireDirectory(previous);
        prepared.PreserveForRecovery();

        try
        {
            _journal.Save(state, lease);

            foreach (var entry in originals)
            {
                MoveEntry(
                    entry, previous,
                    BackupRestorePhase.SavingOriginals);
            }

            state = state with { Phase = BackupRestorePhase.Installing };
            _journal.Save(state, lease);

            foreach (var entry in staged)
            {
                MoveEntry(
                    entry, _journal.DataDirectory,
                    BackupRestorePhase.Installing);
            }

            state = state with { Phase = BackupRestorePhase.Committed };
            _journal.Save(state, lease);
        }
        catch (Exception restoreFailure) when (
            restoreFailure is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (Recover(lease) is null)
                {
                    // The initial journal write failed before any data moved.
                    CleanupCompleted(workspace, lease);
                }
            }
            catch (Exception recoveryFailure) when (
                recoveryFailure is IOException or
                    UnauthorizedAccessException or InvalidDataException)
            {
                throw new IOException(
                    "Restore failed and recovery could not finish. " +
                    "Restart clircs to retry recovery before using this data directory.",
                    new AggregateException(restoreFailure, recoveryFailure));
            }

            throw new IOException(
                "Restore failed. The original data was preserved.",
                restoreFailure);
        }

        try
        {
            CleanupCompleted(workspace, lease);
        }
        catch (Exception cleanupFailure) when (
            cleanupFailure is IOException or
                UnauthorizedAccessException or InvalidDataException)
        {
            throw new IOException(
                "The backup was restored, but temporary restore files could not be removed. " +
                "Restart clircs to finish cleanup.",
                cleanupFailure);
        }
    }

    public BackupRestorePhase? Recover(DataDirectoryLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureExclusiveFor(_journal.DataDirectory);

        var state = _journal.Load();
        if (state is null) return null;

        EnsureSafeDataDirectory();
        var workspace = _journal.WorkspaceFor(state);

        if (state.Phase is BackupRestorePhase.Committed or BackupRestorePhase.RolledBack)
        {
            CleanupCompleted(workspace, lease);
            return state.Phase;
        }

        RequireDirectory(workspace);
        RequireDirectory(Path.Combine(
            workspace, PreparedBackupRestore.StagingDirectoryName));

        var previous = Path.Combine(workspace, PreviousDirectoryName);
        RequireDirectory(previous);
        var originals = Entries(previous);

        switch (state.Phase)
        {
            case BackupRestorePhase.SavingOriginals:
            case BackupRestorePhase.RestoringOriginals:
                break;

            case BackupRestorePhase.Installing:
                foreach (var entry in Entries(
                    _journal.DataDirectory, excludeBackups: true))
                {
                    DeleteEntry(entry);
                }
                break;

            default:
                throw new InvalidDataException(
                    "The restore recovery information contains an unknown phase.");
        }

        if (state.Phase != BackupRestorePhase.RestoringOriginals)
        {
            state = state with { Phase = BackupRestorePhase.RestoringOriginals };
            _journal.Save(state, lease);
        }

        foreach (var entry in originals)
        {
            MoveEntry(
                entry, _journal.DataDirectory,
                BackupRestorePhase.RestoringOriginals);
        }

        state = state with { Phase = BackupRestorePhase.RolledBack };
        _journal.Save(state, lease);
        CleanupCompleted(workspace, lease);
        return state.Phase;
    }

    private void CleanupCompleted(
        string workspace,
        DataDirectoryLease lease)
    {
        var exists = true;

        try
        {
            RequireDirectory(workspace);
        }
        catch (FileNotFoundException)
        {
            exists = false;
        }
        catch (DirectoryNotFoundException)
        {
            exists = false;
        }

        if (exists)
        {
            DeleteEntry(workspace);
        }

        _journal.Delete(lease);
    }

    private void EnsureSafeDataDirectory()
    {
        var root = _journal.DataDirectory;
        RequireDirectory(root);
        RequireDirectory(_journal.BackupDirectory);
        var application = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(AppContext.BaseDirectory));

        if (string.Equals(root, application, StringComparison.OrdinalIgnoreCase) ||
            application.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                "The clircs data directory must be separate from the running application.");
        }
    }

    private static void RequireDirectory(string path)
    {
        var attributes = File.GetAttributes(path);

        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Restore requires an ordinary directory: {path}");
        }
    }

    private static string[] Entries(
        string directory,
        bool excludeBackups = false)
    {
        var entries = Directory.EnumerateFileSystemEntries(directory)
            .Where(path => !excludeBackups ||
                !Path.GetFileName(path).Equals(
                    "backups", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var entry in entries)
        {
            BackupRestoreArchive.ValidatePath(
                Path.GetFileName(entry), isDirectory: false);
        }

        return entries;
    }

    private void MoveEntry(
        string source,
        string destinationDirectory,
        BackupRestorePhase phase)
    {
        var name = Path.GetFileName(source);
        var destination = Path.Combine(destinationDirectory, name);
        var attributes = File.GetAttributes(source);

        if ((attributes & FileAttributes.Directory) != 0)
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination, overwrite: false);
        }

        _afterMove?.Invoke(phase, name);
    }

    private static void DeleteEntry(string path)
    {
        var attributes = File.GetAttributes(path);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        var isLink = (attributes & FileAttributes.ReparsePoint) != 0;

        if (isDirectory && !isLink)
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            };

            foreach (var entry in new DirectoryInfo(path)
                .EnumerateFileSystemInfos("*", options))
            {
                var entryAttributes = entry.Attributes;

                if ((entryAttributes & FileAttributes.ReparsePoint) == 0 &&
                    (entryAttributes & FileAttributes.ReadOnly) != 0)
                {
                    entry.Attributes = entryAttributes & ~FileAttributes.ReadOnly;
                }
            }
        }

        if (!isLink && (attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }

        if (isDirectory)
        {
            Directory.Delete(path, recursive: true);
        }
        else
        {
            File.Delete(path);
        }
    }
}
