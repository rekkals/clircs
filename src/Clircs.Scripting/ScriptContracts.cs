using Clircs.Commands;
using Clircs.Sessions;

namespace Clircs.Scripting;

public enum ScriptPermission
{
    Commands,
    Events,
    Timers,
    Output,
    Storage,
    Irc,
    LocalNetwork,
    Secrets,
    Ui
}

public sealed record ScriptHeaderContribution(
    string Id,
    string Text,
    string? BufferId,
    int Priority,
    int MinimumWidth);

public sealed record ScriptCommandRegistration(
    string ScriptId,
    string Name,
    IReadOnlyList<string> Aliases,
    string Summary,
    Func<CommandContext, IReadOnlyList<string>, CancellationToken, ValueTask<CommandResult>> Handler);

public sealed record ScriptHostServices(
    Action<string, string> Print,
    Func<ScriptCommandRegistration, IDisposable> RegisterCommand,
    Action<string, CommandContext, string> QueueCommand,
    Action<string, ScriptHeaderContribution> SetHeader,
    Action<string, string> ClearHeader,
    Action<string> ClearHeaders,
    Func<string, string, string?> ReadSecret);

public sealed record ScriptInfo(
    string Id,
    string Name,
    string Version,
    string Status,
    IReadOnlyList<ScriptPermission> RequestedPermissions,
    IReadOnlyList<ScriptPermission> GrantedPermissions,
    string? LastError);

public sealed record ScriptError(
    DateTimeOffset Timestamp,
    string ScriptId,
    string Operation,
    string Message);

internal sealed class ScriptManifest
{
    public int SchemaVersion { get; set; }

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Entry { get; set; } = "main.js";

    public string[] Permissions { get; set; } = [];
}

internal sealed record DiscoveredScript(string Directory, ScriptManifest Manifest);
