using Clircs.Identity;
using Clircs.Sessions;

namespace Clircs.Scripting;

public sealed record ScriptOutputStyle(
    TextColorRole? Role = null,
    ConsoleColor? Color = null);

public sealed record ScriptOutputPart(
    string Text,
    ScriptOutputStyle? Style = null);

public sealed record ScriptWindowOutput(
    IReadOnlyList<ScriptOutputPart> Parts,
    ScriptOutputStyle? Style = null,
    DateTimeOffset? Timestamp = null);

public sealed record ScriptWindowHostServices(
    Func<BufferId?> Active,
    Func<NetworkSessionId?, BufferId?> Status,
    Func<string, NetworkSessionId?, BufferId?> Find,
    Func<string, string, NetworkSessionId?, BufferId> Open,
    Func<string, BufferId, ScriptWindowOutput, bool> Write);
