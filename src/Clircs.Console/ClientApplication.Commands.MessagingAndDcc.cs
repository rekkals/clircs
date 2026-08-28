using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Clircs.Commands;
using Clircs.Dcc;
using Clircs.Networking;
using Clircs.Sessions;
using Clircs.State;
using Clircs.Transport;

namespace Clircs.ConsoleClient;

// Owns message, notice, CTCP, and DCC commands and their presentation.
internal sealed partial class ClientApplication
{
    private async ValueTask<CommandResult> MessageAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (!TrySplitFirst(input.RawArguments, out var target, out var text))
        {
            return CommandResult.Failure("Usage: /msg <target> <message>");
        }

        var session = RequireSession(out var failure);
        if (session is null)
        {
            return failure;
        }

        await session.SendMessageAsync(target, text, cancellationToken, createQueryBuffer: false);
        session.State.TryGetBuffer(target, out var destination);
        EchoInActiveBuffer(
            session,
            SessionEventKind.Message,
            $"-> {target}: {text}",
            destination?.Id);

        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> NoticeAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (!TrySplitFirst(input.RawArguments, out var target, out var text))
        {
            return CommandResult.Failure("Usage: /notice <target> <message>");
        }

        var session = RequireSession(out var failure);
        if (session is null)
        {
            return failure;
        }

        await session.SendNoticeAsync(target, text, cancellationToken);
        var destination = session.Features.IsChannel(target) && session.State.TryGetBuffer(target, out var existing)
            ? existing!
            : session.State.StatusBuffer;
        EchoInActiveBuffer(
            session,
            SessionEventKind.Notice,
            $"->{target}<- {text}",
            destination.Id);

        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> ServiceAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        if (!ServiceTargets.TryGetValue(input.Name, out var target))
        {
            return CommandResult.Failure($"Unknown service command: /{input.Name}");
        }

        var session = RequireSession(out var failure);
        if (session is null) return failure;

        var text = input.RawArguments.Trim();
        var localText = text;
        if (input.Name.Equals("nickserv", StringComparison.OrdinalIgnoreCase) &&
            input.Arguments.Count > 0 &&
            input.Arguments[0].Equals("identify", StringComparison.OrdinalIgnoreCase))
        {
            if (input.Arguments.Count == 1)
            {
                var password = _presenter.ReadSecret("NickServ password: ");
                if (string.IsNullOrEmpty(password)) return CommandResult.Failure("NickServ identification canceled");
                text = $"identify {password}";
                localText = "identify ********";
            }
            else
            {
                localText = MaskServiceCommand(input.Name, text, input.Arguments);
            }
        }

        if (text.Length == 0)
        {
            return CommandResult.Failure($"Usage: /{input.Name} <command> [arguments]");
        }

        await session.SendMessageAsync(target, text, cancellationToken, createQueryBuffer: false);
        session.State.TryGetBuffer(target, out var destination);
        EchoInActiveBuffer(session, SessionEventKind.Message, $"-> {target}: {localText}", destination?.Id);
        return CommandResult.Success();
    }

    internal static string MaskServiceCommand(
        string service,
        string text,
        IReadOnlyList<string> arguments)
    {
        if (!service.Equals("nickserv", StringComparison.OrdinalIgnoreCase) ||
            arguments.Count < 2 ||
            !arguments[0].Equals("identify", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }
        var password = arguments[^1];
        return text[..Math.Max(0, text.Length - password.Length)] + "********";
    }

    private ValueTask<CommandResult> SayCommandAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SayAsync(input.RawArguments, cancellationToken);

    private async ValueTask<CommandResult> SayAsync(string text, CancellationToken cancellationToken)
    {
        if (text.Length == 0)
        {
            return CommandResult.Success();
        }

        var activeBuffer = ActiveBuffer();
        if (activeBuffer?.Kind == BufferKind.DccChat)
        {
            return await SendDccChatAsync(activeBuffer, text, cancellationToken);
        }

        var session = RequireSession(out var failure);
        var target = ActiveTarget();
        if (session is null)
        {
            return failure;
        }

        if (target is null)
        {
            return CommandResult.Failure("Switch to a channel or query before sending text.");
        }

        await session.SendMessageAsync(target, text, cancellationToken);
        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> MeAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        var activeBuffer = ActiveBuffer();
        if (activeBuffer?.Kind == BufferKind.DccChat)
        {
            return input.RawArguments.Length == 0
                ? CommandResult.Failure("Usage in a DCC chat: /me <action>")
                : await SendDccChatAsync(activeBuffer, $"\u0001ACTION {input.RawArguments}\u0001", cancellationToken);
        }
        var session = RequireSession(out var failure);
        var target = ActiveTarget();
        if (session is null)
        {
            return failure;
        }

        if (target is null || input.RawArguments.Length == 0)
        {
            return CommandResult.Failure("Usage in a channel/query: /me <action>");
        }

        await session.SendActionAsync(target, input.RawArguments, cancellationToken);
        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> DescribeAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (!TrySplitFirst(input.RawArguments, out var target, out var text))
        {
            return CommandResult.Failure("Usage: /describe <target> <action>");
        }

        var session = RequireSession(out var failure);
        if (session is null)
        {
            return failure;
        }

        await session.SendActionAsync(target, text, cancellationToken);
        return CommandResult.Success();
    }

    private ValueTask<CommandResult> AllChannelActionAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SendToAllChannelsAsync(input.RawArguments, action: true, cancellationToken);

    private ValueTask<CommandResult> AllChannelMessageAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SendToAllChannelsAsync(input.RawArguments, action: false, cancellationToken);

    private async ValueTask<CommandResult> SendToAllChannelsAsync(string text, bool action, CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        if (session is null)
        {
            return failure;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return CommandResult.Failure($"Usage: /{(action ? "ame" : "amsg")} <text>");
        }

        var channels = session.State.Channels.Select(channel => channel.Name).ToArray();
        if (channels.Length == 0)
        {
            return CommandResult.Failure("You are not joined to any channels on this network.");
        }

        foreach (var channel in channels)
        {
            if (action)
            {
                await session.SendActionAsync(channel, text, cancellationToken);
            }
            else
            {
                await session.SendMessageAsync(channel, text, cancellationToken);
            }
        }

        return CommandResult.Success($"Sent to {channels.Length} channel(s) on {session.State.DisplayName}.");
    }

    private async ValueTask<CommandResult> QueryAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        if (session is null)
        {
            return failure;
        }

        if (input.Arguments.Count == 0)
        {
            return CommandResult.Failure("Usage: /query <nick> [message]");
        }

        var buffer = session.State.GetOrCreateBuffer(BufferKind.Query, input.Arguments[0]);
        SwitchTo(session, buffer);
        if (TrySplitFirst(input.RawArguments, out _, out var message))
        {
            await session.SendMessageAsync(buffer.Name, message, cancellationToken);
        }

        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> CtcpAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (input.Arguments.Count < 2)
        {
            return CommandResult.Failure("Usage: /ctcp <nick> <command> [arguments]");
        }

        var session = RequireSession(out var failure);
        if (session is null)
        {
            return failure;
        }

        var command = input.Arguments[1].ToUpperInvariant();
        if (command.Any(char.IsWhiteSpace) || command.IndexOfAny(['\r', '\n', '\0', '\u0001']) >= 0)
        {
            return CommandResult.Failure("Invalid CTCP command.");
        }

        var payload = input.Arguments.Count == 2
            ? command == "PING" ? $"PING {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" : command
            : $"{command} {string.Join(' ', input.Arguments.Skip(2))}";
        TrackOutputRequest(session, "ctcp");
        try
        {
            await session.SendAsync("PRIVMSG", [input.Arguments[0], $"\u0001{payload}\u0001"], cancellationToken: cancellationToken);
        }
        catch
        {
            CancelOutputRequest(session, "ctcp");
            throw;
        }
        return CommandResult.Success($"CTCP {command} sent to {input.Arguments[0]}.");
    }

    private async ValueTask<CommandResult> XdccAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        const string usage = "Usage: /xdcc <get|sget> <bot> <pack>";
        if (input.Arguments.Count != 3 ||
            !TryBuildXdccRequest(input.Arguments[0], input.Arguments[2], out var request, out var pack))
        {
            return CommandResult.Failure(usage);
        }

        var session = RequireSession(out var failure);
        if (session is null)
        {
            return failure;
        }

        var bot = input.Arguments[1];
        await session.SendMessageAsync(bot, request, cancellationToken, createQueryBuffer: false);
        var transferCommand = input.Arguments[0].Equals("sget", StringComparison.OrdinalIgnoreCase)
            ? "SSEND"
            : "SEND";
        return CommandResult.Success($"XDCC {transferCommand} request sent to {bot} for pack {pack}");
    }

    internal static bool TryBuildXdccRequest(
        string operation,
        string packArgument,
        out string request,
        out string normalizedPack)
    {
        request = string.Empty;
        normalizedPack = string.Empty;

        var transferCommand = operation.ToLowerInvariant() switch
        {
            "get" => "SEND",
            "sget" => "SSEND",
            _ => null
        };
        if (transferCommand is null)
        {
            return false;
        }

        var digits = packArgument.StartsWith('#') ? packArgument[1..] : packArgument;
        if (!uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var packNumber) ||
            packNumber == 0)
        {
            return false;
        }

        normalizedPack = $"#{packNumber.ToString(CultureInfo.InvariantCulture)}";
        request = $"XDCC {transferCommand} {normalizedPack}";
        return true;
    }

    private async ValueTask<CommandResult> DccAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        var operation = input.Arguments.Count == 0 ? "list" : input.Arguments[0].ToLowerInvariant();
        if (operation is "chat" or "schat")
        {
            if (input.Arguments.Count < 2)
            {
                return CommandResult.Failure($"Usage: /dcc {operation} <nick> [--passive]");
            }
            var options = input.Arguments.Skip(2).Select(option => option.ToLowerInvariant()).ToArray();
            if (options.Any(option => option != "--passive") ||
                options.Distinct(StringComparer.Ordinal).Count() != options.Length)
            {
                return CommandResult.Failure($"Usage: /dcc {operation} <nick> [--passive]");
            }
            var passive = options.Contains("--passive", StringComparer.Ordinal);
            var secure = operation == "schat";
            return await StartOutgoingDccChatAsync(input.Arguments[1], passive, secure, cancellationToken);
        }

        if (operation is "send" or "ssend")
        {
            if (input.Arguments.Count < 3)
            {
                return CommandResult.Failure($"Usage: /dcc {operation} <nick> <file> [--passive]");
            }
            var pathArguments = input.Arguments.Skip(2).ToList();
            var options = new List<string>();
            while (pathArguments.Count > 0 && pathArguments[^1].StartsWith("--", StringComparison.Ordinal))
            {
                options.Insert(0, pathArguments[^1].ToLowerInvariant());
                pathArguments.RemoveAt(pathArguments.Count - 1);
            }
            if (options.Any(option => option != "--passive") ||
                options.Distinct(StringComparer.Ordinal).Count() != options.Count ||
                pathArguments.Count == 0)
            {
                return CommandResult.Failure($"Usage: /dcc {operation} <nick> <file> [--passive]");
            }
            var passive = options.Contains("--passive", StringComparer.Ordinal);
            var secure = operation == "ssend";
            return await StartOutgoingDccSendAsync(
                input.Arguments[1],
                string.Join(' ', pathArguments),
                passive,
                secure,
                cancellationToken);
        }

        if (operation == "list")
        {
            if (input.Arguments.Count > 1)
            {
                return CommandResult.Failure(DccUsage());
            }

            var activeSession = ActiveSession();
            if (activeSession is not null) SwitchTo(activeSession, DccBuffer(activeSession));
            var requests = _dcc.Requests.Snapshot()
                .Where(request => !DccRequestRegistry.IsTerminal(request.State))
                .ToArray();
            if (requests.Length == 0)
            {
                return CommandResult.Success(new PresentationBlock("DCC Requests", Summary: "No DCC requests."));
            }

            var rows = requests.Select(request => (IReadOnlyList<string>)
            [
                request.Id.ToString(CultureInfo.InvariantCulture),
                request.Network,
                request.Direction == DccRequestDirection.Incoming ? "in" : "out",
                DccProtocolName(request.Offer),
                request.Sender,
                DccItem(request),
                DccEndpoint(request.Offer),
                DccStateText(request.State)
            ]).ToArray();
            return CommandResult.Success(new PresentationBlock(
                "DCC Requests",
                Table: new PresentationTable(
                    ["ID", "Network", "Dir", "Type", "Peer", "Item", "Endpoint", "State"],
                    rows,
                    new HashSet<int> { 0, 2, 3, 7 })));
        }

        if (input.Arguments.Count != 2 || !int.TryParse(input.Arguments[1], NumberStyles.None,
                CultureInfo.InvariantCulture, out var id) || id < 1)
        {
            return CommandResult.Failure(DccUsage());
        }
        if (!_dcc.Requests.TryGet(id, out var request))
        {
            return CommandResult.Failure($"No DCC request has ID {id}.");
        }

        if (operation == "show")
        {
            if (FindSession(request!.NetworkSessionId) is { } requestSession)
            {
                SwitchTo(requestSession, DccBuffer(requestSession));
            }
            return CommandResult.Success(DccRequestPresentation(request!, detailed: true));
        }

        if (operation == "accept")
        {
            if (request!.Direction != DccRequestDirection.Incoming)
            {
                return CommandResult.Failure($"DCC request #{id} is outgoing and cannot be accepted locally.");
            }
            if (request!.State != DccRequestState.Pending)
            {
                return CommandResult.Failure(
                    $"DCC request #{id} is {DccStateText(request.State)}, not pending.");
            }
            if (request.Offer.Type == DccRequestType.Send)
            {
                if (request.Offer.IsPassiveRequest) return await StartIncomingPassiveDccSendAsync(request, cancellationToken);
                return StartIncomingDccSend(request);
            }
            if (request.Offer.IsPassiveRequest) return await AcceptPassiveDccChatAsync(request, cancellationToken);
            return await AcceptDccChatAsync(request, cancellationToken);
        }

        if (operation == "resume")
        {
            if (request!.Direction != DccRequestDirection.Incoming || request.Offer.Type != DccRequestType.Send)
            {
                return CommandResult.Failure($"DCC request #{id} is not an incoming file transfer");
            }
            if (request.State != DccRequestState.Pending)
            {
                return CommandResult.Failure($"DCC request #{id} is {DccStateText(request.State)}, not pending");
            }
            return await RequestDccResumeAsync(request, cancellationToken);
        }

        if (operation is "reject" or "cancel")
        {
            if (operation == "cancel" && request!.Offer.Type == DccRequestType.Chat &&
                (request.Direction == DccRequestDirection.Outgoing ||
                 request.State is DccRequestState.Connecting or DccRequestState.Connected))
            {
                return await CancelDccChatAsync(request);
            }
            if (operation == "cancel" && request!.Offer.Type == DccRequestType.Send &&
                request.State is DccRequestState.Pending or DccRequestState.Connecting or DccRequestState.Connected)
            {
                var direction = request.Direction == DccRequestDirection.Incoming ? "incoming" : "outgoing";
                if (!_dcc.Requests.TryTransition(id, DccRequestState.Cancelled,
                        $"The {direction} DCC SEND was canceled locally.", out var cancelled))
                {
                    return CommandResult.Failure($"DCC request #{id} can no longer be canceled.");
                }
                CancelDccExpiration(id);
                CancelDccTransfer(id);
                if (request.State == DccRequestState.Pending && request.Offer.IsPassiveRequest)
                    CleanupUnstartedPassiveSend(id);
                var peer = cancelled!.Direction == DccRequestDirection.Incoming ? "from" : "to";
                PublishDccState(cancelled!,
                    $"DCC {DccProtocolName(cancelled.Offer)} #{id} {peer} {cancelled.Sender} was canceled");
                return CommandResult.Success();
            }
            if (operation == "reject" && request!.Direction != DccRequestDirection.Incoming)
            {
                return CommandResult.Failure($"DCC request #{id} is outgoing. Use /dcc cancel {id}.");
            }
            if (operation == "cancel" && request!.Direction != DccRequestDirection.Outgoing)
            {
                return CommandResult.Failure($"DCC request #{id} is incoming. Use /dcc reject {id}.");
            }
            var state = operation == "reject" ? DccRequestState.Rejected : DccRequestState.Cancelled;
            if (!_dcc.Requests.TryTransition(id, state,
                    state == DccRequestState.Rejected
                        ? "The request was rejected locally."
                        : "The request was canceled locally.", out var updated))
            {
                return CommandResult.Failure(
                    $"DCC request #{id} is {DccStateText(request!.State)} and cannot be {DccStateText(state)}");
            }
            CancelDccExpiration(id);
            CancelPendingDccResume(id);
            await StopDccChatListenerAsync(id);
            if (state == DccRequestState.Cancelled)
            {
                var canceled = updated!;
                PublishDccChatStatus(canceled,
                    $"DCC {DccProtocolName(canceled.Offer)} request #{id} to {canceled.Sender} was canceled");
            }
            PublishDccState(updated!, $"DCC request #{id} with {updated!.Sender} was {DccStateText(state)}");
            return CommandResult.Success();
        }

        return CommandResult.Failure(DccUsage());
    }

    private async ValueTask<CommandResult> CancelDccChatAsync(DccRequest request)
    {
        var protocol = DccProtocolName(request.Offer);
        if (request.State == DccRequestState.Connected)
        {
            await EndDccChatAsync(
                request.Id,
                DccRequestState.Cancelled,
                $"DCC {protocol} closed locally");
            return CommandResult.Success();
        }

        if (!_dcc.Requests.TryTransition(
                request.Id,
                DccRequestState.Cancelled,
                $"The DCC {protocol} request was canceled locally",
                out var cancelled))
        {
            return CommandResult.Failure(
                $"DCC request #{request.Id} is {DccStateText(request.State)} and cannot be canceled");
        }

        CancelDccExpiration(request.Id);
        CancelDccChatConnection(request.Id);
        await StopDccChatListenerAsync(request.Id);
        PublishDccChatStatus(cancelled!,
            $"DCC {protocol} request #{request.Id} with {request.Sender} was canceled");
        PublishDccState(cancelled!,
            $"DCC {protocol} request #{request.Id} with {request.Sender} was canceled");
        return CommandResult.Success();
    }

    private static string DccUsage() =>
        "Usage: /dcc [chat|schat|send|ssend|list|show|accept|resume|reject|cancel]";

    private static string DccStateText(DccRequestState state) =>
        state == DccRequestState.Cancelled ? "canceled" : state.ToString().ToLowerInvariant();

    internal static PresentationBlock DccRequestPresentation(DccRequest request, bool detailed = false)
    {
        var peerLabel = request.Direction == DccRequestDirection.Incoming ? "From" : "To";
        var fields = new List<PresentationField> { new(peerLabel, request.Sender) };
        if (request.Offer.Filename is { } filename) fields.Add(new PresentationField("File", filename));
        if (request.Offer.Size is { } size) fields.Add(new PresentationField("Size", FormatFileSize(size)));
        fields.Add(new PresentationField("Address", request.Offer.Address));
        fields.Add(request.Offer.IsPassive
            ? new PresentationField("Port", "passive")
            : new PresentationField("Port", request.Offer.Port.ToString(CultureInfo.InvariantCulture)));
        if (request.Offer.PassiveToken is { } token) fields.Add(new PresentationField("Token", token));
        fields.Add(new PresentationField("Status", DccStateText(request.State)));
        fields.Add(new PresentationField("Secure", request.Offer.IsSecure ? "yes" : "no"));
        if (request.State == DccRequestState.Pending)
        {
            var use = request.Direction == DccRequestDirection.Incoming
                ? request.Offer.Type == DccRequestType.Send
                    ? $"/dcc accept {request.Id}, /dcc resume {request.Id}, or /dcc reject {request.Id}"
                    : $"/dcc accept {request.Id} or /dcc reject {request.Id}"
                : $"/dcc cancel {request.Id}";
            fields.Add(new PresentationField("Use", use));
        }
        if (detailed)
        {
            fields.Add(new PresentationField("Direction", request.Direction.ToString().ToLowerInvariant()));
            fields.Add(new PresentationField("Network", request.Network));
            fields.Add(new PresentationField("Received",
                request.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            if (request.State == DccRequestState.Pending)
            {
                fields.Add(new PresentationField("Expires",
                    request.ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            }
        }
        if (request.StateReason is { } reason) fields.Add(new PresentationField("Reason", reason));
        return new PresentationBlock(
            $"DCC {DccProtocolName(request.Offer)}:",
            fields,
            TitleHighlight: $"#{request.Id}");
    }

    private static string DccItem(DccRequest request) => request.Offer.Type == DccRequestType.Send
        ? $"{request.Offer.Filename} ({FormatFileSize(request.Offer.Size ?? 0)})"
        : "chat";

    private static string DccProtocolName(DccOffer offer) => offer switch
    {
        { Type: DccRequestType.Chat, IsSecure: true } => "SCHAT",
        { Type: DccRequestType.Send, IsSecure: true } => "SSEND",
        { Type: DccRequestType.Chat } => "CHAT",
        _ => "SEND"
    };

    private static string DccEndpoint(DccOffer offer)
    {
        var address = offer.Address.Contains(':')
            ? $"[{offer.Address}]"
            : offer.Address;
        return offer.IsPassive ? $"{address}:passive" : $"{address}:{offer.Port}";
    }

    private static BufferState DccBuffer(IrcNetworkSession session) =>
        session.State.GetOrCreateBuffer(BufferKind.Results, "=dcc");

    private async ValueTask<CommandResult> RequestDccResumeAsync(
        DccRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Offer.Filename is not { } filename || request.Offer.Size is not { } size)
            return CommandResult.Failure($"DCC {DccProtocolName(request.Offer)} request #{request.Id} is incomplete");
        if (FindSession(request.NetworkSessionId) is not { } session ||
            session.ConnectionState != IrcConnectionState.Online)
            return CommandResult.Failure("Not connected to a server");

        var store = new DccDownloadStore(_preferences.DccDownloads);
        DccDownloadTarget? target;
        try
        {
            target = store.FindResumeTarget(filename, size, request.Network, request.Sender);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return CommandResult.Failure($"Could not inspect the DCC download: {exception.Message}");
        }
        if (target is null)
            return CommandResult.Failure($"No incomplete {filename} download was found in {_preferences.DccDownloads}");

        if (!_dcc.TryBeginResume(request.Id, new PendingDccResume(target, target.InitialOffset)))
            return CommandResult.Failure($"DCC RESUME request #{request.Id} is already pending");

        var port = request.Offer.IsPassiveRequest ? 0 : request.Offer.Port;
        var payload = DccResumeParser.Format(
            DccResumeOperation.Resume,
            filename,
            port,
            target.InitialOffset,
            request.Offer.PassiveToken);
        try
        {
            SwitchTo(session, DccBuffer(session));
            PublishDccState(request,
                $"DCC RESUME request #{request.Id} sent to {request.Sender} at {FormatFileSize(target.InitialOffset)}");
            await session.SendAsync("PRIVMSG", [request.Sender, $"\u0001{payload}\u0001"],
                cancellationToken: cancellationToken);
            return CommandResult.Success();
        }
        catch
        {
            _dcc.ClearPendingResume(request.Id);
            throw;
        }
    }

    private CommandResult StartIncomingDccSend(DccRequest request, DccDownloadTarget? resumeTarget = null)
    {
        if (request.Offer.Filename is not { } filename || request.Offer.Size is not { } size)
        {
            return CommandResult.Failure(
                $"DCC {DccProtocolName(request.Offer)} request #{request.Id} is missing its filename or size.");
        }

        var store = new DccDownloadStore(_preferences.DccDownloads);
        DccDownloadTarget target;
        try
        {
            target = resumeTarget ?? store.CreatePartial(
                filename,
                new DccDownloadIdentity(request.Network, request.Sender, size));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return CommandResult.Failure($"Could not prepare the DCC download: {exception.Message}");
        }

        if (!_dcc.Requests.TryTransition(
                request.Id,
                DccRequestState.Connecting,
                "Connecting to the offered DCC SEND endpoint.",
                out var connecting))
        {
            if (!target.IsResume) DccDownloadStore.Discard(target);
            return CommandResult.Failure($"DCC request #{request.Id} is no longer pending.");
        }

        var transfer = new ActiveDccTransfer(
            request.Id,
            target,
            CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token),
            InitialOffset: target.InitialOffset);
        _dcc.SetTransfer(request.Id, transfer);
        CancelDccExpiration(request.Id);
        if (FindSession(request.NetworkSessionId) is { } session)
        {
            SwitchTo(session, DccBuffer(session));
        }
        PublishDccState(connecting!,
            target.IsResume
                ? $"Resuming DCC {DccProtocolName(request.Offer)} #{request.Id} from {request.Sender} at {FormatFileSize(target.InitialOffset)}"
                : $"Connecting to DCC {DccProtocolName(request.Offer)} #{request.Id} from {request.Sender}: " +
                  $"{filename} ({FormatFileSize(size)})");
        _dcc.TrackTask(request.Id, RunIncomingDccSendAsync(connecting!, transfer, store));
        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> StartIncomingPassiveDccSendAsync(
        DccRequest request,
        CancellationToken cancellationToken,
        DccDownloadTarget? resumeTarget = null)
    {
        if (request.Offer.Filename is not { } filename || request.Offer.Size is not { } size ||
            request.Offer.PassiveToken is not { } token)
        {
            return CommandResult.Failure($"DCC {DccProtocolName(request.Offer)} request #{request.Id} is incomplete");
        }
        if (FindSession(request.NetworkSessionId) is not { } session ||
            session.ConnectionState != IrcConnectionState.Online)
        {
            return CommandResult.Failure("Not connected to a server");
        }

        DccFileReceiveListener? listener = null;
        DccDownloadTarget? target = null;
        ActiveDccTransfer? transfer = null;
        try
        {
            var address = await DccAddressSelector.SelectAdvertisedAddressAsync(
                _preferences.DccAddress, session.State.VisibleHost, session.Options.Endpoint.Host,
                session.Options.Endpoint.Port, cancellationToken);
            listener = DccFileReceiveListener.Start(address, _preferences.DccPorts, request.Offer.IsSecure);
            var store = new DccDownloadStore(_preferences.DccDownloads);
            target = resumeTarget ?? store.CreatePartial(
                filename,
                new DccDownloadIdentity(request.Network, request.Sender, size));
            var wireFilename = filename.Any(char.IsWhiteSpace) ? $"\"{filename}\"" : filename;
            var addressToken = DccAddressSelector.ToDccAddressToken(address);
            var payload = $"DCC {(request.Offer.IsSecure ? "SSEND" : "SEND")} {wireFilename} " +
                $"{addressToken} {listener.Port} {size} {token}";
            var responseOffer = new DccOffer(
                DccRequestType.Send, filename, address.ToString(), listener.Port, size, token, payload,
                request.Offer.IsSecure);
            if (!_dcc.Requests.TryTransitionWithOffer(request.Id, DccRequestState.Connecting,
                    responseOffer, $"Waiting for the passive DCC {DccProtocolName(request.Offer)} sender",
                    out var connecting))
            {
                if (!target.IsResume) DccDownloadStore.Discard(target);
                await listener.DisposeAsync();
                return CommandResult.Failure($"DCC request #{request.Id} is no longer pending");
            }

            transfer = new ActiveDccTransfer(
                request.Id, target, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token), listener,
                target.InitialOffset);
            _dcc.SetTransfer(request.Id, transfer);
            CancelDccExpiration(request.Id);
            SwitchTo(session, DccBuffer(session));
            PublishDccState(connecting!,
                target.IsResume
                    ? $"Waiting to resume passive DCC {DccProtocolName(request.Offer)} #{request.Id} from " +
                      $"{request.Sender} at {FormatFileSize(target.InitialOffset)}"
                    : $"Waiting for passive DCC {DccProtocolName(request.Offer)} #{request.Id} from " +
                      $"{request.Sender}: {filename} ({FormatFileSize(size)})");
            await session.SendAsync("PRIVMSG", [request.Sender, $"\u0001{payload}\u0001"],
                cancellationToken: cancellationToken);
            _dcc.TrackTask(request.Id, RunIncomingDccSendAsync(connecting!, transfer, store));
            return CommandResult.Success();
        }
        catch (Exception exception) when (exception is SocketException or IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or NotSupportedException)
        {
            if (target is not null && !target.IsResume) DccDownloadStore.Discard(target);
            if (listener is not null) await listener.DisposeAsync();
            if (transfer is not null)
            {
                _dcc.ClearTransfer(request.Id, transfer);
                transfer.Lifetime.Cancel();
                transfer.Lifetime.Dispose();
            }
            _dcc.Requests.TryTransition(request.Id, DccRequestState.Failed, exception.Message, out _);
            return CommandResult.Failure(
                $"Could not accept passive DCC {DccProtocolName(request.Offer)}: {exception.Message}");
        }
    }

    private async Task RunIncomingDccSendAsync(
        DccRequest request,
        ActiveDccTransfer transfer,
        DccDownloadStore store)
    {
        try
        {
            DccFileReceiveTransport transport;
            using (var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(transfer.Lifetime.Token))
            {
                connectionTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    transport = transfer.Listener is null
                        ? await DccFileReceiveTransport.ConnectAsync(
                            request.Offer.Address, request.Offer.Port, connectionTimeout.Token,
                            request.Offer.IsSecure)
                        : await transfer.Listener.AcceptAsync(connectionTimeout.Token);
                }
                catch (OperationCanceledException) when (!transfer.Lifetime.IsCancellationRequested)
                {
                    throw new TimeoutException($"The DCC {DccProtocolName(request.Offer)} connection timed out.");
                }
            }

            await using (transport)
            {
                if (!_dcc.Requests.TryTransition(request.Id, DccRequestState.Connected, null, out var connected))
                {
                    return;
                }
                PublishDccState(connected!,
                    $"Receiving {request.Offer.Filename} from {request.Sender} " +
                    $"({FormatFileSize(request.Offer.Size ?? 0)}){DccSecurityDetails(transport)}");

                await using var destination = store.OpenPartial(transfer.Target);
                var lastReport = TimeSpan.Zero;
                await transport.ReceiveAsync(
                    destination,
                    request.Offer.Size ?? 0,
                    progress =>
                    {
                        if (progress.BytesReceived != progress.TotalBytes &&
                            progress.Elapsed - lastReport < TimeSpan.FromSeconds(1))
                        {
                            return;
                        }
                        lastReport = progress.Elapsed;
                        PublishDccTransferProgress(request, progress);
                    },
                    initialOffset: transfer.InitialOffset,
                    cancellationToken: transfer.Lifetime.Token);
            }

            string? completedPath = null;
            if (_dcc.Requests.TryTransitionAfter(
                    request.Id,
                    DccRequestState.Completed,
                    () => completedPath = store.Complete(transfer.Target),
                    out var completed))
            {
                PublishDccState(completed!,
                    $"Received {request.Offer.Filename} from {request.Sender} ({FormatFileSize(request.Offer.Size ?? 0)}): {completedPath}");
            }
        }
        catch (OperationCanceledException) when (transfer.Lifetime.IsCancellationRequested)
        {
            if (_dcc.Requests.TryTransition(request.Id, DccRequestState.Cancelled,
                    "The incoming DCC SEND was canceled.", out var cancelled))
            {
                PublishDccState(cancelled!,
                    $"DCC {DccProtocolName(request.Offer)} #{request.Id} from {request.Sender} was canceled");
            }
        }
        catch (Exception exception) when (exception is SocketException or IOException or UnauthorizedAccessException or
            TimeoutException or InvalidOperationException or ObjectDisposedException or
            System.Security.Authentication.AuthenticationException)
        {
            if (_dcc.Requests.TryTransition(request.Id, DccRequestState.Failed, exception.Message, out var failed))
            {
                PublishDccState(failed!,
                    $"DCC {DccProtocolName(request.Offer)} #{request.Id} failed: {DccConnectionError(exception)}");
            }
        }
        finally
        {
            if (transfer.Listener is not null) await transfer.Listener.DisposeAsync();
            DccDownloadStore.DiscardIfEmpty(transfer.Target);
            _dcc.ClearTransfer(request.Id, transfer);
            transfer.Lifetime.Dispose();
        }
    }

    private void PublishDccTransferProgress(DccRequest request, DccReceiveProgress progress)
    {
        if (FindSession(request.NetworkSessionId) is not { } session) return;
        var percentage = progress.TotalBytes == 0 ? 100 : progress.BytesReceived * 100d / progress.TotalBytes;
        var speed = FormatFileSize((long)Math.Max(0, progress.BytesPerSecond));
        OnSessionEvent(new SessionEvent(
            session.State.Id,
            DccBuffer(session).Id,
            SessionEventKind.Status,
            $"DCC {DccProtocolName(request.Offer)} #{request.Id}: {request.Offer.Filename} - " +
            $"{FormatFileSize(progress.BytesReceived)} of " +
            $"{FormatFileSize(progress.TotalBytes)} ({percentage:0}%, {speed}/s)",
            DateTimeOffset.Now,
            new Dictionary<string, string?>
            {
                ["event"] = "dcc.transfer.progress",
                ["dcc.id"] = request.Id.ToString(CultureInfo.InvariantCulture),
                ["dcc.state"] = "connected",
                ["dcc.bytes"] = progress.BytesReceived.ToString(CultureInfo.InvariantCulture),
                ["dcc.size"] = progress.TotalBytes.ToString(CultureInfo.InvariantCulture),
                ["history.transientKey"] = DccProgressHistoryKey(request.Id),
                ["history.replaceKey"] = DccProgressHistoryKey(request.Id),
                ["suppressActivity"] = "true"
            }));
    }

    private void CancelDccTransfer(int requestId)
    {
        var (transfer, outgoing) = _dcc.TransferHandles(requestId);
        DccCoordinator.CancelLifetime(transfer?.Lifetime);
        DccCoordinator.CancelLifetime(outgoing?.Lifetime);
    }

    private void CancelPendingDccResume(int requestId)
    {
        _dcc.ClearPendingResume(requestId);
    }

    private void CleanupUnstartedPassiveSend(int requestId)
    {
        var outgoing = _dcc.TakeUnstartedPassiveSend(requestId);
        if (outgoing is null) return;
        outgoing.Lifetime.Dispose();
    }

    private async ValueTask<CommandResult> StartOutgoingDccSendAsync(
        string nickname,
        string requestedPath,
        bool passive,
        bool secure,
        CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        if (session is null) return failure;
        if (session.ConnectionState != IrcConnectionState.Online)
        {
            return CommandResult.Failure("Not connected to a server.");
        }
        if (string.IsNullOrWhiteSpace(nickname) || nickname.Any(char.IsWhiteSpace) ||
            nickname.IndexOfAny(['\r', '\n', '\0', '\u0001']) >= 0)
        {
            return CommandResult.Failure($"Usage: /dcc {(secure ? "ssend" : "send")} <nick> <file> [--passive]");
        }

        string filePath;
        FileInfo file;
        try
        {
            filePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(requestedPath));
            file = new FileInfo(filePath);
            if (!file.Exists) return CommandResult.Failure($"File not found: {filePath}");
            if ((file.Attributes & FileAttributes.Directory) != 0)
            {
                return CommandResult.Failure("DCC SEND requires a file, not a directory.");
            }
            using var readable = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        {
            return CommandResult.Failure($"Could not open the DCC SEND file: {exception.Message}");
        }

        DccFileSendListener? listener = null;
        DccRequest? request = null;
        OutgoingDccSend? outgoing = null;
        try
        {
            var filename = file.Name;
            var wireFilename = filename.Any(char.IsWhiteSpace) ? $"\"{filename}\"" : filename;
            IPAddress address;
            string? token = null;
            int port;
            if (passive)
            {
                address = IPAddress.Parse("1.1.1.1");
                token = NewDccPassiveToken();
                port = 0;
            }
            else
            {
                address = await DccAddressSelector.SelectAdvertisedAddressAsync(
                    _preferences.DccAddress, session.State.VisibleHost, session.Options.Endpoint.Host,
                    session.Options.Endpoint.Port, cancellationToken);
                listener = DccFileSendListener.Start(address, _preferences.DccPorts, secure);
                port = listener.Port;
            }
            var addressToken = DccAddressSelector.ToDccAddressToken(address);
            var payload = $"DCC {(secure ? "SSEND" : "SEND")} {wireFilename} {addressToken} {port} {file.Length}" +
                (token is null ? string.Empty : $" {token}");
            var network = ProfileFor(session)?.DisplayName ?? session.Features.NetworkName ?? session.State.DisplayName;
            request = _dcc.Requests.Add(
                session.State.Id,
                network,
                nickname,
                new DccOffer(DccRequestType.Send, filename, address.ToString(), port, file.Length, token, payload,
                    secure),
                DateTimeOffset.Now,
                direction: DccRequestDirection.Outgoing);
            outgoing = new OutgoingDccSend(
                request.Id,
                filePath,
                file.Length,
                file.LastWriteTimeUtc,
                listener,
                CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
            _dcc.SetOutgoingSend(request.Id, outgoing);
            ScheduleDccExpiration(request);
            _dcc.PruneTerminalRuntimes();
            SwitchTo(session, DccBuffer(session));
            PublishDccState(request,
                $"DCC {DccProtocolName(request.Offer)} request #{request.Id} sent to {nickname}: {filename} " +
                $"({FormatFileSize(file.Length)}); waiting for connection");
            await session.SendAsync(
                "PRIVMSG",
                [nickname, $"\u0001{payload}\u0001"],
                cancellationToken: cancellationToken);
            if (!passive) _dcc.TrackTask(request.Id, RunOutgoingDccSendAsync(request, outgoing));
            return CommandResult.Success();
        }
        catch (Exception exception) when (exception is SocketException or IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or NotSupportedException or
            System.Security.Authentication.AuthenticationException)
        {
            if (outgoing is not null)
            {
                outgoing.Lifetime.Cancel();
                _dcc.ClearOutgoingSend(outgoing.RequestId, outgoing);
                if (outgoing.Listener is not null) await outgoing.Listener.DisposeAsync();
                outgoing.Lifetime.Dispose();
            }
            else if (listener is not null)
            {
                await listener.DisposeAsync();
            }
            if (request is not null)
            {
                CancelDccExpiration(request.Id);
                _dcc.Requests.TryTransition(request.Id, DccRequestState.Failed, exception.Message, out var failed);
                if (failed is not null)
                {
                    PublishDccState(failed,
                        $"DCC {DccProtocolName(request.Offer)} request #{request.Id} failed: {exception.Message}");
                }
            }
            return CommandResult.Failure($"Could not start DCC {(secure ? "SSEND" : "SEND")}: {exception.Message}");
        }
    }

    private async Task RunOutgoingDccSendAsync(DccRequest request, OutgoingDccSend outgoing)
    {
        try
        {
            await using var transport = await outgoing.Listener!.AcceptAsync(outgoing.Lifetime.Token);
            await CompleteOutgoingDccSendAsync(request, outgoing, transport);
        }
        catch (OperationCanceledException) when (outgoing.Lifetime.IsCancellationRequested)
        {
            if (_dcc.Requests.TryTransition(request.Id, DccRequestState.Cancelled,
                    "The outgoing DCC SEND was canceled.", out var cancelled))
                PublishDccState(cancelled!,
                    $"DCC {DccProtocolName(request.Offer)} #{request.Id} to {request.Sender} was canceled");
        }
        catch (Exception exception) when (exception is SocketException or IOException or UnauthorizedAccessException or
            TimeoutException or InvalidOperationException or ObjectDisposedException or
            System.Security.Authentication.AuthenticationException)
        {
            if (_dcc.Requests.TryTransition(request.Id, DccRequestState.Failed, exception.Message, out var failed))
                PublishDccState(failed!,
                    $"DCC {DccProtocolName(request.Offer)} #{request.Id} failed: {DccConnectionError(exception)}");
        }
        finally
        {
            if (outgoing.Listener is not null) await outgoing.Listener.DisposeAsync();
            _dcc.ClearOutgoingSend(request.Id, outgoing);
            outgoing.Lifetime.Dispose();
        }
    }

    private async Task RunOutgoingPassiveDccSendAsync(DccRequest request, OutgoingDccSend outgoing)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(outgoing.Lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await using var transport = await DccFileSendTransport.ConnectAsync(
                request.Offer.Address, request.Offer.Port, timeout.Token, request.Offer.IsSecure);
            await CompleteOutgoingDccSendAsync(request, outgoing, transport);
        }
        catch (OperationCanceledException) when (outgoing.Lifetime.IsCancellationRequested)
        {
            if (_dcc.Requests.TryTransition(request.Id, DccRequestState.Cancelled,
                    "The outgoing DCC SEND was canceled.", out var cancelled))
                PublishDccState(cancelled!,
                    $"DCC {DccProtocolName(request.Offer)} #{request.Id} to {request.Sender} was canceled");
        }
        catch (OperationCanceledException)
        {
            if (_dcc.Requests.TryTransition(request.Id, DccRequestState.Failed,
                    $"The passive DCC {DccProtocolName(request.Offer)} connection timed out", out var failed))
                PublishDccState(failed!,
                    $"DCC {DccProtocolName(request.Offer)} #{request.Id} failed: connection timed out");
        }
        catch (Exception exception) when (exception is SocketException or IOException or UnauthorizedAccessException or
            TimeoutException or InvalidOperationException or ObjectDisposedException or
            System.Security.Authentication.AuthenticationException)
        {
            if (_dcc.Requests.TryTransition(request.Id, DccRequestState.Failed, exception.Message, out var failed))
                PublishDccState(failed!,
                    $"DCC {DccProtocolName(request.Offer)} #{request.Id} failed: {DccConnectionError(exception)}");
        }
        finally
        {
            _dcc.ClearOutgoingSend(request.Id, outgoing);
            outgoing.Lifetime.Dispose();
        }
    }

    private async Task CompleteOutgoingDccSendAsync(
        DccRequest request,
        OutgoingDccSend outgoing,
        DccFileSendTransport transport)
    {
            var resumeOffset = outgoing.ResumeOffset;
            if (!_dcc.Requests.TryTransition(request.Id, DccRequestState.Connected, null, out var connected))
            {
                return;
            }
            CancelDccExpiration(request.Id);
            PublishDccState(connected!,
                resumeOffset > 0
                    ? $"Resuming {request.Offer.Filename} to {request.Sender} at {FormatFileSize(resumeOffset)}" +
                      DccSecurityDetails(transport)
                    : $"Sending {request.Offer.Filename} to {request.Sender} " +
                      $"({FormatFileSize(outgoing.ExpectedBytes)}){DccSecurityDetails(transport)}");

            await using var source = new FileStream(
                outgoing.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length != outgoing.ExpectedBytes ||
                File.GetLastWriteTimeUtc(outgoing.FilePath) != outgoing.LastWriteTimeUtc)
            {
                throw new IOException("The source file changed while the DCC request was pending.");
            }
            var lastReport = TimeSpan.Zero;
            await transport.SendAsync(
                source,
                outgoing.ExpectedBytes,
                progress =>
                {
                    if (progress.BytesAcknowledged != progress.TotalBytes &&
                        progress.Elapsed - lastReport < TimeSpan.FromSeconds(1))
                    {
                        return;
                    }
                    lastReport = progress.Elapsed;
                    PublishDccSendProgress(request, progress);
                },
                initialOffset: resumeOffset,
                cancellationToken: outgoing.Lifetime.Token);

            if (_dcc.Requests.TryTransition(request.Id, DccRequestState.Completed, outgoing.FilePath, out var completed))
            {
                PublishDccState(completed!,
                    $"Sent {request.Offer.Filename} to {request.Sender} ({FormatFileSize(outgoing.ExpectedBytes)})");
            }
    }

    private void PublishDccSendProgress(DccRequest request, DccSendProgress progress)
    {
        if (FindSession(request.NetworkSessionId) is not { } session) return;
        var percentage = progress.TotalBytes == 0 ? 100 : progress.BytesAcknowledged * 100d / progress.TotalBytes;
        var speed = FormatFileSize((long)Math.Max(0, progress.BytesPerSecond));
        OnSessionEvent(new SessionEvent(
            session.State.Id,
            DccBuffer(session).Id,
            SessionEventKind.Status,
            $"DCC {DccProtocolName(request.Offer)} #{request.Id}: {request.Offer.Filename} - " +
            $"{FormatFileSize(progress.BytesAcknowledged)} of " +
            $"{FormatFileSize(progress.TotalBytes)} ({percentage:0}%, {speed}/s)",
            DateTimeOffset.Now,
            new Dictionary<string, string?>
            {
                ["event"] = "dcc.transfer.progress",
                ["dcc.id"] = request.Id.ToString(CultureInfo.InvariantCulture),
                ["dcc.state"] = "connected",
                ["dcc.bytes"] = progress.BytesAcknowledged.ToString(CultureInfo.InvariantCulture),
                ["dcc.size"] = progress.TotalBytes.ToString(CultureInfo.InvariantCulture),
                ["history.transientKey"] = DccProgressHistoryKey(request.Id),
                ["history.replaceKey"] = DccProgressHistoryKey(request.Id),
                ["suppressActivity"] = "true"
            }));
    }

    private async ValueTask<CommandResult> StartOutgoingDccChatAsync(
        string nickname,
        bool passive,
        bool secure,
        CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        if (session is null) return failure;
        if (session.ConnectionState != IrcConnectionState.Online)
        {
            return CommandResult.Failure("Not connected to a server.");
        }
        if (string.IsNullOrWhiteSpace(nickname) || nickname.Any(char.IsWhiteSpace) ||
            nickname.IndexOfAny(['\r', '\n', '\0', '\u0001']) >= 0)
        {
            return CommandResult.Failure($"Usage: /dcc {(secure ? "schat" : "chat")} <nick> [--passive]");
        }

        DccChatListener? listener = null;
        DccRequest? request = null;
        try
        {
            IPAddress address;
            string? token = null;
            int port;
            if (passive)
            {
                address = IPAddress.Parse("1.1.1.1");
                token = NewDccPassiveToken();
                port = 0;
            }
            else
            {
                address = await DccAddressSelector.SelectAdvertisedAddressAsync(
                    _preferences.DccAddress, session.State.VisibleHost, session.Options.Endpoint.Host,
                    session.Options.Endpoint.Port, cancellationToken);
                listener = DccChatListener.Start(address, _preferences.DccPorts, secure);
                port = listener.Port;
            }
            var addressToken = DccAddressSelector.ToDccAddressToken(address);
            var payload = $"DCC {(secure ? "SCHAT" : "CHAT")} chat {addressToken} {port}" +
                (token is null ? string.Empty : $" {token}");
            var network = ProfileFor(session)?.DisplayName ?? session.Features.NetworkName ?? session.State.DisplayName;
            request = _dcc.Requests.Add(
                session.State.Id,
                network,
                nickname,
                new DccOffer(DccRequestType.Chat, null, address.ToString(), port, null, token, payload, secure),
                DateTimeOffset.Now,
                direction: DccRequestDirection.Outgoing);
            PendingDccChat? pending = null;
            if (listener is not null)
            {
                pending = new PendingDccChat(
                    listener, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
                _dcc.SetChatListener(request.Id, pending);
            }
            ScheduleDccExpiration(request);
            _dcc.PruneTerminalRuntimes();
            var buffer = DccChatBuffer(session, request);
            SwitchTo(session, buffer);
            PublishDccChatStatus(request,
                $"DCC {DccProtocolName(request.Offer)} request #{request.Id} sent to {nickname}; waiting for connection");
            await session.SendAsync(
                "PRIVMSG",
                [nickname, $"\u0001{payload}\u0001"],
                cancellationToken: cancellationToken);
            if (pending is not null) _dcc.TrackTask(request.Id, AwaitOutgoingDccChatAsync(request, pending));
            return CommandResult.Success();
        }
        catch (Exception exception) when (exception is SocketException or IOException or InvalidOperationException or
            ArgumentException or System.Security.Authentication.AuthenticationException)
        {
            if (request is not null)
            {
                await StopDccChatListenerAsync(request.Id);
                CancelDccExpiration(request.Id);
                _dcc.Requests.TryTransition(request.Id, DccRequestState.Failed, exception.Message, out var failed);
                if (failed is not null) PublishDccState(failed,
                    $"DCC {DccProtocolName(request.Offer)} request #{request.Id} failed: {DccConnectionError(exception)}");
            }
            else
            {
                if (listener is not null) await listener.DisposeAsync();
            }
            return CommandResult.Failure($"Could not start DCC {(secure ? "SCHAT" : "CHAT")}: {DccConnectionError(exception)}");
        }
    }

    private async ValueTask<CommandResult> AcceptPassiveDccChatAsync(
        DccRequest request,
        CancellationToken cancellationToken)
    {
        var protocol = DccProtocolName(request.Offer);
        if (request.Offer.PassiveToken is not { } token)
            return CommandResult.Failure($"DCC {protocol} request #{request.Id} has no passive token");
        if (FindSession(request.NetworkSessionId) is not { } session ||
            session.ConnectionState != IrcConnectionState.Online)
            return CommandResult.Failure("Not connected to a server");

        DccChatListener? listener = null;
        try
        {
            var address = await DccAddressSelector.SelectAdvertisedAddressAsync(
                _preferences.DccAddress, session.State.VisibleHost, session.Options.Endpoint.Host,
                session.Options.Endpoint.Port, cancellationToken);
            listener = DccChatListener.Start(address, _preferences.DccPorts, request.Offer.IsSecure);
            var addressToken = DccAddressSelector.ToDccAddressToken(address);
            var payload = $"DCC {(request.Offer.IsSecure ? "SCHAT" : "CHAT")} chat {addressToken} {listener.Port} {token}";
            var responseOffer = new DccOffer(
                DccRequestType.Chat, null, address.ToString(), listener.Port, null, token, payload,
                request.Offer.IsSecure);
            if (!_dcc.Requests.TryTransitionWithOffer(request.Id, DccRequestState.Connecting,
                    responseOffer, $"Waiting for the passive DCC {protocol} sender", out var connecting))
            {
                await listener.DisposeAsync();
                return CommandResult.Failure($"DCC request #{request.Id} is no longer pending");
            }
            var pending = new PendingDccChat(
                listener, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
            _dcc.SetChatListener(request.Id, pending);
            CancelDccExpiration(request.Id);
            SwitchTo(session, DccChatBuffer(session, connecting!));
            PublishDccChatStatus(connecting!,
                $"Waiting for passive DCC {protocol} #{request.Id} from {request.Sender}");
            await session.SendAsync("PRIVMSG", [request.Sender, $"\u0001{payload}\u0001"],
                cancellationToken: cancellationToken);
            _dcc.TrackTask(request.Id, AwaitOutgoingDccChatAsync(connecting!, pending, switchToBuffer: true));
            return CommandResult.Success();
        }
        catch (Exception exception) when (exception is SocketException or IOException or InvalidOperationException or
            ArgumentException or System.Security.Authentication.AuthenticationException)
        {
            await StopDccChatListenerAsync(request.Id);
            if (listener is not null) await listener.DisposeAsync();
            _dcc.Requests.TryTransition(request.Id, DccRequestState.Failed, exception.Message, out _);
            return CommandResult.Failure($"Could not accept passive DCC {protocol}: {exception.Message}");
        }
    }

    private async ValueTask<CommandResult> AcceptDccChatAsync(
        DccRequest request,
        CancellationToken cancellationToken)
    {
        if (!_dcc.Requests.TryTransition(
                request.Id,
                DccRequestState.Connecting,
                $"Connecting to the offered DCC {DccProtocolName(request.Offer)} endpoint",
                out var connecting))
        {
            return CommandResult.Failure($"DCC request #{request.Id} is no longer pending");
        }
        CancelDccExpiration(request.Id);
        var pendingConnection = BeginDccChatConnection(request.Id, cancellationToken);
        var protocol = DccProtocolName(request.Offer);
        PublishDccState(connecting!, $"Connecting to DCC {protocol} request #{request.Id} from {request.Sender}");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(pendingConnection.Lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var transport = await DccChatTransport.ConnectAsync(
                request.Offer.Address,
                request.Offer.Port,
                timeout.Token,
                request.Offer.IsSecure);
            if (!_dcc.Requests.TryTransition(request.Id, DccRequestState.Connected, null, out var connected))
            {
                await transport.DisposeAsync();
                return CommandResult.Failure($"DCC request #{request.Id} was canceled before it connected");
            }
            await ActivateDccChatAsync(connected!, transport, switchToBuffer: true);
            return CommandResult.Success();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            if (_dcc.Requests.TryGet(request.Id, out var current) &&
                current!.State != DccRequestState.Connecting)
            {
                return CommandResult.Failure(
                    $"DCC request #{request.Id} is {DccStateText(current.State)} and is no longer connecting");
            }
            return FailDccConnection(request.Id, $"The DCC {protocol} connection timed out");
        }
        catch (Exception exception) when (exception is SocketException or IOException or InvalidOperationException or
            System.Security.Authentication.AuthenticationException)
        {
            return FailDccConnection(request.Id, $"The DCC {protocol} connection failed: {DccConnectionError(exception)}");
        }
        finally
        {
            RemoveDccChatConnection(request.Id, pendingConnection);
        }
    }

    private CommandResult FailDccConnection(int requestId, string message)
    {
        _dcc.Requests.TryTransition(requestId, DccRequestState.Failed, message, out _);
        return CommandResult.Failure(message);
    }

    private async Task AwaitOutgoingDccChatAsync(
        DccRequest request,
        PendingDccChat pending,
        bool switchToBuffer = false)
    {
        try
        {
            var transport = await pending.Listener.AcceptAsync(pending.Lifetime.Token);
            if (!_dcc.Requests.TryTransition(request.Id, DccRequestState.Connected, null, out var connected))
            {
                await transport.DisposeAsync();
                return;
            }
            CancelDccExpiration(request.Id);
            await ActivateDccChatAsync(connected!, transport, switchToBuffer);
        }
        catch (OperationCanceledException) when (pending.Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is SocketException or IOException or InvalidOperationException or
            System.Security.Authentication.AuthenticationException)
        {
            if (_dcc.Requests.TryTransition(request.Id, DccRequestState.Failed, exception.Message, out var failed))
            {
                PublishDccState(failed!,
                    $"DCC {DccProtocolName(request.Offer)} request #{request.Id} failed: {DccConnectionError(exception)}");
            }
        }
        finally
        {
            await RemoveDccChatListenerAsync(request.Id, pending);
        }
    }

    private void StartOutgoingPassiveDccChat(DccRequest request)
    {
        var pending = BeginDccChatConnection(request.Id, CancellationToken.None);
        _dcc.TrackTask(request.Id, ConnectOutgoingPassiveDccChatAsync(request, pending));
    }

    private async Task ConnectOutgoingPassiveDccChatAsync(
        DccRequest request,
        PendingDccConnection pending)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(pending.Lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var transport = await DccChatTransport.ConnectAsync(
                request.Offer.Address, request.Offer.Port, timeout.Token, request.Offer.IsSecure);
            if (!_dcc.Requests.TryTransition(request.Id, DccRequestState.Connected, null, out var connected))
            {
                await transport.DisposeAsync();
                return;
            }
            await ActivateDccChatAsync(connected!, transport, switchToBuffer: false);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            if (_dcc.Requests.TryGet(request.Id, out var current) &&
                current!.State == DccRequestState.Connecting &&
                _dcc.Requests.TryTransition(request.Id, DccRequestState.Failed,
                    $"The passive DCC {DccProtocolName(request.Offer)} connection timed out", out var failed))
                PublishDccState(failed!,
                    $"DCC {DccProtocolName(request.Offer)} request #{request.Id} failed: connection timed out");
        }
        catch (Exception exception) when (exception is SocketException or IOException or InvalidOperationException or
            System.Security.Authentication.AuthenticationException)
        {
            if (_dcc.Requests.TryTransition(request.Id, DccRequestState.Failed, exception.Message, out var failed))
                PublishDccState(failed!,
                    $"DCC {DccProtocolName(request.Offer)} request #{request.Id} failed: {DccConnectionError(exception)}");
        }
        finally
        {
            RemoveDccChatConnection(request.Id, pending);
        }
    }

    private PendingDccConnection BeginDccChatConnection(
        int requestId,
        CancellationToken cancellationToken)
    {
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            return _dcc.BeginChatConnection(requestId, lifetime);
        }
        catch
        {
            lifetime.Dispose();
            throw;
        }
    }

    private void CancelDccChatConnection(int requestId)
    {
        var pending = _dcc.ChatConnection(requestId);
        DccCoordinator.CancelLifetime(pending?.Lifetime);
    }

    private void RemoveDccChatConnection(int requestId, PendingDccConnection pending)
    {
        if (_dcc.RemoveChatConnection(requestId, pending)) pending.Lifetime.Dispose();
    }

    private string NewDccPassiveToken()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var token = RandomNumberGenerator.GetInt32(1, int.MaxValue)
                .ToString(CultureInfo.InvariantCulture);
            if (_dcc.Requests.Snapshot().All(request => request.Offer.PassiveToken != token)) return token;
        }
        throw new InvalidOperationException("Could not allocate a passive DCC token");
    }

    private async Task ActivateDccChatAsync(DccRequest request, DccChatTransport transport, bool switchToBuffer)
    {
        var session = FindSession(request.NetworkSessionId);
        ActiveDccChat? active = null;
        BufferState? buffer = null;
        if (session is not null &&
            _dcc.Requests.TryGet(request.Id, out var current) &&
            current!.State == DccRequestState.Connected)
        {
            buffer = DccChatBuffer(session, current);
            active = new ActiveDccChat(
                request.Id,
                transport,
                buffer,
                CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
            _dcc.SetChat(request.Id, active);
            request = current;
        }
        else if (session is null)
        {
            _dcc.Requests.TryTransition(
                request.Id,
                DccRequestState.Invalidated,
                $"The network session closed before DCC {DccProtocolName(request.Offer)} activation",
                out _);
        }
        if (active is null || session is null || buffer is null)
        {
            await transport.DisposeAsync();
            return;
        }
        if (switchToBuffer) SwitchTo(session, buffer);
        var secureDetails = transport.IsSecure
            ? $" using {FormatTlsProtocol(transport.SecurityProtocol)}" +
              (transport.PeerCertificateFingerprint is { } fingerprint
                  ? $"; peer SHA-256 {fingerprint}"
                  : string.Empty) +
              "; encrypted, peer identity not verified"
            : string.Empty;
        PublishDccChatStatus(request,
            $"DCC {DccProtocolName(request.Offer)} connected to {request.Sender} ({transport.RemoteAddress}){secureDetails}");
        PublishDccState(request,
            $"DCC {DccProtocolName(request.Offer)} request #{request.Id} with {request.Sender} connected");
        _dcc.TrackTask(request.Id, RunDccChatReadLoopAsync(request, active));
    }

    private static string FormatTlsProtocol(string? protocol) => protocol switch
    {
        "Tls13" => "TLS 1.3",
        "Tls12" => "TLS 1.2",
        null or "" => "TLS",
        _ => protocol
    };

    private static string DccSecurityDetails(DccFileReceiveTransport transport) =>
        DccSecurityDetails(
            transport.IsSecure,
            transport.SecurityProtocol,
            transport.PeerCertificateFingerprint);

    private static string DccSecurityDetails(DccFileSendTransport transport) =>
        DccSecurityDetails(
            transport.IsSecure,
            transport.SecurityProtocol,
            transport.PeerCertificateFingerprint);

    private static string DccSecurityDetails(bool secure, string? protocol, string? fingerprint)
    {
        if (!secure) return string.Empty;
        return $" using {FormatTlsProtocol(protocol)}" +
            (fingerprint is null ? string.Empty : $"; peer SHA-256 {fingerprint}") +
            "; encrypted, peer identity not verified";
    }

    private static string DccConnectionError(Exception exception) => exception switch
    {
        System.Security.Authentication.AuthenticationException =>
            "The secure DCC connection received invalid TLS data",
        IOException { InnerException: System.Security.Authentication.AuthenticationException } =>
            "The secure DCC connection received invalid TLS data",
        _ => exception.Message
    };

    private async Task RunDccChatReadLoopAsync(DccRequest request, ActiveDccChat active)
    {
        try
        {
            await foreach (var line in active.Transport.ReadLinesAsync(active.Lifetime.Token))
            {
                PublishDccChatMessage(request, line, local: false);
            }
            await EndDccChatAsync(request.Id, DccRequestState.Closed,
                $"The remote user closed the DCC {DccProtocolName(request.Offer)}");
        }
        catch (OperationCanceledException) when (active.Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is SocketException or IOException or InvalidDataException or ObjectDisposedException)
        {
            await EndDccChatAsync(request.Id, DccRequestState.Failed,
                $"DCC {DccProtocolName(request.Offer)} closed with an error: {DccConnectionError(exception)}");
        }
    }

    private async ValueTask<CommandResult> SendDccChatAsync(
        BufferState buffer,
        string text,
        CancellationToken cancellationToken)
    {
        if (_dcc.ChatForBuffer(buffer.Id) is not { } binding)
            return CommandResult.Failure("DCC CHAT is not connected");
        var (requestId, active) = binding;
        if (!_dcc.Requests.TryGet(requestId, out var request))
        {
            return CommandResult.Failure("The DCC CHAT session is no longer available");
        }
        try
        {
            await active.Transport.SendLineAsync(text, cancellationToken);
            PublishDccChatMessage(request!, text, local: true);
            return CommandResult.Success();
        }
        catch (Exception exception) when (exception is IOException or SocketException or InvalidOperationException or ObjectDisposedException)
        {
            var protocol = DccProtocolName(request!.Offer);
            await EndDccChatAsync(requestId, DccRequestState.Failed,
                $"DCC {protocol} send failed: {DccConnectionError(exception)}");
            return CommandResult.Failure($"DCC {protocol} send failed: {DccConnectionError(exception)}");
        }
    }

    private void PublishDccChatMessage(DccRequest request, string text, bool local)
    {
        if (FindSession(request.NetworkSessionId) is not { } session ||
            DccChatBufferFor(request.Id) is not { } buffer)
        {
            return;
        }
        var nickname = local ? session.CurrentNickname : request.Sender;
        var isAction = text.Length >= 9 && text[0] == '\u0001' && text[^1] == '\u0001' &&
            text[1..^1].StartsWith("ACTION ", StringComparison.OrdinalIgnoreCase);
        if (isAction)
        {
            var formattedAction = IrcTextFormatting.Parse(text[8..^1]);
            var action = formattedAction.PlainText;
            OnSessionEvent(new SessionEvent(
                session.State.Id,
                buffer.Id,
                SessionEventKind.Action,
                $"* {nickname} {action}",
                DateTimeOffset.Now,
                DccChatFields(request, nickname, action, local, "dcc.chat.action"),
                FormattedContent: formattedAction));
            return;
        }
        var formatted = IrcTextFormatting.Parse(text);
        var sanitized = formatted.PlainText;
        OnSessionEvent(new SessionEvent(
            session.State.Id,
            buffer.Id,
            SessionEventKind.Message,
            $"<{nickname}> {sanitized}",
            DateTimeOffset.Now,
            DccChatFields(request, nickname, sanitized, local, "dcc.chat.message"),
            FormattedContent: formatted));
    }

    private static IReadOnlyDictionary<string, string?> DccChatFields(
        DccRequest request,
        string nickname,
        string message,
        bool local,
        string eventName) => new Dictionary<string, string?>
    {
        ["event"] = eventName,
        ["dcc.id"] = request.Id.ToString(CultureInfo.InvariantCulture),
        ["dcc.type"] = "chat",
        ["dcc.direction"] = request.Direction.ToString().ToLowerInvariant(),
        ["dcc.network"] = request.Network,
        ["dcc.sender"] = request.Sender,
        ["dcc.secure"] = request.Offer.IsSecure ? "true" : "false",
        ["nick"] = nickname,
        ["message"] = message,
        ["self"] = local ? "true" : "false"
    };

    private void PublishDccChatStatus(DccRequest request, string text)
    {
        if (FindSession(request.NetworkSessionId) is not { } session ||
            DccChatBufferFor(request.Id) is not { } buffer)
        {
            return;
        }
        OnSessionEvent(new SessionEvent(
            session.State.Id,
            buffer.Id,
            SessionEventKind.Status,
            text,
            DateTimeOffset.Now,
            new Dictionary<string, string?>
            {
                ["event"] = "dcc.chat.state",
                ["dcc.id"] = request.Id.ToString(CultureInfo.InvariantCulture),
                ["dcc.state"] = request.State.ToString().ToLowerInvariant(),
                ["dcc.sender"] = request.Sender,
                ["dcc.secure"] = request.Offer.IsSecure ? "true" : "false"
            }));
    }

    private async Task EndDccChatAsync(int requestId, DccRequestState state, string reason)
    {
        DccRequest? updatedWithoutTransport = null;
        var active = _dcc.TakeChat(requestId);
        if (active is null)
        {
            _dcc.Requests.TryTransition(requestId, state, reason, out updatedWithoutTransport);
        }
        if (active is null)
        {
            if (updatedWithoutTransport is not null)
                PublishDccState(updatedWithoutTransport,
                    $"DCC {DccProtocolName(updatedWithoutTransport.Offer)} request #{requestId} with " +
                    $"{updatedWithoutTransport.Sender} is {DccStateText(state)}");
            return;
        }
        active.Lifetime.Cancel();
        await active.Transport.DisposeAsync();
        active.Lifetime.Dispose();
        if (_dcc.Requests.TryTransition(requestId, state, reason, out var updated))
        {
            var completed = updated!;
            PublishDccChatStatus(completed, reason);
            PublishDccState(completed,
                $"DCC {DccProtocolName(completed.Offer)} request #{requestId} with {completed.Sender} is {DccStateText(state)}");
        }
    }

    private BufferState DccChatBuffer(IrcNetworkSession session, DccRequest request)
    {
        if (_dcc.ChatBufferId(request.Id) is { } existingId &&
            session.State.TryGetBuffer(existingId, out var existing))
        {
            return existing!;
        }
        var name = $"={request.Sender}";
        if (session.State.TryGetBuffer(name, out _)) name = $"={request.Sender}:{request.Id}";
        var buffer = session.State.GetOrCreateBuffer(BufferKind.DccChat, name);
        _dcc.SetChatBuffer(request.Id, buffer.Id);
        return buffer;
    }

    private BufferState? DccChatBufferFor(int requestId)
    {
        if (_dcc.ChatBufferId(requestId) is not { } bufferId) return null;
        foreach (var session in SessionsSnapshot())
        {
            if (session.State.TryGetBuffer(bufferId, out var buffer)) return buffer;
        }
        return null;
    }

    private async Task StopDccChatListenerAsync(int requestId)
    {
        var pending = _dcc.TakeChatListener(requestId);
        if (pending is null) return;
        pending.Lifetime.Cancel();
        await pending.Listener.DisposeAsync();
        pending.Lifetime.Dispose();
    }

    private async Task RemoveDccChatListenerAsync(int requestId, PendingDccChat pending)
    {
        if (_dcc.RemoveChatListener(requestId, pending))
        {
            await pending.Listener.DisposeAsync();
            pending.Lifetime.Dispose();
        }
    }

}
