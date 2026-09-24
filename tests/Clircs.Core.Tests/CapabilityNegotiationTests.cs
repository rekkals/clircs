using System.Text;
using System.Threading.Channels;
using Clircs.Networking;
using Clircs.Sessions;

namespace Clircs.Core.Tests;

internal static class CapabilityNegotiationTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("CAP negotiation requests supported capabilities without SASL", SupportedCapabilitiesAreRequestedWithoutSaslAsync);
        suite.Add("CAP negotiation accepts a multiline capability list", MultilineCapabilityListIsCollectedAsync);
        suite.Add("CAP negotiation continues when multi-prefix is rejected", RejectedMultiPrefixIsNonfatalAsync);
        suite.Add("split CAP ACK applies capabilities only after the final line", SplitAckWaitsForCompleteReplyAsync);
        suite.Add("CAP DEL prevents a pending ACK from restoring withdrawn capabilities", DelCancelsPendingRequestAsync);
        suite.Add("CAP NEW waits for an outstanding capability request", NewWaitsForOutstandingRequestAsync);
        suite.Add("CAP NAK discards partial ACK and later stray ACK", NakDiscardsPartialAckAsync);
        suite.Add("CAP NAK listing one capability rejects the entire request", NakSubsetRejectsWholeRequestAsync);
        suite.Add("servers without CAP support complete ordinary registration", UnsupportedCapabilityNegotiationIsNonfatalAsync);
        suite.Add("CAP NEW can enable supported capabilities after registration", NewCapabilitiesAreRequestedAsync);
        suite.Add("server-time timestamps require capability acknowledgement", NegotiatedServerTimeControlsTimestampsAsync);
        suite.Add("message-tags does not enable server-time semantics", MessageTagsDoesNotEnableServerTimeAsync);
        suite.Add("unnegotiated tagged messages are ignored", UnnegotiatedTaggedMessagesAreIgnoredAsync);
        suite.Add("CAP DEL disables tag-bearing capability behavior", DeletedTagCapabilitiesAreDisabledAsync);
    }

    private static async ValueTask SupportedCapabilitiesAreRequestedWithoutSaslAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new ScriptedTransport();
        await using var session = Session(transport);
        var connecting = session.ConnectAsync(timeout.Token).AsTask();

        await AssertRegistrationStartAsync(transport, timeout.Token);
        transport.Receive(":server CAP * LS :away-notify echo-message message-tags multi-prefix server-time");
        Assert.Equal(
            "CAP REQ :multi-prefix echo-message message-tags server-time",
            await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server CAP TestNick ACK :multi-prefix echo-message message-tags server-time");
        Assert.Equal("CAP END", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server 001 TestNick :Welcome");
        await connecting;

        Assert.Equal(IrcConnectionState.Online, session.ConnectionState);
        await DisconnectAsync(session, transport, timeout.Token);
    }

    private static async ValueTask MultilineCapabilityListIsCollectedAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new ScriptedTransport();
        await using var session = Session(transport);
        var connecting = session.ConnectAsync(timeout.Token).AsTask();

        await AssertRegistrationStartAsync(transport, timeout.Token);
        transport.Receive(":server CAP * LS * :away-notify echo-message account-notify message-tags");
        transport.Receive(":server CAP * LS :multi-prefix server-time");
        Assert.Equal(
            "CAP REQ :multi-prefix echo-message message-tags server-time",
            await transport.NextSentAsync(timeout.Token));
        transport.Receive(
            ":server CAP TestNick ACK :multi-prefix echo-message message-tags server-time");
        Assert.Equal("CAP END", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server 001 TestNick :Welcome");
        await connecting;

        await DisconnectAsync(session, transport, timeout.Token);
    }

    private static async ValueTask NakDiscardsPartialAckAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new ScriptedTransport();
        await using var connection = new IrcClientConnection(new ScriptedTransportFactory(transport));
        var strayAckProcessed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        connection.MessageReceived += message =>
        {
            if (message.Command == "CAP" &&
                message.Parameters.Count >= 3 &&
                message.Parameters[1] == "ACK" &&
                message.Parameters[^1] == "multi-prefix echo-message")
            {
                strayAckProcessed.TrySetResult(true);
            }

            return ValueTask.CompletedTask;
        };

        await connection.ConnectAsync(
            new IrcConnectionOptions(
                new IrcEndpoint("irc.example.test", 6667, useTls: false),
                new IrcIdentity(["TestNick"], "test", "TestUser")),
            timeout.Token);
        await AssertRegistrationStartAsync(transport, timeout.Token);

        transport.Receive(":server CAP * LS :multi-prefix echo-message");
        Assert.Equal(
            "CAP REQ :multi-prefix echo-message",
            await transport.NextSentAsync(timeout.Token));

        transport.Receive(":server CAP TestNick ACK :multi-prefix");
        transport.Receive(":server CAP TestNick NAK :multi-prefix echo-message");
        Assert.Equal("CAP END", await transport.NextSentAsync(timeout.Token));

        Assert.False(connection.IsCapabilityEnabled("multi-prefix"));
        Assert.False(connection.IsCapabilityEnabled("echo-message"));

        transport.Receive(":server 001 TestNick :Welcome");
        transport.Receive(":server CAP TestNick ACK :multi-prefix echo-message");
        await strayAckProcessed.Task.WaitAsync(timeout.Token);

        Assert.False(connection.IsCapabilityEnabled("multi-prefix"));
        Assert.False(connection.IsCapabilityEnabled("echo-message"));
        Assert.False(transport.HasPendingSentLines);

        await connection.DisconnectAsync("done", timeout.Token);
        Assert.Equal("QUIT done", await transport.NextSentAsync(timeout.Token));
    }

    private static async ValueTask NakSubsetRejectsWholeRequestAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new ScriptedTransport();
        await using var connection = new IrcClientConnection(new ScriptedTransportFactory(transport));

        await connection.ConnectAsync(
            new IrcConnectionOptions(
                new IrcEndpoint("irc.example.test", 6667, useTls: false),
                new IrcIdentity(["TestNick"], "test", "TestUser")),
            timeout.Token);
        await AssertRegistrationStartAsync(transport, timeout.Token);

        transport.Receive(":server CAP * LS :multi-prefix echo-message");
        Assert.Equal(
            "CAP REQ :multi-prefix echo-message",
            await transport.NextSentAsync(timeout.Token));

        transport.Receive(":server CAP TestNick NAK :echo-message");
        Assert.Equal("CAP END", await transport.NextSentAsync(timeout.Token));

        Assert.False(connection.IsCapabilityEnabled("multi-prefix"));
        Assert.False(connection.IsCapabilityEnabled("echo-message"));

        await connection.DisconnectAsync("done", timeout.Token);
        Assert.Equal("QUIT done", await transport.NextSentAsync(timeout.Token));
    }

    private static async ValueTask SplitAckWaitsForCompleteReplyAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new ScriptedTransport();
        await using var connection = new IrcClientConnection(new ScriptedTransportFactory(transport));
        var firstAckProcessed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        connection.MessageReceived += message =>
        {
            if (message.Command == "CAP" &&
                message.Parameters.Count >= 3 &&
                message.Parameters[1] == "ACK" &&
                message.Parameters[^1] == "multi-prefix echo-message")
            {
                firstAckProcessed.TrySetResult(true);
            }

            return ValueTask.CompletedTask;
        };

        await connection.ConnectAsync(
            new IrcConnectionOptions(
                new IrcEndpoint("irc.example.test", 6667, useTls: false),
                new IrcIdentity(["TestNick"], "test", "TestUser")),
            timeout.Token);
        await AssertRegistrationStartAsync(transport, timeout.Token);

        transport.Receive(":server CAP * LS :multi-prefix echo-message message-tags server-time");
        Assert.Equal(
            "CAP REQ :multi-prefix echo-message message-tags server-time",
            await transport.NextSentAsync(timeout.Token));

        transport.Receive(":server CAP TestNick ACK :multi-prefix echo-message");
        await firstAckProcessed.Task.WaitAsync(timeout.Token);

        Assert.False(connection.IsCapabilityEnabled("multi-prefix"));
        Assert.False(connection.IsCapabilityEnabled("echo-message"));
        Assert.False(transport.HasPendingSentLines);

        transport.Receive(":server CAP TestNick ACK :message-tags server-time");
        Assert.Equal("CAP END", await transport.NextSentAsync(timeout.Token));

        Assert.True(connection.IsCapabilityEnabled("multi-prefix"));
        Assert.True(connection.IsCapabilityEnabled("echo-message"));
        Assert.True(connection.IsCapabilityEnabled("message-tags"));
        Assert.True(connection.IsCapabilityEnabled("server-time"));

        await connection.DisconnectAsync("done", timeout.Token);
        Assert.Equal("QUIT done", await transport.NextSentAsync(timeout.Token));
    }

    private static async ValueTask DelCancelsPendingRequestAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new ScriptedTransport();
        await using var connection = new IrcClientConnection(new ScriptedTransportFactory(transport));

        await connection.ConnectAsync(
            new IrcConnectionOptions(
                new IrcEndpoint("irc.example.test", 6667, useTls: false),
                new IrcIdentity(["TestNick"], "test", "TestUser")),
            timeout.Token);
        await AssertRegistrationStartAsync(transport, timeout.Token);

        transport.Receive(":server CAP * LS :multi-prefix echo-message");
        Assert.Equal(
            "CAP REQ :multi-prefix echo-message",
            await transport.NextSentAsync(timeout.Token));

        transport.Receive(":server CAP TestNick ACK :multi-prefix");
        transport.Receive(":server CAP TestNick DEL :multi-prefix");
        transport.Receive(":server CAP TestNick ACK :echo-message");
        Assert.Equal("CAP END", await transport.NextSentAsync(timeout.Token));

        Assert.False(connection.IsCapabilityEnabled("multi-prefix"));
        Assert.False(connection.IsCapabilityEnabled("echo-message"));

        transport.Receive(":server 001 TestNick :Welcome");
        transport.Receive(":server CAP TestNick NEW :multi-prefix");
        Assert.Equal("CAP REQ multi-prefix", await transport.NextSentAsync(timeout.Token));

        transport.Receive(":server CAP TestNick ACK :multi-prefix");
        transport.Receive("PING :probe");
        Assert.Equal("PONG probe", await transport.NextSentAsync(timeout.Token));

        Assert.True(connection.IsCapabilityEnabled("multi-prefix"));

        await connection.DisconnectAsync("done", timeout.Token);
        Assert.Equal("QUIT done", await transport.NextSentAsync(timeout.Token));
    }

    private static async ValueTask RejectedMultiPrefixIsNonfatalAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new ScriptedTransport();
        await using var session = Session(transport);
        var connecting = session.ConnectAsync(timeout.Token).AsTask();

        await AssertRegistrationStartAsync(transport, timeout.Token);
        transport.Receive(":server CAP * LS :multi-prefix");
        Assert.Equal("CAP REQ multi-prefix", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server CAP TestNick NAK :multi-prefix");
        Assert.Equal("CAP END", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server 001 TestNick :Welcome");
        await connecting;

        Assert.Equal(IrcConnectionState.Online, session.ConnectionState);
        await DisconnectAsync(session, transport, timeout.Token);
    }

    private static async ValueTask UnsupportedCapabilityNegotiationIsNonfatalAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new ScriptedTransport();
        await using var session = Session(transport);
        var events = new List<SessionEvent>();
        session.EventRaised += events.Add;
        var connecting = session.ConnectAsync(timeout.Token).AsTask();

        await AssertRegistrationStartAsync(transport, timeout.Token);
        transport.Receive(":server 421 TestNick CAP :Unknown command");
        transport.Receive(":server 001 TestNick :Welcome");
        await connecting;

        Assert.Equal(IrcConnectionState.Online, session.ConnectionState);
        Assert.False(events.Any(item => item.Text.Contains("[421]", StringComparison.Ordinal)));
        Assert.False(events.Any(item => item.Text.Contains("Unknown command", StringComparison.Ordinal)));
        await DisconnectAsync(session, transport, timeout.Token);
    }

    private static async ValueTask NewWaitsForOutstandingRequestAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new ScriptedTransport();
        await using var connection = new IrcClientConnection(new ScriptedTransportFactory(transport));
        var secondNewProcessed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        connection.MessageReceived += message =>
        {
            if (message.Command == "CAP" &&
                message.Parameters.Count >= 3 &&
                message.Parameters[1] == "NEW" &&
                message.Parameters[^1] == "message-tags server-time")
            {
                secondNewProcessed.TrySetResult(true);
            }

            return ValueTask.CompletedTask;
        };

        await connection.ConnectAsync(
            new IrcConnectionOptions(
                new IrcEndpoint("irc.example.test", 6667, useTls: false),
                new IrcIdentity(["TestNick"], "test", "TestUser")),
            timeout.Token);
        await AssertRegistrationStartAsync(transport, timeout.Token);

        transport.Receive(":server CAP * LS :away-notify");
        Assert.Equal("CAP END", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server 001 TestNick :Welcome");

        transport.Receive(":server CAP TestNick NEW :multi-prefix echo-message");
        Assert.Equal(
            "CAP REQ :multi-prefix echo-message",
            await transport.NextSentAsync(timeout.Token));

        transport.Receive(":server CAP TestNick NEW :message-tags server-time");
        await secondNewProcessed.Task.WaitAsync(timeout.Token);
        Assert.False(transport.HasPendingSentLines);

        transport.Receive(":server CAP TestNick ACK :multi-prefix echo-message");
        Assert.Equal(
            "CAP REQ :message-tags server-time",
            await transport.NextSentAsync(timeout.Token));

        Assert.True(connection.IsCapabilityEnabled("multi-prefix"));
        Assert.True(connection.IsCapabilityEnabled("echo-message"));
        Assert.False(connection.IsCapabilityEnabled("message-tags"));
        Assert.False(connection.IsCapabilityEnabled("server-time"));

        transport.Receive(":server CAP TestNick ACK :message-tags server-time");
        transport.Receive("PING :probe");
        Assert.Equal("PONG probe", await transport.NextSentAsync(timeout.Token));

        Assert.True(connection.IsCapabilityEnabled("message-tags"));
        Assert.True(connection.IsCapabilityEnabled("server-time"));

        await connection.DisconnectAsync("done", timeout.Token);
        Assert.Equal("QUIT done", await transport.NextSentAsync(timeout.Token));
    }

    private static async ValueTask NewCapabilitiesAreRequestedAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new ScriptedTransport();
        await using var session = Session(transport);
        var connecting = session.ConnectAsync(timeout.Token).AsTask();

        await AssertRegistrationStartAsync(transport, timeout.Token);
        transport.Receive(":server CAP * LS :away-notify");
        Assert.Equal("CAP END", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server 001 TestNick :Welcome");
        await connecting;

        transport.Receive(":server CAP TestNick NEW :away-notify echo-message message-tags multi-prefix server-time");
        Assert.Equal(
            "CAP REQ :multi-prefix echo-message message-tags server-time",
            await transport.NextSentAsync(timeout.Token));
        transport.Receive(
            ":server CAP TestNick ACK :multi-prefix echo-message message-tags server-time");
        await DisconnectAsync(session, transport, timeout.Token);
    }

    private static async ValueTask NegotiatedServerTimeControlsTimestampsAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new ScriptedTransport();
        await using var session = Session(transport);
        var connecting = session.ConnectAsync(timeout.Token).AsTask();

        await AssertRegistrationStartAsync(transport, timeout.Token);
        transport.Receive(":server CAP * LS :server-time");
        Assert.Equal(
            "CAP REQ server-time",
            await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server CAP TestNick ACK :server-time");
        Assert.Equal("CAP END", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server 001 TestNick :Welcome");
        await connecting;

        var received = NextMessageAsync(
            session,
            "historical",
            timeout.Token);
        transport.Receive(
            "@time=2020-01-02T03:04:05.678Z " +
            ":Other!user@example PRIVMSG TestNick :historical");

        var sessionEvent = await received;
        var expected = new DateTimeOffset(
            2020, 1, 2, 3, 4, 5, 678, TimeSpan.Zero);

        Assert.Equal(expected.ToLocalTime(), sessionEvent.Timestamp);
        Assert.True(sessionEvent.ReceivedAt > sessionEvent.Timestamp);

        await DisconnectAsync(session, transport, timeout.Token);
    }

    private static async ValueTask MessageTagsDoesNotEnableServerTimeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new ScriptedTransport();
        await using var session = Session(transport);
        var events = new List<SessionEvent>();
        session.EventRaised += events.Add;
        var connecting = session.ConnectAsync(timeout.Token).AsTask();

        await AssertRegistrationStartAsync(transport, timeout.Token);
        transport.Receive(":server CAP * LS :message-tags");
        Assert.Equal(
            "CAP REQ message-tags",
            await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server CAP TestNick ACK :message-tags");
        Assert.Equal("CAP END", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server 001 TestNick :Welcome");
        await connecting;

        transport.Receive(
            "@example=value :Other!user@example TAGMSG TestNick");

        var received = NextMessageAsync(
            session,
            "current",
            timeout.Token);
        transport.Receive(
            "@time=2020-01-02T03:04:05.678Z " +
            ":Other!user@example PRIVMSG TestNick :current");

        var sessionEvent = await received;

        Assert.Equal(sessionEvent.ReceivedAt, sessionEvent.Timestamp);
        Assert.False(events.Any(item =>
            item.Text.Contains("TAGMSG", StringComparison.OrdinalIgnoreCase)));

        await DisconnectAsync(session, transport, timeout.Token);
    }

    private static async ValueTask UnnegotiatedTaggedMessagesAreIgnoredAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new ScriptedTransport();
        await using var session = Session(transport);
        var events = new List<SessionEvent>();
        session.EventRaised += events.Add;
        var connecting = session.ConnectAsync(timeout.Token).AsTask();

        await AssertRegistrationStartAsync(transport, timeout.Token);
        transport.Receive(":server CAP * LS :message-tags server-time");
        Assert.Equal(
            "CAP REQ :message-tags server-time",
            await transport.NextSentAsync(timeout.Token));
        transport.Receive(
            ":server CAP TestNick NAK :message-tags server-time");
        Assert.Equal("CAP END", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server 001 TestNick :Welcome");
        await connecting;

        transport.Receive(
            "@time=2020-01-02T03:04:05.678Z " +
            ":Other!user@example PRIVMSG TestNick :ignored");

        var delivered = NextMessageAsync(
            session,
            "delivered",
            timeout.Token);
        transport.Receive(
            ":Other!user@example PRIVMSG TestNick :delivered");
        await delivered;

        Assert.False(events.Any(item =>
            item.Fields?.GetValueOrDefault("message") == "ignored"));
        Assert.True(events.Any(item =>
            item.Kind == SessionEventKind.Diagnostic &&
            item.Text.Contains(
                "no tag-bearing capability was negotiated",
                StringComparison.Ordinal)));

        await DisconnectAsync(session, transport, timeout.Token);
    }

    private static async ValueTask DeletedTagCapabilitiesAreDisabledAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new ScriptedTransport();
        await using var session = Session(transport);
        var events = new List<SessionEvent>();
        session.EventRaised += events.Add;
        var connecting = session.ConnectAsync(timeout.Token).AsTask();

        await AssertRegistrationStartAsync(transport, timeout.Token);
        transport.Receive(":server CAP * LS :message-tags server-time");
        Assert.Equal(
            "CAP REQ :message-tags server-time",
            await transport.NextSentAsync(timeout.Token));
        transport.Receive(
            ":server CAP TestNick ACK :message-tags server-time");
        Assert.Equal("CAP END", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server 001 TestNick :Welcome");
        await connecting;

        transport.Receive(
            ":server CAP TestNick DEL :message-tags server-time");
        transport.Receive(
            "@time=2020-01-02T03:04:05.678Z " +
            ":Other!user@example PRIVMSG TestNick :ignored-after-del");

        var delivered = NextMessageAsync(
            session,
            "delivered-after-del",
            timeout.Token);
        transport.Receive(
            ":Other!user@example PRIVMSG TestNick :delivered-after-del");
        await delivered;

        Assert.False(events.Any(item =>
            item.Fields?.GetValueOrDefault("message") ==
            "ignored-after-del"));

        await DisconnectAsync(session, transport, timeout.Token);
    }

    private static Task<SessionEvent> NextMessageAsync(
        IrcNetworkSession session,
        string text,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<SessionEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        session.EventRaised += sessionEvent =>
        {
            if (sessionEvent.Fields?.GetValueOrDefault("message") == text)
            {
                completion.TrySetResult(sessionEvent);
            }
        };

        return completion.Task.WaitAsync(cancellationToken);
    }

    private static async Task AssertRegistrationStartAsync(
        ScriptedTransport transport,
        CancellationToken cancellationToken)
    {
        Assert.Equal("CAP LS 302", await transport.NextSentAsync(cancellationToken));
        Assert.Equal("NICK TestNick", await transport.NextSentAsync(cancellationToken));
        Assert.Equal("USER test 0 * :TestUser", await transport.NextSentAsync(cancellationToken));
    }

    private static async Task DisconnectAsync(
        IrcNetworkSession session,
        ScriptedTransport transport,
        CancellationToken cancellationToken)
    {
        await session.DisconnectAsync("done", cancellationToken);
        Assert.Equal("QUIT done", await transport.NextSentAsync(cancellationToken));
    }

    private static IrcNetworkSession Session(ScriptedTransport transport) => new(
        "test",
        new IrcConnectionOptions(
            new IrcEndpoint("irc.example.test", 6667, useTls: false),
            new IrcIdentity(["TestNick"], "test", "TestUser")),
        new ScriptedTransportFactory(transport));

    private sealed class ScriptedTransportFactory(ScriptedTransport transport) : IIrcTransportFactory
    {
        public ValueTask<IIrcTransport> ConnectAsync(
            IrcTransportOptions options,
            CancellationToken cancellationToken) => new(transport);
    }

    private sealed class ScriptedTransport : IIrcTransport
    {
        private readonly Channel<byte[]> _received = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<string> _sent = Channel.CreateUnbounded<string>();

        public string RemoteDescription => "scripted server";

        public void Receive(string line) =>
            _received.Writer.TryWrite(Encoding.UTF8.GetBytes(line + "\r\n"));

        public ValueTask<string> NextSentAsync(CancellationToken cancellationToken) =>
            _sent.Reader.ReadAsync(cancellationToken);

        public bool HasPendingSentLines => _sent.Reader.TryPeek(out _);

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var bytes = await _received.Reader.ReadAsync(cancellationToken);
            bytes.CopyTo(buffer);
            return bytes.Length;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            var line = Encoding.UTF8.GetString(bytes.Span).TrimEnd('\r', '\n');
            return _sent.Writer.WriteAsync(line, cancellationToken);
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            _received.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
