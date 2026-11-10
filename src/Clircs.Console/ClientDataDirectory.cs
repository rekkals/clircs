namespace Clircs.ConsoleClient;

internal static class ClientDataDirectory
{
    public static string Resolve()
    {
        var preferredOverride = Environment.GetEnvironmentVariable("CLIRCS_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(preferredOverride)) return Path.GetFullPath(preferredOverride);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "clircs");
    }

    public static DataDirectoryLease AcquireForStartup(
        string dataDirectory,
        out BackupRestorePhase? recoveredPhase)
    {
        Directory.CreateDirectory(dataDirectory);
        var lease = DataDirectoryLease.AcquireExclusive(dataDirectory);
        try
        {
            var transaction = new BackupRestoreTransaction(dataDirectory);
            recoveredPhase = transaction.Recover(lease);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }
}
