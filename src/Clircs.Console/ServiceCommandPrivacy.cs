namespace Clircs.ConsoleClient;

internal readonly record struct ServiceCommandPrivacyResult(
    string DisplayText,
    bool ContainsSensitiveData);

internal static class ServiceCommandPrivacy
{
    private const string Redacted = "<redacted>";

    private static readonly HashSet<string> NickServRecoveryCommands =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ghost",
            "recover",
            "regain",
            "release",
            "group"
        };

    public static ServiceCommandPrivacyResult Apply(
        string service,
        string text,
        IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(arguments);

        var sensitiveIndexes = SensitiveArgumentIndexes(service, arguments);
        if (sensitiveIndexes.Count == 0)
        {
            return new ServiceCommandPrivacyResult(text, false);
        }

        var displayedArguments = arguments.ToArray();
        foreach (var index in sensitiveIndexes)
        {
            displayedArguments[index] = Redacted;
        }

        return new ServiceCommandPrivacyResult(
            string.Join(' ', displayedArguments),
            true);
    }

    private static IReadOnlyList<int> SensitiveArgumentIndexes(
        string service,
        IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return [];
        }

        if (service.Equals("nickserv", StringComparison.OrdinalIgnoreCase))
        {
            if (Is(arguments[0], "identify"))
            {
                return LastArgument(arguments, minimumCount: 2);
            }

            if (Is(arguments[0], "register"))
            {
                return ArgumentsFrom(arguments, firstIndex: 1);
            }

            if (arguments.Count >= 2 &&
                Is(arguments[0], "set") &&
                Is(arguments[1], "password"))
            {
                return ArgumentsFrom(arguments, firstIndex: 2);
            }

            if (arguments.Count >= 2 &&
                ((Is(arguments[0], "confirm") &&
                  (Is(arguments[1], "register") || Is(arguments[1], "email"))) ||
                 (Is(arguments[0], "verify") && Is(arguments[1], "register"))))
            {
                return ArgumentsFrom(arguments, firstIndex: 2);
            }

            if (NickServRecoveryCommands.Contains(arguments[0]))
            {
                return LastArgument(arguments, minimumCount: 3);
            }
        }

        if (service.Equals("chanserv", StringComparison.OrdinalIgnoreCase))
        {
            if (Is(arguments[0], "identify"))
            {
                return LastArgument(arguments, minimumCount: 2);
            }

            if (Is(arguments[0], "register") && arguments.Count >= 3)
            {
                return [2];
            }
        }

        if (service.Equals("operserv", StringComparison.OrdinalIgnoreCase) &&
            Is(arguments[0], "identify"))
        {
            return LastArgument(arguments, minimumCount: 2);
        }

        return [];
    }

    private static bool Is(string value, string expected) =>
        value.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<int> LastArgument(
        IReadOnlyList<string> arguments,
        int minimumCount) =>
        arguments.Count >= minimumCount ? [arguments.Count - 1] : [];

    private static IReadOnlyList<int> ArgumentsFrom(
        IReadOnlyList<string> arguments,
        int firstIndex) =>
        Enumerable.Range(firstIndex, arguments.Count - firstIndex).ToArray();
}
