using System.Globalization;
using System.IO.Compression;
using Clircs.Infrastructure;

namespace Clircs.ConsoleClient;

internal sealed class BackupManager
{
    private readonly string _dataDirectory;
    private readonly Func<DateTimeOffset> _clock;

    public BackupManager(string dataDirectory, Func<DateTimeOffset>? clock = null)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory);
        BackupDirectory = Path.Combine(_dataDirectory, "backups");
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public string BackupDirectory { get; }

    public string Create()
        => DurableFileWriter.ReadSnapshot(CreateSnapshot);

    private string CreateSnapshot()
    {
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(BackupDirectory);
        var timestamp = _clock().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var destination = UniqueDestination($"clircs-backup-{timestamp}");
        var temporary = destination + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var file in BackupFiles(_dataDirectory))
                {
                    var relative = Path.GetRelativePath(_dataDirectory, file).Replace('\\', '/');
                    var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                    entry.LastWriteTime = File.GetLastWriteTime(file);
                    using var source = new FileStream(
                        file, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var target = entry.Open();
                    source.CopyTo(target);
                }
            }
            ValidateArchive(temporary);
            File.Move(temporary, destination);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
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
                if ((Path.GetFileName(child).Equals("backups", StringComparison.OrdinalIgnoreCase) ||
                     Path.GetFileName(child).Equals("logs", StringComparison.OrdinalIgnoreCase)) &&
                    Path.GetDirectoryName(child)!.Equals(root, StringComparison.OrdinalIgnoreCase))
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
}
