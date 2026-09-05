using System.ComponentModel;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Clircs.Networking;
using Clircs.Protocol;
using Clircs.Sessions;
using Clircs.Transport;

namespace Clircs.Core.Tests;

internal static class NetworkingIntegrationTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("TCP connection registers, falls back nick, answers PING, and quits", RegistrationPingAndQuitAsync);
        suite.Add("registration pauses for a user nickname after configured names fail", NicknameFallbackExhaustionAsync);
        suite.Add("two live sessions remain isolated through identical code paths", TwoLiveSessionsRemainIsolatedAsync);
        suite.Add("confirmed self joins automatically query channel modes", SelfJoinQueriesModesAsync);
        suite.Add("failed joins never become reconnect restoration targets", FailedJoinIsNotRememberedAsync);
        suite.Add("one session reconnects without losing its channel buffer", SessionReconnectPreservesBuffersAsync);
        suite.Add("authentication rejection ends registration without retry recommendation", AuthenticationRejectionStopsRegistrationAsync);
        suite.Add("cancelled registration does not render a generic connection-lost error", CancelledRegistrationIsQuietAsync);
        suite.Add("reconnect waits for an in-progress disconnect", ReconnectWaitsForDisconnectAsync);
        suite.Add("disconnect cancels an in-progress DNS or transport connection", DisconnectCancelsInProgressConnectAsync);
        suite.Add("oversized incoming lines are discarded without disconnecting", OversizedIncomingLineDoesNotDisconnectAsync);
        suite.Add("excess incoming parameters are accepted with one diagnostic", ExcessIncomingParametersProduceOneDiagnosticAsync);
        suite.Add("raw IRC observers receive exact inbound and outbound wire lines", RawWireLinesAreObservableAsync);
        suite.Add("self-signed TLS is accepted only through an explicit certificate policy", SelfSignedTlsUsesPolicyAsync);
        suite.Add("self-signed TLS is rejected when no policy is provided", SelfSignedTlsRejectsByDefaultAsync);
    }

    private static async ValueTask DisconnectCancelsInProgressConnectAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var factory = new CancellableConnectFactory();
        await using var connection = new IrcClientConnection(factory);
        var options = new IrcConnectionOptions(
            new IrcEndpoint("unresolvable.example", 6667, useTls: false),
            new IrcIdentity(["TestNick"], "test", "Test User"));

        var connecting = connection.ConnectAsync(options, timeout.Token).AsTask();
        await factory.Started.Task.WaitAsync(timeout.Token);
        await connection.DisconnectAsync("superseded", timeout.Token).AsTask().WaitAsync(timeout.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(() => connecting);
        Assert.True(factory.Canceled);
    }

    private static async ValueTask FailedJoinIsNotRememberedAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n"
            };
            _ = await CompleteEmptyCapabilityNegotiationAsync(reader, writer, "TestNick", timeout.Token);
            await writer.WriteLineAsync(":server 001 TestNick :Welcome".AsMemory(), timeout.Token);
            Assert.Equal("JOIN #invite-only key", (await reader.ReadLineAsync(timeout.Token))!);
            await writer.WriteLineAsync(":server 473 TestNick #invite-only :Cannot join channel (+i)".AsMemory(), timeout.Token);
            return await reader.ReadLineAsync(timeout.Token);
        }, timeout.Token);

        var options = new IrcConnectionOptions(
            new IrcEndpoint("127.0.0.1", port, useTls: false),
            new IrcIdentity(["TestNick"], "test", "Test User"));
        await using var session = new IrcNetworkSession("test", options, new TcpIrcTransportFactory());
        await session.ConnectAsync(timeout.Token);
        session.PrepareJoin("#invite-only", "key");
        await session.SendAsync("JOIN", ["#invite-only", "key"], cancellationToken: timeout.Token);
        await Task.Delay(50, timeout.Token);

        Assert.False(session.ChannelsToRestore.ContainsKey("#invite-only"));
        await session.DisconnectAsync("done", timeout.Token);
        Assert.Equal("QUIT done", (await serverTask)!);
        listener.Stop();
    }

    private static async ValueTask RawWireLinesAreObservableAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n"
            };
            _ = await CompleteEmptyCapabilityNegotiationAsync(reader, writer, "TestNick", timeout.Token);
            await writer.WriteLineAsync(":server 001 TestNick :Welcome".AsMemory(), timeout.Token);
            await writer.WriteLineAsync(":server 005 TestNick MONITOR=100 :supported".AsMemory(), timeout.Token);
            await writer.WriteLineAsync(":server 730 TestNick :Alice!user@example".AsMemory(), timeout.Token);
            await writer.WriteLineAsync("@label=wire-test :alice!user@example NOTICE TestNick :raw test".AsMemory(), timeout.Token);
            var sent = await reader.ReadLineAsync(timeout.Token);
            var quit = await reader.ReadLineAsync(timeout.Token);
            return (sent, quit);
        }, timeout.Token);

        var options = new IrcConnectionOptions(
            new IrcEndpoint("127.0.0.1", port, useTls: false),
            new IrcIdentity(["TestNick"], "test", "Test User"));
        await using var session = new IrcNetworkSession("test", options, new TcpIrcTransportFactory());
        var wireLines = new List<IrcWireLine>();
        var monitorOnline = new List<string>();
        session.WireLineTransferred += (_, line) => wireLines.Add(line);
        session.MonitorStatusReceived += (_, online, nicknames) =>
        {
            if (online) monitorOnline.AddRange(nicknames);
        };

        await session.ConnectAsync(timeout.Token);
        await session.SendAsync("PRIVMSG", ["alice", "\u0001PING 123\u0001"], cancellationToken: timeout.Token);
        await session.DisconnectAsync("done", timeout.Token);
        var serverLines = await serverTask;
        listener.Stop();

        Assert.True(wireLines.Any(line => line.Direction == IrcWireDirection.Sent && line.Line == "NICK TestNick"));
        Assert.True(wireLines.Any(line => line.Direction == IrcWireDirection.Sent && line.Line == "USER test 0 * :Test User"));
        Assert.True(wireLines.Any(line => line.Direction == IrcWireDirection.Received &&
            line.Line == ":server 001 TestNick :Welcome"));
        Assert.True(wireLines.Any(line => line.Direction == IrcWireDirection.Received &&
            line.Line == "@label=wire-test :alice!user@example NOTICE TestNick :raw test"));
        Assert.True(monitorOnline.SequenceEqual(new[] { "Alice" }));
        Assert.Equal("PRIVMSG alice :\u0001PING 123\u0001", serverLines.sent!);
        Assert.Equal("QUIT done", serverLines.quit!);
    }

    private static async ValueTask SessionReconnectPreservesBuffersAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using (var first = await listener.AcceptTcpClientAsync(timeout.Token))
            {
                await using var stream = first.GetStream();
                using var reader = new StreamReader(stream, new UTF8Encoding(false), false, leaveOpen: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\r\n"
                };
                _ = await CompleteEmptyCapabilityNegotiationAsync(reader, writer, "TestNick", timeout.Token);
                await writer.WriteLineAsync(":server 001 TestNick :Welcome".AsMemory(), timeout.Token);
                await writer.WriteLineAsync(":TestNick!user@host JOIN #kept".AsMemory(), timeout.Token);
                await writer.WriteLineAsync(":server 376 TestNick :End of MOTD".AsMemory(), timeout.Token);
                await Task.Delay(100, timeout.Token);
            }

            using var second = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var secondStream = second.GetStream();
            using var secondReader = new StreamReader(secondStream, new UTF8Encoding(false), false, leaveOpen: true);
            await using var secondWriter = new StreamWriter(secondStream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n"
            };
            _ = await CompleteEmptyCapabilityNegotiationAsync(secondReader, secondWriter, "TestNick", timeout.Token);
            await secondWriter.WriteLineAsync(":server 001 TestNick :Welcome back".AsMemory(), timeout.Token);
            await secondWriter.WriteLineAsync(":server 376 TestNick :End of MOTD".AsMemory(), timeout.Token);
            return await secondReader.ReadLineAsync(timeout.Token);
        }, timeout.Token);

        var options = new IrcConnectionOptions(
            new IrcEndpoint("127.0.0.1", port, useTls: false),
            new IrcIdentity(["TestNick"], "test", "Test User"));
        await using var session = new IrcNetworkSession("test", options, new TcpIrcTransportFactory());
        var disconnected = new TaskCompletionSource<SessionDisconnectInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Disconnected += (_, info) => disconnected.TrySetResult(info);
        await session.ConnectAsync(timeout.Token);
        while (!session.State.TryGetBuffer("#kept", out _))
        {
            await Task.Delay(10, timeout.Token);
        }
        session.State.TryGetBuffer("#kept", out var originalBuffer);
        var info = await disconnected.Task.WaitAsync(timeout.Token);
        Assert.Equal(SessionDisconnectKind.Accidental, info.Kind);
        Assert.True(info.AnnounceToBuffers);
        Assert.True(session.ChannelsToRestore.ContainsKey("#kept"));

        await session.ReconnectAsync(cancellationToken: timeout.Token);
        Assert.True(session.State.TryGetBuffer("#kept", out var preservedBuffer));
        Assert.Equal(originalBuffer!.Id, preservedBuffer!.Id);
        await session.DisconnectAsync("done", timeout.Token);
        Assert.Equal("QUIT done", (await serverTask)!);
        listener.Stop();
    }

    private static async ValueTask AuthenticationRejectionStopsRegistrationAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n"
            };
            Assert.Equal("PASS wrong", (await reader.ReadLineAsync(timeout.Token))!);
            _ = await CompleteEmptyCapabilityNegotiationAsync(reader, writer, "TestNick", timeout.Token);
            await writer.WriteLineAsync(":server 464 TestNick :Password incorrect".AsMemory(), timeout.Token);
            return await reader.ReadLineAsync(timeout.Token);
        }, timeout.Token);

        var options = new IrcConnectionOptions(
            new IrcEndpoint("127.0.0.1", port, useTls: false),
            new IrcIdentity(["TestNick"], "test", "Test User"),
            "wrong");
        await using var session = new IrcNetworkSession("test", options, new TcpIrcTransportFactory());
        var disconnected = new TaskCompletionSource<SessionDisconnectInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rendered = new List<SessionEvent>();
        session.EventRaised += rendered.Add;
        session.Disconnected += (_, info) => disconnected.TrySetResult(info);

        var failure = await Assert.ThrowsAsync<IrcProtocolException>(() => session.ConnectAsync(timeout.Token).AsTask());
        Assert.True(failure.Message.Contains("Authentication failed", StringComparison.Ordinal));
        var info = await disconnected.Task.WaitAsync(timeout.Token);
        Assert.False(info.RetryRecommended);
        Assert.False(info.AnnounceToBuffers);
        Assert.True(info.Message.Contains("Authentication failed", StringComparison.Ordinal));
        Assert.Equal(1, rendered.Count(item => item.Text.Contains("Authentication failed", StringComparison.Ordinal)));
        Assert.False(rendered.Any(item => item.Text.StartsWith("Connection lost:", StringComparison.Ordinal)));
        Assert.True(await serverTask is null);
        listener.Stop();
    }

    private static async ValueTask CancelledRegistrationIsQuietAsync()
    {
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(testTimeout.Token);
            accepted.TrySetResult();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, leaveOpen: true);
            while (await reader.ReadLineAsync(testTimeout.Token) is not null)
            {
            }
        }, testTimeout.Token);

        var options = new IrcConnectionOptions(
            new IrcEndpoint("127.0.0.1", port, useTls: false),
            new IrcIdentity(["TestNick"], "test", "Test User"));
        await using var session = new IrcNetworkSession("test", options, new TcpIrcTransportFactory());
        var rendered = new List<SessionEvent>();
        session.EventRaised += rendered.Add;
        using var attemptTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var connecting = session.ConnectAsync(attemptTimeout.Token).AsTask();
        await accepted.Task.WaitAsync(testTimeout.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(() => connecting);
        Assert.False(rendered.Any(item =>
            item.Text.StartsWith("Connection lost:", StringComparison.Ordinal)));

        await serverTask.WaitAsync(testTimeout.Token);
        listener.Stop();
    }

    private static async ValueTask ReconnectWaitsForDisconnectAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var firstAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            using (var first = await listener.AcceptTcpClientAsync(timeout.Token))
            {
                await using var stream = first.GetStream();
                using var reader = new StreamReader(stream, new UTF8Encoding(false), false, leaveOpen: true);
                Assert.Equal("CAP LS 302", (await reader.ReadLineAsync(timeout.Token))!);
                Assert.Equal("NICK TestNick", (await reader.ReadLineAsync(timeout.Token))!);
                Assert.Equal("USER test 0 * :Test User", (await reader.ReadLineAsync(timeout.Token))!);
                firstAccepted.TrySetResult();
                while (await reader.ReadLineAsync(timeout.Token) is not null)
                {
                }
            }

            using var second = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var secondStream = second.GetStream();
            using var secondReader = new StreamReader(secondStream, new UTF8Encoding(false), false, leaveOpen: true);
            await using var secondWriter = new StreamWriter(secondStream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n"
            };
            _ = await CompleteEmptyCapabilityNegotiationAsync(secondReader, secondWriter, "TestNick", timeout.Token);
            await secondWriter.WriteLineAsync(":server 001 TestNick :Welcome back".AsMemory(), timeout.Token);
            return await secondReader.ReadLineAsync(timeout.Token);
        }, timeout.Token);

        var options = new IrcConnectionOptions(
            new IrcEndpoint("127.0.0.1", port, useTls: false),
            new IrcIdentity(["TestNick"], "test", "Test User"));
        await using var session = new IrcNetworkSession("test", options, new TcpIrcTransportFactory());
        var initialConnect = session.ConnectAsync(timeout.Token).AsTask();
        await firstAccepted.Task.WaitAsync(timeout.Token);
        var disconnect = session.DisconnectAsync("first attempt cancelled", timeout.Token).AsTask();
        var reconnect = session.ReconnectAsync(cancellationToken: timeout.Token).AsTask();
        await Assert.ThrowsAsync<IOException>(() => initialConnect);
        await Task.WhenAll(disconnect, reconnect);
        Assert.Equal(IrcConnectionState.Online, session.ConnectionState);
        await session.DisconnectAsync("done", timeout.Token);
        Assert.Equal("QUIT done", (await serverTask)!);
        listener.Stop();
    }

    private static async ValueTask TwoLiveSessionsRemainIsolatedAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var firstListener = new TcpListener(IPAddress.Loopback, 0);
        var secondListener = new TcpListener(IPAddress.Loopback, 0);
        firstListener.Start();
        secondListener.Start();

        var firstPort = ((IPEndPoint)firstListener.LocalEndpoint).Port;
        var secondPort = ((IPEndPoint)secondListener.LocalEndpoint).Port;
        var firstServer = RunIsolatedServerAsync(firstListener, "SharedNick", "first", timeout.Token);
        var secondServer = RunIsolatedServerAsync(secondListener, "SharedNick", "second", timeout.Token);

        var identity = new IrcIdentity(["SharedNick"], "test", "Test User");
        await using var first = new IrcNetworkSession(
            "first",
            new IrcConnectionOptions(new IrcEndpoint("127.0.0.1", firstPort, useTls: false), identity),
            new TcpIrcTransportFactory());
        await using var second = new IrcNetworkSession(
            "second",
            new IrcConnectionOptions(new IrcEndpoint("127.0.0.1", secondPort, useTls: false), identity),
            new TcpIrcTransportFactory());

        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.EventRaised += sessionEvent =>
        {
            if (sessionEvent.Text == "first ready")
            {
                firstReady.TrySetResult();
            }
        };
        second.EventRaised += sessionEvent =>
        {
            if (sessionEvent.Text == "second ready")
            {
                secondReady.TrySetResult();
            }
        };

        await Task.WhenAll(
            first.ConnectAsync(timeout.Token).AsTask(),
            second.ConnectAsync(timeout.Token).AsTask());
        await Task.WhenAll(
            firstReady.Task.WaitAsync(timeout.Token),
            secondReady.Task.WaitAsync(timeout.Token));

        await first.SendMessageAsync("#shared", "from first", timeout.Token);
        await second.SendMessageAsync("#shared", "from second", timeout.Token);
        Assert.True(first.State.TryGetBuffer("#shared", out var firstBuffer));
        Assert.True(second.State.TryGetBuffer("#shared", out var secondBuffer));
        Assert.False(first.State.Id == second.State.Id);
        Assert.False(firstBuffer!.Id == secondBuffer!.Id);

        await Task.WhenAll(
            first.DisconnectAsync("first done", timeout.Token).AsTask(),
            second.DisconnectAsync("second done", timeout.Token).AsTask());
        var transcripts = await Task.WhenAll(firstServer, secondServer);
        firstListener.Stop();
        secondListener.Stop();

        Assert.Equal("PRIVMSG #shared :from first", transcripts[0][2]);
        Assert.Equal("QUIT :first done", transcripts[0][3]);
        Assert.Equal("PRIVMSG #shared :from second", transcripts[1][2]);
        Assert.Equal("QUIT :second done", transcripts[1][3]);
    }

    private static async Task<string[]> RunIsolatedServerAsync(
        TcpListener listener,
        string nickname,
        string marker,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\r\n"
        };

        var registration = await CompleteEmptyCapabilityNegotiationAsync(reader, writer, nickname, cancellationToken);
        var transcript = new List<string> { registration.Nick, registration.User };
        await writer.WriteLineAsync($":server 001 {nickname} :{marker} ready".AsMemory(), cancellationToken);
        transcript.Add((await reader.ReadLineAsync(cancellationToken))!);
        transcript.Add((await reader.ReadLineAsync(cancellationToken))!);
        return transcript.ToArray();
    }

    private static async ValueTask RegistrationPingAndQuitAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = RunServerAsync(listener, timeout.Token);
        var options = new IrcConnectionOptions(
            new IrcEndpoint("127.0.0.1", port, useTls: false),
            new IrcIdentity(["TestNick", "TestNick_"], "test", "Test User"));
        await using var session = new IrcNetworkSession("test", options, new TcpIrcTransportFactory(), () => "Test quote");
        var receivedWelcome = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedEchoMarker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var localEchoCount = 0;
        var registrationCount = 0;
        var fallbackMessages = new List<string>();
        session.RegistrationCompleted += _ => registrationCount++;
        session.EventRaised += sessionEvent =>
        {
            if (sessionEvent.Fields?.GetValueOrDefault("event") == "nicknameFallback")
            {
                fallbackMessages.Add(sessionEvent.Text);
            }
            if (sessionEvent.Text.Contains("Welcome to the test network", StringComparison.Ordinal))
            {
                receivedWelcome.TrySetResult();
            }

            if (sessionEvent.Kind == SessionEventKind.Message && sessionEvent.Text == "<TestNick_> hello")
            {
                localEchoCount++;
            }

            if (sessionEvent.Text.Contains("echo complete", StringComparison.Ordinal))
            {
                receivedEchoMarker.TrySetResult();
            }
        };

        await session.ConnectAsync(timeout.Token);
        await receivedWelcome.Task.WaitAsync(timeout.Token);
        Assert.Equal(IrcConnectionState.Online, session.ConnectionState);
        Assert.Equal("TestNick_", session.CurrentNickname);
        Assert.Equal(1, registrationCount);
        Assert.Equal(1, fallbackMessages.Count);
        Assert.Equal("Nickname TestNick is in use; trying alternate TestNick_.", fallbackMessages[0]);

        await session.SendMessageAsync("#test", "hello", timeout.Token);
        await receivedEchoMarker.Task.WaitAsync(timeout.Token);
        Assert.Equal(1, localEchoCount);

        await session.SendMessageAsync("NickServ", "help", timeout.Token, createQueryBuffer: false);
        Assert.False(session.State.TryGetBuffer("NickServ", out _));

        await session.DisconnectAsync("test complete", timeout.Token);
        var transcript = await serverTask;
        listener.Stop();

        Assert.Equal("NICK TestNick", transcript[0]);
        Assert.Equal("USER test 0 * :Test User", transcript[1]);
        Assert.Equal("NICK TestNick_", transcript[2]);
        Assert.Equal("PONG cookie", transcript[3]);
        Assert.Equal($"NOTICE alice :\u0001VERSION {ProductInfo.DisplayName}\u0001", transcript[4]);
        Assert.Equal("PRIVMSG #test hello", transcript[5]);
        Assert.Equal("PRIVMSG NickServ help", transcript[6]);
        Assert.Equal("QUIT :test complete", transcript[7]);
    }

    private static async ValueTask NicknameFallbackExhaustionAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n"
            };

            var registration = await CompleteEmptyCapabilityNegotiationAsync(reader, writer, "Primary", timeout.Token);
            Assert.Equal("NICK Primary", registration.Nick);
            await writer.WriteLineAsync(":server 433 * Primary :Nickname is already in use".AsMemory(), timeout.Token);
            Assert.Equal("NICK Alternate", (await reader.ReadLineAsync(timeout.Token))!);
            await writer.WriteLineAsync(":server 433 * Alternate :Nickname is already in use".AsMemory(), timeout.Token);
            Assert.Equal("NICK Chosen", (await reader.ReadLineAsync(timeout.Token))!);
            await writer.WriteLineAsync(":server 001 Chosen :Welcome".AsMemory(), timeout.Token);
            return (await reader.ReadLineAsync(timeout.Token))!;
        }, timeout.Token);

        var options = new IrcConnectionOptions(
            new IrcEndpoint("127.0.0.1", port, useTls: false),
            new IrcIdentity(["Primary", "Alternate"], "test", "Test User"));
        await using var session = new IrcNetworkSession("test", options, new TcpIrcTransportFactory());
        var messages = new List<SessionEvent>();
        var nicknameRequired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.EventRaised += sessionEvent =>
        {
            if (sessionEvent.Fields?.GetValueOrDefault("event") is "nicknameFallback" or "nicknameRequired")
            {
                messages.Add(sessionEvent);
            }
            if (sessionEvent.Fields?.GetValueOrDefault("event") == "nicknameRequired")
            {
                nicknameRequired.TrySetResult();
            }
        };

        var connecting = session.ConnectAsync(timeout.Token).AsTask();
        await nicknameRequired.Task.WaitAsync(timeout.Token);
        Assert.Equal(2, messages.Count);
        Assert.Equal("Nickname Primary is in use; trying alternate Alternate.", messages[0].Text);
        Assert.Equal("Nickname Alternate is also unavailable. Choose a different one.", messages[1].Text);
        Assert.Equal("/nick ", messages[1].Fields!["prefill"]!);
        Assert.False(messages.Any(item => item.Text.Contains("already in use:", StringComparison.Ordinal)));

        await session.SendNicknameAsync("Chosen", timeout.Token);
        await connecting;
        Assert.Equal("Chosen", session.CurrentNickname);
        await session.DisconnectAsync("done", timeout.Token);
        Assert.Equal("QUIT done", await serverTask);
        listener.Stop();
    }

    private static async ValueTask SelfJoinQueriesModesAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n"
            };
            _ = await CompleteEmptyCapabilityNegotiationAsync(reader, writer, "TestNick", timeout.Token);
            await writer.WriteLineAsync(":server 001 TestNick :Welcome".AsMemory(), timeout.Token);
            await writer.WriteLineAsync(":TestNick!user@host JOIN #clirc".AsMemory(), timeout.Token);
            return await reader.ReadLineAsync(timeout.Token);
        }, timeout.Token);

        var options = new IrcConnectionOptions(
            new IrcEndpoint("127.0.0.1", port, useTls: false),
            new IrcIdentity(["TestNick"], "test", "Test User"));
        await using var session = new IrcNetworkSession("test", options, new TcpIrcTransportFactory());
        await session.ConnectAsync(timeout.Token);
        var modeQuery = await serverTask;
        listener.Stop();

        Assert.Equal("MODE #clirc", modeQuery!);
    }

    private static async Task<string[]> RunServerAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\r\n"
        };

        var registration = await CompleteEmptyCapabilityNegotiationAsync(reader, writer, "TestNick", cancellationToken);
        var transcript = new List<string> { registration.Nick, registration.User };

        await writer.WriteLineAsync(":server 433 * TestNick :Nickname is already in use".AsMemory(), cancellationToken);
        transcript.Add((await reader.ReadLineAsync(cancellationToken))!);
        await writer.WriteLineAsync("PING :cookie".AsMemory(), cancellationToken);
        transcript.Add((await reader.ReadLineAsync(cancellationToken))!);
        await writer.WriteLineAsync(":alice!u@h PRIVMSG TestNick_ :\u0001VERSION\u0001".AsMemory(), cancellationToken);
        transcript.Add((await reader.ReadLineAsync(cancellationToken))!);
        await writer.WriteLineAsync(":server 001 TestNick_ :Welcome to the test network".AsMemory(), cancellationToken);
        transcript.Add((await reader.ReadLineAsync(cancellationToken))!);
        await writer.WriteLineAsync(":TestNick_!test@localhost PRIVMSG #test :hello".AsMemory(), cancellationToken);
        await writer.WriteLineAsync(":server NOTICE TestNick_ :echo complete".AsMemory(), cancellationToken);
        transcript.Add((await reader.ReadLineAsync(cancellationToken))!);
        transcript.Add((await reader.ReadLineAsync(cancellationToken))!);
        return transcript.ToArray();
    }

    private static async ValueTask OversizedIncomingLineDoesNotDisconnectAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var pongReceived = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(false),
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            await using var writer = new StreamWriter(
                stream,
                new UTF8Encoding(false),
                leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n"
            };

            _ = await CompleteEmptyCapabilityNegotiationAsync(
                reader,
                writer,
                "TestNick",
                timeout.Token);

            await writer.WriteLineAsync(
                ":server 001 TestNick :Welcome".AsMemory(),
                timeout.Token);

            var oversizedThenPing = Enumerable
                .Repeat((byte)'x', IrcLineFramer.MaximumPayloadBytes + 1)
                .Concat("\r\nPING :after-oversized\r\n"u8.ToArray())
                .ToArray();

            await stream.WriteAsync(oversizedThenPing, timeout.Token);

            var pong = (await reader.ReadLineAsync(timeout.Token))!;
            pongReceived.TrySetResult(pong);

            return await reader.ReadLineAsync(timeout.Token);
        }, timeout.Token);

        var options = new IrcConnectionOptions(
            new IrcEndpoint("127.0.0.1", port, useTls: false),
            new IrcIdentity(["TestNick"], "test", "Test User"));

        await using var connection = new IrcClientConnection(
            new TcpIrcTransportFactory());

        var diagnostics = new List<string>();
        connection.Diagnostic += diagnostics.Add;

        await connection.ConnectAsync(options, timeout.Token);

        Assert.Equal(
            "PONG after-oversized",
            await pongReceived.Task.WaitAsync(timeout.Token));
        Assert.Equal(IrcConnectionState.Online, connection.State);
        Assert.Equal(
            1,
            diagnostics.Count(message =>
                message == "Ignored an oversized IRC line exceeding 510 payload bytes."));

        await connection.DisconnectAsync("done", timeout.Token);

        Assert.Equal("QUIT done", (await serverTask)!);
        listener.Stop();
    }

    private static async ValueTask ExcessIncomingParametersProduceOneDiagnosticAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var pongReceived = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(false),
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            await using var writer = new StreamWriter(
                stream,
                new UTF8Encoding(false),
                leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n"
            };

            _ = await CompleteEmptyCapabilityNegotiationAsync(
                reader,
                writer,
                "TestNick",
                timeout.Token);

            await writer.WriteLineAsync(
                ":server 001 TestNick :Welcome".AsMemory(),
                timeout.Token);

            var parameters = string.Join(
                ' ',
                Enumerable.Range(
                    1,
                    IrcMessage.TraditionalParameterLimit + 1)
                    .Select(index => $"parameter{index}"));

            await writer.WriteLineAsync(
                $":server TEST {parameters}".AsMemory(),
                timeout.Token);
            await writer.WriteLineAsync(
                $":server TEST {parameters}".AsMemory(),
                timeout.Token);
            await writer.WriteLineAsync(
                "PING :after-excess-parameters".AsMemory(),
                timeout.Token);

            var pong = (await reader.ReadLineAsync(timeout.Token))!;
            pongReceived.TrySetResult(pong);

            return await reader.ReadLineAsync(timeout.Token);
        }, timeout.Token);

        var options = new IrcConnectionOptions(
            new IrcEndpoint("127.0.0.1", port, useTls: false),
            new IrcIdentity(["TestNick"], "test", "Test User"));

        await using var connection = new IrcClientConnection(
            new TcpIrcTransportFactory());

        var diagnostics = new List<string>();
        var received = new List<IrcMessage>();
        connection.Diagnostic += diagnostics.Add;
        connection.MessageReceived += message =>
        {
            received.Add(message);
            return ValueTask.CompletedTask;
        };

        await connection.ConnectAsync(options, timeout.Token);

        Assert.Equal(
            "PONG after-excess-parameters",
            await pongReceived.Task.WaitAsync(timeout.Token));
        Assert.Equal(IrcConnectionState.Online, connection.State);
        Assert.Equal(
            2,
            received.Count(message =>
                message.Command == "TEST" &&
                message.ExceedsTraditionalParameterLimit));
        Assert.Equal(
            1,
            diagnostics.Count(message =>
                message.StartsWith(
                    "Accepted a nonstandard IRC message with ",
                    StringComparison.Ordinal)));

        await connection.DisconnectAsync("done", timeout.Token);

        Assert.Equal("QUIT done", (await serverTask)!);
        listener.Stop();
    }

    private static async Task<(string Nick, string User)> CompleteEmptyCapabilityNegotiationAsync(
        StreamReader reader,
        StreamWriter writer,
        string nickname,
        CancellationToken cancellationToken)
    {
        Assert.Equal("CAP LS 302", (await reader.ReadLineAsync(cancellationToken))!);
        var nick = (await reader.ReadLineAsync(cancellationToken))!;
        var user = (await reader.ReadLineAsync(cancellationToken))!;
        Assert.Equal($"NICK {nickname}", nick);
        await writer.WriteLineAsync($":server CAP * LS :".AsMemory(), cancellationToken);
        Assert.Equal("CAP END", (await reader.ReadLineAsync(cancellationToken))!);
        return (nick, user);
    }

    private static async ValueTask SelfSignedTlsUsesPolicyAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var certificate = CreateSelfSignedCertificate();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = AcceptTlsClientAsync(listener, certificate, timeout.Token, expectSuccess: true);
        var policy = new RecordingTlsPolicy(TlsCertificateDecision.Accept);

        IIrcTransport transport;
        try
        {
            transport = await new TcpIrcTransportFactory(policy).ConnectAsync(
                new IrcEndpoint("127.0.0.1", port, useTls: true),
                timeout.Token);
        }
        catch (AuthenticationException exception) when (IsSchannelKeyStoreUnavailable(exception))
        {
            timeout.Cancel();
            listener.Stop();
            try
            {
                await serverTask;
            }
            catch (Exception cleanupException) when (cleanupException is AuthenticationException or IOException or OperationCanceledException)
            {
            }

            throw new TestSkippedException(
                "Windows Schannel cannot access its per-user test key store in this sandbox. This test passes in a normal Windows process.");
        }

        await using (transport)
        {
            await serverTask;
        }
        listener.Stop();

        Assert.True(policy.Seen is not null);
        Assert.Equal("127.0.0.1", policy.Seen!.Endpoint.Host);
        Assert.Equal(port, policy.Seen.Endpoint.Port);
        Assert.True(policy.Seen.Problems.HasFlag(TlsCertificateProblems.ChainErrors));
        Assert.Equal(64, policy.Seen.Sha256Fingerprint.Length);
    }

    private static async ValueTask SelfSignedTlsRejectsByDefaultAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var certificate = CreateSelfSignedCertificate();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = AcceptTlsClientAsync(listener, certificate, timeout.Token, expectSuccess: false);
        var rejected = false;

        try
        {
            await using var transport = await new TcpIrcTransportFactory().ConnectAsync(
                new IrcEndpoint("127.0.0.1", port, useTls: true),
                timeout.Token);
        }
        catch (AuthenticationException)
        {
            rejected = true;
        }

        await serverTask;
        listener.Stop();
        Assert.True(rejected, "A self-signed certificate was not rejected by default.");
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
            critical: true));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(
            created.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
    }

    private static bool IsSchannelKeyStoreUnavailable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is Win32Exception win32 &&
                win32.NativeErrorCode == unchecked((int)0x8009030E))
            {
                return true;
            }

            if (current.InnerException is null)
            {
                break;
            }
        }

        return false;
    }

    private static async Task AcceptTlsClientAsync(
        TcpListener listener,
        X509Certificate2 certificate,
        CancellationToken cancellationToken,
        bool expectSuccess)
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificateContext = SslStreamCertificateContext.Create(
                        certificate,
                        additionalCertificates: null,
                        offline: true),
                    EnabledSslProtocols = SslProtocols.None
                },
                cancellationToken);
        }
        catch (AuthenticationException) when (!expectSuccess)
        {
        }
        catch (IOException) when (!expectSuccess)
        {
        }
    }

    private sealed class RecordingTlsPolicy(TlsCertificateDecision decision) : ITlsCertificatePolicy
    {
        public TlsCertificateInfo? Seen { get; private set; }

        public TlsCertificateDecision Decide(TlsCertificateInfo certificate)
        {
            Seen = certificate;
            return decision;
        }
    }

    private sealed class CancellableConnectFactory : IIrcTransportFactory
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Canceled { get; private set; }

        public async ValueTask<IIrcTransport> ConnectAsync(
            IrcTransportOptions options,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Canceled = true;
                throw;
            }
            throw new InvalidOperationException("The connect operation unexpectedly completed.");
        }
    }
}
