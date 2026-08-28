namespace Clircs.ConsoleClient;

internal static class ClientDataDirectory
{
    public static string Resolve()
    {
        var preferredOverride = Environment.GetEnvironmentVariable("CLIRCS_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(preferredOverride)) return Path.GetFullPath(preferredOverride);

        var legacyOverride = Environment.GetEnvironmentVariable("CLIRC_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(legacyOverride)) return Path.GetFullPath(legacyOverride);

        return ResolveDefault(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    }

    internal static string ResolveDefault(string applicationData)
    {
        var preferred = Path.Combine(applicationData, "clircs");
        var legacy = Path.Combine(applicationData, "clirc");
        if (Directory.Exists(preferred) || !Directory.Exists(legacy)) return preferred;

        var staging = preferred + ".migrating-" + Guid.NewGuid().ToString("N");
        try
        {
            CopyDirectory(legacy, staging);
            Directory.Move(staging, preferred);
            return preferred;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                // The legacy directory remains the safe fallback even if staging cleanup is denied.
            }
            return legacy;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: false);
        }
    }
}
