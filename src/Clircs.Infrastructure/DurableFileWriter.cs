using System.Text;

namespace Clircs.Infrastructure;

/// <summary>
/// Commits small persistent-state files without exposing a partially written file.
/// It also provides the process-wide boundary used by backups to take a coherent snapshot.
/// </summary>
public sealed class DurableFileWriter
{
    private static readonly ReaderWriterLockSlim PersistenceLock = new(LockRecursionPolicy.SupportsRecursion);

    public static DurableFileWriter Shared { get; } = new();

    private readonly Action<string>? _beforeCommit;

    public DurableFileWriter()
    {
    }

    internal DurableFileWriter(Action<string> beforeCommit)
    {
        _beforeCommit = beforeCommit;
    }

    public void WriteText(string path, string contents, bool retainBackup = true, Encoding? encoding = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The persistent file has no parent directory");
        Directory.CreateDirectory(directory);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";

        PersistenceLock.EnterWriteLock();
        try
        {
            using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, encoding ?? new UTF8Encoding(false)))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            _beforeCommit?.Invoke(path);
            if (File.Exists(path) && retainBackup)
            {
                File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, path, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            PersistenceLock.ExitWriteLock();
        }
    }

    public static T ReadSnapshot<T>(Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        PersistenceLock.EnterReadLock();
        try
        {
            return read();
        }
        finally
        {
            PersistenceLock.ExitReadLock();
        }
    }
}
