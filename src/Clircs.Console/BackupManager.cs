using System.Globalization;
using System.IO.Compression;
using Clircs.Infrastructure;

namespace Clircs.ConsoleClient;

internal sealed class BackupManager
{
    private readonly string _dataDirectory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action? _afterSnapshot;

    public BackupManager(
        string dataDirectory,
        Func<DateTimeOffset>? clock = null,
        Action? afterSnapshot = null)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory);
        BackupDirectory = Path.Combine(_dataDirectory, "backups");
        _clock = clock ?? (() => DateTimeOffset.Now);
        _afterSnapshot = afterSnapshot;
    }

    public string BackupDirectory { get; }

    public string Create()
    {
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(BackupDirectory);
        var timestamp = _clock().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var destination = UniqueDestination($"clircs-backup-{timestamp}");
        var temporary = Path.Combine(
            BackupDirectory, $".clircs-backup-{Guid.NewGuid():N}.tmp");
        var snapshotDirectory = temporary + ".files";

        try
        {
            Directory.CreateDirectory(snapshotDirectory);
            var snapshot = DurableFileWriter.ReadSnapshot(
                () => CaptureSnapshot(snapshotDirectory));
            _afterSnapshot?.Invoke();

            using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var buffer = new byte[81920];
                foreach (var file in snapshot)
                {
                    var entry = archive.CreateEntry(
                        file.RelativePath, CompressionLevel.Optimal);
                    entry.LastWriteTime = file.LastWriteTime;
                    using var target = entry.Open();
                    CopySnapshotFile(file, target, buffer);
                }
            }

            ValidateArchive(temporary);
            Directory.Delete(snapshotDirectory, recursive: true);
            File.Move(temporary, destination);
            return destination;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            finally
            {
                if (Directory.Exists(snapshotDirectory))
                    Directory.Delete(snapshotDirectory, recursive: true);
            }
        }
    }

    private IReadOnlyList<SnapshotFile> CaptureSnapshot(string snapshotDirectory)
    {
        var snapshot = new List<SnapshotFile>();

        foreach (var file in BackupFiles(_dataDirectory))
        {
            var relative = Path.GetRelativePath(_dataDirectory, file).Replace('\\', '/');
            var information = new FileInfo(file);
            var length = information.Length;
            var lastWriteTime = information.LastWriteTime;
            var source = file;

            if (!relative.StartsWith("logs/", StringComparison.OrdinalIgnoreCase))
            {
                source = Path.Combine(snapshotDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                File.Copy(file, source);
                length = new FileInfo(source).Length;
            }

            // Logs are append-only. Their captured length ends at a completed
            // append because the persistence lock is held throughout capture.
            snapshot.Add(new SnapshotFile(relative, source, length, lastWriteTime));
        }

        return snapshot;
    }

    private static void CopySnapshotFile(
        SnapshotFile file,
        Stream target,
        byte[] buffer)
    {
        using var source = new FileStream(
            file.SourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var remaining = file.Length;

        while (remaining > 0)
        {
            var read = source.Read(
                buffer, 0, (int)Math.Min(remaining, buffer.Length));
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Backup source became shorter: {file.RelativePath}");
            }

            target.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static void ValidateArchive(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        foreach (var entry in archive.Entries)
        {
            using var source = entry.Open();
            source.CopyTo(Stream.Null);
        }
    }

    public string ResolveRestorePath(string requestedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);
        var path = Environment.ExpandEnvironmentVariables(requestedPath);

        if (Path.IsPathFullyQualified(path))
        {
            return Path.GetFullPath(path);
        }

        if (path.IndexOfAny(['/', '\\', ':']) >= 0 ||
            path is "." or "..")
        {
            throw new ArgumentException(
                "Use a backup filename or a full path.");
        }

        return Path.GetFullPath(Path.Combine(BackupDirectory, path));
    }

    public IReadOnlyList<FileInfo> List() =>
        Directory.Exists(BackupDirectory)
            ? new DirectoryInfo(BackupDirectory)
                .EnumerateFiles("clircs-backup-*.zip", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray()
            : [];

    private string UniqueDestination(string stem)
    {
        var candidate = Path.Combine(BackupDirectory, stem + ".zip");
        for (var suffix = 2; File.Exists(candidate) || File.Exists(candidate + ".tmp"); suffix++)
        {
            candidate = Path.Combine(BackupDirectory, $"{stem}-{suffix}.zip");
        }
        return candidate;
    }

    private static IEnumerable<string> BackupFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (directory.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(child).Equals("backups", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (!Path.GetExtension(file).Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }

    private sealed record SnapshotFile(
        string RelativePath,
        string SourcePath,
        long Length,
        DateTimeOffset LastWriteTime);
}
