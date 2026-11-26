using System.Text;
using Clircs;
using Clircs.ConsoleClient;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 1 && args[0].Equals("--version", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(ProductInfo.DisplayName);
    return;
}

if (args.Length == 1 && args[0].Equals("--help", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine();
    Console.WriteLine("""
        Usage: clircs --server <host> [port] [--tls] [--password]
                      --profile <name> [<name> ...]
                      --config
                      --version
                      --help

        Options:
            --server <host> [port]    Connect to an IRC server.
                                      Default port is 6667, or 6697 with --tls.
            --tls                     Connect to a server over TLS.
            --password                Prompt for the server password before connecting.
            --profile <name> ...      Connect using one or more saved network profiles.
            --config                  Show the data directory and exit.
            --version                 Show the version and exit.
            --help                    Show this help and exit.
        """);
    Console.WriteLine();
    return;
}

var showConfig = args.Length == 1 &&
    args[0].Equals("--config", StringComparison.OrdinalIgnoreCase);

StartupConnectionRequest? startupRequest;
try
{
    startupRequest = showConfig ? null : StartupConnectionRequest.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine("Use clircs --help for launch syntax.");
    Environment.ExitCode = 1;
    return;
}

string dataDirectory;
DataDirectoryLease lease;
BackupRestorePhase? recoveredRestore;

try
{
    dataDirectory = ClientDataDirectory.Resolve();

    if (startupRequest is null)
    {
        Console.WriteLine(dataDirectory);
        return;
    }

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

using var dataDirectoryLease = lease;
PreparedBackupRestore? pendingRestore = null;

try
{
    await using (var application = new ClientApplication(
        dataDirectory, recoveredRestore))
    {
        await application.RunAsync(startupRequest);
        pendingRestore = application.TakePendingRestore();
    }

    if (pendingRestore is null)
    {
        return;
    }

    try
    {
        var transaction = new BackupRestoreTransaction(dataDirectory);
        transaction.Apply(pendingRestore, dataDirectoryLease);
        Console.WriteLine("Backup restored. Start clircs again to use it.");
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException or InvalidDataException)
    {
        Console.Error.WriteLine(exception.Message);
        Environment.ExitCode = 1;
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
