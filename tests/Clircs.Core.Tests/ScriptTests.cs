using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clircs.Commands;
using Clircs.ConsoleClient;
using Clircs.Identity;
using Clircs.Scripting;
using Clircs.Sessions;

namespace Clircs.Core.Tests;

internal static class ScriptTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("script help reaches core command metadata without executing handlers", ScriptHelpUsesCoreCommandsAsync);
        suite.Add("script help is replaced on reload and removed on disposal", ScriptHelpFollowsLifecycleAsync);
        suite.Add("invalid script help rejects the load and rolls back registered commands", InvalidScriptHelpIsRejectedAsync);
        suite.Add("script help accepts its size boundaries and rejects oversized data", ScriptHelpSizeLimitsAsync);
        suite.Add("scripts register commands, receive scoped events, and persist private storage", LifecycleAndStorageAsync);
        suite.Add("loaded script state survives restart and unload", LoadedStatePersistsAsync);
        suite.Add("missing or declined scripts do not block other startup restores", RestoreFailuresAreIsolatedAsync);
        suite.Add("damaged script load state is preserved before replacement", DamagedLoadStateIsPreservedAsync);
        suite.Add("damaged script permissions require approval before recovery", DamagedPermissionsFailClosedAsync);
        suite.Add("first-load approval precedes script execution and grants the requested permissions together", FirstLoadPermissionApprovalAsync);
        suite.Add("script approval survives reload, version changes, and startup restoration", PermissionApprovalPersistsAsync);
        suite.Add("new permissions are reviewed before replacing a running script", AdditionalPermissionsRequireApprovalAsync);
        suite.Add("approving additional permissions preserves explicit revocations across restart", AdditionalApprovalPreservesRevocationsAsync);
        suite.Add("script approval must be saved before execution", PermissionSaveFailurePreventsExecutionAsync);
        suite.Add("revocation takes effect when replacement approval is canceled", RevocationSurvivesCanceledReloadAsync);
        suite.Add("scripts cannot use undeclared capabilities", UndeclaredCapabilityFailsAsync);
        suite.Add("normal IRC commands do not require a permission", NormalIrcCommandsDoNotRequirePermissionAsync);
        suite.Add("raw command requests carry only explicitly granted raw access", RawPermissionRequiresGrantAsync);
        suite.Add("raw permission survives restart and remains revoked after restart", RawPermissionPersistsAndRevokesAsync);
        suite.Add("local network permission requires an explicit grant", LocalNetworkPermissionRequiresGrantAsync);
        suite.Add("script window bindings preserve IDs scopes and return values", WindowBindingsPreserveContextAsync);
        suite.Add("script window bindings copy text and presentation data", WindowBindingsPreserveOutputAsync);
        suite.Add("script window operations require output permission", WindowBindingsRequireOutputAsync);
        suite.Add("script window timestamps preserve offsets and fractional seconds", WindowBindingsPreserveTimestampsAsync);
        suite.Add("script window bindings reject malformed lookup arguments", WindowBindingsRejectLookupArgumentsAsync);
        suite.Add("script window bindings reject malformed output", WindowBindingsRejectOutputArgumentsAsync);
        suite.Add("script window bindings enforce output boundaries", WindowBindingsEnforceOutputLimitsAsync);
        suite.Add("script command policy allows raw only with its explicit grant", ScriptCommandRequestsAreRestricted);
        suite.Add("script headers are removed when a script unloads", ScriptHeadersAreOwnedAsync);
        suite.Add("failed script loads roll back host resources", FailedLoadRollsBackResourcesAsync);
        suite.Add("loaded scripts remain manageable when their source disappears", MissingLoadedSourceRemainsVisibleAsync);
        suite.Add("script callback queues are bounded", CallbackQueueIsBoundedAsync);
        suite.Add("script host resource counts are bounded", HostResourcesAreBoundedAsync);
        suite.Add("listing scripts does not persist default permissions", ListingScriptsIsReadOnlyAsync);
        suite.Add("script secrets are encrypted and survive reload", ScriptSecretsPersistAsync);
        suite.Add("human secret entry does not consume the script time budget", ScriptSecretPromptIsUntimedAsync);
        suite.Add("packaged musikcube addon loads without special core knowledge", MusikcubeAddonLoadsAsync);
        suite.Add("musikcube addon authenticates and controls a local protocol peer", MusikcubeAddonProtocolAsync);
        suite.Add("runaway scripts are bounded and faulted without blocking the host", RunawayScriptIsBoundedAsync);
    }

    private static async ValueTask WindowBindingsPreserveContextAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "window-context", """
            clircs.registerCommand("windowprobe", [], "Inspect window bindings.", context => {
              return JSON.stringify({
                active: clircs.windows.active(),
                status: clircs.windows.status(context.networkId),
                localStatus: clircs.windows.status(),
                found: clircs.windows.find("#room", context.networkId),
                opened: clircs.windows.open("report", { networkId: context.networkId }),
                local: clircs.windows.open("local"),
                accepted: clircs.windows.write(context.bufferId, "context output")
              });
            });
            """, ["commands", "output"]);

        var visible = BufferId.New();
        var status = BufferId.New();
        var found = BufferId.New();
        var windows = new WindowBindingHost
        {
            ActiveResult = visible,
            StatusResult = status,
            FindResult = found
        };
        var host = new TestScriptHost();
        await using var manager = new ScriptManager(
            directory.Path, host.Services with { Windows = windows.Services });
        await manager.LoadAsync("window-context");

        var network = NetworkSessionId.New();
        var origin = BufferId.New();
        var context = new CommandContext(network, origin);
        var result = await host.Commands["windowprobe"].Handler(
            context, [], CancellationToken.None);

        Assert.True(result.Succeeded);
        using var document = JsonDocument.Parse(result.Message!);
        var root = document.RootElement;
        Assert.Equal(visible.Value.ToString(), root.GetProperty("active").GetString()!);
        Assert.Equal(status.Value.ToString(), root.GetProperty("status").GetString()!);
        Assert.Equal(status.Value.ToString(), root.GetProperty("localStatus").GetString()!);
        Assert.Equal(found.Value.ToString(), root.GetProperty("found").GetString()!);
        Assert.Equal(windows.OpenResult.Value.ToString(), root.GetProperty("opened").GetString()!);
        Assert.Equal(windows.OpenResult.Value.ToString(), root.GetProperty("local").GetString()!);
        Assert.True(root.GetProperty("accepted").GetBoolean());

        var statuses = windows.StatusRequests.ToArray();
        Assert.Equal(2, statuses.Length);
        Assert.True(statuses[0] == network);
        Assert.True(statuses[1] is null);

        var lookup = windows.Finds.Single();
        Assert.Equal("#room", lookup.Name);
        Assert.True(lookup.NetworkId == network);

        var opens = windows.Opens.ToArray();
        Assert.Equal(2, opens.Length);
        Assert.Equal("window-context", opens[0].ScriptId);
        Assert.Equal("report", opens[0].Name);
        Assert.True(opens[0].NetworkId == network);
        Assert.Equal("local", opens[1].Name);
        Assert.True(opens[1].NetworkId is null);

        var write = windows.Writes.Single();
        Assert.Equal("window-context", write.ScriptId);
        Assert.Equal(origin, write.BufferId);
        Assert.Equal("context output", write.Output.Parts.Single().Text);

        windows.ActiveResult = null;
        windows.StatusResult = null;
        windows.FindResult = null;
        windows.WriteResult = false;

        var missing = await host.Commands["windowprobe"].Handler(
            context, [], CancellationToken.None);
        Assert.True(missing.Succeeded);
        using var missingDocument = JsonDocument.Parse(missing.Message!);
        var missingRoot = missingDocument.RootElement;
        Assert.Equal(JsonValueKind.Null, missingRoot.GetProperty("active").ValueKind);
        Assert.Equal(JsonValueKind.Null, missingRoot.GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.Null, missingRoot.GetProperty("found").ValueKind);
        Assert.False(missingRoot.GetProperty("accepted").GetBoolean());
    }

    private static async ValueTask WindowBindingsPreserveOutputAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "window-output", """
            const target = "11111111-1111-1111-1111-111111111111";
            const piece = clircs.style("WaRnInG", "\u0002warning\u000f");
            if (!Object.isFrozen(piece) || piece.role !== "warning") {
              throw new Error("Styled pieces must be frozen plain data.");
            }
            const parts = ["before ", piece, { text: " fixed", color: "GREEN" }, " after.  "];
            const options = { role: "NOTICE" };
            clircs.windows.write(target, parts, options);
            parts[0] = "changed";
            options.role = "error";
            clircs.windows.write(target, "plain");
            clircs.windows.write(target, "fixed", { color: "darkmagenta" });
            """, ["output"]);

        var windows = new WindowBindingHost();
        var host = new TestScriptHost();
        await using var manager = new ScriptManager(
            directory.Path, host.Services with { Windows = windows.Services });
        await manager.LoadAsync("window-output");

        var writes = windows.Writes.ToArray();
        Assert.Equal(3, writes.Length);
        Assert.Equal("window-output", writes[0].ScriptId);
        Assert.Equal(
            new BufferId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            writes[0].BufferId);

        var output = writes[0].Output;
        Assert.True(output.Style!.Role == TextColorRole.Notice);
        Assert.True(output.Timestamp is null);
        Assert.Equal(4, output.Parts.Count);
        Assert.Equal("before ", output.Parts[0].Text);
        Assert.Equal("\u0002warning\u000f", output.Parts[1].Text);
        Assert.True(output.Parts[1].Style!.Role == TextColorRole.Warning);
        Assert.True(output.Parts[2].Style!.Color == ConsoleColor.Green);
        Assert.Equal(" after.  ", output.Parts[3].Text);

        Assert.Equal("plain", writes[1].Output.Parts.Single().Text);
        Assert.True(writes[1].Output.Style is null);
        Assert.True(writes[2].Output.Style!.Color == ConsoleColor.DarkMagenta);
    }

    private static async ValueTask WindowBindingsRequireOutputAsync()
    {
        string[] operations =
        [
            """clircs.windows.active();""",
            """clircs.windows.status(network);""",
            """clircs.windows.find("#room", network);""",
            """clircs.windows.open("report");""",
            """clircs.windows.write(target, "text");""",
            """clircs.style("warning", "text");"""
        ];

        foreach (var operation in operations)
        {
            await AssertWindowScriptRejectedAsync(operation, [], "output");
        }
    }

    private static async ValueTask WindowBindingsPreserveTimestampsAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "window-timestamps", """
            const target = "11111111-1111-1111-1111-111111111111";
            for (const timestamp of [
              "2026-10-27T12:34:56Z",
              "2026-10-27T12:34:56.123Z",
              "2026-10-27T12:34:56.1234567+05:30",
              "2026-10-27T12:34:56-04:00"
            ]) {
              clircs.windows.write(target, "timestamp", { timestamp });
            }
            """, ["output"]);

        var windows = new WindowBindingHost();
        var host = new TestScriptHost();
        await using var manager = new ScriptManager(
            directory.Path, host.Services with { Windows = windows.Services });
        await manager.LoadAsync("window-timestamps");

        DateTimeOffset[] expected =
        [
            new DateTimeOffset(2026, 10, 27, 12, 34, 56, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 27, 12, 34, 56, TimeSpan.Zero)
                .AddMilliseconds(123),
            new DateTimeOffset(2026, 10, 27, 12, 34, 56, new TimeSpan(5, 30, 0))
                .AddTicks(1_234_567),
            new DateTimeOffset(2026, 10, 27, 12, 34, 56, TimeSpan.FromHours(-4))
        ];

        var writes = windows.Writes.ToArray();
        Assert.Equal(expected.Length, writes.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            var actual = writes[index].Output.Timestamp!.Value;
            Assert.Equal(expected[index], actual);
            Assert.Equal(expected[index].Offset, actual.Offset);
        }
    }

    private static async ValueTask WindowBindingsRejectLookupArgumentsAsync()
    {
        string[] cases =
        [
            """clircs.windows.status(42);""",
            """clircs.windows.status("not-an-id");""",
            """clircs.windows.status("00000000-0000-0000-0000-000000000000");""",
            """clircs.windows.find(null, network);""",
            """clircs.windows.find(" ", network);""",
            """clircs.windows.find(["#room"], network);""",
            """clircs.windows.find("#room", false);""",
            """clircs.windows.find("#room", "not-an-id");""",
            """clircs.windows.open(42);""",
            """clircs.windows.open(" ");""",
            """clircs.windows.open("report", null);""",
            """clircs.windows.open("report", []);""",
            """clircs.windows.open("report", new Date());""",
            """clircs.windows.open("report", { networkId: 42 });""",
            """clircs.windows.open("report", { networkId: "not-an-id" });""",
            """clircs.windows.open("report", { networkId: "00000000-0000-0000-0000-000000000000" });""",
            """clircs.windows.open("report", { NetworkId: network });""",
            """clircs.windows.open("report", { focus: true });""",
            """clircs.windows.open("report", { [Symbol("unknown")]: network });""",
            """clircs.windows.open("report", Object.create({ networkId: network }));"""
        ];

        foreach (var source in cases)
        {
            await AssertWindowScriptRejectedAsync(source, ["output"]);
        }
    }

    private static async ValueTask WindowBindingsRejectOutputArgumentsAsync()
    {
        string[] cases =
        [
            """clircs.windows.write(null, "text");""",
            """clircs.windows.write("not-an-id", "text");""",
            """clircs.windows.write("00000000-0000-0000-0000-000000000000", "text");""",
            """clircs.windows.write(target, 42);""",
            """clircs.windows.write(target, null);""",
            """clircs.windows.write(target, [["nested"]]);""",
            """clircs.windows.write(target, new Array(1));""",
            """clircs.windows.write(target, [{}]);""",
            """clircs.windows.write(target, [{ text: 42 }]);""",
            """clircs.windows.write(target, [{ text: "x", role: "unknown" }]);""",
            """clircs.windows.write(target, [{ text: "x", color: "unknown" }]);""",
            """clircs.windows.write(target, [{ text: "x", role: "warning", color: "red" }]);""",
            """clircs.windows.write(target, [{ text: "x", scriptId: "other" }]);""",
            """clircs.windows.write(target, "text", null);""",
            """clircs.windows.write(target, "text", { role: "warning", color: "red" });""",
            """clircs.windows.write(target, "text", { role: "1" });""",
            """clircs.windows.write(target, "text", { color: "0" });""",
            """clircs.windows.write(target, "text", { role: null });""",
            """clircs.windows.write(target, "text", { scriptId: "other" });""",
            """clircs.windows.write(target, "text", { [Symbol("unknown")]: "x" });""",
            """clircs.windows.write(target, "text", { timestamp: 42 });""",
            """clircs.windows.write(target, "text", { timestamp: "not-a-date" });""",
            """clircs.windows.write(target, "text", { timestamp: "2026-10-27T12:34:56" });""",
            """clircs.windows.write(target, "text", { timestamp: "2026-02-30T12:34:56Z" });""",
            """clircs.windows.write(target, "text", { timestamp: "2026-10-27T12:34:56.Z" });""",
            """clircs.style("unknown", "text");""",
            """clircs.style("0", "text");""",
            """clircs.style(42, "text");""",
            """clircs.style("warning", null);""",
            """__clirc_windows_write(target, '{"parts":["x"],"scriptId":"other"}');""",
            """__clirc_windows_write(target, '{"parts":["x"],"role":1}');""",
            """__clirc_windows_write(target, '{"parts":["x"],"role":"notice","role":"warning"}');"""
        ];

        foreach (var source in cases)
        {
            await AssertWindowScriptRejectedAsync(source, ["output"]);
        }
    }

    private static async ValueTask WindowBindingsEnforceOutputLimitsAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "window-limits", """
            const target = "11111111-1111-1111-1111-111111111111";
            clircs.windows.write(target, "x".repeat(4096));
            clircs.windows.write(target, Array(4096).fill(""));
            clircs.style("highlight", "x".repeat(4096));
            const payload = '{"parts":["x"]}';
            __clirc_windows_write(
              target, " ".repeat(262144 - payload.length) + payload);
            """, ["output"]);

        var windows = new WindowBindingHost();
        var host = new TestScriptHost();
        await using var manager = new ScriptManager(
            directory.Path, host.Services with { Windows = windows.Services });
        await manager.LoadAsync("window-limits");

        var writes = windows.Writes.ToArray();
        Assert.Equal(3, writes.Length);
        Assert.Equal(new string('x', 4096), writes[0].Output.Parts.Single().Text);
        Assert.Equal(4096, writes[1].Output.Parts.Count);
        Assert.True(writes[1].Output.Parts.All(part => part.Text.Length == 0));
        Assert.Equal("x", writes[2].Output.Parts.Single().Text);

        string[] rejected =
        [
            """clircs.windows.write(target, "x".repeat(4097));""",
            """clircs.windows.write(target, ["x".repeat(2048), "y".repeat(2049)]);""",
            """clircs.windows.write(target, "\u0002".repeat(4097));""",
            """clircs.windows.write(target, Array(4097).fill(""));""",
            """clircs.style("warning", "x".repeat(4097));""",
            """__clirc_windows_write(target, JSON.stringify({ parts: Array(4097).fill("") }));""",
            """__clirc_windows_write(target, " ".repeat(262145) + '{"parts":["x"]}');"""
        ];

        foreach (var source in rejected)
        {
            await AssertWindowScriptRejectedAsync(source, ["output"]);
        }
    }

    private static async ValueTask AssertWindowScriptRejectedAsync(
        string source,
        string[] permissions,
        string? expectedMessage = null)
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "window-rejected", """
            const target = "11111111-1111-1111-1111-111111111111";
            const network = "22222222-2222-2222-2222-222222222222";
            """ + Environment.NewLine + source, permissions);

        var windows = new WindowBindingHost();
        var host = new TestScriptHost();
        await using var manager = new ScriptManager(
            directory.Path, host.Services with { Windows = windows.Services });

        InvalidOperationException exception;
        try
        {
            exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.LoadAsync("window-rejected").AsTask());
        }
        catch (Exception failure)
        {
            throw new InvalidOperationException(
                $"Expected this script to be rejected: {source}", failure);
        }

        Assert.True(
            windows.Calls.IsEmpty,
            $"Rejected input reached the window host: {source}");

        if (expectedMessage is not null)
        {
            Assert.True(
                exception.Message.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase),
                $"Unexpected rejection for: {source}{Environment.NewLine}{exception.Message}");
        }
    }

    private static void ScriptCommandRequestsAreRestricted()
    {
        static ValueTask<CommandResult> Handler(
            CommandContext context,
            CommandInput input,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(CommandResult.Success());

        var commands = new CommandRegistry();
        var definitions = new (string Name, string[] Aliases)[]
        {
            ("notice", ["n"]),
            ("dcc", []),
            ("users", []),
            ("backup", []),
            ("server", ["connect"]),
            ("network", []),
            ("set", []),
            ("logging", ["log"]),
            ("tls", []),
            ("raw", ["quote"]),
            ("debug", []),
            ("script", []),
            ("quit", ["exit"])
        };

        foreach (var (name, aliases) in definitions)
        {
            commands.Register(new CommandDefinition(
                name,
                aliases,
                $"/{name}",
                $"Test {name} command",
                Handler));
        }

        Assert.True(ScriptCommandRequestPolicy.IsAllowed(
            commands,
            (CommandInput)CommandLineParser.Parse("/notice Nick hello")));
        Assert.True(ScriptCommandRequestPolicy.IsAllowed(
            commands,
            (CommandInput)CommandLineParser.Parse("/n Nick hello")));

        string[] denied =
        [
            "/dcc send Nick secret.txt",
            "/users export stolen.json",
            "/backup create",
            "/backup restore snapshot.zip",
            "/server hostile.example",
            "/connect hostile.example",
            "/network remove EFnet",
            "/set dcc.downloads C:\\Temp",
            "/logging on",
            "/tls forget example.test 6697",
            "/debug",
            "/script permissions attacker localnetwork on",
            "/quit",
            "/exit",
            "/not-a-command"
        ];

        foreach (var commandLine in denied)
        {
            var command = (CommandInput)CommandLineParser.Parse(commandLine);
            Assert.False(ScriptCommandRequestPolicy.IsAllowed(commands, command));
            Assert.False(ScriptCommandRequestPolicy.IsAllowed(commands, command, allowRaw: true));
        }

        string[] rawCommands =
        [
            "/raw VENDORCOMMAND first :two  spaces",
            "/quote VENDORCOMMAND first :two  spaces",
            "/RAW VERSION",
            "/QuOtE VERSION"
        ];

        foreach (var commandLine in rawCommands)
        {
            var command = (CommandInput)CommandLineParser.Parse(commandLine);
            Assert.False(ScriptCommandRequestPolicy.IsAllowed(commands, command));
            Assert.True(ScriptCommandRequestPolicy.IsAllowed(commands, command, allowRaw: true));
        }
    }

    private static async ValueTask ScriptHelpUsesCoreCommandsAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "help-metadata", """
            const help = {
              usage: "/helpdemo <operation>",
              fields: [{ label: "Default", value: "Shows status." }],
              topics: {
                Volume: {
                  usage: "/helpdemo volume <0-100>",
                  description: "Set playback volume.",
                  fields: [{ label: "Example", value: "/helpdemo volume 50" }]
                },
                status: { description: "Show playback status." }
              }
            };
            let calls = 0;
            clircs.registerCommand("helpdemo", ["hd"], "Control test playback.", context => {
              clircs.run("/version");
              return `${context.name}:${++calls}:${context.args.join(" ")}`;
            }, help);
            clircs.registerCommand("legacyhelp", [], "Simple script command.", () => "simple");

            help.usage = "/changed";
            help.fields[0].value = "changed";
            help.topics.Volume.description = "changed";
            help.topics.extra = { description: "Added too late." };
            """, ["commands"]);

        var host = new TestScriptHost();
        var registry = new CommandRegistry();
        await using var manager = new ScriptManager(
            directory.Path, WithCoreCommandRegistry(host, registry));
        await manager.LoadAsync("help-metadata");

        var command = registry.Definitions.Single(definition => definition.Name == "helpdemo");
        Assert.True(registry.TryResolve("HD", out var alias));
        Assert.True(ReferenceEquals(command, alias));
        Assert.Equal("/helpdemo <operation>", command.Usage);
        Assert.Equal("Control test playback.", command.Description);
        Assert.Equal("help-metadata",
            command.ExtendedHelp.Single(field => field.Label == "Script").Value);
        Assert.Equal("Shows status.",
            command.ExtendedHelp.Single(field => field.Label == "Default").Value);
        Assert.Equal("status, volume",
            command.ExtendedHelp.Single(field => field.Label == "Topics").Value);

        var volume = command.TopicHelp?.Invoke("VoLuMe")
            ?? throw new InvalidOperationException("Expected volume help.");
        Assert.Equal("/helpdemo volume", volume.TitleHighlight!);
        Assert.Equal("/helpdemo volume <0-100>",
            volume.Fields!.Single(field => field.Label == "Usage").Value);
        Assert.Equal("Set playback volume.",
            volume.Fields!.Single(field => field.Label == "Description").Value);
        Assert.Equal("/helpdemo volume 50",
            volume.Fields!.Single(field => field.Label == "Example").Value);

        var status = command.TopicHelp!.Invoke("status")
            ?? throw new InvalidOperationException("Expected status help.");
        Assert.Equal("/helpdemo status [arguments]",
            status.Fields!.Single(field => field.Label == "Usage").Value);
        Assert.True(command.TopicHelp!.Invoke("missing") is null);
        Assert.True(command.TopicHelp!.Invoke("extra") is null);

        var simple = registry.Definitions.Single(definition => definition.Name == "legacyhelp");
        Assert.Equal("/legacyhelp [arguments]", simple.Usage);
        Assert.True(simple.TopicHelp is null);
        Assert.True(host.CommandRequests.IsEmpty);

        var context = new CommandContext(NetworkSessionId.New(), BufferId.New());
        var result = await registry.ExecuteAsync(
            context, (CommandInput)CommandLineParser.Parse("/HD volume 25"));
        Assert.True(result.Succeeded);
        Assert.Equal("helpdemo:1:volume 25", result.Message!);
        Assert.True(host.CommandRequests.TryDequeue(out var request));
        Assert.Equal(context, request.Context);
        Assert.Equal("/version", request.CommandLine);
        Assert.False(request.AllowRaw);
        Assert.True(host.CommandRequests.IsEmpty);

        Assert.True(await manager.UnloadAsync("help-metadata"));
        Assert.Equal(0, registry.Definitions.Count);
        Assert.False(registry.TryResolve("helpdemo", out _));
        Assert.False(registry.TryResolve("hd", out _));
        Assert.False(registry.TryResolve("legacyhelp", out _));
    }

    private static async ValueTask ScriptHelpFollowsLifecycleAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "help-reload", """
            clircs.registerCommand("changinghelp", ["ch"], "Original description.", () => "original", {
              usage: "/changinghelp first",
              topics: { first: { description: "Original topic." } }
            });
            """, ["commands"]);

        var host = new TestScriptHost();
        var registry = new CommandRegistry();
        await using (var manager = new ScriptManager(
            directory.Path, WithCoreCommandRegistry(host, registry)))
        {
            await manager.LoadAsync("help-reload");
            var first = registry.Definitions.Single();
            Assert.True(first.TopicHelp!.Invoke("first") is not null);

            WriteScript(directory.Path, "help-reload", """
                clircs.registerCommand("changinghelp", ["ch"], "Updated description.", () => "updated", {
                  usage: "/changinghelp second",
                  topics: { second: { description: "Updated topic." } }
                });
                """, ["commands"], version: "1.0.1");
            await manager.ReloadAsync("help-reload");

            var second = registry.Definitions.Single();
            Assert.False(ReferenceEquals(first, second));
            Assert.True(registry.TryResolve("ch", out var alias));
            Assert.True(ReferenceEquals(second, alias));
            Assert.Equal("/changinghelp second", second.Usage);
            Assert.Equal("Updated description.", second.Description);
            Assert.True(second.TopicHelp!.Invoke("first") is null);

            var topic = second.TopicHelp!.Invoke("second")
                ?? throw new InvalidOperationException("Expected replacement help.");
            Assert.Equal("Updated topic.",
                topic.Fields!.Single(field => field.Label == "Description").Value);
        }

        Assert.Equal(0, registry.Definitions.Count);
        Assert.False(registry.TryResolve("changinghelp", out _));
        Assert.False(registry.TryResolve("ch", out _));
    }

    private static async ValueTask InvalidScriptHelpIsRejectedAsync()
    {
        (string Call, string Message)[] cases =
        [
            ("register([])", "object or null"),
            ("register({usage:7})", "'usage' must be text"),
            ("register({fields:{}})", "'fields' must be an array"),
            ("register({fields:[null]})", "field must be an object"),
            ("register({fields:[{label:'Example'}]})", "requires 'value'"),
            ("register({topics:[]})", "'topics' must be an object"),
            ("register({topics:{volume:[]}})", "topic 'volume' must be an object"),
            ("register({topics:{volume:{}}})", "requires 'description'"),
            ("register({topics:{volume:{description:' '}}})", "nonempty description"),
            ("register({topics:{'two words':{description:'x'}}})", "single tokens"),
            ("register({topics:{volume:{description:'x'},Volume:{description:'y'}}})", "Duplicate help topic"),
            ("register({topics:{volume:{description:'x',fields:[{label:'Example',value:false}]}}})", "'value' must be text"),
            ("register(null, 'x'.repeat(4097))", "summaries")
        ];

        foreach (var item in cases)
        {
            using var directory = new TemporaryDirectory();
            WriteScript(directory.Path, "invalid-help", $$"""
                clircs.registerCommand("before", [], "Before failure.", () => "ready");
                function register(help, summary = "Invalid help.") {
                  clircs.registerCommand("badhelp", ["bh"], summary, () => "bad", help);
                }
                {{item.Call}};
                """, ["commands"]);

            var host = new TestScriptHost();
            var registry = new CommandRegistry();
            await using var manager = new ScriptManager(
                directory.Path, WithCoreCommandRegistry(host, registry));

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.LoadAsync("invalid-help").AsTask());
            Assert.True(failure.Message.Contains(item.Message, StringComparison.OrdinalIgnoreCase),
                $"Unexpected failure for {item.Call}: {failure.Message}");
            Assert.Equal(0, registry.Definitions.Count);
            Assert.False(registry.TryResolve("before", out _));
            Assert.False(registry.TryResolve("badhelp", out _));
            Assert.False(registry.TryResolve("bh", out _));
            Assert.Equal("unloaded", manager.List().Single().Status);
        }
    }

    private static async ValueTask ScriptHelpSizeLimitsAsync()
    {
        (string Name, int Limit, Func<int, object> Help, string Message)[] cases =
        [
            ("usage text", 4096,
                length => new { usage = new string('x', length) },
                "'usage'"),
            ("field labels", 64,
                length => new
                {
                    fields = new[] { new { label = new string('x', length), value = "x" } }
                },
                "'label'"),
            ("field count", 32,
                count => new
                {
                    fields = Enumerable.Range(0, count)
                        .Select(_ => new { label = "Example", value = "x" }).ToArray()
                },
                "32 fields"),
            ("topic count", 64,
                count => new
                {
                    topics = Enumerable.Range(0, count)
                        .ToDictionary(index => $"topic{index}", _ => new { description = "Topic." })
                },
                "64 topics"),
            ("topic names", 64,
                length => new
                {
                    topics = new Dictionary<string, object>
                    {
                        [new string('x', length)] = new { description = "Topic." }
                    }
                },
                "single tokens"),
            ("total help size", 15,
                count => new
                {
                    fields = Enumerable.Range(0, count)
                        .Select(_ => new { label = "Data", value = new string('x', 4096) }).ToArray()
                },
                "65536")
        ];

        foreach (var item in cases)
        {
            foreach (var size in new[] { item.Limit, item.Limit + 1 })
            {
                using var directory = new TemporaryDirectory();
                var helpJson = JsonSerializer.Serialize(item.Help(size));
                WriteScript(directory.Path, "help-limits", $$"""
                    clircs.registerCommand("limited", ["lim"], "Limit test.", () => "ready", {{helpJson}});
                    """, ["commands"]);

                var host = new TestScriptHost();
                var registry = new CommandRegistry();
                await using var manager = new ScriptManager(
                    directory.Path, WithCoreCommandRegistry(host, registry));

                if (size == item.Limit)
                {
                    await manager.LoadAsync("help-limits");
                    Assert.True(registry.TryResolve("lim", out _),
                        $"Valid {item.Name} boundary was not registered.");
                }
                else
                {
                    var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => manager.LoadAsync("help-limits").AsTask());
                    Assert.True(failure.Message.Contains(item.Message, StringComparison.OrdinalIgnoreCase),
                        $"Unexpected {item.Name} failure: {failure.Message}");
                    Assert.Equal(0, registry.Definitions.Count);
                    Assert.False(registry.TryResolve("lim", out _));
                }
            }
        }
    }

    private static async ValueTask LifecycleAndStorageAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "greeter", """
            clircs.registerCommand("hello", ["hi"], "Say hello.", context => {
              const count = Number(clircs.storage.get("count", "0")) + 1;
              clircs.storage.set("count", String(count));
              clircs.setTimeout(() => clircs.print("timer-fired"), 20);
              clircs.run("/version");
              return `hello ${context.args.join(" ")} (${count})`;
            });
            clircs.on("message", event => clircs.print(`${event.kind}:${event.networkId}:${event.text}:${event.fields.actor}`));
            """, ["commands", "events", "timers", "output", "storage"]);

        var host = new TestScriptHost();
        await using (var manager = new ScriptManager(directory.Path, host.Services))
        {
            var loaded = await manager.LoadAsync("greeter");
            Assert.Equal("loaded", loaded.Status);
            Assert.True(host.Commands.TryGetValue("hello", out var command));
            var result = await command!.Handler(new CommandContext(null, null), ["world"], CancellationToken.None);
            Assert.True(result.Succeeded);
            Assert.Equal("hello world (1)", result.Message!);
            await host.WaitForOutputAsync(text => text == "timer-fired", TimeSpan.FromSeconds(2));
            Assert.True(host.CommandRequests.TryDequeue(out var request));
            Assert.Equal("/version", request!.CommandLine);

            var networkId = NetworkSessionId.New();
            manager.Publish(new SessionEvent(
                networkId,
                BufferId.New(),
                SessionEventKind.Message,
                "first",
                DateTimeOffset.UtcNow,
                new Dictionary<string, string?> { ["actor"] = "Alice" }));
            manager.Publish(new SessionEvent(
                networkId,
                BufferId.New(),
                SessionEventKind.Message,
                "second",
                DateTimeOffset.UtcNow,
                new Dictionary<string, string?> { ["actor"] = "Bob" }));
            await host.WaitForOutputAsync(text => text == $"message:{networkId.Value}:first:Alice", TimeSpan.FromSeconds(2));
            await host.WaitForOutputAsync(text => text == $"message:{networkId.Value}:second:Bob", TimeSpan.FromSeconds(2));

            Assert.True(await manager.UnloadAsync("greeter"));
            Assert.False(host.Commands.ContainsKey("hello"));
        }

        var secondHost = new TestScriptHost();
        await using var secondManager = new ScriptManager(directory.Path, secondHost.Services);
        await secondManager.LoadAsync("greeter");
        var secondResult = await secondHost.Commands["hello"].Handler(
            new CommandContext(null, null),
            ["again"],
            CancellationToken.None);
        Assert.Equal("hello again (2)", secondResult.Message!);
    }

    private static async ValueTask LoadedStatePersistsAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "remembered", """
            clircs.registerCommand("remembered", [], "Remember me.", () => "loaded");
            """, ["commands"]);

        var firstHost = new TestScriptHost();
        await using (var first = new ScriptManager(directory.Path, firstHost.Services))
        {
            await first.LoadAsync("remembered");
        }

        var statePath = Path.Combine(directory.Path, "script-load-state.json");
        Assert.True(File.ReadAllText(statePath).Contains("remembered", StringComparison.Ordinal));

        var secondHost = new TestScriptHost();
        await using (var second = new ScriptManager(directory.Path, secondHost.Services))
        {
            var failures = await second.RestoreLoadedAsync();
            Assert.Equal(0, failures.Count);
            Assert.True(secondHost.Commands.ContainsKey("remembered"));
            Assert.True(await second.UnloadAsync("remembered"));
        }

        var thirdHost = new TestScriptHost();
        await using var third = new ScriptManager(directory.Path, thirdHost.Services);
        var finalFailures = await third.RestoreLoadedAsync();
        Assert.Equal(0, finalFailures.Count);
        Assert.False(thirdHost.Commands.ContainsKey("remembered"));
    }

    private static async ValueTask RestoreFailuresAreIsolatedAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "aaa-declined", """
            clircs.run("/version");
            clircs.registerCommand("declined", [], "Must not load.", () => "no");
            """, ["commands"]);
        WriteScript(directory.Path, "available", """
            clircs.registerCommand("available", [], "Still loads.", () => "yes");
            """, ["commands"]);
        File.WriteAllText(
            Path.Combine(directory.Path, "script-load-state.json"),
            """
            {
              "SchemaVersion": 1,
              "Loaded": [ "aaa-declined", "available", "missing" ]
            }
            """);

        var host = new TestScriptHost
        {
            PermissionApproval = request => request.Id != "aaa-declined"
        };
        await using var manager = new ScriptManager(directory.Path, host.Services);
        var failures = await manager.RestoreLoadedAsync();

        Assert.True(host.Commands.ContainsKey("available"));
        Assert.False(host.Commands.ContainsKey("declined"));
        Assert.True(host.CommandRequests.IsEmpty);
        Assert.Equal(2, host.PermissionRequests.Count);
        Assert.Equal(2, failures.Count);
        Assert.True(failures.Any(failure =>
            failure.Contains("aaa-declined", StringComparison.OrdinalIgnoreCase)));
        Assert.True(failures.Any(failure =>
            failure.Contains("missing", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(0, manager.Errors.Count);

        Assert.True(await manager.UnloadAsync("missing"));
        Assert.False(File.ReadAllText(Path.Combine(directory.Path, "script-load-state.json"))
            .Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    private static async ValueTask DamagedLoadStateIsPreservedAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "recovery", """
            clircs.registerCommand("recovery", [], "Recover.", () => "yes");
            """, ["commands"]);
        var statePath = Path.Combine(directory.Path, "script-load-state.json");
        File.WriteAllText(statePath, "{ this is not json");

        var host = new TestScriptHost();
        await using var manager = new ScriptManager(directory.Path, host.Services);
        var failures = await manager.RestoreLoadedAsync();
        Assert.Equal(1, failures.Count);
        await manager.LoadAsync("recovery");

        Assert.True(Directory.GetFiles(directory.Path, "script-load-state.invalid-*.json").Length == 1);
        Assert.True(File.ReadAllText(statePath).Contains("recovery", StringComparison.Ordinal));
    }

    private static async ValueTask DamagedPermissionsFailClosedAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "permission-recovery", """
            clircs.run("/version");
            clircs.registerCommand("recovered", [], "Recovered script.", () => "ready");
            """, ["commands", "raw", "localNetwork"]);

        const string damaged = "{ broken";
        var permissionsPath = Path.Combine(directory.Path, "script-permissions.json");
        File.WriteAllText(permissionsPath, damaged);

        var host = new TestScriptHost { PermissionApproval = _ => false };
        await using var manager = new ScriptManager(directory.Path, host.Services);

        var failures = await manager.RestoreLoadedAsync();
        Assert.Equal(1, failures.Count);
        Assert.True(failures[0].Contains("permissions", StringComparison.OrdinalIgnoreCase));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => manager.LoadAsync("permission-recovery").AsTask());

        Assert.True(host.CommandRequests.IsEmpty);
        Assert.False(host.Commands.ContainsKey("recovered"));
        Assert.Equal(damaged, File.ReadAllText(permissionsPath));
        Assert.Equal(0, manager.Errors.Count);

        host.PermissionApproval = _ => true;
        var loaded = await manager.LoadAsync("permission-recovery");

        Assert.True(loaded.GrantedPermissions.Contains(ScriptPermission.Raw));
        Assert.True(loaded.GrantedPermissions.Contains(ScriptPermission.LocalNetwork));
        Assert.True(host.Commands.ContainsKey("recovered"));
        Assert.Equal(2, host.PermissionRequests.Count);

        var preserved = Directory.GetFiles(directory.Path, "script-permissions.json.invalid-*");
        Assert.Equal(1, preserved.Length);
        Assert.Equal(damaged, File.ReadAllText(preserved[0]));
    }

    private static async ValueTask FirstLoadPermissionApprovalAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "approval", """
            clircs.run("/version");
            clircs.storage.set("loaded", "yes");
            clircs.registerCommand("approved", [], "Approval test.", () => "ready");
            clircs.ui.setHeader("approval", "loaded", { scope: "all" });
            clircs.run("/quote VERSION");
            """, ["commands", "storage", "ui", "raw", "localNetwork"]);

        var storagePath = Path.Combine(directory.Path, "script-data", "approval.json");
        var host = new TestScriptHost { PermissionApproval = _ => false };
        await using var manager = new ScriptManager(directory.Path, host.Services);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => manager.LoadAsync("approval").AsTask());

        var review = host.PermissionRequests.Single();
        Assert.Equal("approval", review.Id);
        Assert.Equal("approval", review.Name);
        Assert.Equal("1.0.0", review.Version);
        Assert.Equal(5, review.RequestedPermissions.Count);
        Assert.Equal(0, review.GrantedPermissions.Count);
        Assert.True(review.NewPermissions.ToHashSet().SetEquals(review.RequestedPermissions));

        Assert.True(host.CommandRequests.IsEmpty);
        Assert.Equal(0, host.Commands.Count);
        Assert.Equal(0, host.Headers.Count);
        Assert.False(File.Exists(storagePath));
        Assert.False(File.Exists(Path.Combine(directory.Path, "script-permissions.json")));
        Assert.False(File.Exists(Path.Combine(directory.Path, "script-load-state.json")));
        Assert.Equal("unloaded", manager.List().Single().Status);
        Assert.Equal(0, manager.Errors.Count);

        host.PermissionApproval = _ =>
        {
            Assert.True(host.CommandRequests.IsEmpty);
            Assert.Equal(0, host.Commands.Count);
            Assert.False(File.Exists(storagePath));
            Thread.Sleep(400);
            return true;
        };

        var loaded = await manager.LoadAsync("approval");
        Assert.Equal("loaded", loaded.Status);
        Assert.True(loaded.GrantedPermissions.ToHashSet().SetEquals(review.RequestedPermissions));
        Assert.Equal(2, host.PermissionRequests.Count);
        Assert.True(host.Commands.ContainsKey("approved"));
        Assert.Equal(1, host.Headers.Count);
        Assert.True(File.Exists(storagePath));

        Assert.True(host.CommandRequests.TryDequeue(out var normal));
        Assert.Equal("/version", normal.CommandLine);
        Assert.True(host.CommandRequests.TryDequeue(out var raw));
        Assert.Equal("/quote VERSION", raw.CommandLine);
        Assert.True(raw.AllowRaw);
        Assert.True(host.CommandRequests.IsEmpty);
    }

    private static async ValueTask PermissionApprovalPersistsAsync()
    {
        using var directory = new TemporaryDirectory();
        const string source = """
            clircs.registerCommand("approved", [], "Approval persistence test.", () => "ready");
            """;
        WriteScript(directory.Path, "approval-persist", source, ["commands"]);

        var firstHost = new TestScriptHost();
        await using (var first = new ScriptManager(directory.Path, firstHost.Services))
        {
            await first.LoadAsync("approval-persist");

            WriteScript(
                directory.Path, "approval-persist", source, ["commands"], version: "1.0.1");
            var reloaded = await first.ReloadAsync("approval-persist");
            Assert.Equal("1.0.1", reloaded.Version);

            Assert.True(await first.UnloadAsync("approval-persist"));
            await first.LoadAsync("approval-persist");
            Assert.Equal(1, firstHost.PermissionRequests.Count);
        }

        var secondHost = new TestScriptHost
        {
            PermissionApproval = _ =>
                throw new InvalidOperationException("Saved approval was requested again.")
        };
        await using var second = new ScriptManager(directory.Path, secondHost.Services);
        var failures = await second.RestoreLoadedAsync();

        Assert.Equal(0, failures.Count);
        Assert.True(secondHost.Commands.ContainsKey("approved"));
        Assert.True(secondHost.PermissionRequests.IsEmpty);
        Assert.Equal("1.0.1", second.List().Single().Version);

        var result = await secondHost.Commands["approved"].Handler(
            new CommandContext(null, null), [], CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal("ready", result.Message!);
    }

    private static async ValueTask AdditionalPermissionsRequireApprovalAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "permission-update", """
            clircs.registerCommand("updated", [], "Original script.", () => "original");
            """, ["commands"]);

        var host = new TestScriptHost();
        await using var manager = new ScriptManager(directory.Path, host.Services);
        await manager.LoadAsync("permission-update");

        var original = host.Commands["updated"];
        var permissionsPath = Path.Combine(directory.Path, "script-permissions.json");
        var savedPermissions = File.ReadAllText(permissionsPath);

        WriteScript(directory.Path, "permission-update", """
            clircs.run("/version");
            clircs.registerCommand("updated", [], "Updated script.", () => {
              clircs.run("/quote VERSION");
              return "updated";
            });
            """, ["commands", "raw"], version: "1.0.1");

        host.PermissionApproval = _ => false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => manager.ReloadAsync("permission-update").AsTask());

        Assert.Equal(2, host.PermissionRequests.Count);
        var review = host.PermissionRequests.Last();
        Assert.Equal(ScriptPermission.Raw, review.NewPermissions.Single());
        Assert.Equal(ScriptPermission.Commands, review.GrantedPermissions.Single());
        Assert.Equal(savedPermissions, File.ReadAllText(permissionsPath));
        Assert.True(ReferenceEquals(original, host.Commands["updated"]));
        Assert.True(host.CommandRequests.IsEmpty);
        Assert.Equal("loaded", manager.List().Single().Status);
        Assert.Equal(0, manager.Errors.Count);

        var originalResult = await original.Handler(
            new CommandContext(null, null), [], CancellationToken.None);
        Assert.True(originalResult.Succeeded);
        Assert.Equal("original", originalResult.Message!);

        host.PermissionApproval = _ => true;
        var updated = await manager.ReloadAsync("permission-update");
        Assert.Equal("1.0.1", updated.Version);
        Assert.True(updated.GrantedPermissions.Contains(ScriptPermission.Raw));
        Assert.Equal(3, host.PermissionRequests.Count);
        Assert.False(ReferenceEquals(original, host.Commands["updated"]));

        var updatedResult = await host.Commands["updated"].Handler(
            new CommandContext(null, null), [], CancellationToken.None);
        Assert.True(updatedResult.Succeeded);
        Assert.Equal("updated", updatedResult.Message!);

        Assert.True(host.CommandRequests.TryDequeue(out var normal));
        Assert.Equal("/version", normal.CommandLine);
        Assert.True(host.CommandRequests.TryDequeue(out var raw));
        Assert.Equal("/quote VERSION", raw.CommandLine);
        Assert.True(raw.AllowRaw);
        Assert.True(host.CommandRequests.IsEmpty);
    }

    private static async ValueTask AdditionalApprovalPreservesRevocationsAsync()
    {
        using var directory = new TemporaryDirectory();
        const string source = """
            if (clircs.permissions.has("storage")) {
              clircs.storage.set("approved", "yes");
            }
            clircs.registerCommand("checkpermissions", [], "Inspect permissions.", () => {
              clircs.run("/quote VERSION");
              return clircs.permissions.has("storage") ? "storage granted" : "storage denied";
            });
            """;
        WriteScript(directory.Path, "permission-revocation", source, ["commands", "raw"]);

        var host = new TestScriptHost();
        await using (var manager = new ScriptManager(directory.Path, host.Services))
        {
            await manager.LoadAsync("permission-revocation");
            await manager.SetPermissionAsync(
                "permission-revocation", ScriptPermission.Raw, granted: false);
            Assert.Equal(1, host.PermissionRequests.Count);

            WriteScript(
                directory.Path, "permission-revocation", source,
                ["commands", "raw", "storage"], version: "1.0.1");

            var reloaded = await manager.ReloadAsync("permission-revocation");
            Assert.Equal(2, host.PermissionRequests.Count);
            var review = host.PermissionRequests.Last();
            Assert.Equal(ScriptPermission.Storage, review.NewPermissions.Single());
            Assert.False(review.GrantedPermissions.Contains(ScriptPermission.Raw));
            Assert.True(reloaded.GrantedPermissions.Contains(ScriptPermission.Storage));
            Assert.False(reloaded.GrantedPermissions.Contains(ScriptPermission.Raw));
            Assert.True(File.Exists(Path.Combine(
                directory.Path, "script-data", "permission-revocation.json")));

            var result = await host.Commands["checkpermissions"].Handler(
                new CommandContext(null, null), [], CancellationToken.None);
            Assert.True(result.Succeeded);
            Assert.Equal("storage granted", result.Message!);
            Assert.True(host.CommandRequests.TryDequeue(out var request));
            Assert.Equal("/quote VERSION", request.CommandLine);
            Assert.False(request.AllowRaw);
            Assert.True(host.CommandRequests.IsEmpty);
        }

        var secondHost = new TestScriptHost { PermissionApproval = _ => false };
        await using var second = new ScriptManager(directory.Path, secondHost.Services);
        var failures = await second.RestoreLoadedAsync();

        Assert.Equal(0, failures.Count);
        Assert.True(secondHost.PermissionRequests.IsEmpty);
        Assert.True(secondHost.Commands.ContainsKey("checkpermissions"));
        var restored = second.List().Single();
        Assert.True(restored.GrantedPermissions.Contains(ScriptPermission.Storage));
        Assert.False(restored.GrantedPermissions.Contains(ScriptPermission.Raw));
    }

    private static async ValueTask PermissionSaveFailurePreventsExecutionAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "save-failure", """
            clircs.run("/version");
            clircs.registerCommand("saved", [], "Saved approval.", () => "ready");
            """, ["commands"]);

        var permissionsPath = Path.Combine(directory.Path, "script-permissions.json");
        Directory.CreateDirectory(permissionsPath);

        var host = new TestScriptHost();
        await using var manager = new ScriptManager(directory.Path, host.Services);
        var failure = await Assert.ThrowsAsync<Exception>(
            () => manager.LoadAsync("save-failure").AsTask());

        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(1, host.PermissionRequests.Count);
        Assert.True(host.CommandRequests.IsEmpty);
        Assert.False(host.Commands.ContainsKey("saved"));
        Assert.Equal("unloaded", manager.List().Single().Status);
        Assert.Equal(0, manager.List().Single().GrantedPermissions.Count);
        Assert.Equal(0, manager.Errors.Count);
        Assert.False(File.Exists(Path.Combine(directory.Path, "script-load-state.json")));

        Directory.Delete(permissionsPath);
        await manager.LoadAsync("save-failure");

        Assert.Equal(2, host.PermissionRequests.Count);
        Assert.True(host.Commands.ContainsKey("saved"));
        Assert.True(host.CommandRequests.TryDequeue(out var request));
        Assert.Equal("/version", request.CommandLine);
        Assert.True(host.CommandRequests.IsEmpty);
    }

    private static async ValueTask RevocationSurvivesCanceledReloadAsync()
    {
        using var directory = new TemporaryDirectory();
        const string source = """
            clircs.registerCommand("privileged", [], "Raw permission test.", () => {
              clircs.run("/quote VERSION");
            });
            """;
        WriteScript(directory.Path, "revoked-reload", source, ["commands", "raw"]);

        var host = new TestScriptHost();
        await using (var manager = new ScriptManager(directory.Path, host.Services))
        {
            await manager.LoadAsync("revoked-reload");
            var original = host.Commands["privileged"];

            WriteScript(
                directory.Path, "revoked-reload", source,
                ["commands", "raw", "storage"], version: "1.0.1");
            host.PermissionApproval = _ => false;

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                manager.SetPermissionAsync(
                    "revoked-reload", ScriptPermission.Raw, granted: false).AsTask());

            Assert.Equal(2, host.PermissionRequests.Count);
            var review = host.PermissionRequests.Last();
            Assert.Equal(ScriptPermission.Storage, review.NewPermissions.Single());
            Assert.False(review.GrantedPermissions.Contains(ScriptPermission.Raw));
            Assert.False(host.Commands.ContainsKey("privileged"));

            var unloaded = manager.List().Single();
            Assert.Equal("unloaded", unloaded.Status);
            Assert.False(unloaded.GrantedPermissions.Contains(ScriptPermission.Raw));

            var staleResult = await original.Handler(
                new CommandContext(null, null), [], CancellationToken.None);
            Assert.False(staleResult.Succeeded);
            Assert.True(host.CommandRequests.IsEmpty);
        }

        var secondHost = new TestScriptHost();
        await using var second = new ScriptManager(directory.Path, secondHost.Services);
        var failures = await second.RestoreLoadedAsync();

        Assert.Equal(0, failures.Count);
        var nextReview = secondHost.PermissionRequests.Single();
        Assert.Equal(ScriptPermission.Storage, nextReview.NewPermissions.Single());
        Assert.False(nextReview.GrantedPermissions.Contains(ScriptPermission.Raw));
        Assert.True(secondHost.Commands.ContainsKey("privileged"));
        Assert.False(second.List().Single().GrantedPermissions.Contains(ScriptPermission.Raw));
    }

    private static async ValueTask UndeclaredCapabilityFailsAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "overreach", """
            clircs.registerCommand("shouldnotexist", [], "No.", () => "no");
            """, ["output"]);
        var host = new TestScriptHost();
        await using var manager = new ScriptManager(directory.Path, host.Services);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.LoadAsync("overreach").AsTask());
        Assert.True(exception.Message.Contains("permission", StringComparison.OrdinalIgnoreCase));
        Assert.False(host.Commands.ContainsKey("shouldnotexist"));
    }

    private static async ValueTask RunawayScriptIsBoundedAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "runaway", """
            clircs.registerCommand("spin", ["sp"], "Attempt an infinite loop.", () => {
              while (true) { }
            }, {
              usage: "/spin",
              topics: { limits: { description: "Exercise the script execution limit." } }
            });
            """, ["commands"]);

        var host = new TestScriptHost();
        var registry = new CommandRegistry();
        await using var manager = new ScriptManager(
            directory.Path, WithCoreCommandRegistry(host, registry));
        await manager.LoadAsync("runaway");

        var command = registry.Definitions.Single();
        Assert.True(command.TopicHelp!.Invoke("limits") is not null);

        var stopwatch = Stopwatch.StartNew();
        var result = await registry.ExecuteAsync(
            new CommandContext(null, null),
            (CommandInput)CommandLineParser.Parse("/sp"));
        stopwatch.Stop();

        Assert.False(result.Succeeded);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            "Runaway script exceeded its execution budget.");
        Assert.Equal("faulted", manager.List().Single(script => script.Id == "runaway").Status);
        Assert.Equal(0, registry.Definitions.Count);
        Assert.False(registry.TryResolve("spin", out _));
        Assert.False(registry.TryResolve("sp", out _));
    }

    private static async ValueTask NormalIrcCommandsDoNotRequirePermissionAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "networking", """
            clircs.run("/version");
            """, []);

        var host = new TestScriptHost();
        await using var manager = new ScriptManager(directory.Path, host.Services);

        var loaded = await manager.LoadAsync("networking");
        Assert.Equal("loaded", loaded.Status);
        Assert.Equal(0, loaded.RequestedPermissions.Count);
        Assert.Equal(0, loaded.GrantedPermissions.Count);
        Assert.True(host.CommandRequests.TryDequeue(out var request));
        Assert.Equal("/version", request.CommandLine);
        Assert.False(request.AllowRaw);
        Assert.True(host.CommandRequests.IsEmpty);
    }

    private static async ValueTask RawPermissionRequiresGrantAsync()
    {
        foreach (var allowRaw in new[] { false, true })
        {
            using var directory = new TemporaryDirectory();
            WriteScript(directory.Path, "raw-check", """
                clircs.registerCommand("rawcheck", [], "Test raw IRC.", () => {
                  clircs.run("/quote PRIVMSG Nick :hello  world");
                });
                """, ["commands", "raw"]);

            var host = new TestScriptHost();
            await using var manager = new ScriptManager(directory.Path, host.Services);

            await manager.SetPermissionAsync("raw-check", ScriptPermission.Raw, granted: allowRaw);

            var loaded = await manager.LoadAsync("raw-check");
            Assert.Equal(allowRaw, loaded.GrantedPermissions.Contains(ScriptPermission.Raw));

            var context = new CommandContext(NetworkSessionId.New(), BufferId.New());
            var result = await host.Commands["rawcheck"].Handler(
                context, [], CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.True(host.CommandRequests.TryDequeue(out var request));
            Assert.Equal("raw-check", request.ScriptId);
            Assert.Equal(context, request.Context);
            Assert.Equal("/quote PRIVMSG Nick :hello  world", request.CommandLine);
            Assert.Equal(allowRaw, request.AllowRaw);
            Assert.True(host.CommandRequests.IsEmpty);
        }
    }

    private static async ValueTask RawPermissionPersistsAndRevokesAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "raw-persist", """
            clircs.registerCommand("rawpersist", [], "Test saved raw access.", () => {
              clircs.run("/raw VERSION");
            });
            """, ["commands", "raw"]);

        var host = new TestScriptHost();
        var context = new CommandContext(null, null);

        await using (var manager = new ScriptManager(directory.Path, host.Services))
        {
            await manager.LoadAsync("raw-persist");
        }

        await using (var manager = new ScriptManager(directory.Path, host.Services))
        {
            var loaded = await manager.LoadAsync("raw-persist");
            Assert.True(loaded.GrantedPermissions.Contains(ScriptPermission.Raw));

            var result = await host.Commands["rawpersist"].Handler(
                context, [], CancellationToken.None);
            Assert.True(result.Succeeded);
            Assert.True(host.CommandRequests.TryDequeue(out var granted));
            Assert.True(granted.AllowRaw);

            await manager.SetPermissionAsync("raw-persist", ScriptPermission.Raw, granted: false);

            result = await host.Commands["rawpersist"].Handler(
                context, [], CancellationToken.None);
            Assert.True(result.Succeeded);
            Assert.True(host.CommandRequests.TryDequeue(out var revoked));
            Assert.False(revoked.AllowRaw);
        }

        await using (var manager = new ScriptManager(directory.Path, host.Services))
        {
            var loaded = await manager.LoadAsync("raw-persist");
            Assert.False(loaded.GrantedPermissions.Contains(ScriptPermission.Raw));

            var result = await host.Commands["rawpersist"].Handler(
                context, [], CancellationToken.None);
            Assert.True(result.Succeeded);
            Assert.True(host.CommandRequests.TryDequeue(out var revoked));
            Assert.False(revoked.AllowRaw);
        }

        Assert.Equal(1, host.PermissionRequests.Count);
    }

    private static async ValueTask LocalNetworkPermissionRequiresGrantAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "localnet", """
            clircs.local.websocket.connect("ws://127.0.0.1:7905", () => {});
            """, ["localNetwork"]);
        var host = new TestScriptHost();
        await using var manager = new ScriptManager(directory.Path, host.Services);

        await manager.SetPermissionAsync("localnet", ScriptPermission.LocalNetwork, granted: false);
        var denied = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.LoadAsync("localnet").AsTask());
        Assert.True(denied.Message.Contains("not granted", StringComparison.OrdinalIgnoreCase));
        await manager.SetPermissionAsync("localnet", ScriptPermission.LocalNetwork, granted: true);
        await manager.LoadAsync("localnet");
    }

    private static async ValueTask ScriptHeadersAreOwnedAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "header", """
            clircs.ui.setHeader("example", "hello", { scope: "all", priority: 4, minimumWidth: 12 });
            """, ["ui"]);
        var host = new TestScriptHost();
        await using var manager = new ScriptManager(directory.Path, host.Services);
        await manager.LoadAsync("header");
        Assert.True(host.Headers.TryGetValue(("header", "example"), out var header));
        Assert.Equal("hello", header!.Text);
        Assert.True(await manager.UnloadAsync("header"));
        Assert.Equal(0, host.Headers.Count);
    }

    private static async ValueTask FailedLoadRollsBackResourcesAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "broken-header", """
            clircs.ui.setHeader("temporary", "must disappear", { scope: "all" });
            throw new Error("load failed");
            """, ["ui"]);
        var host = new TestScriptHost();
        await using var manager = new ScriptManager(directory.Path, host.Services);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.LoadAsync("broken-header").AsTask());
        Assert.Equal(0, host.Headers.Count);
    }

    private static async ValueTask MissingLoadedSourceRemainsVisibleAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "vanishing", """
            clircs.registerCommand("vanishing", [], "Temporary command.", () => "present");
            """, ["commands"]);
        var host = new TestScriptHost();
        await using var manager = new ScriptManager(directory.Path, host.Services);
        await manager.LoadAsync("vanishing");

        Directory.Delete(Path.Combine(directory.Path, "scripts", "vanishing"), recursive: true);
        var listed = manager.List().Single(script => script.Id == "vanishing");
        Assert.Equal("loaded (source missing)", listed.Status);
        Assert.True(await manager.UnloadAsync("vanishing"));
        Assert.False(host.Commands.ContainsKey("vanishing"));
    }

    private static async ValueTask CallbackQueueIsBoundedAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "backlog", """
            clircs.on("message", () => clircs.secrets.prompt("token", "delayed prompt"));
            """, ["events", "secrets"]);
        var host = new TestScriptHost { BlockSecretPrompt = true };
        await using var manager = new ScriptManager(directory.Path, host.Services);
        await manager.LoadAsync("backlog");

        var networkId = NetworkSessionId.New();
        var firstPublish = Task.Run(() => manager.Publish(new SessionEvent(
            networkId,
            BufferId.New(),
            SessionEventKind.Message,
            "blocked",
            DateTimeOffset.UtcNow)));
        await host.SecretPromptEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var index = 0; index < 600; index++)
        {
            manager.Publish(new SessionEvent(
                networkId,
                BufferId.New(),
                SessionEventKind.Message,
                index.ToString(),
                DateTimeOffset.UtcNow));
        }

        host.ReleaseSecretPrompt.Set();
        await firstPublish.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("faulted", manager.List().Single(script => script.Id == "backlog").Status);
        Assert.True(manager.Errors.Any(error => error.Message.Contains("callback queue", StringComparison.OrdinalIgnoreCase)));
    }

    private static async ValueTask HostResourcesAreBoundedAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "timer-hoard", """
            for (let index = 0; index < 257; index++) {
              clircs.setTimeout(() => {}, 86400000);
            }
            """, ["timers"]);
        var host = new TestScriptHost();
        await using var manager = new ScriptManager(directory.Path, host.Services);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.LoadAsync("timer-hoard").AsTask());
        Assert.True(exception.Message.Contains("256", StringComparison.Ordinal));
    }

    private static async ValueTask ListingScriptsIsReadOnlyAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "listed", "clircs.print('available');", ["output"]);
        var host = new TestScriptHost();
        await using var manager = new ScriptManager(directory.Path, host.Services);

        Assert.Equal("unloaded", manager.List().Single(script => script.Id == "listed").Status);
        Assert.False(File.Exists(Path.Combine(directory.Path, "script-permissions.json")));
    }

    private static async ValueTask ScriptSecretsPersistAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "secret", """
            clircs.registerCommand("secretvalue", [], "Read a private secret.", () => {
              if (!clircs.secrets.get("token")) {
                clircs.secrets.prompt("token", "test token");
              }
              return clircs.secrets.get("token");
            });
            """, ["commands", "secrets"]);
        var firstHost = new TestScriptHost { SecretValue = "swordfish" };
        await using (var manager = new ScriptManager(directory.Path, firstHost.Services))
        {
            await manager.LoadAsync("secret");
            var result = await firstHost.Commands["secretvalue"].Handler(
                new CommandContext(null, null), [], CancellationToken.None);
            Assert.Equal("swordfish", result.Message!);
        }

        var secretFile = Directory.GetFiles(Path.Combine(directory.Path, "script-secrets")).Single();
        Assert.False(File.ReadAllText(secretFile).Contains("swordfish", StringComparison.Ordinal));
        var secondHost = new TestScriptHost();
        await using var secondManager = new ScriptManager(directory.Path, secondHost.Services);
        await secondManager.LoadAsync("secret");
        var second = await secondHost.Commands["secretvalue"].Handler(
            new CommandContext(null, null), [], CancellationToken.None);
        Assert.Equal("swordfish", second.Message!);
    }

    private static async ValueTask ScriptSecretPromptIsUntimedAsync()
    {
        using var directory = new TemporaryDirectory();
        WriteScript(directory.Path, "slow-secret", """
            clircs.registerCommand("slowsecret", [], "Prompt for a secret.", () => {
              return clircs.secrets.prompt("token", "slow token")
                ? clircs.secrets.get("token")
                : "cancelled";
            });
            """, ["commands", "secrets"]);
        var host = new TestScriptHost
        {
            SecretValue = "eventually",
            SecretDelay = TimeSpan.FromMilliseconds(400)
        };
        await using var manager = new ScriptManager(directory.Path, host.Services);
        await manager.LoadAsync("slow-secret");
        var result = await host.Commands["slowsecret"].Handler(
            new CommandContext(null, null), [], CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal("eventually", result.Message!);
        Assert.Equal("loaded", manager.List().Single().Status);
    }

    private static async ValueTask MusikcubeAddonLoadsAsync()
    {
        using var directory = new TemporaryDirectory();
        var examples = Path.Combine(Directory.GetCurrentDirectory(), "examples");
        var host = new TestScriptHost();
        await using var manager = new ScriptManager(directory.Path, host.Services, examples);
        await manager.SetPermissionAsync("musikcube", ScriptPermission.LocalNetwork, granted: false);
        var loaded = await manager.LoadAsync("musikcube");
        Assert.Equal("loaded", loaded.Status);
        Assert.True(host.Commands.ContainsKey("song"));
        Assert.True(host.Commands.ContainsKey("music"));
        var status = await host.Commands["music"].Handler(
            new CommandContext(null, null), ["status"], CancellationToken.None);
        Assert.True(status.Message!.Contains("localnetwork", StringComparison.OrdinalIgnoreCase));
    }

    private static async ValueTask MusikcubeAddonProtocolAsync()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(Directory.GetCurrentDirectory(), "examples", "musikcube");
        var destination = Path.Combine(directory.Path, "scripts", "musikcube");
        CopyDirectory(source, destination);
        Directory.CreateDirectory(Path.Combine(directory.Path, "script-data"));
        var port = FreePort();
        File.WriteAllText(
            Path.Combine(directory.Path, "script-data", "musikcube.json"),
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["address"] = "127.0.0.1",
                ["port"] = port.ToString()
            }));

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var handshaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broadcastAllowed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pollReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var periodicRefreshReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TcpClient? acceptedClient = null;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            acceptedClient = client;
            await using var stream = client.GetStream();
            await AcceptWebSocketAsync(stream);
            handshaken.TrySetResult();
            var authenticate = JsonDocument.Parse(await ReceiveWebSocketTextAsync(stream));
            await SendWebSocketTextAsync(stream, JsonSerializer.Serialize(new
            {
                name = "authenticate",
                type = "response",
                id = authenticate.RootElement.GetProperty("id").GetString(),
                options = new
                {
                    authenticated = true,
                    environment = new { api_version = 20, app_version = "3.0.5" }
                }
            }));

            var overviewRequest = JsonDocument.Parse(await ReceiveWebSocketTextAsync(stream));
            Assert.Equal("get_playback_overview", overviewRequest.RootElement.GetProperty("name").GetString()!);
            await SendWebSocketTextAsync(stream, JsonSerializer.Serialize(new
            {
                name = "get_playback_overview",
                type = "response",
                id = overviewRequest.RootElement.GetProperty("id").GetString(),
                options = new
                {
                    state = "playing",
                    playing_duration = 349,
                    playing_track = new { artist = "The Smashing Pumpkins", title = "Mayonaise" }
                }
            }));

            await broadcastAllowed.Task;
            await SendWebSocketTextAsync(stream, JsonSerializer.Serialize(new
            {
                name = "playback_overview_changed",
                type = "broadcast",
                id = "musikcube-broadcast-1",
                options = new
                {
                    state = "paused",
                    playing_duration = 133,
                    playing_track = new { artist = "The White Stripes", title = "Apple Blossom" }
                }
            }));

            while (true)
            {
                var request = JsonDocument.Parse(await ReceiveWebSocketTextAsync(stream));
                var name = request.RootElement.GetProperty("name").GetString();
                if (name == "get_playback_overview" && !pollReceived.Task.IsCompleted)
                {
                    await SendWebSocketTextAsync(stream, JsonSerializer.Serialize(new
                    {
                        name = "get_playback_overview",
                        type = "response",
                        id = request.RootElement.GetProperty("id").GetString(),
                        options = new
                        {
                            state = "playing",
                            playing_duration = 187,
                            playing_track = new { artist = "The White Stripes", title = "Little Bird" }
                        }
                    }));
                    pollReceived.TrySetResult();
                }
                else if (name == "get_playback_overview" && !periodicRefreshReceived.Task.IsCompleted)
                {
                    await SendWebSocketTextAsync(stream, JsonSerializer.Serialize(new
                    {
                        name = "get_playback_overview",
                        type = "response",
                        id = request.RootElement.GetProperty("id").GetString(),
                        options = new
                        {
                            state = "playing",
                            playing_duration = 231,
                            playing_track = new { artist = "The White Stripes", title = "Dead Leaves and the Dirty Ground" }
                        }
                    }));
                    periodicRefreshReceived.TrySetResult();
                }
                else if (name == "next")
                {
                    nextReceived.TrySetResult();
                    return;
                }
            }
        });

        var host = new TestScriptHost();
        await using var manager = new ScriptManager(directory.Path, host.Services);
        var loaded = await manager.LoadAsync("musikcube");
        Assert.True(loaded.GrantedPermissions.Contains(ScriptPermission.LocalNetwork));
        Assert.Equal(1, host.PermissionRequests.Count);
        var initialStatus = await host.Commands["music"].Handler(
            new CommandContext(null, null), ["status"], CancellationToken.None);
        ScriptHeaderContribution header;
        try
        {
            header = await host.WaitForHeaderAsync(
                item => item.Text.Contains("Playing: The Smashing Pumpkins - Mayonaise [5:49]", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException exception)
        {
            if (!handshaken.Task.IsCompleted)
            {
                listener.Stop();
                acceptedClient?.Dispose();
                throw new TestSkippedException(
                    "ClientWebSocket loopback handshakes are unavailable in this sandbox; protocol behavior is covered by addon and ownership tests.");
            }
            var serverError = server.IsFaulted ? server.Exception?.GetBaseException().Message : "server still waiting";
            var scriptErrors = string.Join("; ", manager.Errors.Select(error => $"{error.Operation}: {error.Message}"));
            throw new InvalidOperationException(
                $"musikcube header did not arrive; status={initialStatus.Message}; {serverError}; {scriptErrors}", exception);
        }
        Assert.True(header.BufferId is null);

        broadcastAllowed.TrySetResult();
        await host.WaitForHeaderAsync(
            item => item.Text.Contains("Paused: The White Stripes - Apple Blossom [2:13]", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        var refreshedStatus = await host.Commands["music"].Handler(
            new CommandContext(null, null), ["status"], CancellationToken.None);
        Assert.True(refreshedStatus.Succeeded);
        await pollReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.WaitForHeaderAsync(
            item => item.Text.Contains("Playing: The White Stripes - Little Bird [3:07]", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        await periodicRefreshReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.WaitForHeaderAsync(
            item => item.Text.Contains("Playing: The White Stripes - Dead Leaves and the Dirty Ground [3:51]", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        var next = await host.Commands["music"].Handler(
            new CommandContext(null, null), ["next"], CancellationToken.None);
        Assert.True(next.Succeeded);
        await nextReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.UnloadAsync("musikcube");
        listener.Stop();
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task AcceptWebSocketAsync(NetworkStream stream)
    {
        var request = new List<byte>();
        var one = new byte[1];
        while (request.Count < 16 * 1024)
        {
            if (await stream.ReadAsync(one) == 0)
            {
                throw new EndOfStreamException();
            }
            request.Add(one[0]);
            if (request.Count >= 4 &&
                request[^4] == '\r' && request[^3] == '\n' &&
                request[^2] == '\r' && request[^1] == '\n')
            {
                break;
            }
        }
        var headers = Encoding.ASCII.GetString(request.ToArray());
        var keyLine = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
        var key = keyLine[(keyLine.IndexOf(':') + 1)..].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(
            Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
        await stream.WriteAsync(response);
    }

    private static async Task<string> ReceiveWebSocketTextAsync(NetworkStream stream)
    {
        var header = new byte[2];
        await ReadExactlyAsync(stream, header);
        if ((header[0] & 0x0f) == 8)
        {
            throw new EndOfStreamException();
        }
        var masked = (header[1] & 0x80) != 0;
        ulong length = (uint)(header[1] & 0x7f);
        if (length == 126)
        {
            var extended = new byte[2];
            await ReadExactlyAsync(stream, extended);
            length = (uint)((extended[0] << 8) | extended[1]);
        }
        else if (length == 127)
        {
            var extended = new byte[8];
            await ReadExactlyAsync(stream, extended);
            if (BitConverter.IsLittleEndian) Array.Reverse(extended);
            length = BitConverter.ToUInt64(extended);
        }
        if (length > 1024 * 1024)
        {
            throw new InvalidDataException();
        }
        var mask = masked ? new byte[4] : [];
        if (masked) await ReadExactlyAsync(stream, mask);
        var payload = new byte[(int)length];
        await ReadExactlyAsync(stream, payload);
        if (masked)
        {
            for (var index = 0; index < payload.Length; index++) payload[index] ^= mask[index % 4];
        }
        return Encoding.UTF8.GetString(payload);
    }

    private static async Task SendWebSocketTextAsync(NetworkStream stream, string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        using var frame = new MemoryStream();
        frame.WriteByte(0x81);
        if (payload.Length < 126)
        {
            frame.WriteByte((byte)payload.Length);
        }
        else
        {
            frame.WriteByte(126);
            frame.WriteByte((byte)(payload.Length >> 8));
            frame.WriteByte((byte)payload.Length);
        }
        frame.Write(payload);
        await stream.WriteAsync(frame.ToArray());
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset));
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    private static ScriptHostServices WithCoreCommandRegistry(
        TestScriptHost host,
        CommandRegistry registry) =>
        host.Services with
        {
            RegisterCommand = registration =>
            {
                var definition = ClientApplication.CreateScriptCommandDefinition(registration);
                registry.Register(definition);
                return new CallbackDisposable(() => registry.Unregister(definition));
            }
        };

    private static void WriteScript(
        string root,
        string id,
        string source,
        string[] permissions,
        string version = "1.0.0")
    {
        var scriptDirectory = System.IO.Path.Combine(root, "scripts", id);
        Directory.CreateDirectory(scriptDirectory);
        var manifest = new
        {
            schemaVersion = 1,
            id,
            name = id,
            version,
            entry = "main.js",
            permissions
        };
        File.WriteAllText(
            System.IO.Path.Combine(scriptDirectory, "clircs-script.json"),
            JsonSerializer.Serialize(manifest));
        File.WriteAllText(System.IO.Path.Combine(scriptDirectory, "main.js"), source);
    }

    private sealed class WindowBindingHost
    {
        public WindowBindingHost()
        {
            Services = new ScriptWindowHostServices(
                () =>
                {
                    Calls.Enqueue("active");
                    return ActiveResult;
                },
                networkId =>
                {
                    Calls.Enqueue("status");
                    StatusRequests.Enqueue(networkId);
                    return StatusResult;
                },
                (name, networkId) =>
                {
                    Calls.Enqueue("find");
                    Finds.Enqueue((name, networkId));
                    return FindResult;
                },
                (scriptId, name, networkId) =>
                {
                    Calls.Enqueue("open");
                    Opens.Enqueue((scriptId, name, networkId));
                    return OpenResult;
                },
                (scriptId, bufferId, output) =>
                {
                    Calls.Enqueue("write");
                    Writes.Enqueue((scriptId, bufferId, output));
                    return WriteResult;
                });
        }

        public BufferId? ActiveResult { get; set; } = BufferId.New();

        public BufferId? StatusResult { get; set; } = BufferId.New();

        public BufferId? FindResult { get; set; } = BufferId.New();

        public BufferId OpenResult { get; } = BufferId.New();

        public bool WriteResult { get; set; } = true;

        public ConcurrentQueue<string> Calls { get; } = new();

        public ConcurrentQueue<NetworkSessionId?> StatusRequests { get; } = new();

        public ConcurrentQueue<(string Name, NetworkSessionId? NetworkId)> Finds { get; } = new();

        public ConcurrentQueue<(string ScriptId, string Name, NetworkSessionId? NetworkId)> Opens { get; } = new();

        public ConcurrentQueue<(string ScriptId, BufferId BufferId, ScriptWindowOutput Output)> Writes { get; } = new();

        public ScriptWindowHostServices Services { get; }
    }

    private sealed class TestScriptHost
    {
        private static InvalidOperationException WindowServicesNotConfigured() =>
            new("Window services were not configured for this test.");
        private readonly ConcurrentQueue<string> _output = new();
        private readonly SemaphoreSlim _outputAvailable = new(0);
        private readonly SemaphoreSlim _headerAvailable = new(0);

        public TestScriptHost()
        {
            Services = new ScriptHostServices(
                (_, text) =>
                {
                    _output.Enqueue(text);
                    _outputAvailable.Release();
                },
                registration =>
                {
                    if (!Commands.TryAdd(registration.Name, registration))
                    {
                        throw new InvalidOperationException("Test command collision.");
                    }

                    return new CallbackDisposable(() => Commands.TryRemove(registration.Name, out _));
                },
                (scriptId, context, commandLine, allowRaw) => CommandRequests.Enqueue((scriptId, context, commandLine, allowRaw)),
                (scriptId, contribution) =>
                {
                    Headers[(scriptId, contribution.Id)] = contribution;
                    _headerAvailable.Release();
                },
                (scriptId, itemId) => Headers.TryRemove((scriptId, itemId), out _),
                scriptId =>
                {
                    foreach (var key in Headers.Keys.Where(key =>
                        key.ScriptId.Equals(scriptId, StringComparison.OrdinalIgnoreCase)).ToArray())
                    {
                        Headers.TryRemove(key, out _);
                    }
                },
                (_, _) =>
                {
                    SecretPromptEntered.TrySetResult();
                    if (BlockSecretPrompt)
                    {
                        ReleaseSecretPrompt.Wait();
                    }
                    if (SecretDelay > TimeSpan.Zero)
                    {
                        Thread.Sleep(SecretDelay);
                    }
                    return SecretValue;
                },
                request =>
                {
                    PermissionRequests.Enqueue(request);
                    return PermissionApproval(request);
                },
                new ScriptWindowHostServices(
                    () => throw WindowServicesNotConfigured(),
                    _ => throw WindowServicesNotConfigured(),
                    (_, _) => throw WindowServicesNotConfigured(),
                    (_, _, _) => throw WindowServicesNotConfigured(),
                    (_, _, _) => throw WindowServicesNotConfigured()));
        }

        public ConcurrentDictionary<string, ScriptCommandRegistration> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ConcurrentQueue<(string ScriptId, CommandContext Context, string CommandLine, bool AllowRaw)> CommandRequests { get; } = new();

        public ConcurrentDictionary<(string ScriptId, string ItemId), ScriptHeaderContribution> Headers { get; } = new();

        public ScriptHostServices Services { get; }

        public ConcurrentQueue<ScriptPermissionRequest> PermissionRequests { get; } = new();

        public Func<ScriptPermissionRequest, bool> PermissionApproval { get; set; } = _ => true;

        public string? SecretValue { get; init; }

        public TimeSpan SecretDelay { get; init; }

        public bool BlockSecretPrompt { get; init; }

        public TaskCompletionSource SecretPromptEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim ReleaseSecretPrompt { get; } = new(initialState: false);

        public async Task<string> WaitForOutputAsync(Func<string, bool> predicate, TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (true)
            {
                while (_output.TryDequeue(out var text))
                {
                    if (predicate(text))
                    {
                        return text;
                    }
                }

                await _outputAvailable.WaitAsync(cancellation.Token);
            }
        }

        public async Task<ScriptHeaderContribution> WaitForHeaderAsync(
            Func<ScriptHeaderContribution, bool> predicate,
            TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (true)
            {
                var match = Headers.Values.FirstOrDefault(predicate);
                if (match is not null)
                {
                    return match;
                }
                await _headerAvailable.WaitAsync(cancellation.Token);
            }
        }
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        private Action? _callback = callback;

        public void Dispose() => Interlocked.Exchange(ref _callback, null)?.Invoke();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"clircs-script-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
