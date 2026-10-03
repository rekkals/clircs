namespace Clircs.ConsoleClient;

internal readonly record struct ServiceCommandPrivacyResult(
    string DisplayText,
    bool ContainsSensitiveData);

internal readonly record struct ServiceCommandSecretPrompt(
    string Prompt,
    string CancellationMessage,
    int InsertAt);

internal readonly record struct ServiceCommandSecretPlan(
    IReadOnlyList<ServiceCommandSecretPrompt> Prompts,
    string? SyntaxError);

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

    public static ServiceCommandSecretPlan CreateSecretPlan(
        string service,
        IReadOnlyList<string> arguments,
        int passwordRequests = 0,
        int codeRequests = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentNullException.ThrowIfNull(arguments);

        if (service.Equals("nickserv", StringComparison.OrdinalIgnoreCase))
        {
            return CreateNickServSecretPlan(arguments, passwordRequests, codeRequests);
        }

        if (service.Equals("chanserv", StringComparison.OrdinalIgnoreCase))
        {
            return CreateChanServSecretPlan(arguments, passwordRequests, codeRequests);
        }

        if (service.Equals("operserv", StringComparison.OrdinalIgnoreCase))
        {
            return CreateOperServSecretPlan(arguments, passwordRequests, codeRequests);
        }

        return passwordRequests == 0 && codeRequests == 0
            ? NoSecretPlan()
            : SyntaxError($"/{service.ToLowerInvariant()} <command> [arguments]");
    }

    private static ServiceCommandSecretPlan CreateNickServSecretPlan(
        IReadOnlyList<string> arguments,
        int passwordRequests,
        int codeRequests)
    {
        if (arguments.Count == 0)
        {
            return passwordRequests == 0 && codeRequests == 0
                ? NoSecretPlan()
                : SyntaxError("/nickserv <command> [arguments]");
        }

        if (passwordRequests > 1 ||
            codeRequests > 1 ||
            (passwordRequests != 0 && codeRequests != 0))
        {
            return SyntaxError("/nickserv <command> [arguments]");
        }

        if (codeRequests != 0 &&
            !Is(arguments[0], "confirm") &&
            !Is(arguments[0], "verify") &&
            !Is(arguments[0], "unregister") &&
            !Is(arguments[0], "erase"))
        {
            return SyntaxError("/nickserv <command> [arguments]");
        }

        if (Is(arguments[0], "identify"))
        {
            return arguments.Count <= 2
                ? PromptPlan(new ServiceCommandSecretPrompt(
                    "NickServ password: ",
                    "NickServ identification canceled",
                    arguments.Count))
                : SyntaxError("/nickserv identify [account]");
        }

        if (Is(arguments[0], "login"))
        {
            return arguments.Count == 2
                ? PromptPlan(new ServiceCommandSecretPrompt(
                    "NickServ password: ",
                    "NickServ login canceled",
                    2))
                : SyntaxError("/nickserv login <account>");
        }

        if (Is(arguments[0], "register"))
        {
            return arguments.Count is 1 or 2
                ? PromptPlan(new ServiceCommandSecretPrompt(
                    "New NickServ password: ",
                    "NickServ registration canceled",
                    1))
                : SyntaxError("/nickserv register [email]");
        }

        if (arguments.Count >= 2 &&
            Is(arguments[0], "set") &&
            Is(arguments[1], "password"))
        {
            return arguments.Count == 2
                ? PromptPlan(new ServiceCommandSecretPrompt(
                    "New NickServ password: ",
                    "NickServ password change canceled",
                    2))
                : SyntaxError("/nickserv set password");
        }

        if (arguments.Count >= 2 &&
            Is(arguments[0], "set") &&
            Is(arguments[1], "email"))
        {
            const string usage =
                "/nickserv set email <address> [--password]";

            if (arguments.Count != 3)
            {
                return SyntaxError(usage);
            }

            return passwordRequests == 1
                ? PromptPlan(new ServiceCommandSecretPrompt(
                    "NickServ password: ",
                    "NickServ email change canceled",
                    3))
                : NoSecretPlan();
        }

        if (Is(arguments[0], "setpass"))
        {
            return arguments.Count == 2
                ? PromptPlan(
                    new ServiceCommandSecretPrompt(
                        "NickServ reset key: ",
                        "NickServ password reset canceled",
                        2),
                    new ServiceCommandSecretPrompt(
                        "New NickServ password: ",
                        "NickServ password reset canceled",
                        3))
                : SyntaxError("/nickserv setpass <account>");
        }

        if (Is(arguments[0], "resetpass"))
        {
            return arguments.Count == 2
                ? PromptPlan(
                    new ServiceCommandSecretPrompt(
                        "NickServ reset code: ",
                        "NickServ password reset canceled",
                        2),
                    new ServiceCommandSecretPrompt(
                        "New NickServ password: ",
                        "NickServ password reset canceled",
                        3))
                : SyntaxError("/nickserv resetpass <account>");
        }

        if (Is(arguments[0], "confirm"))
        {
            if (passwordRequests != 0)
            {
                return SyntaxError(
                    "/nickserv confirm [register|email|resetpass]");
            }

            if (arguments.Count == 1)
            {
                return PromptPlan(new ServiceCommandSecretPrompt(
                    "NickServ confirmation code: ",
                    "NickServ confirmation canceled",
                    1));
            }

            if (arguments.Count == 2 &&
                (Is(arguments[1], "register") ||
                 Is(arguments[1], "email") ||
                 Is(arguments[1], "resetpass")))
            {
                return PromptPlan(new ServiceCommandSecretPrompt(
                    "NickServ confirmation code: ",
                    "NickServ confirmation canceled",
                    2));
            }

            return SyntaxError(
                "/nickserv confirm [register|email|resetpass]");
        }

        if (Is(arguments[0], "verify"))
        {
            if (passwordRequests != 0)
            {
                return SyntaxError(
                    "/nickserv verify <account> | " +
                    "/nickserv verify <register|emailchg> <account>");
            }

            if (arguments.Count == 3 &&
                (Is(arguments[1], "register") ||
                 Is(arguments[1], "emailchg")))
            {
                return PromptPlan(new ServiceCommandSecretPrompt(
                    "NickServ verification code: ",
                    "NickServ verification canceled",
                    3));
            }

            if (arguments.Count == 2 &&
                !Is(arguments[1], "register") &&
                !Is(arguments[1], "emailchg"))
            {
                return PromptPlan(new ServiceCommandSecretPrompt(
                    "NickServ verification code: ",
                    "NickServ verification canceled",
                    2));
            }

            return SyntaxError(
                "/nickserv verify <account> | " +
                "/nickserv verify <register|emailchg> <account>");
        }

        if (Is(arguments[0], "unregister") ||
            Is(arguments[0], "erase"))
        {
            var usage =
                $"/nickserv {arguments[0].ToLowerInvariant()} <account> [--code]";

            if (passwordRequests != 0 || arguments.Count != 2)
            {
                return SyntaxError(usage);
            }

            return codeRequests == 1
                ? PromptPlan(new ServiceCommandSecretPrompt(
                    "NickServ confirmation code: ",
                    "NickServ account removal canceled",
                    2))
                : NoSecretPlan();
        }

        if (Is(arguments[0], "passwd") ||
            Is(arguments[0], "password"))
        {
            if (arguments.Count == 1)
            {
                return PromptPlan(
                    new ServiceCommandSecretPrompt(
                        "Current NickServ password: ",
                        "NickServ password change canceled",
                        1),
                    new ServiceCommandSecretPrompt(
                        "New NickServ password: ",
                        "NickServ password change canceled",
                        2),
                    new ServiceCommandSecretPrompt(
                        "Repeat new NickServ password: ",
                        "NickServ password change canceled",
                        3));
            }

            if (arguments.Count == 2)
            {
                return PromptPlan(new ServiceCommandSecretPrompt(
                    "New NickServ password: ",
                    "NickServ password change canceled",
                    2));
            }

            return SyntaxError(
                $"/nickserv {arguments[0].ToLowerInvariant()} [account]");
        }

        if (arguments.Count >= 3 &&
            Is(arguments[0], "saset") &&
            Is(arguments[2], "password"))
        {
            return arguments.Count == 3
                ? PromptPlan(new ServiceCommandSecretPrompt(
                    "New NickServ password: ",
                    "NickServ password change canceled",
                    3))
                : SyntaxError("/nickserv saset <account> password");
        }

        if (Is(arguments[0], "saregister"))
        {
            const string usage =
                "/nickserv saregister <account> [--password]";

            if (arguments.Count != 2)
            {
                return SyntaxError(usage);
            }

            return passwordRequests == 1
                ? PromptPlan(new ServiceCommandSecretPrompt(
                    "New NickServ password: ",
                    "NickServ registration canceled",
                    2))
                : NoSecretPlan();
        }

        if (NickServRecoveryCommands.Contains(arguments[0]))
        {
            var usage =
                $"/nickserv {arguments[0].ToLowerInvariant()} <target> [--password]";

            if (arguments.Count != 2)
            {
                return SyntaxError(usage);
            }

            return passwordRequests == 1
                ? PromptPlan(new ServiceCommandSecretPrompt(
                    "NickServ password: ",
                    "NickServ command canceled",
                    2))
                : NoSecretPlan();
        }

        return passwordRequests == 0 && codeRequests == 0
            ? NoSecretPlan()
            : SyntaxError("/nickserv <command> [arguments]");
    }

    private static ServiceCommandSecretPlan CreateChanServSecretPlan(
        IReadOnlyList<string> arguments,
        int passwordRequests,
        int codeRequests)
    {
        if (arguments.Count == 0)
        {
            return passwordRequests == 0 && codeRequests == 0
                ? NoSecretPlan()
                : SyntaxError("/chanserv <command> [arguments]");
        }

        if (passwordRequests > 1 || codeRequests != 0)
        {
            return SyntaxError("/chanserv <command> [arguments]");
        }

        if (Is(arguments[0], "identify"))
        {
            return arguments.Count == 2
                ? PromptPlan(new ServiceCommandSecretPrompt(
                    "ChanServ password: ",
                    "ChanServ identification canceled",
                    2))
                : SyntaxError("/chanserv identify <channel>");
        }

        if (Is(arguments[0], "register"))
        {
            const string usage =
                "/chanserv register <channel> [description] [--password]";

            if (arguments.Count < 2)
            {
                return SyntaxError(usage);
            }

            return passwordRequests == 1
                ? PromptPlan(new ServiceCommandSecretPrompt(
                    "New ChanServ password: ",
                    "ChanServ registration canceled",
                    2))
                : NoSecretPlan();
        }

        return passwordRequests == 0 && codeRequests == 0
            ? NoSecretPlan()
            : SyntaxError("/chanserv <command> [arguments]");
    }

    private static ServiceCommandSecretPlan CreateOperServSecretPlan(
        IReadOnlyList<string> arguments,
        int passwordRequests,
        int codeRequests)
    {
        if (arguments.Count == 0)
        {
            return passwordRequests == 0 && codeRequests == 0
                ? NoSecretPlan()
                : SyntaxError("/operserv <command> [arguments]");
        }

        if (passwordRequests > 1 || codeRequests != 0)
        {
            return SyntaxError("/operserv <command> [arguments]");
        }

        if (Is(arguments[0], "identify") ||
            Is(arguments[0], "login"))
        {
            return arguments.Count == 1
                ? PromptPlan(new ServiceCommandSecretPrompt(
                    "OperServ password: ",
                    "OperServ authentication canceled",
                    1))
                : SyntaxError(
                    $"/operserv {arguments[0].ToLowerInvariant()}");
        }

        return passwordRequests == 0 && codeRequests == 0
            ? NoSecretPlan()
            : SyntaxError("/operserv <command> [arguments]");
    }

    private static ServiceCommandSecretPlan PromptPlan(
        params ServiceCommandSecretPrompt[] prompts) =>
        new(prompts, null);

    private static ServiceCommandSecretPlan SyntaxError(string usage) =>
        new([], $"Usage: {usage}");

    private static ServiceCommandSecretPlan NoSecretPlan() =>
        new([], null);

    public static ServiceCommandPrivacyResult Apply(
        string service,
        string text,
        IReadOnlyList<string> arguments,
        IReadOnlyList<int>? additionalSensitiveIndexes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(arguments);

        var sensitiveIndexes = SensitiveArgumentIndexes(service, arguments)
            .Concat(additionalSensitiveIndexes ?? [])
            .Distinct()
            .ToArray();

        if (sensitiveIndexes.Length == 0)
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

            if (Is(arguments[0], "login"))
            {
                return LastArgument(arguments, minimumCount: 3);
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
                Is(arguments[0], "set") &&
                Is(arguments[1], "email"))
            {
                return LastArgument(arguments, minimumCount: 4);
            }

            if (Is(arguments[0], "setpass") ||
                Is(arguments[0], "resetpass"))
            {
                return ArgumentsFrom(arguments, firstIndex: 2);
            }

            if (Is(arguments[0], "confirm"))
            {
                return LastArgument(arguments, minimumCount: 2);
            }

            if (Is(arguments[0], "verify"))
            {
                return LastArgument(arguments, minimumCount: 3);
            }

            if (Is(arguments[0], "passwd") ||
                Is(arguments[0], "password"))
            {
                return arguments.Count >= 4
                    ? ArgumentsFrom(arguments, firstIndex: 1)
                    : LastArgument(arguments, minimumCount: 3);
            }

            if (arguments.Count >= 3 &&
                Is(arguments[0], "saset") &&
                Is(arguments[2], "password"))
            {
                return ArgumentsFrom(arguments, firstIndex: 3);
            }

            if (Is(arguments[0], "saregister"))
            {
                return LastArgument(arguments, minimumCount: 3);
            }

            if (Is(arguments[0], "unregister") ||
                Is(arguments[0], "erase"))
            {
                return LastArgument(arguments, minimumCount: 3);
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
        }

        if (service.Equals("operserv", StringComparison.OrdinalIgnoreCase) &&
            (Is(arguments[0], "identify") ||
             Is(arguments[0], "login")))
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
        arguments.Count > firstIndex
            ? Enumerable.Range(firstIndex, arguments.Count - firstIndex).ToArray()
            : [];
}
