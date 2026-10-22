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
        var journal = new BackupRestoreJournal(dataDirectory);
        recoveredPhase = null;

        while (true)
        {
            var shared = DataDirectoryLease.AcquireShared(dataDirectory);

            try
            {
                if (journal.Load() is null)
                {
                    return shared;
                }
            }
            catch
            {
                shared.Dispose();
                throw;
            }

            shared.Dispose();

            using (var exclusive = DataDirectoryLease.AcquireExclusive(dataDirectory))
            {
                var transaction = new BackupRestoreTransaction(dataDirectory);
                recoveredPhase = transaction.Recover(exclusive) ?? recoveredPhase;
            }
        }
    }
}
