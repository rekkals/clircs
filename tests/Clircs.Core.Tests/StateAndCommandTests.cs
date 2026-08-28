using Clircs.Commands;
using Clircs.Identity;
using Clircs.Protocol;
using Clircs.Sessions;
using Clircs.State;

namespace Clircs.Core.Tests;

internal static class StateAndCommandTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("same channel on two networks remains isolated", SameChannelRemainsIsolated);
        suite.Add("one network applies its own case mapping", SessionUsesOwnCaseMapping);
        suite.Add("session safely reindexes buffers when CASEMAPPING changes", SessionReindexesCaseMapping);
        suite.Add("session IDs reject empty GUIDs", SessionIdRejectsEmpty);
        suite.Add("command parser supports aliases and quoted arguments", CommandParsing);
        suite.Add("command parser preserves quoted Windows paths", CommandParsingPreservesWindowsPaths);
        suite.Add("command parser accepts whitespace after command names", CommandParsingAcceptsTabs);
        suite.Add("double slash sends literal slash text", DoubleSlashIsChat);
        suite.Add("command registry resolves aliases to one handler", RegistryResolvesAlias);
        suite.Add("command registry rejects name collisions", RegistryRejectsCollisions);
        suite.Add("command definitions own their help metadata", CommandDefinitionsOwnHelpMetadata);
        suite.Add("command execution is serialized and retains its context", CommandExecutionIsSerialized);
        suite.Add("channel member modes honor the server batching limit", ChannelModesAreBatched);
    }

    private static void SameChannelRemainsIsolated()
    {
        var first = new NetworkSessionState(NetworkSessionId.New(), "Ratbox One", IrcCaseMapping.Rfc1459);
        var second = new NetworkSessionState(NetworkSessionId.New(), "Ratbox Two", IrcCaseMapping.Rfc1459);
        var firstBuffer = first.GetOrCreateBuffer(BufferKind.Channel, "#clirc");
        var secondBuffer = second.GetOrCreateBuffer(BufferKind.Channel, "#clirc");

        Assert.False(firstBuffer.Id == secondBuffer.Id);
        Assert.Equal(first.Id, firstBuffer.NetworkSessionId);
        Assert.Equal(second.Id, secondBuffer.NetworkSessionId);
    }

    private static void SessionUsesOwnCaseMapping()
    {
        var session = new NetworkSessionState(NetworkSessionId.New(), "Ratbox", IrcCaseMapping.Rfc1459);
        var created = session.GetOrCreateBuffer(BufferKind.Channel, "#[ops]");
        var found = session.TryGetBuffer("#{OPS}", out var resolved);
        Assert.True(found);
        Assert.Equal(created.Id, resolved!.Id);
    }

    private static void SessionReindexesCaseMapping()
    {
        var session = new NetworkSessionState(NetworkSessionId.New(), "Ratbox", IrcCaseMapping.Ascii);
        var created = session.GetOrCreateBuffer(BufferKind.Channel, "#[ops]");
        Assert.False(session.TryGetBuffer("#{ops}", out _));

        session.UpdateCaseMapping(IrcCaseMapping.Rfc1459);

        Assert.True(session.TryGetBuffer("#{ops}", out var resolved));
        Assert.Equal(created.Id, resolved!.Id);
    }

    private static void SessionIdRejectsEmpty() =>
        Assert.Throws<ArgumentException>(() => new NetworkSessionId(Guid.Empty));

    private static void CommandParsing()
    {
        var parsed = (CommandInput)CommandLineParser.Parse("/SERVER irc.example.test --tls \"My Network\"");
        Assert.Equal("server", parsed.Name);
        Assert.Equal(3, parsed.Arguments.Count);
        Assert.Equal("My Network", parsed.Arguments[2]);
        Assert.Equal("irc.example.test --tls \"My Network\"", parsed.RawArguments);
    }

    private static void ChannelModesAreBatched()
    {
        var batches = ChannelModeBatcher.Create('o', adding: true, ["One", "Two", "Three", "Four", "Two"], 3);

        Assert.Equal(2, batches.Count);
        Assert.Equal("+ooo", batches[0].ModeString);
        Assert.Equal(3, batches[0].Targets.Count);
        Assert.Equal("+o", batches[1].ModeString);
        Assert.Equal("Four", batches[1].Targets[0]);
    }

    private static void CommandParsingPreservesWindowsPaths()
    {
        var parsed = (CommandInput)CommandLineParser.Parse("/dcc send slak \"C:\\Users\\alice\\My File.txt\"");
        Assert.Equal("C:\\Users\\alice\\My File.txt", parsed.Arguments[2]);

        var escaped = (CommandInput)CommandLineParser.Parse("/say \"quoted \\\"text\\\" and \\\\ slash\"");
        Assert.Equal("quoted \"text\" and \\ slash", escaped.Arguments[0]);
    }

    private static void CommandParsingAcceptsTabs()
    {
        var parsed = (CommandInput)CommandLineParser.Parse("/join\t#clircs");
        Assert.Equal("join", parsed.Name);
        Assert.Equal("#clircs", parsed.Arguments[0]);
    }

    private static void DoubleSlashIsChat()
    {
        var parsed = (ChatInput)CommandLineParser.Parse("//join is text");
        Assert.Equal("/join is text", parsed.Text);
    }

    private static async ValueTask RegistryResolvesAlias()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDefinition(
            "join",
            ["j"],
            "/join <channel>",
            "Join a channel",
            (_, input, _) => ValueTask.FromResult(CommandResult.Success(input.Arguments[0]))));

        var input = (CommandInput)CommandLineParser.Parse("/j #clirc");
        var result = await registry.ExecuteAsync(new CommandContext(null, null), input);
        Assert.True(result.Succeeded);
        Assert.Equal("#clirc", result.Message!);
    }

    private static void RegistryRejectsCollisions()
    {
        static ValueTask<CommandResult> Handler(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
            ValueTask.FromResult(CommandResult.Success());

        var registry = new CommandRegistry();
        registry.Register(new CommandDefinition("join", ["j"], "/join <channel>", "Join", Handler));
        Assert.Throws<InvalidOperationException>(() =>
            registry.Register(new CommandDefinition("j", [], "/j", "Collision", Handler)));
    }

    private static void CommandDefinitionsOwnHelpMetadata()
    {
        static ValueTask<CommandResult> Handler(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
            ValueTask.FromResult(CommandResult.Success());

        var definition = new CommandDefinition(
            "internal",
            ["int"],
            "/internal <value>",
            "Exercise an internal command",
            Handler,
            visibleInHelp: false,
            [new PresentationField("Values", "one, two")]);

        Assert.Equal("/internal <value>", definition.Usage);
        Assert.Equal("Exercise an internal command", definition.Description);
        Assert.False(definition.VisibleInHelp);
        Assert.Equal("Values", definition.ExtendedHelp[0].Label);
    }

    private static async ValueTask CommandExecutionIsSerialized()
    {
        var registry = new CommandRegistry();
        var coordinator = new CommandExecutionCoordinator(registry);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = false;
        var firstContext = new CommandContext(NetworkSessionId.New(), BufferId.New());
        var secondContext = new CommandContext(NetworkSessionId.New(), BufferId.New());

        registry.Register(new CommandDefinition(
            "first", [], "/first", "First", async (context, _, _) =>
            {
                Assert.Equal(firstContext, coordinator.CurrentContext!);
                firstStarted.SetResult();
                await releaseFirst.Task;
                Assert.Equal(firstContext, coordinator.CurrentContext!);
                return CommandResult.Success();
            }));
        registry.Register(new CommandDefinition(
            "second", [], "/second", "Second", (context, _, _) =>
            {
                secondStarted = true;
                Assert.Equal(secondContext, coordinator.CurrentContext!);
                return ValueTask.FromResult(CommandResult.Success());
            }));

        var first = coordinator.ExecuteAsync(firstContext, (CommandInput)CommandLineParser.Parse("/first")).AsTask();
        await firstStarted.Task;
        var second = coordinator.ExecuteAsync(secondContext, (CommandInput)CommandLineParser.Parse("/second")).AsTask();
        await Task.Delay(20);
        Assert.False(secondStarted);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);
        Assert.True(secondStarted);
    }
}
