using Clircs.Commands;
using Clircs.Networking;
using Clircs.Protocol;
using Clircs.Sessions;
using Clircs.State;

namespace Clircs.ConsoleClient;

// Owns joins, modes, topics, bans, and other channel commands.
internal sealed partial class ClientApplication
{
    private ValueTask<CommandResult> PingAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        input.Arguments.Count == 1
            ? CtcpAsync(context, new CommandInput("ctcp", [input.Arguments[0], "PING", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()], string.Empty), cancellationToken)
            : ValueTask.FromResult(CommandResult.Failure("Usage: /ping <nick>"));

    private async ValueTask<CommandResult> ShowVersionAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        if (input.Arguments.Count != 0)
        {
            return CommandResult.Failure("Usage: /sv");
        }

        var session = RequireSession(out var failure);
        var target = ActiveTarget();
        if (session is null)
        {
            return failure;
        }

        if (target is null)
        {
            return CommandResult.Failure("Switch to a channel or query before using /sv.");
        }

        await session.SendMessageAsync(target, ProductInfo.DisplayName, cancellationToken);
        return CommandResult.Success();
    }

    private ValueTask<CommandResult> TimeAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        input.Arguments.Count == 0
            ? ValueTask.FromResult(CommandResult.Success(DateTimeOffset.Now.ToString("F")))
            : CtcpAsync(context, new CommandInput("ctcp", [input.Arguments[0], "TIME"], string.Empty), cancellationToken);

    private async ValueTask<CommandResult> RawAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        if (session is null)
        {
            return failure;
        }

        if (input.RawArguments.Length == 0)
        {
            return CommandResult.Failure("Usage: /raw <IRC command>");
        }

        await session.SendRawAsync(input.RawArguments, cancellationToken);
        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> JoinAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        if (session is null)
        {
            return failure;
        }

        if (input.Arguments.Count is < 1 or > 2)
        {
            return CommandResult.Failure("Usage: /join <channels> [keys]");
        }

        await SendJoinAsync(session, input.Arguments, IrcOutboundPriority.Interactive, cancellationToken,
            ActiveBuffer()?.Id ?? session.State.StatusBuffer.Id);
        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> PartAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        var active = ActiveBuffer();
        if (session is null)
        {
            return failure;
        }

        string channel;
        string? reason = null;
        if (input.Arguments.Count > 0 && session.Features.IsChannel(input.Arguments[0]))
        {
            channel = input.Arguments[0];
            TrySplitFirst(input.RawArguments, out _, out reason);
        }
        else if (active?.Kind == BufferKind.Channel)
        {
            channel = active.Name;
            reason = input.RawArguments.Length == 0 ? null : input.RawArguments;
        }
        else
        {
            return CommandResult.Failure("Usage outside a channel: /part <channel> [reason]");
        }

        var parameters = reason is null ? new[] { channel } : [channel, reason];
        await session.SendAsync("PART", parameters, cancellationToken: cancellationToken);
        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> CycleAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        var active = ActiveBuffer();
        if (session is null)
        {
            return failure;
        }

        if (active?.Kind != BufferKind.Channel)
        {
            return CommandResult.Failure("/cycle requires an active channel.");
        }

        MarkCyclePending(session, active.Name);
        try
        {
            await session.SendAsync("PART", [active.Name, input.RawArguments.Length == 0 ? "Cycling" : input.RawArguments], cancellationToken: cancellationToken);
            await SendJoinAsync(session, [active.Name], IrcOutboundPriority.Interactive, cancellationToken);
        }
        catch
        {
            ClearCyclePending(session.State.Id, active.Name);
            throw;
        }
        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> InviteAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        if (session is null)
        {
            return failure;
        }

        var channel = input.Arguments.Count > 1 ? input.Arguments[1] : ActiveChannel();
        if (input.Arguments.Count == 0 || channel is null)
        {
            return CommandResult.Failure("Usage: /invite <nick> [channel]");
        }

        await session.SendAsync("INVITE", [input.Arguments[0], channel], cancellationToken: cancellationToken);
        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> TopicAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        if (session is null)
        {
            return failure;
        }

        var channel = ActiveChannel();
        var text = input.RawArguments;
        if (input.Arguments.Count > 0 && session.Features.IsChannel(input.Arguments[0]))
        {
            channel = input.Arguments[0];
            text = TrySplitFirst(input.RawArguments, out _, out var rest) ? rest : string.Empty;
        }

        if (channel is null)
        {
            return CommandResult.Failure("Usage outside a channel: /topic <channel> [topic]");
        }

        await session.SendAsync("TOPIC", text.Length == 0 ? [channel] : [channel, text], cancellationToken: cancellationToken);
        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> RandomTopicAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        if (session is null) return failure;
        var channel = input.Arguments.Count == 0 ? ActiveChannel() : input.Arguments.Count == 1 ? input.Arguments[0] : null;
        if (channel is null || !session.Features.IsChannel(channel))
        {
            return CommandResult.Failure("Usage: /rt [channel]");
        }
        await session.SendAsync("TOPIC", [channel, ResolveTopicMessage(null)], cancellationToken: cancellationToken);
        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> ModeAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        if (session is null)
        {
            return failure;
        }

        var activeChannel = ActiveChannel();
        string[] parameters;
        if (input.Arguments.Count == 0 && activeChannel is not null)
        {
            parameters = [activeChannel];
        }
        else if (input.Arguments.Count > 0 && input.Arguments[0].Length > 0 && input.Arguments[0][0] is '+' or '-' && activeChannel is not null)
        {
            parameters = [activeChannel, .. input.Arguments];
        }
        else
        {
            parameters = input.Arguments.ToArray();
        }
        if (parameters.Length == 0)
        {
            return CommandResult.Failure("Usage: /mode <target> [modes] [parameters]");
        }

        await session.SendAsync("MODE", parameters, cancellationToken: cancellationToken);
        return CommandResult.Success();
    }

    private ValueTask<CommandResult> OpAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SetRequestedMemberModeAsync('o', adding: true, input.Arguments, "/op <nick> [nick...]", cancellationToken);

    private ValueTask<CommandResult> DeopAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SetRequestedMemberModeAsync('o', adding: false, input.Arguments, "/deop <nick> [nick...]", cancellationToken);

    private ValueTask<CommandResult> VoiceAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SetRequestedMemberModeAsync('v', adding: true, input.Arguments, "/voice <nick> [nick...]", cancellationToken);

    private ValueTask<CommandResult> DevoiceAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SetRequestedMemberModeAsync('v', adding: false, input.Arguments, "/devoice <nick> [nick...]", cancellationToken);

    private async ValueTask<CommandResult> KickAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (!TryGetOperatorChannel(out var session, out var channel, out var failure))
        {
            return failure;
        }

        if (input.Arguments.Count == 0)
        {
            return CommandResult.Failure("Usage: /kick <nick> [reason]");
        }

        var reason = ResolveKickMessage(TrySplitFirst(input.RawArguments, out _, out var suppliedReason) ? suppliedReason : null);
        await session!.SendAsync("KICK", [channel!.Name, input.Arguments[0], reason], cancellationToken: cancellationToken);
        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> KickBanAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (!TryGetOperatorChannel(out var session, out var channel, out var failure))
        {
            return failure;
        }

        if (input.Arguments.Count == 0 || !channel!.TryGetMember(input.Arguments[0], out var member))
        {
            return CommandResult.Failure("Usage: /kickban <channel-member> [reason]");
        }

        if (string.IsNullOrWhiteSpace(member!.Username) || string.IsNullOrWhiteSpace(member.Host))
        {
            return CommandResult.Failure($"No synchronized host is known for {member.Nickname}; run /who {channel.Name} first.");
        }

        var mask = BanmaskFormatter.Create(member, _preferences.BanmaskStyle);
        var reason = ResolveKickMessage(TrySplitFirst(input.RawArguments, out _, out var suppliedReason) ? suppliedReason : null);
        await session!.SendAsync("MODE", [channel!.Name, "+b", mask], cancellationToken: cancellationToken);
        await session.SendAsync("KICK", [channel.Name, member.Nickname, reason], cancellationToken: cancellationToken);
        return CommandResult.Success($"Banning {mask} and kicking {member.Nickname}.");
    }

    private async ValueTask<CommandResult> BanAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (!TryGetOperatorChannel(out var session, out var channel, out var failure))
        {
            return failure;
        }
        if (input.Arguments.Count != 1)
        {
            return CommandResult.Failure("Usage: /ban <nick|mask>");
        }

        var target = input.Arguments[0];
        string mask;
        if (target.Contains('!') && target.Contains('@') && target.IndexOfAny([' ', '\r', '\n', '\0']) < 0)
        {
            mask = target;
        }
        else if (channel!.TryGetMember(target, out var member) && member!.Username is not null && member.Host is not null)
        {
            mask = BanmaskFormatter.Create(member, _preferences.BanmaskStyle);
        }
        else
        {
            return CommandResult.Failure($"No synchronized user and host are known for {target}; supply nick!user@host explicitly.");
        }

        await session!.SendAsync("MODE", [channel!.Name, "+b", mask], cancellationToken: cancellationToken);
        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> TimedBanAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (!TryGetOperatorChannel(out var session, out var channel, out var failure))
        {
            return failure;
        }

        if (input.Arguments.Count < 2 || !TryParseDuration(input.Arguments[1], out var duration) ||
            input.Arguments.Count == 3 ||
            (input.Arguments.Count > 2 && !input.Arguments[2].Equals("--reason", StringComparison.OrdinalIgnoreCase)))
        {
            return CommandResult.Failure("Usage: /tban <nick-or-mask> <duration> [--reason <text>]");
        }

        var target = input.Arguments[0];
        string mask;
        if (channel!.TryGetMember(target, out var member))
        {
            if (string.IsNullOrWhiteSpace(member!.Username) || string.IsNullOrWhiteSpace(member.Host))
            {
                return CommandResult.Failure($"No synchronized host is known for {member.Nickname}; run /who {channel.Name} first.");
            }

            mask = BanmaskFormatter.Create(member, _preferences.BanmaskStyle);
        }
        else if (target.Contains('!') && target.Contains('@') && target.IndexOfAny([' ', '\r', '\n', '\0']) < 0)
        {
            mask = target;
        }
        else
        {
            return CommandResult.Failure("The target must be a synchronized channel member or nick!user@host mask.");
        }

        var reason = input.Arguments.Count > 3 ? string.Join(' ', input.Arguments.Skip(3)) : null;
        await session!.SendAsync("MODE", [channel.Name, "+b", mask], cancellationToken: cancellationToken);
        ScheduleTimedUnban(session, channel.Name, mask, duration);
        return CommandResult.Success(
            $"Timed ban {mask} set for {FormatDuration(duration)}." +
            (string.IsNullOrWhiteSpace(reason) ? string.Empty : $" Reason: {reason}"));
    }

    private void ScheduleTimedUnban(
        IrcNetworkSession session,
        string channel,
        string mask,
        TimeSpan duration)
    {
        var timerId = Guid.NewGuid();
        var timer = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _sessionTransientState.AddTimedBan(timerId, session.State.Id, timer);

        StartSessionWork(
            session,
            "timed channel unban",
            () => RunTimedUnbanAsync(timerId, timer, session, channel, mask, duration));
    }

    private async Task RunTimedUnbanAsync(
        Guid timerId,
        CancellationTokenSource timer,
        IrcNetworkSession session,
        string channel,
        string mask,
        TimeSpan duration)
    {
        try
        {
            await Task.Delay(duration, timer.Token);
            if (FindSession(session.State.Id) is not null)
            {
                await session.SendAsync("MODE", [channel, "-b", mask], IrcOutboundPriority.Automation, timer.Token);
                PublishStatus(session, SessionEventKind.Mode, $"Timed ban expired in {channel}: {mask}");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (FindSession(session.State.Id) is not null)
            {
                PublishStatus(session, SessionEventKind.Error, $"Could not expire timed ban {mask} in {channel}: {exception.Message}");
            }
        }
        finally
        {
            _sessionTransientState.RemoveTimedBan(timerId);
            timer.Dispose();
        }
    }

    private ValueTask<CommandResult> MassOpAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SetSelectedMemberModeAsync('o', true, member => !member.PrefixModes.Contains('o'), includeSelf: false, cancellationToken);

    private ValueTask<CommandResult> MassDeopAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SetSelectedMemberModeAsync('o', false, member => member.PrefixModes.Contains('o'), includeSelf: false, cancellationToken);

    private ValueTask<CommandResult> MassVoiceAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SetSelectedMemberModeAsync(
            'v',
            true,
            member => !member.PrefixModes.Contains('o') && !member.PrefixModes.Contains('v'),
            includeSelf: false,
            cancellationToken);

    private ValueTask<CommandResult> MassDevoiceAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SetSelectedMemberModeAsync('v', false, member => member.PrefixModes.Contains('v'), includeSelf: false, cancellationToken);

    private async ValueTask<CommandResult> MultiModeAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (input.Arguments.Count < 2 || input.Arguments[0].Length != 2 || input.Arguments[0][0] is not ('+' or '-'))
        {
            return CommandResult.Failure("Usage: /mmode <+mode|-mode> <nick> [nick...]");
        }

        return await SetRequestedMemberModeAsync(
            input.Arguments[0][1],
            input.Arguments[0][0] == '+',
            input.Arguments.Skip(1).ToArray(),
            "Usage: /mmode <+mode|-mode> <nick> [nick...]",
            cancellationToken);
    }

    private ValueTask<CommandResult> BanListAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        RequestChannelListAsync(input, 'b', "/banlist [channel]", cancellationToken);

    private ValueTask<CommandResult> ExceptListAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        RequestChannelListAsync(input, 'e', "/exceptlist [channel]", cancellationToken);

    private ValueTask<CommandResult> InviteListAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        RequestChannelListAsync(input, 'I', "/invitelist [channel]", cancellationToken);

    private ValueTask<CommandResult> QuietListAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        RequestChannelListAsync(input, 'q', "/quietlist [channel]", cancellationToken);

    private async ValueTask<CommandResult> RequestChannelListAsync(
        CommandInput input,
        char mode,
        string usage,
        CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        if (session is null) return failure;
        if (input.Arguments.Count > 1) return CommandResult.Failure($"Usage: {usage}");

        var channelName = input.Arguments.Count == 1 ? input.Arguments[0] : ActiveChannel();
        if (channelName is null)
        {
            return CommandResult.Failure($"Usage: {usage}");
        }

        if (!session.Features.IsChannel(channelName))
        {
            return CommandResult.Failure($"'{channelName}' is not a channel");
        }

        if (!session.Features.ChannelModesA.Contains(mode) || session.Features.IsPrefixMode(mode))
        {
            return CommandResult.Failure($"This server does not advertise channel list mode +{mode}");
        }

        if (!session.State.TryGetChannel(channelName, out var channel))
        {
            return CommandResult.Failure($"You are not on {channelName}");
        }

        channel!.BeginChannelListSynchronization(mode);
        await session.SendAsync("MODE", [channelName, $"+{mode}"], cancellationToken: cancellationToken);
        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> UnbanAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (!TryGetOperatorChannel(out var session, out var channel, out var failure))
        {
            return failure;
        }

        if (input.Arguments.Count != 1)
        {
            return CommandResult.Failure("Usage: /unban <mask>");
        }

        await session!.SendAsync("MODE", [channel!.Name, "-b", input.Arguments[0]], cancellationToken: cancellationToken);
        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> ClearBansAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (!TryGetOperatorChannel(out var session, out var channel, out var failure))
        {
            return failure;
        }

        if (!channel!.BanListSynchronized)
        {
            return CommandResult.Failure("The ban list is not synchronized; run /banlist first.");
        }

        return await SendModeBatchesAsync(session!, channel, 'b', false, channel.Bans, cancellationToken);
    }

    private async ValueTask<CommandResult> AppendTopicAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (!TryGetOperatorChannel(out var session, out var channel, out var failure))
        {
            return failure;
        }

        if (string.IsNullOrWhiteSpace(input.RawArguments))
        {
            return CommandResult.Failure("Usage: /appendtopic <text>");
        }

        var topic = string.IsNullOrEmpty(channel!.Topic) ? input.RawArguments : $"{channel.Topic} {input.RawArguments}";
        await session!.SendAsync("TOPIC", [channel.Name, topic], cancellationToken: cancellationToken);
        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> ClearTopicAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (!TryGetOperatorChannel(out var session, out var channel, out var failure))
        {
            return failure;
        }

        await session!.SendAsync("TOPIC", [channel!.Name, string.Empty], cancellationToken: cancellationToken);
        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> SetRequestedMemberModeAsync(
        char mode,
        bool adding,
        IReadOnlyList<string> targets,
        string usage,
        CancellationToken cancellationToken)
    {
        if (!TryGetOperatorChannel(out var session, out var channel, out var failure))
        {
            return failure;
        }

        if (!session!.Features.IsPrefixMode(mode))
        {
            return CommandResult.Failure($"This server does not advertise channel prefix mode '{mode}'.");
        }

        if (targets.Count == 0)
        {
            return CommandResult.Failure($"Usage: {usage}");
        }

        var unknown = targets.FirstOrDefault(target => !channel!.TryGetMember(target, out _));
        if (unknown is not null)
        {
            return CommandResult.Failure($"'{unknown}' is not in the synchronized member list for {channel!.Name}.");
        }

        return await SendModeBatchesAsync(session, channel!, mode, adding, targets, cancellationToken);
    }

    private async ValueTask<CommandResult> SetSelectedMemberModeAsync(
        char mode,
        bool adding,
        Func<ChannelMemberState, bool> predicate,
        bool includeSelf,
        CancellationToken cancellationToken)
    {
        if (!TryGetOperatorChannel(out var session, out var channel, out var failure))
        {
            return failure;
        }

        var comparer = new IrcNameComparer(session!.State.CaseMapping);
        var targets = channel!.Members
            .Where(predicate)
            .Where(member => includeSelf || !comparer.Equals(member.Nickname, session.CurrentNickname))
            .Select(member => member.Nickname)
            .ToArray();
        return await SendModeBatchesAsync(session, channel, mode, adding, targets, cancellationToken);
    }

    private static async ValueTask<CommandResult> SendModeBatchesAsync(
        IrcNetworkSession session,
        ChannelState channel,
        char mode,
        bool adding,
        IEnumerable<string> targets,
        CancellationToken cancellationToken,
        IrcOutboundPriority priority = IrcOutboundPriority.Interactive)
    {
        var batches = ChannelModeBatcher.Create(mode, adding, targets, session.Features.ModesPerCommand);
        if (batches.Count == 0)
        {
            return CommandResult.Success("No matching channel members or masks.");
        }

        foreach (var batch in batches)
        {
            await session.SendAsync(
                "MODE",
                [channel.Name, batch.ModeString, .. batch.Targets],
                priority,
                cancellationToken: cancellationToken);
        }

        return CommandResult.Success();
    }

    private bool TryGetOperatorChannel(
        out IrcNetworkSession? session,
        out ChannelState? channel,
        out CommandResult failure)
    {
        session = ActiveSession();
        channel = null;
        if (session is null)
        {
            failure = CommandResult.Failure("Not connected. Use /server first.");
            return false;
        }

        var channelName = ActiveChannel();
        if (channelName is null || !session.State.TryGetChannel(channelName, out channel))
        {
            failure = CommandResult.Failure("This command requires an active joined channel.");
            return false;
        }

        if (!channel!.NamesSynchronized)
        {
            failure = CommandResult.Failure($"Member state for {channel.Name} is not synchronized yet.");
            return false;
        }

        if (!channel.TryGetMember(session.CurrentNickname, out var self) || !HasOperatorPrivilege(session.Features, self!))
        {
            failure = CommandResult.Failure($"You are not a channel operator in {channel.Name}.");
            return false;
        }

        failure = CommandResult.Success();
        return true;
    }

    private static bool HasOperatorPrivilege(ServerFeatures features, ChannelMemberState member)
    {
        var orderedModes = features.PrefixModes.Keys.ToArray();
        var operatorIndex = Array.IndexOf(orderedModes, 'o');
        return operatorIndex >= 0 && member.PrefixModes.Any(mode =>
        {
            var index = Array.IndexOf(orderedModes, mode);
            return index >= 0 && index <= operatorIndex;
        });
    }

}
