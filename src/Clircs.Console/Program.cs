using System.Text;
using Clircs;
using Clircs.ConsoleClient;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--version", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(ProductInfo.DisplayName);
    return;
}

string dataDirectory;
DataDirectoryLease lease;
BackupRestorePhase? recoveredRestore;

try
{
    dataDirectory = ClientDataDirectory.Resolve();
    lease = ClientDataDirectory.AcquireForStartup(
        dataDirectory, out recoveredRestore);
}
catch (Exception exception) when (
    exception is IOException or UnauthorizedAccessException or InvalidDataException)
{
    Console.Error.WriteLine($"Unable to open clircs data: {exception.Message}");
    Environment.ExitCode = 1;
    return;
}

PreparedBackupRestore? pendingRestore = null;

try
{
    using (lease)
    {
        await using var application = new ClientApplication(
            dataDirectory, recoveredRestore);
        await application.RunAsync();
        pendingRestore = application.TakePendingRestore();
    }

    if (pendingRestore is null)
    {
        return;
    }

    DataDirectoryLease exclusive;

    try
    {
        exclusive = DataDirectoryLease.AcquireExclusive(dataDirectory);
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(
            "Unable to start restore. Close other clircs instances using " +
            "this data directory and try again.");
        Console.Error.WriteLine(exception.Message);
        Environment.ExitCode = 1;
        return;
    }

    using (exclusive)
    {
        try
        {
            var transaction = new BackupRestoreTransaction(dataDirectory);
            transaction.Apply(pendingRestore, exclusive);
            Console.WriteLine("Backup restored. Start clircs again to use it.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine(exception.Message);
            Environment.ExitCode = 1;
        }
    }
}
finally
{
    try
    {
        pendingRestore?.Dispose();
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(
            $"Unable to remove temporary restore files: {exception.Message}");
        Environment.ExitCode = 1;
    }
}
