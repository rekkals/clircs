using System.IO.Compression;

namespace Clircs.ConsoleClient;

internal sealed class PreparedBackupRestore : IDisposable
{
    public const string WorkspacePrefix = ".clircs-restore-";
    public const string WorkspaceSuffix = ".tmp";
    public const string StagingDirectoryName = "data";
    private bool _preserveForRecovery;
    private bool _disposed;

    private PreparedBackupRestore(
        string dataDirectory,
        string archivePath,
        string workspaceDirectory)
    {
        DataDirectory = dataDirectory;
        ArchivePath = archivePath;
        WorkspaceDirectory = workspaceDirectory;
        StagingDirectory = Path.Combine(workspaceDirectory, StagingDirectoryName);
    }

    public string DataDirectory { get; }

    public string ArchivePath { get; }

    public string WorkspaceDirectory { get; }

    public string StagingDirectory { get; }

    public static PreparedBackupRestore Create(
        string dataDirectory,
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        cancellationToken.ThrowIfCancellationRequested();

        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(dataDirectory));
        var sourcePath = Path.GetFullPath(archivePath);

        if (!Directory.Exists(root) || Path.GetDirectoryName(root) is null)
        {
            throw new IOException(
                "Restore requires an existing data directory below the filesystem root.");
        }

        var backups = Path.Combine(root, "backups");
        var workspace = Path.Combine(
            backups, $"{WorkspacePrefix}{Guid.NewGuid():N}{WorkspaceSuffix}");

        if (Directory.Exists(workspace) || File.Exists(workspace))
        {
            throw new IOException(
                "Unable to allocate a temporary restore directory.");
        }

        var prepared = new PreparedBackupRestore(root, sourcePath, workspace);

        try
        {
            using var source = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(source, ZipArchiveMode.Read);

            Directory.CreateDirectory(backups);
            if ((File.GetAttributes(backups) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "The backups directory must not be a link.");
            }

            Directory.CreateDirectory(prepared.StagingDirectory);
            BackupRestoreArchive.Extract(
                archive, prepared.StagingDirectory, cancellationToken);

            return prepared;
        }
        catch (Exception preparationFailure)
        {
            try
            {
                prepared.Dispose();
            }
            catch (Exception cleanupFailure) when (
                cleanupFailure is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Restore preparation failed and its temporary files could not be removed: {workspace}",
                    new AggregateException(preparationFailure, cleanupFailure));
            }

            throw;
        }
    }

    public void PreserveForRecovery()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _preserveForRecovery = true;
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (!_preserveForRecovery)
        {
            try
            {
                Directory.Delete(WorkspaceDirectory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // The workspace was never created or has already been removed.
            }
        }

        _disposed = true;
    }
}
