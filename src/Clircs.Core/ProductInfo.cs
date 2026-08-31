using System.Reflection;

namespace Clircs;

public static class ProductInfo
{
    public static string Version { get; } = ReadVersion();

    public static string DisplayVersion { get; } = "v" + Version;

    public static string DisplayName { get; } = "clircs " + DisplayVersion;

    public const string Description = "Command line IRC software (for Windows)";

    public const string StartupQuote = Description;

    public const string StartupHelp = "Type /help for commands. Type /server <host|profile> [port] [--tls] [--new] [--password] to connect.";

    private static string ReadVersion()
    {
        var informationalVersion = typeof(ProductInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? throw new InvalidOperationException(
                "The clircs assembly has no informational version.");

        var metadataSeparator = informationalVersion.IndexOf('+');

        return metadataSeparator < 0
            ? informationalVersion
            : informationalVersion[..metadataSeparator];
    }
}
