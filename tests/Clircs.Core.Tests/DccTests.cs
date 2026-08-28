using Clircs.Dcc;
using Clircs.ConsoleClient;
using Clircs.Identity;
using Clircs.Protocol;
using Clircs.Sessions;
using Clircs.State;
using Clircs.Transport;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Clircs.Core.Tests;

internal static class DccTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("DCC CHAT parses traditional integer IPv4 endpoints", ChatParsesIntegerAddress);
        suite.Add("DCC CHAT parses modern literal IPv6 endpoints", ChatParsesIpv6Address);
        suite.Add("Secure DCC CHAT and SEND offers retain their secure state", SecureOffersParse);
        suite.Add("DCC SEND parses quoted and unquoted spaced filenames", SendParsesSpacedFilenames);
        suite.Add("DCC SEND accepts zero-byte and very large files", SendAcceptsBoundarySizes);
        suite.Add("DCC SEND recognizes passive offers", SendRecognizesPassiveOffers);
        suite.Add("DCC parses passive CHAT and SEND responses", ParsesPassiveResponses);
        suite.Add("DCC passive responses match their outgoing request token", PassiveResponsesMatchRequests);
        suite.Add("DCC rejects unsafe filenames and malformed endpoints", RejectsUnsafeOffers);
        suite.Add("DCC RESUME and ACCEPT parse active and passive wire forms", ResumeParsesWireForms);
        suite.Add("DCC resume control messages become structured events", SessionProducesStructuredResume);
        suite.Add("DCC request registry owns IDs states expiry and network invalidation", RegistryTracksLifecycle);
        suite.Add("DCC request registry never trims live requests", RegistryPreservesLiveRequests);
        suite.Add("DCC request finalization is atomic with terminal state changes", RegistryFinalizationIsAtomicAsync);
        suite.Add("DCC coordinator isolates runtime state by request", CoordinatorIsolatesRuntimeState);
        suite.Add("DCC coordinator awaits request-owned work", CoordinatorAwaitsOwnedWorkAsync);
        suite.Add("DCC CTCP offers become structured events without query windows", SessionProducesStructuredOffer);
        suite.Add("DCC request presentation hides wire endpoint numbers", RequestPresentationIsHumanReadable);
        suite.Add("DCC CHAT transport exchanges complete lines in both directions", ChatTransportExchangesLinesAsync);
        suite.Add("DCC CHAT transport listens on IPv6 when advertising IPv6", ChatTransportExchangesIpv6LinesAsync);
        suite.Add("Secure DCC CHAT negotiates TLS and exchanges complete lines", SecureChatTransportExchangesLinesAsync);
        suite.Add("DCC listeners stop waiting promptly when their request is canceled", ListenersRespectCancellationAsync);
        suite.Add("DCC address conversion matches traditional integer notation", AddressConversionMatchesWireFormat);
        suite.Add("DCC port settings accept random single and ranged ports", PortRangeParses);
        suite.Add("DCC automatic address selection prefers the IRC-visible public IPv6 address", AutoAddressUsesVisibleHostAsync);
        suite.Add("DCC address discovery consumes USERHOST without raw output", UserHostUpdatesVisibleAddress);
        suite.Add("DCC SEND receiver writes bytes and returns cumulative acknowledgements", SendReceiverTransfersAndAcknowledgesAsync);
        suite.Add("DCC SEND receiver rejects interrupted transfers", SendReceiverRejectsInterruptedTransferAsync);
        suite.Add("DCC SEND receiver completes zero-byte files", SendReceiverCompletesZeroBytesAsync);
        suite.Add("DCC SEND receiver resumes with cumulative acknowledgements", SendReceiverResumesAsync);
        suite.Add("DCC SEND sender streams bytes and waits for cumulative acknowledgements", SendSenderTransfersAndReadsAcknowledgementsAsync);
        suite.Add("DCC SEND sender rejects a source that changes size", SendSenderRejectsShortSourceAsync);
        suite.Add("DCC SEND sender completes zero-byte files", SendSenderCompletesZeroBytesAsync);
        suite.Add("DCC SEND sender resumes from the negotiated offset", SendSenderResumesAsync);
        suite.Add("DCC SEND sender ignores premature acknowledgements", SendSenderIgnoresPrematureAcknowledgementsAsync);
        suite.Add("DCC SEND operations stop while waiting for peer traffic when canceled", SendOperationsRespectCancellationAsync);
        suite.Add("DCC passive SEND reverses the listener and connector roles", PassiveSendReversesTransportAsync);
        suite.Add("Secure DCC SEND negotiates TLS and transfers with acknowledgements", SecureSendTransfersAsync);
        suite.Add("Secure passive DCC SEND reverses roles over TLS", SecurePassiveSendReversesTransportAsync);
        suite.Add("DCC downloads never overwrite existing files", DownloadStoreAvoidsCollisions);
        suite.Add("DCC partial downloads survive and can be resumed", DownloadStoreFindsPartialResume);
        suite.Add("DCC partial downloads are scoped to their network and sender", DownloadStoreScopesPartialResume);
        suite.Add("DCC resume rejects a partial file changed after selection", DownloadStoreRejectsChangedPartial);
        suite.Add("XDCC requests normalize packs and distinguish SEND from SSEND", XdccRequestsAreValidated);
    }

    private static void XdccRequestsAreValidated()
    {
        Assert.True(ClientApplication.TryBuildXdccRequest("get", "123", out var ordinary, out var ordinaryPack));
        Assert.Equal("XDCC SEND #123", ordinary);
        Assert.Equal("#123", ordinaryPack);

        Assert.True(ClientApplication.TryBuildXdccRequest("SGET", "#0042", out var secure, out var securePack));
        Assert.Equal("XDCC SSEND #42", secure);
        Assert.Equal("#42", securePack);

        Assert.False(ClientApplication.TryBuildXdccRequest("send", "12", out _, out _));
        Assert.False(ClientApplication.TryBuildXdccRequest("get", "0", out _, out _));
        Assert.False(ClientApplication.TryBuildXdccRequest("get", "##12", out _, out _));
        Assert.False(ClientApplication.TryBuildXdccRequest("get", "12x", out _, out _));
    }

    private static void ChatParsesIntegerAddress()
    {
        Assert.True(DccOfferParser.TryParse("DCC CHAT chat 2130706433 5000", out var offer, out _));
        Assert.Equal(DccRequestType.Chat, offer!.Type);
        Assert.Equal("127.0.0.1", offer.Address);
        Assert.Equal(5000, offer.Port);
        Assert.False(offer.IsPassive);
    }

    private static void ChatParsesIpv6Address()
    {
        Assert.True(DccOfferParser.TryParse(
            "DCC CHAT chat 2603:8081:3000:48b3:d585:8976:ce5f:865c 5000",
            out var offer,
            out _));
        Assert.Equal("2603:8081:3000:48b3:d585:8976:ce5f:865c", offer!.Address);
        Assert.Equal(5000, offer.Port);
    }

    private static void SecureOffersParse()
    {
        Assert.True(DccOfferParser.TryParse("DCC SCHAT chat 2130706433 5000", out var chat, out _));
        Assert.Equal(DccRequestType.Chat, chat!.Type);
        Assert.True(chat.IsSecure);

        Assert.True(DccOfferParser.TryParse(
            "DCC SSEND \"some file.txt\" 2130706433 5001 1234", out var send, out _));
        Assert.Equal(DccRequestType.Send, send!.Type);
        Assert.True(send.IsSecure);
        Assert.Equal("some file.txt", send.Filename!);
    }

    private static void SendParsesSpacedFilenames()
    {
        Assert.True(DccOfferParser.TryParse("DCC SEND \"some file.txt\" 2130706433 5001 1234", out var quoted, out _));
        Assert.Equal("some file.txt", quoted!.Filename!);
        Assert.Equal(1234L, quoted.Size!.Value);

        Assert.True(DccOfferParser.TryParse("DCC SEND another file.txt 127.0.0.1 5002 99", out var unquoted, out _));
        Assert.Equal("another file.txt", unquoted!.Filename!);
        Assert.Equal("127.0.0.1", unquoted.Address);
    }

    private static void ResumeParsesWireForms()
    {
        Assert.True(DccResumeParser.TryParse(
            "DCC RESUME \"some file.zip\" 5051 12345", out var active, out _));
        Assert.Equal(DccResumeOperation.Resume, active!.Operation);
        Assert.Equal("some file.zip", active.Filename);
        Assert.Equal(5051, active.Port);
        Assert.Equal(12_345L, active.Position);
        Assert.False(active.IsPassive);

        Assert.True(DccResumeParser.TryParse(
            "DCC ACCEPT some file.zip 0 9876 42", out var passive, out _));
        Assert.Equal(DccResumeOperation.Accept, passive!.Operation);
        Assert.Equal("some file.zip", passive.Filename);
        Assert.Equal(0, passive.Port);
        Assert.Equal(9_876L, passive.Position);
        Assert.Equal("42", passive.PassiveToken!);
        Assert.True(passive.IsPassive);
        Assert.Equal(
            "DCC RESUME \"some file.zip\" 0 9876 42",
            DccResumeParser.Format(DccResumeOperation.Resume, "some file.zip", 0, 9_876, "42"));
        Assert.False(DccResumeParser.TryParse("DCC RESUME file.zip 0 99", out _, out _));
    }

    private static void SessionProducesStructuredResume()
    {
        var state = new NetworkSessionState(NetworkSessionId.New(), "test", IrcCaseMapping.Rfc1459);
        var processor = new IrcSessionProcessor(state, "me");
        var events = processor.Process(IrcMessageParser.Parse(
            ":alice!user@example PRIVMSG me :\u0001DCC RESUME \"some file.zip\" 5051 12345\u0001"));

        Assert.Equal(1, events.Count);
        Assert.Equal("dcc.control", events[0].Fields!["event"]!);
        Assert.Equal("resume", events[0].Fields!["dcc.operation"]!);
        Assert.Equal("some file.zip", events[0].Fields!["dcc.filename"]!);
        Assert.Equal("12345", events[0].Fields!["dcc.position"]!);
    }

    private static void SendRecognizesPassiveOffers()
    {
        Assert.True(DccOfferParser.TryParse("DCC SEND file.zip 2130706433 0 42 98765", out var offer, out _));
        Assert.True(offer!.IsPassive);
        Assert.Equal("98765", offer.PassiveToken!);
        Assert.Equal(0, offer.Port);
    }

    private static void ParsesPassiveResponses()
    {
        Assert.True(DccOfferParser.TryParse("DCC CHAT chat 2130706433 0 4217", out var chatRequest, out _));
        Assert.True(chatRequest!.IsPassiveRequest);
        Assert.True(DccOfferParser.TryParse("DCC CHAT chat 2130706433 5050 4217", out var chatResponse, out _));
        Assert.True(chatResponse!.IsPassiveResponse);
        Assert.Equal("4217", chatResponse.PassiveToken!);

        Assert.True(DccOfferParser.TryParse(
            "DCC SEND \"some file.zip\" 2130706433 5051 12345 9912", out var sendResponse, out _));
        Assert.True(sendResponse!.IsPassiveResponse);
        Assert.Equal("some file.zip", sendResponse.Filename!);
        Assert.Equal(12345L, sendResponse.Size!.Value);
        Assert.False(DccOfferParser.TryParse("DCC CHAT chat 2130706433 0 nope", out _, out _));
    }

    private static void PassiveResponsesMatchRequests()
    {
        var sessionId = NetworkSessionId.New();
        var now = DateTimeOffset.UtcNow;
        var request = new DccRequest(9, sessionId, "EFNet", "[Alice]",
            new DccOffer(DccRequestType.Send, "some file.zip", "1.1.1.1", 0, 12345, "9912",
                "DCC SEND \"some file.zip\" 16843009 0 12345 9912"),
            now, now.AddMinutes(2), DccRequestState.Pending, Direction: DccRequestDirection.Outgoing);
        var response = new DccOffer(DccRequestType.Send, "some file.zip", "127.0.0.1", 5051, 12345, "9912",
            "DCC SEND \"some file.zip\" 2130706433 5051 12345 9912");
        Assert.True(ClientApplication.PassiveResponseMatches(
            request, sessionId, "{alice}", response, IrcCaseMapping.Rfc1459));
        Assert.False(ClientApplication.PassiveResponseMatches(
            request, sessionId, "bob", response, IrcCaseMapping.Rfc1459));
        Assert.False(ClientApplication.PassiveResponseMatches(
            request, sessionId, "{alice}", response with { PassiveToken = "9913" }, IrcCaseMapping.Rfc1459));
    }

    private static void SendAcceptsBoundarySizes()
    {
        Assert.True(DccOfferParser.TryParse("DCC SEND empty.txt 2130706433 5001 0", out var empty, out _));
        Assert.Equal(0L, empty!.Size!.Value);

        Assert.True(DccOfferParser.TryParse("DCC SEND large.mkv 2130706433 5002 5368709120", out var large, out _));
        Assert.Equal(5_368_709_120L, large!.Size!.Value);
    }

    private static void RejectsUnsafeOffers()
    {
        Assert.False(DccOfferParser.TryParse("DCC SEND ../secret.txt 2130706433 5000 1", out _, out _));
        Assert.False(DccOfferParser.TryParse("DCC SEND file.txt 2130706433 70000 1", out _, out _));
        Assert.False(DccOfferParser.TryParse("DCC CHAT chat 2130706433 0", out _, out _));
        Assert.False(DccOfferParser.TryParse("DCC EXEC nope", out _, out _));
    }

    private static void RegistryTracksLifecycle()
    {
        var registry = new DccRequestRegistry();
        var firstSession = NetworkSessionId.New();
        var secondSession = NetworkSessionId.New();
        var now = DateTimeOffset.UtcNow;
        var offer = new DccOffer(DccRequestType.Chat, null, "127.0.0.1", 5000, null, null,
            "DCC CHAT chat 2130706433 5000");
        var first = registry.Add(firstSession, "EFNet", "alice", offer, now, TimeSpan.FromSeconds(1));
        var second = registry.Add(secondSession, "DALnet", "bob", offer, now, TimeSpan.FromMinutes(1));
        var expiring = registry.Add(firstSession, "EFNet", "carol", offer, now, TimeSpan.FromSeconds(1));
        Assert.Equal(first.Id + 1, second.Id);
        Assert.True(registry.TryTransition(first.Id, DccRequestState.Rejected, "no", out var rejected));
        Assert.Equal(DccRequestState.Rejected, rejected!.State);
        Assert.False(registry.TryTransition(first.Id, DccRequestState.Cancelled, null, out _));
        var expired = registry.Expire(now.AddSeconds(2));
        Assert.Equal(1, expired.Count);
        Assert.Equal(expiring.Id, expired[0].Id);
        var invalidated = registry.Invalidate(secondSession, "disconnect");
        Assert.Equal(1, invalidated.Count);
        Assert.Equal(DccRequestState.Invalidated, invalidated[0].State);

        var connected = registry.Add(firstSession, "EFNet", "dave", offer, now, TimeSpan.FromMinutes(1));
        Assert.False(registry.TryTransition(connected.Id, DccRequestState.Connected, null, out _));
        Assert.True(registry.TryTransition(connected.Id, DccRequestState.Connecting, null, out _));
        Assert.True(registry.TryTransition(connected.Id, DccRequestState.Connected, null, out _));
        Assert.Equal(0, registry.Invalidate(firstSession, "IRC disconnected").Count);
        Assert.True(registry.TryTransition(connected.Id, DccRequestState.Closed, "done", out var closed));
        Assert.Equal(DccRequestState.Closed, closed!.State);

        var transfer = registry.Add(firstSession, "EFNet", "erin",
            offer with { Type = DccRequestType.Send, Filename = "file.bin", Size = 10 },
            now, TimeSpan.FromMinutes(1));
        Assert.True(registry.TryTransition(transfer.Id, DccRequestState.Connecting, null, out _));
        Assert.True(registry.TryTransition(transfer.Id, DccRequestState.Connected, null, out _));
        Assert.True(registry.TryTransition(transfer.Id, DccRequestState.Completed, "file.bin", out var completed));
        Assert.True(DccRequestRegistry.IsTerminal(completed!.State));

        var passive = registry.Add(firstSession, "EFNet", "frank",
            offer with { Port = 0, PassiveToken = "7" }, now, TimeSpan.FromMinutes(1),
            DccRequestDirection.Outgoing);
        var response = passive.Offer with { Address = "127.0.0.1", Port = 5009 };
        Assert.True(registry.TryTransitionWithOffer(passive.Id, DccRequestState.Connecting,
            response, "response", out var responding));
        Assert.Equal(5009, responding!.Offer.Port);
        Assert.True(responding.Offer.IsPassiveResponse);

        var cancelledWhileConnecting = registry.Add(
            firstSession, "EFNet", "grace", offer, now, TimeSpan.FromMinutes(1));
        Assert.True(registry.TryTransition(
            cancelledWhileConnecting.Id, DccRequestState.Connecting, null, out _));
        Assert.True(registry.TryTransition(
            cancelledWhileConnecting.Id, DccRequestState.Cancelled, "cancel", out var cancelled));
        Assert.True(DccRequestRegistry.IsTerminal(cancelled!.State));
        Assert.False(registry.TryTransition(
            cancelledWhileConnecting.Id, DccRequestState.Failed, "late failure", out _));
    }

    private static void RegistryPreservesLiveRequests()
    {
        var registry = new DccRequestRegistry();
        var sessionId = NetworkSessionId.New();
        var offer = new DccOffer(DccRequestType.Chat, null, "127.0.0.1", 5000, null, null,
            "DCC CHAT chat 2130706433 5000");
        var requests = Enumerable.Range(0, 205)
            .Select(index => registry.Add(sessionId, "EFNet", $"user{index}", offer, DateTimeOffset.UtcNow))
            .ToArray();

        Assert.Equal(205, registry.Snapshot().Count);
        Assert.True(registry.TryGet(requests[0].Id, out _));

        for (var index = 0; index < 10; index++)
            Assert.True(registry.TryTransition(requests[index].Id, DccRequestState.Rejected, null, out _));
        registry.Add(sessionId, "EFNet", "another", offer, DateTimeOffset.UtcNow);

        Assert.False(registry.TryGet(requests[0].Id, out _));
        Assert.True(registry.TryGet(requests[10].Id, out _));
        Assert.Equal(200, registry.Snapshot().Count);
    }

    private static void CoordinatorIsolatesRuntimeState()
    {
        var coordinator = new DccCoordinator();
        var firstBuffer = BufferId.New();
        var secondBuffer = BufferId.New();
        var firstTarget = new DccDownloadTarget("one.bin", "one.part", "one.bin", 10);
        var secondTarget = new DccDownloadTarget("two.bin", "two.part", "two.bin", 20);
        var firstResume = new PendingDccResume(firstTarget, 10);
        var secondResume = new PendingDccResume(secondTarget, 20);

        coordinator.SetChatBuffer(1, firstBuffer);
        coordinator.SetChatBuffer(2, secondBuffer);
        Assert.Equal(1, coordinator.RequestIdForChatBuffer(firstBuffer)!.Value);
        Assert.Equal(2, coordinator.RequestIdForChatBuffer(secondBuffer)!.Value);
        Assert.True(coordinator.TryBeginResume(1, firstResume));
        Assert.True(coordinator.TryBeginResume(2, secondResume));
        Assert.False(coordinator.TryBeginResume(1, secondResume));
        Assert.True(coordinator.TakePendingResume(1, firstResume));
        Assert.True(coordinator.PendingResume(1) is null);
        Assert.True(ReferenceEquals(secondResume, coordinator.PendingResume(2)));

        coordinator.ClearChatBuffer(firstBuffer);
        Assert.True(coordinator.ChatBufferId(1) is null);
        Assert.Equal(secondBuffer, coordinator.ChatBufferId(2)!.Value);
    }

    private static async ValueTask CoordinatorAwaitsOwnedWorkAsync()
    {
        var coordinator = new DccCoordinator();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.TrackTask(7, completion.Task);

        var waiting = coordinator.AwaitTasksAsync([7]);
        Assert.False(waiting.IsCompleted);
        completion.SetResult();
        await waiting;
    }

    private static async ValueTask RegistryFinalizationIsAtomicAsync()
    {
        var registry = new DccRequestRegistry();
        var offer = new DccOffer(DccRequestType.Send, "file.bin", "127.0.0.1", 5000, 10, null,
            "DCC SEND file.bin 2130706433 5000 10");
        var request = registry.Add(NetworkSessionId.New(), "EFNet", "alice", offer, DateTimeOffset.UtcNow);
        Assert.True(registry.TryTransition(request.Id, DccRequestState.Connecting, null, out _));
        Assert.True(registry.TryTransition(request.Id, DccRequestState.Connected, null, out _));

        using var finalizerEntered = new ManualResetEventSlim();
        using var releaseFinalizer = new ManualResetEventSlim();
        var completed = Task.Run(() => registry.TryTransitionAfter(
            request.Id,
            DccRequestState.Completed,
            () =>
            {
                finalizerEntered.Set();
                releaseFinalizer.Wait(TimeSpan.FromSeconds(5));
                return "file.bin";
            },
            out _));

        Assert.True(finalizerEntered.Wait(TimeSpan.FromSeconds(5)));
        var cancelled = Task.Run(() =>
            registry.TryTransition(request.Id, DccRequestState.Cancelled, "cancelled", out _));
        await Task.Delay(50);
        Assert.False(cancelled.IsCompleted);
        releaseFinalizer.Set();

        Assert.True(await completed);
        Assert.False(await cancelled);
        Assert.True(registry.TryGet(request.Id, out var final));
        Assert.Equal(DccRequestState.Completed, final!.State);
        Assert.Equal("file.bin", final.StateReason!);
    }

    private static void SessionProducesStructuredOffer()
    {
        var state = new NetworkSessionState(NetworkSessionId.New(), "test", IrcCaseMapping.Rfc1459);
        var processor = new IrcSessionProcessor(state, "me");
        var events = processor.Process(IrcMessageParser.Parse(
            ":alice!user@example PRIVMSG me :\u0001DCC SEND file.txt 2130706433 5000 123\u0001"));

        Assert.Equal(1, events.Count);
        Assert.Equal("dcc.request", events[0].Fields!["event"]!);
        Assert.Equal("send", events[0].Fields!["dcc.type"]!);
        Assert.Equal("127.0.0.1", events[0].Fields!["dcc.address"]!);
        Assert.Equal("true", events[0].Fields!["private"]!);
        Assert.Equal(1, state.Buffers.Count);
    }

    private static void RequestPresentationIsHumanReadable()
    {
        var now = DateTimeOffset.UtcNow;
        var request = new DccRequest(
            4,
            NetworkSessionId.New(),
            "EFNet",
            "alice",
            new DccOffer(DccRequestType.Send, "some file.mkv", "203.0.113.42", 1024, 950_792_1023L, null,
                "DCC SEND \"some file.mkv\" 3405803818 1024 9507921023"),
            now,
            now.AddMinutes(2),
            DccRequestState.Pending);

        var presentation = ClientApplication.DccRequestPresentation(request);
        Assert.Equal("DCC SEND:", presentation.Title);
        Assert.Equal("#4", presentation.TitleHighlight!);
        var fields = presentation.Fields!;
        Assert.True(fields.Any(field => field.Label == "Address" && field.Value == "203.0.113.42"));
        Assert.True(fields.Any(field => field.Label == "Port" && field.Value == "1024"));
        Assert.False(fields.Any(field => field.Value.Contains("3405803818", StringComparison.Ordinal)));

        var outgoing = ClientApplication.DccRequestPresentation(request with
        {
            Direction = DccRequestDirection.Outgoing
        });
        var outgoingFields = outgoing.Fields!;
        Assert.True(outgoingFields.Any(field => field.Label == "To" && field.Value == "alice"));
        Assert.True(outgoingFields.Any(field => field.Label == "Use" && field.Value == "/dcc cancel 4"));

        var secure = ClientApplication.DccRequestPresentation(request with
        {
            Offer = request.Offer with { IsSecure = true }
        });
        Assert.Equal("DCC SSEND:", secure.Title);
        var secureFields = secure.Fields!;
        Assert.True(secureFields.Any(field => field.Label == "Secure" && field.Value == "yes"));
        Assert.True(secureFields.Any(field => field.Label == "Use" &&
            field.Value == "/dcc accept 4, /dcc resume 4, or /dcc reject 4"));
    }

    private static async ValueTask ChatTransportExchangesLinesAsync()
    {
        await using var listener = DccChatListener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = listener.AcceptAsync(timeout.Token).AsTask();
        await using var outgoing = await DccChatTransport.ConnectAsync("127.0.0.1", listener.Port, timeout.Token);
        await using var incoming = await accepting;

        await outgoing.SendLineAsync("hello from outgoing", timeout.Token);
        await incoming.SendLineAsync("hello from incoming", timeout.Token);
        Assert.Equal("hello from outgoing", await ReadOneLineAsync(incoming, timeout.Token));
        Assert.Equal("hello from incoming", await ReadOneLineAsync(outgoing, timeout.Token));
    }

    private static async ValueTask ChatTransportExchangesIpv6LinesAsync()
    {
        if (!System.Net.Sockets.Socket.OSSupportsIPv6) return;
        await using var listener = DccChatListener.Start(IPAddress.IPv6Loopback, DccPortRange.Random);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = listener.AcceptAsync(timeout.Token).AsTask();
        await using var outgoing = await DccChatTransport.ConnectAsync("::1", listener.Port, timeout.Token);
        await using var incoming = await accepting;

        await outgoing.SendLineAsync("hello over IPv6", timeout.Token);
        Assert.Equal("hello over IPv6", await ReadOneLineAsync(incoming, timeout.Token));
    }

    private static async ValueTask SecureChatTransportExchangesLinesAsync()
    {
        try
        {
            await using var listener = DccChatListener.Start(
                IPAddress.Loopback, DccPortRange.Random, secure: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var accepting = listener.AcceptAsync(timeout.Token).AsTask();
            await using var outgoing = await DccChatTransport.ConnectAsync(
                "127.0.0.1", listener.Port, timeout.Token, secure: true);
            await using var incoming = await accepting;

            Assert.True(outgoing.IsSecure);
            Assert.True(incoming.IsSecure);
            Assert.True(outgoing.SecurityProtocol is "Tls12" or "Tls13");
            Assert.True(incoming.SecurityProtocol is "Tls12" or "Tls13");
            Assert.True(!string.IsNullOrWhiteSpace(outgoing.PeerCertificateFingerprint));
            Assert.True(string.IsNullOrWhiteSpace(incoming.PeerCertificateFingerprint));

            await outgoing.SendLineAsync("hello over secure DCC", timeout.Token);
            await incoming.SendLineAsync("secure reply", timeout.Token);
            Assert.Equal("hello over secure DCC", await ReadOneLineAsync(incoming, timeout.Token));
            Assert.Equal("secure reply", await ReadOneLineAsync(outgoing, timeout.Token));
        }
        catch (AuthenticationException exception) when (IsSchannelKeyStoreUnavailable(exception))
        {
            throw new TestSkippedException(
                "Windows Schannel cannot access its per-user test key store in this sandbox. This test passes in a normal Windows process.");
        }
    }

    private static async ValueTask ListenersRespectCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        await using var chat = DccChatListener.Start(IPAddress.Loopback, DccPortRange.Random);
        await using var send = DccFileSendListener.Start(IPAddress.Loopback, DccPortRange.Random);
        await using var receive = DccFileReceiveListener.Start(IPAddress.Loopback, DccPortRange.Random);

        var waits = new Task[]
        {
            chat.AcceptAsync(cancellation.Token).AsTask(),
            send.AcceptAsync(cancellation.Token).AsTask(),
            receive.AcceptAsync(cancellation.Token).AsTask()
        };
        cancellation.Cancel();

        foreach (var wait in waits)
            await Assert.ThrowsAsync<OperationCanceledException>(() => wait);
    }

    private static bool IsSchannelKeyStoreUnavailable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is Win32Exception win32 &&
                win32.NativeErrorCode == unchecked((int)0x8009030E)) return true;
            if (current.InnerException is null) break;
        }
        return false;
    }

    private static async ValueTask<string> ReadOneLineAsync(
        DccChatTransport transport,
        CancellationToken cancellationToken)
    {
        await using var lines = transport.ReadLinesAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        Assert.True(await lines.MoveNextAsync());
        return lines.Current;
    }

    private static void AddressConversionMatchesWireFormat()
    {
        Assert.Equal(3_405_803_818u, DccAddressSelector.ToDccInteger(IPAddress.Parse("203.0.113.42")));
        Assert.Equal(2_130_706_433u, DccAddressSelector.ToDccInteger(IPAddress.Loopback));
        Assert.Equal("3405803818", DccAddressSelector.ToDccAddressToken(IPAddress.Parse("203.0.113.42")));
        Assert.Equal(
            "2603:8081:3000:48b3:d585:8976:ce5f:865c",
            DccAddressSelector.ToDccAddressToken(IPAddress.Parse("2603:8081:3000:48b3:d585:8976:ce5f:865c")));
    }

    private static void PortRangeParses()
    {
        Assert.True(DccPortRange.TryParse("random", out var random));
        Assert.Equal("random", random.ToString());
        Assert.True(DccPortRange.TryParse("50000", out var single));
        Assert.Equal(new DccPortRange(50000, 50000), single);
        Assert.True(DccPortRange.TryParse("50000-50009", out var range));
        Assert.Equal(new DccPortRange(50000, 50009), range);
        Assert.False(DccPortRange.TryParse("0", out _));
        Assert.False(DccPortRange.TryParse("60000-50000", out _));
    }

    private static async ValueTask AutoAddressUsesVisibleHostAsync()
    {
        var selected = await DccAddressSelector.SelectAdvertisedAddressAsync(
            "auto", "2603:8081:3000:48b3:d585:8976:ce5f:865c", "127.0.0.1", 6667);
        Assert.Equal("2603:8081:3000:48b3:d585:8976:ce5f:865c", selected.ToString());
        Assert.True(DccAddressSelector.IsGlobalIPv6(selected));
        Assert.False(DccAddressSelector.IsGlobalIPv6(IPAddress.Parse("fd00::1")));
        Assert.False(DccAddressSelector.IsPublicIPv4(IPAddress.Parse("192.168.1.51")));
    }

    private static void UserHostUpdatesVisibleAddress()
    {
        var state = new NetworkSessionState(NetworkSessionId.New(), "test", IrcCaseMapping.Rfc1459);
        var processor = new IrcSessionProcessor(state, "slakker");
        var events = processor.Process(IrcMessageParser.Parse(
            ":irc.example 302 slakker :slakker=+~slakker@203.0.113.42"));
        Assert.Equal(0, events.Count);
        Assert.Equal("203.0.113.42", state.VisibleHost!);
    }

    private static async ValueTask SendReceiverTransfersAndAcknowledgesAsync()
    {
        var payload = Enumerable.Range(0, 150_000).Select(index => (byte)(index % 251)).ToArray();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sender = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            var sent = 0;
            var acknowledgement = new byte[sizeof(uint)];
            while (sent < payload.Length)
            {
                var count = Math.Min(11_111, payload.Length - sent);
                await stream.WriteAsync(payload.AsMemory(sent, count), timeout.Token);
                sent += count;
                uint acknowledged = 0;
                while (acknowledged < sent)
                {
                    await stream.ReadExactlyAsync(acknowledgement, timeout.Token);
                    acknowledged = BinaryPrimitives.ReadUInt32BigEndian(acknowledgement);
                }
                Assert.Equal((uint)sent, acknowledged);
            }
        }, timeout.Token);

        await using var receiver = await DccFileReceiveTransport.ConnectAsync("127.0.0.1", port, timeout.Token);
        await using var destination = new MemoryStream();
        var progress = new List<DccReceiveProgress>();
        await receiver.ReceiveAsync(destination, payload.Length, progress.Add,
            cancellationToken: timeout.Token);
        await sender;

        Assert.True(payload.SequenceEqual(destination.ToArray()));
        Assert.True(progress.Count > 0);
        Assert.Equal((long)payload.Length, progress[^1].BytesReceived);
    }

    private static async ValueTask SendReceiverRejectsInterruptedTransferAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sender = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 1, 2, 3 }, timeout.Token);
            var acknowledgement = new byte[sizeof(uint)];
            await stream.ReadExactlyAsync(acknowledgement, timeout.Token);
            Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(acknowledgement));
            client.Client.Shutdown(SocketShutdown.Send);
        }, timeout.Token);

        await using var receiver = await DccFileReceiveTransport.ConnectAsync("127.0.0.1", port, timeout.Token);
        await using var destination = new MemoryStream();
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            receiver.ReceiveAsync(destination, 10, cancellationToken: timeout.Token).AsTask());
        await sender;
        Assert.Equal(3L, destination.Length);
    }

    private static async ValueTask SendReceiverCompletesZeroBytesAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sender = listener.AcceptTcpClientAsync(timeout.Token).AsTask();

        await using var receiver = await DccFileReceiveTransport.ConnectAsync("127.0.0.1", port, timeout.Token);
        using var accepted = await sender;
        await using var destination = new MemoryStream();
        DccReceiveProgress? final = null;
        await receiver.ReceiveAsync(destination, 0, progress => final = progress,
            cancellationToken: timeout.Token);
        Assert.Equal(0L, destination.Length);
        Assert.Equal(0L, final!.Value.BytesReceived);
    }

    private static async ValueTask SendReceiverResumesAsync()
    {
        var payload = Enumerable.Range(0, 120_000).Select(index => (byte)(index % 241)).ToArray();
        const int offset = 31_337;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sender = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(payload.AsMemory(offset), timeout.Token);
            var acknowledgement = new byte[sizeof(uint)];
            uint acknowledged = 0;
            while (acknowledged < payload.Length)
            {
                await stream.ReadExactlyAsync(acknowledgement, timeout.Token);
                acknowledged = BinaryPrimitives.ReadUInt32BigEndian(acknowledgement);
            }
            Assert.Equal((uint)payload.Length, acknowledged);
        }, timeout.Token);

        await using var receiver = await DccFileReceiveTransport.ConnectAsync("127.0.0.1", port, timeout.Token);
        await using var destination = new MemoryStream();
        await destination.WriteAsync(payload.AsMemory(0, offset), timeout.Token);
        DccReceiveProgress? final = null;
        await receiver.ReceiveAsync(destination, payload.Length, progress => final = progress,
            initialOffset: offset, cancellationToken: timeout.Token);
        await sender;

        Assert.True(payload.SequenceEqual(destination.ToArray()));
        Assert.Equal((long)payload.Length, final!.Value.BytesReceived);
        Assert.Equal(offset, final.Value.InitialOffset);
    }

    private static async ValueTask SendSenderTransfersAndReadsAcknowledgementsAsync()
    {
        var payload = Enumerable.Range(0, 180_000).Select(index => (byte)(index % 239)).ToArray();
        await using var listener = DccFileSendListener.Start(IPAddress.Loopback, DccPortRange.Random);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepting = listener.AcceptAsync(timeout.Token).AsTask();
        using var receiver = new TcpClient();
        await receiver.ConnectAsync(IPAddress.Loopback, listener.Port, timeout.Token);
        await using var sender = await accepting;
        await using var source = new MemoryStream(payload);
        var received = new MemoryStream();
        var receiverTask = Task.Run(async () =>
        {
            var stream = receiver.GetStream();
            var buffer = new byte[17_777];
            var acknowledgement = new byte[sizeof(uint)];
            while (received.Length < payload.Length)
            {
                var count = await stream.ReadAsync(buffer, timeout.Token);
                if (count == 0) throw new EndOfStreamException();
                await received.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                BinaryPrimitives.WriteUInt32BigEndian(acknowledgement, (uint)received.Length);
                await stream.WriteAsync(acknowledgement.AsMemory(0, 2), timeout.Token);
                await stream.WriteAsync(acknowledgement.AsMemory(2, 2), timeout.Token);
            }
        }, timeout.Token);

        var progress = new List<DccSendProgress>();
        await sender.SendAsync(source, payload.Length, progress.Add, cancellationToken: timeout.Token);
        await receiverTask;

        Assert.True(payload.SequenceEqual(received.ToArray()));
        Assert.True(progress.Count > 0);
        Assert.Equal((long)payload.Length, progress[^1].BytesAcknowledged);
    }

    private static async ValueTask SendSenderRejectsShortSourceAsync()
    {
        await using var listener = DccFileSendListener.Start(IPAddress.Loopback, DccPortRange.Random);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = listener.AcceptAsync(timeout.Token).AsTask();
        using var receiver = new TcpClient();
        await receiver.ConnectAsync(IPAddress.Loopback, listener.Port, timeout.Token);
        await using var sender = await accepting;
        await using var source = new MemoryStream(new byte[] { 1, 2, 3 });
        var receiverTask = Task.Run(async () =>
        {
            var stream = receiver.GetStream();
            var bytes = new byte[3];
            await stream.ReadExactlyAsync(bytes, timeout.Token);
            var acknowledgement = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32BigEndian(acknowledgement, 3);
            await stream.WriteAsync(acknowledgement, timeout.Token);
        }, timeout.Token);

        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            sender.SendAsync(source, 10, cancellationToken: timeout.Token).AsTask());
        await receiverTask;
    }

    private static async ValueTask SendSenderCompletesZeroBytesAsync()
    {
        await using var listener = DccFileSendListener.Start(IPAddress.Loopback, DccPortRange.Random);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = listener.AcceptAsync(timeout.Token).AsTask();
        using var receiver = new TcpClient();
        await receiver.ConnectAsync(IPAddress.Loopback, listener.Port, timeout.Token);
        await using var sender = await accepting;
        await using var source = new MemoryStream();
        DccSendProgress? final = null;
        await sender.SendAsync(source, 0, progress => final = progress, cancellationToken: timeout.Token);
        Assert.Equal(0L, final!.Value.BytesAcknowledged);
    }

    private static async ValueTask SendSenderResumesAsync()
    {
        var payload = Enumerable.Range(0, 140_000).Select(index => (byte)(index % 233)).ToArray();
        const int offset = 44_444;
        await using var listener = DccFileSendListener.Start(IPAddress.Loopback, DccPortRange.Random);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepting = listener.AcceptAsync(timeout.Token).AsTask();
        using var receiver = new TcpClient();
        await receiver.ConnectAsync(IPAddress.Loopback, listener.Port, timeout.Token);
        await using var sender = await accepting;
        await using var source = new MemoryStream(payload);
        await using var received = new MemoryStream();
        var receiverTask = Task.Run(async () =>
        {
            var stream = receiver.GetStream();
            var buffer = new byte[64 * 1024];
            while (received.Length < payload.Length - offset)
            {
                var count = await stream.ReadAsync(buffer, timeout.Token);
                if (count == 0) throw new EndOfStreamException();
                await received.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                var acknowledgement = new byte[sizeof(uint)];
                BinaryPrimitives.WriteUInt32BigEndian(
                    acknowledgement, unchecked((uint)(offset + received.Length)));
                await stream.WriteAsync(acknowledgement, timeout.Token);
            }
        }, timeout.Token);

        DccSendProgress? final = null;
        await sender.SendAsync(source, payload.Length, progress => final = progress,
            initialOffset: offset, cancellationToken: timeout.Token);
        await receiverTask;

        Assert.True(payload.AsSpan(offset).SequenceEqual(received.ToArray()));
        Assert.Equal((long)payload.Length, final!.Value.BytesAcknowledged);
        Assert.Equal(offset, final.Value.InitialOffset);
    }

    private static async ValueTask SendSenderIgnoresPrematureAcknowledgementsAsync()
    {
        var payload = Enumerable.Range(0, 150_000).Select(index => (byte)(index % 229)).ToArray();
        await using var listener = DccFileSendListener.Start(IPAddress.Loopback, DccPortRange.Random);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepting = listener.AcceptAsync(timeout.Token).AsTask();
        using var receiver = new TcpClient();
        await receiver.ConnectAsync(IPAddress.Loopback, listener.Port, timeout.Token);
        await using var sender = await accepting;
        await using var source = new MemoryStream(payload);
        await using var received = new MemoryStream();
        var receiverTask = Task.Run(async () =>
        {
            var stream = receiver.GetStream();
            var buffer = new byte[16_384];
            var acknowledgement = new byte[sizeof(uint)];
            var first = true;
            while (received.Length < payload.Length)
            {
                var count = await stream.ReadAsync(buffer, timeout.Token);
                if (count == 0) throw new EndOfStreamException();
                await received.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                BinaryPrimitives.WriteUInt32BigEndian(
                    acknowledgement,
                    first ? uint.MaxValue : unchecked((uint)received.Length));
                first = false;
                await stream.WriteAsync(acknowledgement, timeout.Token);
            }
        }, timeout.Token);

        await sender.SendAsync(source, payload.Length, cancellationToken: timeout.Token);
        await receiverTask;
        Assert.True(payload.SequenceEqual(received.ToArray()));
    }

    private static async ValueTask SendOperationsRespectCancellationAsync()
    {
        var payload = Enumerable.Range(0, 32_000).Select(index => (byte)(index % 211)).ToArray();
        await using (var listener = DccFileSendListener.Start(IPAddress.Loopback, DccPortRange.Random))
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        using (var receiver = new TcpClient())
        {
            var accepting = listener.AcceptAsync(cancellation.Token).AsTask();
            await receiver.ConnectAsync(IPAddress.Loopback, listener.Port, cancellation.Token);
            await using var sender = await accepting;
            await using var source = new MemoryStream(payload);
            var sending = sender.SendAsync(
                source, payload.Length, cancellationToken: cancellation.Token).AsTask();
            var received = new byte[payload.Length];
            await receiver.GetStream().ReadExactlyAsync(received, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => sending);
        }

        using var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start(1);
        var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        using var receiveCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var acceptingPeer = tcpListener.AcceptTcpClientAsync(receiveCancellation.Token).AsTask();
        await using var receiving = await DccFileReceiveTransport.ConnectAsync(
            "127.0.0.1", port, receiveCancellation.Token);
        using var idlePeer = await acceptingPeer;
        await using var destination = new MemoryStream();
        var receiveTask = receiving.ReceiveAsync(
            destination, 10, cancellationToken: receiveCancellation.Token).AsTask();
        receiveCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => receiveTask);
    }

    private static async ValueTask PassiveSendReversesTransportAsync()
    {
        var payload = Enumerable.Range(0, 96_000).Select(index => (byte)(index % 227)).ToArray();
        await using var listener = DccFileReceiveListener.Start(IPAddress.Loopback, DccPortRange.Random);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepting = listener.AcceptAsync(timeout.Token).AsTask();
        await using var sender = await DccFileSendTransport.ConnectAsync("127.0.0.1", listener.Port, timeout.Token);
        await using var receiver = await accepting;
        await using var source = new MemoryStream(payload);
        await using var destination = new MemoryStream();

        var receiveTask = receiver.ReceiveAsync(destination, payload.Length,
            cancellationToken: timeout.Token).AsTask();
        await sender.SendAsync(source, payload.Length, cancellationToken: timeout.Token);
        await receiveTask;
        Assert.True(payload.SequenceEqual(destination.ToArray()));
    }

    private static async ValueTask SecureSendTransfersAsync()
    {
        try
        {
            var payload = Enumerable.Range(0, 128_000).Select(index => (byte)(index % 223)).ToArray();
            const int offset = 23_456;
            await using var listener = DccFileSendListener.Start(
                IPAddress.Loopback, DccPortRange.Random, secure: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var accepting = listener.AcceptAsync(timeout.Token).AsTask();
            await using var receiver = await DccFileReceiveTransport.ConnectAsync(
                "127.0.0.1", listener.Port, timeout.Token, secure: true);
            await using var sender = await accepting;
            await using var source = new MemoryStream(payload);
            await using var destination = new MemoryStream();
            await destination.WriteAsync(payload.AsMemory(0, offset), timeout.Token);

            var receiveTask = receiver.ReceiveAsync(
                destination,
                payload.Length,
                initialOffset: offset,
                cancellationToken: timeout.Token).AsTask();
            await sender.SendAsync(
                source,
                payload.Length,
                initialOffset: offset,
                cancellationToken: timeout.Token);
            await receiveTask;

            Assert.True(sender.IsSecure);
            Assert.True(receiver.IsSecure);
            Assert.True(sender.SecurityProtocol is "Tls12" or "Tls13");
            Assert.True(receiver.SecurityProtocol is "Tls12" or "Tls13");
            Assert.True(!string.IsNullOrWhiteSpace(receiver.PeerCertificateFingerprint));
            Assert.True(payload.SequenceEqual(destination.ToArray()));
        }
        catch (AuthenticationException exception) when (IsSchannelKeyStoreUnavailable(exception))
        {
            throw new TestSkippedException(
                "Windows Schannel cannot access its per-user test key store in this sandbox. This test passes in a normal Windows process.");
        }
    }

    private static async ValueTask SecurePassiveSendReversesTransportAsync()
    {
        try
        {
            var payload = Enumerable.Range(0, 96_000).Select(index => (byte)(index % 211)).ToArray();
            await using var listener = DccFileReceiveListener.Start(
                IPAddress.Loopback, DccPortRange.Random, secure: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var accepting = listener.AcceptAsync(timeout.Token).AsTask();
            await using var sender = await DccFileSendTransport.ConnectAsync(
                "127.0.0.1", listener.Port, timeout.Token, secure: true);
            await using var receiver = await accepting;
            await using var source = new MemoryStream(payload);
            await using var destination = new MemoryStream();

            var receiveTask = receiver.ReceiveAsync(
                destination, payload.Length, cancellationToken: timeout.Token).AsTask();
            await sender.SendAsync(source, payload.Length, cancellationToken: timeout.Token);
            await receiveTask;

            Assert.True(sender.IsSecure);
            Assert.True(receiver.IsSecure);
            Assert.True(!string.IsNullOrWhiteSpace(sender.PeerCertificateFingerprint));
            Assert.True(payload.SequenceEqual(destination.ToArray()));
        }
        catch (AuthenticationException exception) when (IsSchannelKeyStoreUnavailable(exception))
        {
            throw new TestSkippedException(
                "Windows Schannel cannot access its per-user test key store in this sandbox. This test passes in a normal Windows process.");
        }
    }

    private static void DownloadStoreAvoidsCollisions()
    {
        var root = Path.Combine(Path.GetTempPath(), "clircs-dcc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "test file.txt"), "existing");
            var store = new DccDownloadStore(root);
            var target = store.CreatePartial("test file.txt");
            using (var stream = store.OpenPartial(target))
            {
                var bytes = Encoding.UTF8.GetBytes("received");
                stream.Write(bytes);
            }
            var completed = store.Complete(target);
            Assert.Equal("test file (1).txt", Path.GetFileName(completed));
            Assert.Equal("existing", File.ReadAllText(Path.Combine(root, "test file.txt")));
            Assert.Equal("received", File.ReadAllText(completed));
            Assert.Throws<ArgumentException>(() => store.CreatePartial("..\\bad.txt"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void DownloadStoreFindsPartialResume()
    {
        var root = Path.Combine(Path.GetTempPath(), "clircs-dcc-resume-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new DccDownloadStore(root);
            var original = store.CreatePartial(
                "large file.bin",
                new DccDownloadIdentity("EFNet", "slak", 50_000));
            using (var stream = store.OpenPartial(original))
            {
                stream.Write(new byte[12_345]);
            }

            var resumed = store.FindResumeTarget("large file.bin", 50_000, "EFNet", "slak");
            Assert.True(resumed is not null);
            Assert.Equal(12_345L, resumed!.InitialOffset);
            using (var stream = store.OpenPartial(resumed))
            {
                Assert.Equal(12_345L, stream.Position);
                stream.Write(new byte[50_000 - 12_345]);
            }
            var completed = store.Complete(resumed);
            Assert.Equal("large file.bin", Path.GetFileName(completed));
            Assert.Equal(50_000L, new FileInfo(completed).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void DownloadStoreScopesPartialResume()
    {
        var root = Path.Combine(Path.GetTempPath(), "clircs-dcc-scope-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new DccDownloadStore(root);
            var target = store.CreatePartial(
                "same.bin",
                new DccDownloadIdentity("EFNet", "alice", 10_000));
            using (var stream = store.OpenPartial(target)) stream.Write(new byte[1_000]);

            Assert.True(store.FindResumeTarget("same.bin", 10_000, "EFNet", "alice") is not null);
            Assert.True(store.FindResumeTarget("same.bin", 10_000, "EFNet", "bob") is null);
            Assert.True(store.FindResumeTarget("same.bin", 10_000, "Libera.Chat", "alice") is null);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void DownloadStoreRejectsChangedPartial()
    {
        var root = Path.Combine(Path.GetTempPath(), "clircs-dcc-change-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new DccDownloadStore(root);
            var target = store.CreatePartial(
                "changing.bin",
                new DccDownloadIdentity("EFNet", "alice", 10_000));
            using (var stream = store.OpenPartial(target)) stream.Write(new byte[1_000]);
            var resumed = store.FindResumeTarget("changing.bin", 10_000, "EFNet", "alice")!;
            using (var stream = new FileStream(resumed.PartialPath, FileMode.Append, FileAccess.Write))
                stream.WriteByte(1);

            Assert.Throws<IOException>(() => store.OpenPartial(resumed));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
