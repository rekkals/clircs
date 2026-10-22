namespace Clircs.ConsoleClient;

internal sealed class DataDirectoryLease : IDisposable
{
    private readonly FileStream _file;
    private bool _disposed;

    private DataDirectoryLease(
        string dataDirectory,
        bool isExclusive,
        FileStream file)
    {
        DataDirectory = dataDirectory;
        IsExclusive = isExclusive;
        _file = file;
    }

    public string DataDirectory { get; }

    public bool IsExclusive { get; }

    public static DataDirectoryLease AcquireShared(string dataDirectory) =>
        Acquire(dataDirectory, isExclusive: false);

    public static DataDirectoryLease AcquireExclusive(string dataDirectory) =>
        Acquire(dataDirectory, isExclusive: true);

    public void EnsureExclusiveFor(string dataDirectory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(dataDirectory));

        if (!IsExclusive ||
            !string.Equals(DataDirectory, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Restore requires exclusive access to this data directory.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _file.Dispose();
        _disposed = true;
    }

    private static DataDirectoryLease Acquire(
        string dataDirectory,
        bool isExclusive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(dataDirectory));

        if (isExclusive &&
            (!Directory.Exists(root) || Path.GetDirectoryName(root) is null))
        {
            throw new IOException(
                "Restore requires an existing data directory below the filesystem root.");
        }

        var backups = Path.Combine(root, "backups");
        Directory.CreateDirectory(backups);

        if (isExclusive &&
            (File.GetAttributes(backups) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                "The backups directory must not be a link.");
        }

        var file = new FileStream(
            Path.Combine(backups, ".clircs-use.lock"),
            FileMode.OpenOrCreate,
            isExclusive ? FileAccess.ReadWrite : FileAccess.Read,
            isExclusive ? FileShare.None : FileShare.Read);

        return new DataDirectoryLease(root, isExclusive, file);
    }
}
