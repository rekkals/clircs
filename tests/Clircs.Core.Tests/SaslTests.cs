using System.Text;
using System.Threading.Channels;
using Clircs.Networking;
using Clircs.Sessions;

namespace Clircs.Core.Tests;

internal static class SaslTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("SASL PLAIN payloads use authzid authcid password and IRC fragmentation", PlainPayloadIsEncodedAndFragmented);
        suite.Add("SASL PLAIN completes CAP negotiation before IRC registration", PlainAuthenticationSucceedsAsync);
        suite.Add("SASL EXTERNAL presents a TLS client certificate and an empty authorization identity", ExternalAuthenticationSucceedsAsync);
        suite.Add("SASL EXTERNAL encodes an explicit authorization identity", ExternalAuthorizationIdentityIsEncoded);
        suite.Add("required SASL stops registration when the capability is unavailable", RequiredSaslFailureStopsRegistrationAsync);
        suite.Add("required SASL stops registration after rejected credentials", RejectedCredentialsStopRegistrationAsync);
        suite.Add("optional SASL continues unidentified when the capability is unavailable", OptionalSaslFailureContinuesAsync);
        suite.Add("reconnect repeats SASL authentication with the configured credentials", ReconnectRepeatsAuthenticationAsync);
    }

    private static void PlainPayloadIsEncodedAndFragmented()
    {
        var ordinary = SaslPlainPayload.Encode("account", "password");
        Assert.Equal(1, ordinary.Count);
        Assert.Equal("account\0account\0password", Encoding.UTF8.GetString(Convert.FromBase64String(ordinary[0])));
        Assert.False(new SaslAuthentication("account", "dont-print-this").ToString()
            .Contains("dont-print-this", StringComparison.Ordinal));

        var exact = Enumerable.Range(1, 1_000)
            .Select(length => new string('x', length))
            .Select(password => (Password: password, Chunks: SaslPlainPayload.Encode("a", password)))
            .First(candidate => candidate.Chunks.Count > 1 && candidate.Chunks[^1] == "+");
        Assert.Equal(400, exact.Chunks[0].Length);
        Assert.Equal("+", exact.Chunks[^1]);
    }

    private static async ValueTask PlainAuthenticationSucceedsAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new ScriptedTransport();
        await using var session = Session(transport, required: true);
        var events = new List<SessionEvent>();
        session.EventRaised += events.Add;
        var connecting = session.ConnectAsync(timeout.Token).AsTask();

        Assert.Equal("CAP LS 302", await transport.NextSentAsync(timeout.Token));
        Assert.Equal("NICK TestNick", await transport.NextSentAsync(timeout.Token));
        Assert.Equal("USER test 0 * :Test User", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server CAP * LS * :multi-prefix away-notify");
        transport.Receive(":server CAP * LS :sasl=EXTERNAL,PLAIN");
        Assert.Equal("CAP REQ :multi-prefix sasl", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server CAP TestNick ACK :multi-prefix sasl");
        Assert.Equal("AUTHENTICATE PLAIN", await transport.NextSentAsync(timeout.Token));
        transport.Receive("AUTHENTICATE +");
        var response = await transport.NextSentAsync(timeout.Token);
        Assert.True(response.StartsWith("AUTHENTICATE ", StringComparison.Ordinal));
        Assert.Equal(
            "account\0account\0password",
            Encoding.UTF8.GetString(Convert.FromBase64String(response["AUTHENTICATE ".Length..])));
        transport.Receive(":server 900 TestNick TestNick!test@localhost account :You are now logged in as account");
        transport.Receive(":server 903 TestNick :SASL authentication successful");
        Assert.Equal("CAP END", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server 001 TestNick :Welcome");
        await connecting;

        Assert.Equal(IrcConnectionState.Online, session.ConnectionState);
        Assert.Equal("account", session.State.AccountName!);
        Assert.Equal(1, events.Count(item => item.Fields?.GetValueOrDefault("event") == "sasl.success"));
        Assert.False(events.Any(item => item.Text.Contains("[903]", StringComparison.Ordinal)));
        await session.DisconnectAsync("done", timeout.Token);
        Assert.Equal("QUIT done", await transport.NextSentAsync(timeout.Token));
    }

    private static void ExternalAuthorizationIdentityIsEncoded()
    {
        Assert.Equal("+", SaslPayload.External(null)[0]);
        Assert.Equal("account", Encoding.UTF8.GetString(Convert.FromBase64String(SaslPayload.External("account")[0])));
    }

    private static async ValueTask ExternalAuthenticationSucceedsAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new ScriptedTransport();
        var factory = new CapturingTransportFactory(transport);
        var certificate = new TlsClientCertificate(@"C:\certificates\irc-client.pfx", "pfx-password");
        await using var session = new IrcNetworkSession(
            "test",
            new IrcConnectionOptions(
                new IrcEndpoint("irc.example.test", 6697, useTls: true),
                new IrcIdentity(["TestNick"], "test", "Test User"),
                Sasl: SaslAuthentication.External(certificate)),
            factory);
        var events = new List<SessionEvent>();
        session.EventRaised += events.Add;
        var connecting = session.ConnectAsync(timeout.Token).AsTask();

        Assert.Equal("CAP LS 302", await transport.NextSentAsync(timeout.Token));
        Assert.Equal("NICK TestNick", await transport.NextSentAsync(timeout.Token));
        Assert.Equal("USER test 0 * :Test User", await transport.NextSentAsync(timeout.Token));
        Assert.Equal(certificate, factory.Options!.ClientCertificate!);
        transport.Receive(":server CAP * LS :sasl=PLAIN,EXTERNAL");
        Assert.Equal("CAP REQ sasl", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server CAP TestNick ACK :sasl");
        Assert.Equal("AUTHENTICATE EXTERNAL", await transport.NextSentAsync(timeout.Token));
        transport.Receive("AUTHENTICATE +");
        Assert.Equal("AUTHENTICATE +", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server 903 TestNick :SASL authentication successful");
        Assert.Equal("CAP END", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server 001 TestNick :Welcome");
        await connecting;

        Assert.True(events.Any(item => item.Text.Contains("TLS client certificate", StringComparison.Ordinal)));
        await session.DisconnectAsync("done", timeout.Token);
        Assert.Equal("QUIT done", await transport.NextSentAsync(timeout.Token));
    }

    private static async ValueTask RequiredSaslFailureStopsRegistrationAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new ScriptedTransport();
        await using var session = Session(transport, required: true);
        var events = new List<SessionEvent>();
        var disconnected = new TaskCompletionSource<SessionDisconnectInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.EventRaised += events.Add;
        session.Disconnected += (_, info) => disconnected.TrySetResult(info);
        var connecting = session.ConnectAsync(timeout.Token).AsTask();
        _ = await transport.NextSentAsync(timeout.Token);
        _ = await transport.NextSentAsync(timeout.Token);
        _ = await transport.NextSentAsync(timeout.Token);
        transport.Receive(":server CAP * LS :multi-prefix away-notify");

        var failure = await Assert.ThrowsAsync<IrcSaslException>(() => connecting);
        var info = await disconnected.Task.WaitAsync(timeout.Token);
        Assert.True(failure.Message.Contains("does not advertise SASL", StringComparison.Ordinal));
        Assert.False(info.RetryRecommended);
        Assert.False(info.AnnounceToBuffers);
        Assert.Equal(1, events.Count(item => item.Fields?.GetValueOrDefault("event") == "sasl.failure"));
    }

    private static async ValueTask OptionalSaslFailureContinuesAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new ScriptedTransport();
        await using var session = Session(transport, required: false);
        var events = new List<SessionEvent>();
        session.EventRaised += events.Add;
        var connecting = session.ConnectAsync(timeout.Token).AsTask();
        _ = await transport.NextSentAsync(timeout.Token);
        _ = await transport.NextSentAsync(timeout.Token);
        _ = await transport.NextSentAsync(timeout.Token);
        transport.Receive(":server CAP * LS :multi-prefix");
        Assert.Equal("CAP REQ multi-prefix", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server CAP TestNick ACK :multi-prefix");
        Assert.Equal("CAP END", await transport.NextSentAsync(timeout.Token));
        transport.Receive(":server 001 TestNick :Welcome");
        await connecting;

        Assert.Equal(IrcConnectionState.Online, session.ConnectionState);
        Assert.True(events.Any(item => item.Text.Contains("continuing without authentication", StringComparison.Ordinal)));
        await session.DisconnectAsync("done", timeout.Token);
        Assert.Equal("QUIT done", await transport.NextSentAsync(timeout.Token));
    }

    private static async ValueTask RejectedCredentialsStopRegistrationAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new ScriptedTransport();
        await using var session = Session(transport, required: true);
        var events = new List<SessionEvent>();
        session.EventRaised += events.Add;
        var connecting = session.ConnectAsync(timeout.Token).AsTask();
        _ = await transport.NextSentAsync(timeout.Token);
        _ = await transport.NextSentAsync(timeout.Token);
        _ = await transport.NextSentAsync(timeout.Token);
        transport.Receive(":server CAP * LS :sasl=PLAIN");
        _ = await transport.NextSentAsync(timeout.Token);
        transport.Receive(":server CAP TestNick ACK :sasl");
        _ = await transport.NextSentAsync(timeout.Token);
        transport.Receive("AUTHENTICATE +");
        _ = await transport.NextSentAsync(timeout.Token);
        transport.Receive(":server 904 TestNick :Invalid account credentials");

        var failure = await Assert.ThrowsAsync<IrcSaslException>(() => connecting);
        Assert.True(failure.Message.Contains("Invalid account credentials", StringComparison.Ordinal));
        Assert.Equal(1, events.Count(item => item.Fields?.GetValueOrDefault("event") == "sasl.failure"));
        Assert.False(events.Any(item => item.Text.Contains("[904]", StringComparison.Ordinal)));
    }

    private static async ValueTask ReconnectRepeatsAuthenticationAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = new ScriptedTransport();
        var second = new ScriptedTransport();
        await using var session = Session(new QueuedTransportFactory(first, second), required: true);

        var initial = session.ConnectAsync(timeout.Token).AsTask();
        await CompleteSuccessfulExchangeAsync(first, initial, timeout.Token);

        var reconnecting = session.ReconnectAsync(cancellationToken: timeout.Token).AsTask();
        Assert.Equal("QUIT Reconnecting", await first.NextSentAsync(timeout.Token));
        await CompleteSuccessfulExchangeAsync(second, reconnecting, timeout.Token);

        Assert.Equal(IrcConnectionState.Online, session.ConnectionState);
        Assert.Equal("account", session.State.AccountName!);
        await session.DisconnectAsync("done", timeout.Token);
        Assert.Equal("QUIT done", await second.NextSentAsync(timeout.Token));
    }

    private static async Task CompleteSuccessfulExchangeAsync(
        ScriptedTransport transport,
        Task connecting,
        CancellationToken cancellationToken)
    {
        Assert.Equal("CAP LS 302", await transport.NextSentAsync(cancellationToken));
        Assert.Equal("NICK TestNick", await transport.NextSentAsync(cancellationToken));
        Assert.Equal("USER test 0 * :Test User", await transport.NextSentAsync(cancellationToken));
        transport.Receive(":server CAP * LS :sasl=PLAIN");
        Assert.Equal("CAP REQ sasl", await transport.NextSentAsync(cancellationToken));
        transport.Receive(":server CAP TestNick ACK :sasl");
        Assert.Equal("AUTHENTICATE PLAIN", await transport.NextSentAsync(cancellationToken));
        transport.Receive("AUTHENTICATE +");
        _ = await transport.NextSentAsync(cancellationToken);
        transport.Receive(":server 900 TestNick TestNick!test@localhost account :You are now logged in as account");
        transport.Receive(":server 903 TestNick :SASL authentication successful");
        Assert.Equal("CAP END", await transport.NextSentAsync(cancellationToken));
        transport.Receive(":server 001 TestNick :Welcome");
        await connecting;
    }

    private static IrcNetworkSession Session(ScriptedTransport transport, bool required) =>
        Session(new ScriptedTransportFactory(transport), required);

    private static IrcNetworkSession Session(IIrcTransportFactory factory, bool required) => new(
        "test",
        new IrcConnectionOptions(
            new IrcEndpoint("irc.example.test", 6697, useTls: true),
            new IrcIdentity(["TestNick"], "test", "Test User"),
            Sasl: new SaslAuthentication("account", "password", required)),
        factory);

    private sealed class ScriptedTransportFactory(ScriptedTransport transport) : IIrcTransportFactory
    {
        public ValueTask<IIrcTransport> ConnectAsync(IrcTransportOptions options, CancellationToken cancellationToken) =>
            new(transport);
    }

    private sealed class CapturingTransportFactory(ScriptedTransport transport) : IIrcTransportFactory
    {
        public IrcTransportOptions? Options { get; private set; }

        public ValueTask<IIrcTransport> ConnectAsync(IrcTransportOptions options, CancellationToken cancellationToken)
        {
            Options = options;
            return new(transport);
        }
    }

    private sealed class QueuedTransportFactory(params ScriptedTransport[] transports) : IIrcTransportFactory
    {
        private readonly Queue<ScriptedTransport> _transports = new(transports);

        public ValueTask<IIrcTransport> ConnectAsync(IrcTransportOptions options, CancellationToken cancellationToken) =>
            new(_transports.Dequeue());
    }

    private sealed class ScriptedTransport : IIrcTransport
    {
        private readonly Channel<byte[]> _received = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<string> _sent = Channel.CreateUnbounded<string>();

        public string RemoteDescription => "scripted TLS server";

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
