using System.IO.Compression;

namespace Clircs.ConsoleClient;

internal static class BackupRestoreArchive
{
    private static readonly uint[] Crc32Table = CreateCrc32Table();

    private static readonly HashSet<string> ReservedDeviceNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "CLOCK$", "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5",
            "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5",
            "LPT6", "LPT7", "LPT8", "LPT9",
            "COM¹", "COM²", "COM³",
            "LPT¹", "LPT²", "LPT³"
        };

    public static IReadOnlyList<ValidatedEntry> Validate(ZipArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var paths = new Dictionary<string, ValidatedEntry>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            var isDirectory = entry.FullName.EndsWith('/');
            var relativePath = ValidatePath(entry.FullName, isDirectory);
            ValidateEntryType(entry, isDirectory);

            if (!paths.TryAdd(
                    relativePath,
                    new ValidatedEntry(entry, relativePath, isDirectory)))
            {
                throw new InvalidDataException(
                    "The backup contains duplicate paths on Windows.");
            }
        }

        foreach (var entry in paths.Values)
        {
            var path = entry.RelativePath;
            for (var separator = path.IndexOf('/');
                 separator >= 0;
                 separator = path.IndexOf('/', separator + 1))
            {
                if (paths.TryGetValue(path[..separator], out var parent) &&
                    !parent.IsDirectory)
                {
                    throw new InvalidDataException(
                        "The backup contains a file where a directory is required.");
                }
            }
        }

        return paths.Values.ToArray();
    }

    public static void Extract(
        ZipArchive archive,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var entries = Validate(archive);
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(destinationDirectory));

        if (!Directory.Exists(root) ||
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
            Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new IOException(
                "The restore staging directory must exist, be empty, and not be a link.");
        }

        var prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            root
        };

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destination = Path.GetFullPath(Path.Combine(
                root,
                entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

            if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The backup contains a path outside its staging directory.");
            }

            var directory = entry.IsDirectory
                ? destination
                : Path.GetDirectoryName(destination)!;
            EnsureDirectory(directory, directories, cancellationToken);

            if (entry.IsDirectory) continue;

            using (var target = new FileStream(
                destination, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.SequentialScan))
            {
                CopyEntry(entry.Source, target, cancellationToken);
                target.Flush(flushToDisk: true);
            }

            File.SetLastWriteTimeUtc(
                destination, entry.Source.LastWriteTime.UtcDateTime);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public static void CopyEntry(
        ZipArchiveEntry entry,
        Stream target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        using var source = entry.Open();
        var remaining = entry.Length;
        var checksum = uint.MaxValue;
        var buffer = new byte[81920];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) break;

            if (read > remaining)
            {
                throw new InvalidDataException(
                    $"The backup entry exceeds its saved length: {entry.FullName}");
            }

            for (var index = 0; index < read; index++)
            {
                var tableIndex = (int)((checksum ^ buffer[index]) & 0xFF);
                checksum = Crc32Table[tableIndex] ^ (checksum >> 8);
            }

            target.Write(buffer, 0, read);
            remaining -= read;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (remaining != 0 || ~checksum != entry.Crc32)
        {
            throw new InvalidDataException(
                $"The backup contains damaged file data: {entry.FullName}");
        }
    }

    private static void EnsureDirectory(
        string path,
        HashSet<string> directories,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        var current = path;

        while (!directories.Contains(current))
        {
            pending.Push(current);
            current = Path.GetDirectoryName(current)
                ?? throw new InvalidDataException(
                    "The backup contains a path outside its staging directory.");
        }

        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // An existing path we did not create can be a Windows alias
            // for another archive entry.
            if (Directory.Exists(directory) || File.Exists(directory))
            {
                throw new InvalidDataException(
                    "The backup contains directory paths that conflict on Windows.");
            }

            Directory.CreateDirectory(directory);
            directories.Add(directory);
        }
    }

    private static uint[] CreateCrc32Table()
    {
        var table = new uint[256];

        for (var index = 0; index < table.Length; index++)
        {
            var checksum = (uint)index;

            for (var bit = 0; bit < 8; bit++)
            {
                // Reflected CRC-32 polynomial used by ZIP.
                checksum = (checksum & 1) == 0
                    ? checksum >> 1
                    : (checksum >> 1) ^ 0xEDB88320u;
            }

            table[index] = checksum;
        }

        return table;
    }

    public static string ValidatePath(string name, bool isDirectory)
    {
        // clircs backups use forward slashes. Reject ambiguous separator forms
        // rather than translating potentially unsafe archive paths.
        if (name.Length == 0 || name.Contains('\\'))
        {
            throw new InvalidDataException(
                "The backup contains an unsafe entry path.");
        }

        var relativePath = isDirectory ? name[..^1] : name;
        var segments = relativePath.Split('/');

        foreach (var segment in segments)
        {
            if (segment.Length == 0 ||
                segment is "." or ".." ||
                segment.EndsWith(' ') ||
                segment.EndsWith('.') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                segment.Any(char.IsControl))
            {
                throw new InvalidDataException(
                    "The backup contains an unsafe entry path.");
            }

            var period = segment.IndexOf('.');
            var stem = (period < 0 ? segment : segment[..period])
                .TrimEnd(' ', '.');

            if (ReservedDeviceNames.Contains(stem))
            {
                throw new InvalidDataException(
                    "The backup contains a reserved Windows device name.");
            }
        }

        if (segments[0].Equals("backups", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The backup must not contain the protected backups directory.");
        }

        return relativePath;
    }

    private static void ValidateEntryType(
        ZipArchiveEntry entry,
        bool isDirectory)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);

        if ((windowsAttributes & FileAttributes.ReparsePoint) != 0 ||
            unixType is not (0 or 0x8000 or 0x4000))
        {
            throw new InvalidDataException(
                "The backup contains a link or an unsupported entry type.");
        }

        if ((unixType == 0x4000 && !isDirectory) ||
            (unixType == 0x8000 && isDirectory) ||
            ((windowsAttributes & FileAttributes.Directory) != 0 && !isDirectory) ||
            (isDirectory && entry.Length != 0))
        {
            throw new InvalidDataException(
                "The backup contains an invalid directory entry.");
        }
    }

    internal sealed record ValidatedEntry(
        ZipArchiveEntry Source,
        string RelativePath,
        bool IsDirectory);
}
