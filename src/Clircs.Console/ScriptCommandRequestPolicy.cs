using Clircs.Commands;

namespace Clircs.ConsoleClient;

internal static class ScriptCommandRequestPolicy
{
    public static bool IsAllowed(CommandInput command) =>
        !command.Name.Equals("script", StringComparison.OrdinalIgnoreCase);
}
