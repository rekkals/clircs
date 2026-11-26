using System.Globalization;
using Clircs.Networking;

namespace Clircs.ConsoleClient;

internal sealed record StartupConnectionRequest(
    IrcEndpoint? Endpoint,
    bool PromptForPassword,
    IReadOnlyList<string> ProfileNames)
{
    public static StartupConnectionRequest Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new StartupConnectionRequest(null, false, []);
        }

        return args[0].ToLowerInvariant() switch
        {
            "--server" => ParseServer(args[1..]),
            "--profile" => ParseProfiles(args[1..]),
            _ => throw new ArgumentException("Unknown startup option.")
        };
    }

    private static StartupConnectionRequest ParseServer(string[] arguments)
    {
        if (arguments.Length == 0 ||
            string.IsNullOrWhiteSpace(arguments[0]) ||
            IsOption(arguments[0]))
        {
            throw new ArgumentException("--server requires a host.");
        }

        var host = arguments[0];
        int? port = null;
        var nextArgument = 1;
        if (nextArgument < arguments.Length && !IsOption(arguments[nextArgument]))
        {
            if (!int.TryParse(
                    arguments[nextArgument],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedPort) ||
                parsedPort is < 1 or > 65535)
            {
                throw new ArgumentException("The server port must be a number from 1 through 65535.");
            }

            port = parsedPort;
            nextArgument++;
        }

        var useTls = false;
        var promptForPassword = false;
        for (; nextArgument < arguments.Length; nextArgument++)
        {
            switch (arguments[nextArgument].ToLowerInvariant())
            {
                case "--tls" when !useTls:
                    useTls = true;
                    break;
                case "--password" when !promptForPassword:
                    promptForPassword = true;
                    break;
                default:
                    throw new ArgumentException("Unexpected argument for --server.");
            }
        }

        var endpoint = new IrcEndpoint(host, port ?? (useTls ? 6697 : 6667), useTls);
        return new StartupConnectionRequest(endpoint, promptForPassword, []);
    }

    private static StartupConnectionRequest ParseProfiles(string[] names)
    {
        if (names.Length == 0)
        {
            throw new ArgumentException("--profile requires at least one profile name.");
        }

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A profile name cannot be blank.");
            }

            if (IsOption(name))
            {
                throw new ArgumentException($"Unexpected option '{name}' after --profile.");
            }
        }

        return new StartupConnectionRequest(null, false, names);
    }

    private static bool IsOption(string argument) =>
        argument.StartsWith("--", StringComparison.Ordinal);
}
