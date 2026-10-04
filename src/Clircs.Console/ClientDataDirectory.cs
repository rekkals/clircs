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
}
