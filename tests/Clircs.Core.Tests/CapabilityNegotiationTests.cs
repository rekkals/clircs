using System.Text;
using System.Threading.Channels;
using Clircs.Networking;
using Clircs.Sessions;

namespace Clircs.Core.Tests;

internal static class CapabilityNegotiationTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("CAP negotiation requests multi-prefix without SASL", MultiPrefixIsRequestedWithoutSaslAsync);
        suite.Add("CAP negotiation accepts a multiline capability list", MultilineCapabilityListIsCollectedAsync);
        suite.Add("CAP negotiation continues when multi-prefix is rejected", RejectedMultiPrefixIsNonfatalAsync);
        suite.Add("servers without CAP support complete ordinary registration", UnsupportedCapabilityNegotiationIsNonfatalAsync);
        suite.Add("CAP NEW can enable multi-prefix after registration", NewMultiPrefixIsRequestedAsync);
    }

    private static async ValueTask MultiPrefixIsRequestedWithoutSaslAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new ScriptedTransport();
        await using var session = Session(transport);
        var connecting = session.ConnectAsync(timeout.Token).AsTask();

        await AssertRegistrationStartAsync(transport, timeout.Token);
        transport.Receive(":server CAP * LS :away-notify multi-prefix");
        Assert.Equal("CAP REQ multi-prefix", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server CAP TestNick ACK :multi-prefix");
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
        transport.Receive(":server CAP * LS * :away-notify account-notify");
        transport.Receive(":server CAP * LS :multi-prefix");
        Assert.Equal("CAP REQ multi-prefix", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server CAP TestNick ACK :multi-prefix");
        Assert.Equal("CAP END", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server 001 TestNick :Welcome");
        await connecting;

        await DisconnectAsync(session, transport, timeout.Token);
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

    private static async ValueTask NewMultiPrefixIsRequestedAsync()
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

        transport.Receive(":server CAP TestNick NEW :multi-prefix");
        Assert.Equal("CAP REQ multi-prefix", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server CAP TestNick ACK :multi-prefix");
        await DisconnectAsync(session, transport, timeout.Token);
    }

    private static async Task AssertRegistrationStartAsync(
        ScriptedTransport transport,
        CancellationToken cancellationToken)
    {
        Assert.Equal("CAP LS 302", await transport.NextSentAsync(cancellationToken));
        Assert.Equal("NICK TestNick", await transport.NextSentAsync(cancellationToken));
        Assert.Equal("USER test 0 * :Test User", await transport.NextSentAsync(cancellationToken));
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
            new IrcIdentity(["TestNick"], "test", "Test User")),
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
