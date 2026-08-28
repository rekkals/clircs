using Clircs.Commands;
using Clircs.Networking;
using Clircs.Protocol;
using Clircs.Protection;
using Clircs.Sessions;
using Clircs.State;
using Clircs.Users;

namespace Clircs.ConsoleClient;

// Owns user directories, mass operations, and channel and personal protection commands.
internal sealed partial class ClientApplication
{
    private ValueTask<CommandResult> AddUserAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        AddUserRecordAsync(input, UserRole.None);

    private ValueTask<CommandResult> AddBotAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        AddUserRecordAsync(input, UserRole.Bot);

    private ValueTask<CommandResult> AddUserRecordAsync(CommandInput input, UserRole initialRoles)
    {
        var usage = AddUserUsage(initialRoles);
        if (!TryGetUserDirectory(out var session, out var directory, out var failure))
        {
            return ValueTask.FromResult(failure);
        }

        if (input.Arguments.Count is < 1 or > 2)
        {
            return ValueTask.FromResult(CommandResult.Failure(usage));
        }

        try
        {
            string? mask = null;
            try
            {
                if (input.Arguments.Count == 1 && initialRoles == UserRole.None)
                {
                    (mask, _) = ResolveUserMask(session!, input.Arguments[0]);
                }
                else if (input.Arguments.Count == 2)
                {
                    (mask, _) = ResolveUserMask(session!, input.Arguments[1]);
                }
            }
            catch (InvalidOperationException)
            {
                return ValueTask.FromResult(CommandResult.Failure(usage));
            }

            var user = directory!.Add(input.Arguments[0], mask, initialRoles);
            SaveUserDirectory(directory);
            return ValueTask.FromResult(CommandResult.Success(
                $"Added {user.Handle} to {ProfileFor(session!)!.DisplayName}." +
                (mask is null ? string.Empty : $" Hostmask: {mask}.")));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(CommandResult.Failure(exception.Message));
        }
    }

    internal static string AddUserUsage(UserRole initialRoles) =>
        initialRoles == UserRole.Bot
            ? "Usage: /addbot <handle> [hostmask|nickname]"
            : "Usage: /adduser <handle> [hostmask|nickname]";

    private ValueTask<CommandResult> RemoveUserAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (!TryGetUserDirectory(out _, out var directory, out var failure))
        {
            return ValueTask.FromResult(failure);
        }

        if (input.Arguments.Count != 1)
        {
            return ValueTask.FromResult(CommandResult.Failure("Usage: /remuser <handle>"));
        }

        if (!directory!.Remove(input.Arguments[0]))
        {
            return ValueTask.FromResult(CommandResult.Failure($"No user named '{input.Arguments[0]}'."));
        }

        SaveUserDirectory(directory);
        return ValueTask.FromResult(CommandResult.Success($"Removed user '{input.Arguments[0]}'"));
    }

    private ValueTask<CommandResult> AddHostAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (!TryGetUserDirectory(out var session, out var directory, out var failure))
        {
            return ValueTask.FromResult(failure);
        }

        if (input.Arguments.Count != 2)
        {
            return ValueTask.FromResult(CommandResult.Failure("Usage: /addhost <handle> <mask-or-visible-nick>"));
        }

        try
        {
            var user = directory!.Find(input.Arguments[0]);
            if (user is null)
            {
                return ValueTask.FromResult(CommandResult.Failure($"No user named '{input.Arguments[0]}'."));
            }

            var (mask, derivation) = ResolveUserMask(session!, input.Arguments[1]);
            directory.AddHostmask(user, mask, session!.State.CaseMapping);
            SaveUserDirectory(directory);
            return ValueTask.FromResult(CommandResult.Success(
                $"Added {mask} to {user.Handle}." + (derivation is null ? string.Empty : $" {derivation}")));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(CommandResult.Failure(exception.Message));
        }
    }

    private ValueTask<CommandResult> RemoveHostAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (!TryGetUserDirectory(out _, out var directory, out var failure))
        {
            return ValueTask.FromResult(failure);
        }

        if (input.Arguments.Count != 2)
        {
            return ValueTask.FromResult(CommandResult.Failure("Usage: /remhost <handle> <mask>"));
        }

        var user = directory!.Find(input.Arguments[0]);
        if (user is null || !user.RemoveHostmask(input.Arguments[1]))
        {
            return ValueTask.FromResult(CommandResult.Failure("User or hostmask not found."));
        }

        SaveUserDirectory(directory);
        return ValueTask.FromResult(CommandResult.Success($"Removed {input.Arguments[1]} from {user.Handle}."));
    }

    private ValueTask<CommandResult> ChangeAttributesAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (!TryGetUserDirectory(out var session, out var directory, out var failure))
        {
            return ValueTask.FromResult(failure);
        }

        if (input.Arguments.Count is < 2 or > 3)
        {
            return ValueTask.FromResult(CommandResult.Failure("Usage: /chattr <handle> <changes> [channel]"));
        }

        try
        {
            var user = directory!.Find(input.Arguments[0]);
            if (user is null)
            {
                return ValueTask.FromResult(CommandResult.Failure($"No user named '{input.Arguments[0]}'."));
            }

            var changes = UserRoleParser.ParseChanges(input.Arguments[1]);
            var channel = input.Arguments.Count == 3 ? input.Arguments[2] : null;
            if (channel is not null && !session!.Features.IsChannel(channel))
            {
                return ValueTask.FromResult(CommandResult.Failure($"'{channel}' is not a channel name on this server."));
            }

            user.ChangeRoles(changes.Add, changes.Remove, channel, session!.State.CaseMapping);
            SaveUserDirectory(directory);
            return ValueTask.FromResult(CommandResult.Success(
                $"{user.Handle} roles{(channel is null ? string.Empty : $" in {channel}")}: " +
                UserRoleParser.Format(user.EffectiveRoles(channel, session.State.CaseMapping))));
        }
        catch (ArgumentException exception)
        {
            return ValueTask.FromResult(CommandResult.Failure(exception.Message));
        }
    }

    private ValueTask<CommandResult> AddUserChannelAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        ChangeUserChannelAsync(input, adding: true);

    private ValueTask<CommandResult> RemoveUserChannelAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        ChangeUserChannelAsync(input, adding: false);

    private ValueTask<CommandResult> ChangeUserChannelAsync(CommandInput input, bool adding)
    {
        if (!TryGetUserDirectory(out var session, out var directory, out var failure))
        {
            return ValueTask.FromResult(failure);
        }

        if (input.Arguments.Count != 2 || !session!.Features.IsChannel(input.Arguments[1]))
        {
            return ValueTask.FromResult(CommandResult.Failure($"Usage: /{(adding ? "addchan" : "remchan")} <handle> <channel>"));
        }

        var user = directory!.Find(input.Arguments[0]);
        if (user is null)
        {
            return ValueTask.FromResult(CommandResult.Failure($"No user named '{input.Arguments[0]}'."));
        }

        if (adding)
        {
            user.AddChannel(input.Arguments[1], session.State.CaseMapping);
        }
        else if (!user.RemoveChannel(input.Arguments[1], session.State.CaseMapping))
        {
            return ValueTask.FromResult(CommandResult.Failure("No matching channel policy exists."));
        }

        SaveUserDirectory(directory);
        return ValueTask.FromResult(CommandResult.Success(
            $"{(adding ? "Added" : "Removed")} {input.Arguments[1]} {(adding ? "for" : "from")} {user.Handle}."));
    }

    private ValueTask<CommandResult> ChangeUserInfoAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (!TryGetUserDirectory(out var session, out var directory, out var failure))
        {
            return ValueTask.FromResult(failure);
        }

        if (input.Arguments.Count < 2)
        {
            return ValueTask.FromResult(CommandResult.Failure("Usage: /chinfo <handle> [channel] <text>"));
        }

        var user = directory!.Find(input.Arguments[0]);
        if (user is null)
        {
            return ValueTask.FromResult(CommandResult.Failure($"No user named '{input.Arguments[0]}'."));
        }

        var hasChannel = session!.Features.IsChannel(input.Arguments[1]);
        if (hasChannel && input.Arguments.Count < 3)
        {
            return ValueTask.FromResult(CommandResult.Failure("A channel comment requires text."));
        }

        var channel = hasChannel ? input.Arguments[1] : null;
        var text = string.Join(' ', input.Arguments.Skip(hasChannel ? 2 : 1));
        if (text.Equals("none", StringComparison.OrdinalIgnoreCase)) text = string.Empty;
        user.SetComment(text, channel, session.State.CaseMapping);
        SaveUserDirectory(directory);
        return ValueTask.FromResult(CommandResult.Success(text.Length == 0
            ? $"Removed {(channel is null ? "global" : channel)} infoline for {user.Handle}."
            : $"Updated {(channel is null ? "global" : channel)} infoline for {user.Handle}."));
    }

    private ValueTask<CommandResult> UsersAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (!TryGetUserDirectory(out var session, out var directory, out var failure))
        {
            return ValueTask.FromResult(failure);
        }

        try
        {
            if (input.Arguments.Count is 2 or 3 &&
                input.Arguments[0].Equals("export", StringComparison.OrdinalIgnoreCase))
            {
                var overwrite = input.Arguments.Count == 3 && input.Arguments[2].Equals("--force", StringComparison.OrdinalIgnoreCase);
                if (input.Arguments.Count == 3 && !overwrite)
                {
                    return ValueTask.FromResult(CommandResult.Failure("Usage: /users export <path> [--force]"));
                }

                _userDirectoryStore.Export(directory!, input.Arguments[1], overwrite);
                return ValueTask.FromResult(CommandResult.Success($"Exported the active network user directory to {Path.GetFullPath(input.Arguments[1])}."));
            }

            if (input.Arguments.Count is 2 or 3 &&
                input.Arguments[0].Equals("import", StringComparison.OrdinalIgnoreCase))
            {
                var imported = _userDirectoryStore.LoadFrom(ProfileFor(session!)!.Id, input.Arguments[1]);
                if (input.Arguments.Count == 2)
                {
                    return ValueTask.FromResult(CommandResult.Success(
                        $"Import preview: {imported.Users.Count} user(s), {imported.PolicyBans.Count} policy ban(s). " +
                        "Re-run with --force to replace the active network directory."));
                }

                if (!input.Arguments[2].Equals("--force", StringComparison.OrdinalIgnoreCase))
                {
                    return ValueTask.FromResult(CommandResult.Failure("Usage: /users import <path> [--force]"));
                }

                _userDirectoryStore.Save(imported);
                _userAndChannelPolicy.ReplaceDirectory(imported);
                return ValueTask.FromResult(CommandResult.Success(
                    $"Imported {imported.Users.Count} user(s) and {imported.PolicyBans.Count} policy ban(s). The previous file was backed up."));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(CommandResult.Failure(exception.Message));
        }

        if (input.Arguments.Count != 0)
        {
            return ValueTask.FromResult(CommandResult.Failure("Usage: /users | /users export <path> [--force] | /users import <path> [--force]"));
        }

        var users = directory!.Users;
        if (users.Count == 0)
        {
            return ValueTask.FromResult(CommandResult.Success("The active network user directory is empty."));
        }

        var network = ProfileFor(session!)?.DisplayName ?? session!.Features.NetworkName ?? session.State.DisplayName;
        var visibleMasks = session!.State.Channels
            .SelectMany(channel => channel.Members)
            .Select(member => member.FullMask)
            .OfType<string>()
            .Where(mask => !string.IsNullOrWhiteSpace(mask))
            .ToArray();
        var rows = users
            .OrderBy(user => user.Handle, StringComparer.OrdinalIgnoreCase)
            .SelectMany(user => UserRows(user, visibleMasks, session.State.CaseMapping))
            .ToArray();
        return ValueTask.FromResult(CommandResult.Success(new PresentationBlock(
            "USERS:",
            Table: new PresentationTable(["Handle", "Masks", "Flags", "Roles", "Channels"], rows),
            Summary: $"{users.Count} user{(users.Count == 1 ? string.Empty : "s")}",
            TitleHighlight: network)));
    }

    private ValueTask<CommandResult> UserSummaryAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (!TryGetUserDirectory(out var session, out var directory, out var failure))
        {
            return ValueTask.FromResult(failure);
        }

        var users = directory!.Users;
        var network = ProfileFor(session!)?.DisplayName ?? session!.Features.NetworkName ?? session.State.DisplayName;
        if (users.Count == 0)
        {
            return ValueTask.FromResult(CommandResult.Success($"No users are defined for {network}."));
        }

        var summaries = new (string Label, UserRole? Role)[]
        {
            ("Users", null),
            ("Bots", UserRole.Bot),
            ("Operator eligible", UserRole.OperatorEligible),
            ("Voice eligible", UserRole.VoiceEligible),
            ("Auto-op", UserRole.AutoOp),
            ("Auto-voice", UserRole.AutoVoice),
            ("Protected", UserRole.Protected),
            ("Deop", UserRole.Deop),
            ("Kick on join", UserRole.KickOnJoin),
            ("Protection exempt", UserRole.ProtectionExempt)
        };
        var grid = summaries.Select(summary =>
            $"{summary.Label}: {(summary.Role is null ? users.Count : users.Count(user => user.Roles.HasFlag(summary.Role.Value)))}").ToArray();
        return ValueTask.FromResult(CommandResult.Success(new PresentationBlock(
            $"User summary: {network}",
            Grid: grid)));
    }

    private ValueTask<CommandResult> ChannelProtectionAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        var arguments = input.Arguments.ToArray();
        var operation = arguments.Length == 0 ? "status" : arguments[0].ToLowerInvariant();
        var tail = arguments.Skip(1).ToArray();

        try
        {
            if (operation is "on" or "off")
            {
                if (!TryFriendlyChannelScope(tail, createUnknown: true, out var scope, out var label, out var created, out var failure))
                    return ValueTask.FromResult(failure);
                _protectionStore.SetChannelEnabled(scope!, operation == "on");
                var suffix = created ? " An unconfigured network profile was created for it." : string.Empty;
                return ValueTask.FromResult(CommandResult.Success(
                    $"Channel protection turned {operation} for {label}. " +
                    $"Action: {ChannelActionName(_protectionStore.SettingsFor(scope!).ChannelAction)}.{suffix}"));
            }

            if (operation is "status" or "show")
            {
                if (!TryFriendlyChannelScope(tail, createUnknown: false, out var scope, out var label, out _, out var failure))
                    return ValueTask.FromResult(failure);
                var effective = _protectionStore.SettingsFor(scope!);
                return ValueTask.FromResult(CommandResult.Success(FriendlyProtectionPresentation(
                    $"Channel protection: {label}", effective, ChannelProtectionDetectors)));
            }

            if (operation == "action")
            {
                if (tail.Length == 0 || !TryParseChannelProtectionAction(tail[0], out var action))
                    return ValueTask.FromResult(CommandResult.Failure(
                        "Usage: /cprot action <monitor|kick|kickban> [network] [channel]"));
                if (!TryFriendlyChannelScope(tail.Skip(1).ToArray(), true, out var actionScope, out var actionLabel,
                        out _, out var actionFailure))
                    return ValueTask.FromResult(actionFailure);
                _protectionStore.SetChannelAction(actionScope!, action);
                return ValueTask.FromResult(CommandResult.Success(
                    $"Channel protection action for {actionLabel} changed to {ChannelActionName(action)}."));
            }

            if (operation == "bantime")
            {
                if (tail.Length == 0)
                    return ValueTask.FromResult(CommandResult.Failure(
                        "Usage: /cprot bantime <duration|permanent> [network] [channel]"));
                var permanent = tail[0].Equals("permanent", StringComparison.OrdinalIgnoreCase);
                var banDuration = TimeSpan.Zero;
                if (!permanent && (!TryParseDuration(tail[0], out banDuration) ||
                                   banDuration > TimeSpan.FromDays(30)))
                    return ValueTask.FromResult(CommandResult.Failure(
                        "Ban time must be permanent or from 1 second through 30 days, such as 30m."));
                if (!TryFriendlyChannelScope(tail.Skip(1).ToArray(), true, out var banScope, out var banLabel,
                        out _, out var banFailure))
                    return ValueTask.FromResult(banFailure);
                var banSeconds = permanent ? 0 : (int)Math.Ceiling(banDuration.TotalSeconds);
                _protectionStore.SetBanSeconds(banScope!, banSeconds);
                return ValueTask.FromResult(CommandResult.Success(
                    $"Channel protection ban time for {banLabel} changed to " +
                    $"{(permanent ? "permanent" : FormatDuration(banDuration))}."));
            }

            var detector = ParseFriendlyProtectionDetector(operation, personal: false);
            if (detector is null)
                return ValueTask.FromResult(CommandResult.Failure(
                    "Usage: /cprot on|off|status [network] [channel], or /cprot <detector> <count> <seconds> [network] [channel]"));

            if (tail.Length == 0)
                return ValueTask.FromResult(CommandResult.Failure(
                    $"Usage: /cprot {operation} <count> <seconds>|off|default [network] [channel]"));

            if (tail[0].Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryFriendlyChannelScope(tail.Skip(1).ToArray(), true, out var scope, out var label, out var created, out var failure))
                    return ValueTask.FromResult(failure);
                _protectionStore.SetRule(scope!, detector.Value, enabled: false);
                return ValueTask.FromResult(CommandResult.Success(
                    $"{DetectorName(detector.Value)} detection turned off for {label}." +
                    (created ? " An unconfigured network profile was created for it." : string.Empty)));
            }

            if (tail[0].Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryFriendlyChannelScope(tail.Skip(1).ToArray(), false, out var scope, out var label, out _, out var failure))
                    return ValueTask.FromResult(failure);
                var changed = _protectionStore.ClearRule(scope!, detector.Value);
                return ValueTask.FromResult(CommandResult.Success(changed
                    ? $"{DetectorName(detector.Value)} now inherits its defaults for {label}."
                    : $"{DetectorName(detector.Value)} was already inherited for {label}."));
            }

            if (tail.Length < 2 || !int.TryParse(tail[0], out var count) ||
                !TryFriendlyProtectionDuration(tail[1], out var duration) || duration.TotalSeconds > 3600)
            {
                return ValueTask.FromResult(CommandResult.Failure(
                    $"Usage: /cprot {operation} <count> <seconds> [network] [channel]"));
            }
            if (!TryFriendlyChannelScope(tail.Skip(2).ToArray(), true, out var ruleScope, out var ruleLabel, out var ruleCreated, out var ruleFailure))
                return ValueTask.FromResult(ruleFailure);
            var seconds = (int)Math.Ceiling(duration.TotalSeconds);
            _protectionStore.SetRule(ruleScope!, detector.Value, enabled: true, threshold: count, windowSeconds: seconds);
            return ValueTask.FromResult(CommandResult.Success(
                $"{DetectorName(detector.Value)} detection set to {count} in {seconds}s for {ruleLabel}." +
                (ruleCreated ? " An unconfigured network profile was created for it." : string.Empty)));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or
            IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(CommandResult.Failure(exception.Message));
        }
    }

    private ValueTask<CommandResult> PersonalProtectionAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        var arguments = input.Arguments.ToArray();
        var operation = arguments.Length == 0 ? "status" : arguments[0].ToLowerInvariant();
        var tail = arguments.Skip(1).ToArray();
        try
        {
            if (operation is "on" or "off")
            {
                if (!TryFriendlyNetworkScope(tail, true, out var scope, out var label, out var created, out var failure))
                    return ValueTask.FromResult(failure);
                _protectionStore.SetPersonalEnabled(scope!, operation == "on");
                return ValueTask.FromResult(CommandResult.Success(
                    $"Personal protection turned {operation} for {label}. Triggered clients are ignored locally." +
                    (created ? " An unconfigured network profile was created for it." : string.Empty)));
            }
            if (operation is "status" or "show")
            {
                if (!TryFriendlyNetworkScope(tail, false, out var scope, out var label, out _, out var failure))
                    return ValueTask.FromResult(failure);
                return ValueTask.FromResult(CommandResult.Success(FriendlyProtectionPresentation(
                    $"Personal protection: {label}", _protectionStore.SettingsFor(scope!), PersonalProtectionDetectors)));
            }

            if (operation == "ignoretime")
            {
                if (tail.Length == 0 || !TryParseDuration(tail[0], out var ignoreDuration) ||
                    ignoreDuration > TimeSpan.FromDays(1))
                    return ValueTask.FromResult(CommandResult.Failure(
                        "Usage: /pprot ignoretime <duration> [network] (maximum 1 day)"));
                if (!TryFriendlyNetworkScope(tail.Skip(1).ToArray(), true, out var ignoreScope, out var ignoreLabel,
                        out _, out var ignoreFailure))
                    return ValueTask.FromResult(ignoreFailure);
                var ignoreSeconds = (int)Math.Ceiling(ignoreDuration.TotalSeconds);
                _protectionStore.SetPersonalIgnoreSeconds(ignoreScope!, ignoreSeconds);
                return ValueTask.FromResult(CommandResult.Success(
                    $"Personal protection ignore time for {ignoreLabel} changed to {FormatDuration(ignoreDuration)}."));
            }

            var detector = ParseFriendlyProtectionDetector(operation, personal: true);
            if (detector is null)
                return ValueTask.FromResult(CommandResult.Failure(
                    "Usage: /pprot on|off|status [network], or /pprot <message|notice|ctcp|invite> <count> <seconds> [network]"));
            if (tail.Length == 0)
                return ValueTask.FromResult(CommandResult.Failure(
                    $"Usage: /pprot {operation} <count> <seconds>|off|default [network]"));

            if (tail[0].Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryFriendlyNetworkScope(tail.Skip(1).ToArray(), true, out var scope, out var label, out var created, out var failure))
                    return ValueTask.FromResult(failure);
                _protectionStore.SetRule(scope!, detector.Value, enabled: false);
                return ValueTask.FromResult(CommandResult.Success(
                    $"{DetectorName(detector.Value)} detection turned off for {label}." +
                    (created ? " An unconfigured network profile was created for it." : string.Empty)));
            }
            if (tail[0].Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryFriendlyNetworkScope(tail.Skip(1).ToArray(), false, out var scope, out var label, out _, out var failure))
                    return ValueTask.FromResult(failure);
                var changed = _protectionStore.ClearRule(scope!, detector.Value);
                return ValueTask.FromResult(CommandResult.Success(changed
                    ? $"{DetectorName(detector.Value)} now inherits its defaults for {label}."
                    : $"{DetectorName(detector.Value)} was already inherited for {label}."));
            }
            if (tail.Length < 2 || !int.TryParse(tail[0], out var count) ||
                !TryFriendlyProtectionDuration(tail[1], out var duration) || duration.TotalSeconds > 3600)
            {
                return ValueTask.FromResult(CommandResult.Failure(
                    $"Usage: /pprot {operation} <count> <seconds> [network]"));
            }
            if (!TryFriendlyNetworkScope(tail.Skip(2).ToArray(), true, out var ruleScope, out var ruleLabel, out var ruleCreated, out var ruleFailure))
                return ValueTask.FromResult(ruleFailure);
            var seconds = (int)Math.Ceiling(duration.TotalSeconds);
            _protectionStore.SetRule(ruleScope!, detector.Value, enabled: true, threshold: count, windowSeconds: seconds);
            return ValueTask.FromResult(CommandResult.Success(
                $"{DetectorName(detector.Value)} detection set to {count} in {seconds}s for {ruleLabel}." +
                (ruleCreated ? " An unconfigured network profile was created for it." : string.Empty)));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or
            IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(CommandResult.Failure(exception.Message));
        }
    }

    private ValueTask<CommandResult> ProtectAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        var arguments = input.Arguments.ToList();
        var operation = arguments.Count == 0 ? "status" : arguments[0].ToLowerInvariant();
        var session = ActiveSession();

        try
        {
            switch (operation)
            {
                case "status":
                {
                    var effective = EffectiveProtection(session, ActiveChannel());
                    return ValueTask.FromResult(CommandResult.Success(ProtectionPresentation(
                        "Protection status", effective, includeRules: false)));
                }
                case "settings":
                case "show":
                {
                    var effective = EffectiveProtection(session, ActiveChannel());
                    var detector = arguments.Count > 1 ? ParseProtectionDetector(arguments[1]) : null;
                    if (arguments.Count > 2 || arguments.Count > 1 && detector is null)
                    {
                        return ValueTask.FromResult(CommandResult.Failure("Usage: /protect show [detector]"));
                    }
                    return ValueTask.FromResult(CommandResult.Success(ProtectionPresentation(
                        detector is null ? "Protection settings" : $"Protection: {DetectorName(detector.Value)}",
                        effective,
                        includeRules: true,
                        detector)));
                }
                case "channel":
                case "personal":
                {
                    if (arguments.Count < 2 || !TryParseOnOff(arguments[1], out var enabled))
                    {
                        return ValueTask.FromResult(CommandResult.Failure($"Usage: /protect {operation} on|off [--global|--network|--channel [name]]"));
                    }
                    if (!TryProtectionScope(session, arguments.Skip(2).ToArray(), operation == "channel", out var scope, out var scopeFailure))
                    {
                        return ValueTask.FromResult(scopeFailure);
                    }
                    if (operation == "channel")
                        _protectionStore.SetChannelEnabled(scope!, enabled);
                    else
                        _protectionStore.SetPersonalEnabled(scope!, enabled);
                    return ValueTask.FromResult(CommandResult.Success(
                        $"{(operation == "channel" ? "Channel" : "Personal")} protection {(enabled ? "enabled" : "disabled")} at {scope!.DisplayName} scope."));
                }
                case "monitor":
                {
                    if (arguments.Count < 2 || !TryParseOnOff(arguments[1], out var enabled))
                    {
                        return ValueTask.FromResult(CommandResult.Failure("Usage: /protect monitor on|off [--global|--network|--channel [name]]"));
                    }
                    if (!TryProtectionScope(session, arguments.Skip(2).ToArray(), defaultChannel: false, out var scope, out var scopeFailure))
                    {
                        return ValueTask.FromResult(scopeFailure);
                    }
                    _protectionStore.SetMonitorOnly(scope!, enabled);
                    return ValueTask.FromResult(CommandResult.Success(
                        $"Monitor-only protection {(enabled ? "enabled" : "disabled")} at {scope!.DisplayName} scope."));
                }
                case "set":
                {
                    if (arguments.Count < 3)
                    {
                        return ValueTask.FromResult(CommandResult.Failure(
                            "Usage: /protect set <detector.count|detector.window|detector.enabled|exempt.*> <value> [scope]"));
                    }
                    if (!TryProtectionScope(session, arguments.Skip(3).ToArray(), defaultChannel: true, out var scope, out var scopeFailure))
                    {
                        return ValueTask.FromResult(scopeFailure);
                    }
                    ChangeProtectionSetting(scope!, arguments[1], arguments[2]);
                    return ValueTask.FromResult(CommandResult.Success(
                        $"Protection setting {arguments[1]} changed to {arguments[2]} at {scope!.DisplayName} scope."));
                }
                case "reset":
                {
                    if (!TryProtectionScope(session, arguments.Skip(1).ToArray(), defaultChannel: true, out var scope, out var scopeFailure))
                    {
                        return ValueTask.FromResult(scopeFailure);
                    }
                    var changed = _protectionStore.Reset(scope!);
                    return ValueTask.FromResult(CommandResult.Success(changed
                        ? $"Protection overrides reset at {scope!.DisplayName} scope."
                        : $"No protection override existed at {scope!.DisplayName} scope."));
                }
                case "audit":
                {
                    if (session is null)
                    {
                        return ValueTask.FromResult(CommandResult.Failure("Connect to a network before opening its protection audit window."));
                    }
                    return ValueTask.FromResult(SwitchTo(session, ProtectionAuditBuffer(session)));
                }
                case "counters":
                {
                    var counters = _userAndChannelPolicy.Counters(DateTimeOffset.Now);
                    if (counters.Count == 0)
                    {
                        return ValueTask.FromResult(CommandResult.Success("No active protection counters."));
                    }
                    return ValueTask.FromResult(CommandResult.Success(new PresentationBlock(
                        "Protection counters",
                        Table: new PresentationTable(
                            ["Detector", "Actor", "Channel", "Count"],
                            counters.Select(counter => (IReadOnlyList<string>)new[]
                            {
                                DetectorName(counter.Detector), counter.Actor, counter.Channel ?? "private", counter.Count.ToString()
                            }).ToArray()))));
                }
                case "test":
                    return ValueTask.FromResult(ProtectionTest(session, arguments));
                default:
                    return ValueTask.FromResult(CommandResult.Failure(
                        "Usage: /protect [status|settings|show|channel|personal|monitor|set|reset|audit|counters|test]"));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(CommandResult.Failure(exception.Message));
        }
    }

    private ValueTask<CommandResult> UserWhoisAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (!TryGetUserDirectory(out var session, out var directory, out var failure))
        {
            return ValueTask.FromResult(failure);
        }

        if (input.Arguments.Count != 1)
        {
            return ValueTask.FromResult(CommandResult.Failure("Usage: /uwhois <handle-or-visible-nick>"));
        }

        var user = directory!.Find(input.Arguments[0]);
        string? matchedMask = null;
        if (user is null)
        {
            var member = session!.State.Channels
                .SelectMany(channel => channel.Members)
                .FirstOrDefault(member => new IrcNameComparer(session.State.CaseMapping).Equals(member.Nickname, input.Arguments[0]));
            if (member?.FullMask is not null)
            {
                var match = directory.Match(member.FullMask, session.State.CaseMapping);
                if (match.Conflict)
                {
                    return ValueTask.FromResult(CommandResult.Failure(
                        $"Hostmask conflict between: {string.Join(", ", match.Candidates.Select(candidate => candidate.Handle))}"));
                }

                user = match.User;
                matchedMask = match.Hostmask;
            }
        }

        return user is null
            ? ValueTask.FromResult(CommandResult.Failure("No matching user record."))
            : ValueTask.FromResult(CommandResult.Success(FormatUserRecord(user, matchedMask)));
    }

    private ValueTask<CommandResult> UserFindAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (!TryGetUserDirectory(out var session, out var directory, out var failure))
        {
            return ValueTask.FromResult(failure);
        }

        var channelName = input.Arguments.Count == 1 ? input.Arguments[0] : ActiveChannel();
        if (channelName is null || !session!.State.TryGetChannel(channelName, out var channel))
        {
            return ValueTask.FromResult(CommandResult.Failure("Usage: /ufind [joined-channel]"));
        }

        var lines = channel!.Members.Select(member =>
        {
            var match = member.FullMask is null ? null : directory!.Match(member.FullMask, session.State.CaseMapping);
            return match?.Conflict == true
                ? $"{member.Nickname}: CONFLICT ({string.Join(", ", match.Candidates.Select(user => user.Handle))})"
                : match?.User is null ? null : $"{member.Nickname}: {match.User.Handle} via {match.Hostmask}";
        }).Where(line => line is not null).ToArray();
        return ValueTask.FromResult(CommandResult.Success(lines.Length == 0 ? "No visible members match user records." : string.Join(Environment.NewLine, lines!)));
    }

    private async ValueTask<CommandResult> AddPolicyBanAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserDirectory(out var session, out var directory, out var failure))
        {
            return failure;
        }

        if (input.Arguments.Count < 2)
        {
            return CommandResult.Failure("Usage: /addban <nick-or-mask> <channel[,channel...]|*> [reason]");
        }

        try
        {
            var (mask, derivation) = ResolveUserMask(session!, input.Arguments[0]);
            var channels = input.Arguments[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (channels.Any(channel => channel != "*" && !session!.Features.IsChannel(channel)))
            {
                return CommandResult.Failure("Policy-ban channels must be valid channel names or *.");
            }

            var reason = input.Arguments.Count > 2 ? string.Join(' ', input.Arguments.Skip(2)) : null;
            var ban = directory!.AddPolicyBan(mask, channels, reason);
            SaveUserDirectory(directory);
            var enforced = 0;
            foreach (var joined in session!.State.Channels.Where(channel => PolicyAppliesToChannel(ban, channel.Name, session.State.CaseMapping)))
            {
                if (joined.TryGetMember(session.CurrentNickname, out var self) &&
                    HasOperatorPrivilege(session.Features, self!) &&
                    !joined.Bans.Contains(mask, StringComparer.OrdinalIgnoreCase))
                {
                    await session.SendAsync("MODE", [joined.Name, "+b", mask], IrcOutboundPriority.Automation, cancellationToken);
                    enforced++;
                }
            }

            return CommandResult.Success(
                $"Added policy ban {ban.ShortId}: {ban.Mask} on {string.Join(',', ban.Channels)}; enforced in {enforced} joined channel(s)." +
                (derivation is null ? string.Empty : $" {derivation}"));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return CommandResult.Failure(exception.Message);
        }
    }

    private ValueTask<CommandResult> RemovePolicyBanAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserDirectory(out _, out var directory, out var failure))
        {
            return ValueTask.FromResult(failure);
        }

        if (input.Arguments.Count != 1)
        {
            return ValueTask.FromResult(CommandResult.Failure("Usage: /remban <mask-or-id>"));
        }

        try
        {
            var removed = directory!.RemovePolicyBan(input.Arguments[0]);
            if (removed is null)
            {
                return ValueTask.FromResult(CommandResult.Failure("No matching persistent policy ban."));
            }

            SaveUserDirectory(directory);
            return ValueTask.FromResult(CommandResult.Success(
                $"Removed policy ban {removed.ShortId}. Existing live channel modes were left unchanged."));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(CommandResult.Failure(exception.Message));
        }
    }

    private ValueTask<CommandResult> PolicyBansAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserDirectory(out _, out var directory, out var failure))
        {
            return ValueTask.FromResult(failure);
        }

        if (input.Arguments.Count != 0)
        {
            return ValueTask.FromResult(CommandResult.Failure("Usage: /bans"));
        }

        var bans = directory!.PolicyBans;
        return ValueTask.FromResult(CommandResult.Success(bans.Count == 0
            ? "No persistent policy bans for this network."
            : string.Join(Environment.NewLine, bans.Select(ban =>
                $"{ban.ShortId} {ban.Mask} [{string.Join(',', ban.Channels)}]" +
                (ban.Reason.Length == 0 ? string.Empty : $" — {ban.Reason}")))));
    }

    private ValueTask<CommandResult> UserMassOpAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SetUserSelectedModeAsync('o', true, roles => roles.HasFlag(UserRole.OperatorEligible), cancellationToken);

    private ValueTask<CommandResult> UserMassDeopAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SetUserSelectedModeAsync('o', false, roles => !roles.HasFlag(UserRole.OperatorEligible), cancellationToken);

    private ValueTask<CommandResult> UserMassVoiceAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SetUserSelectedModeAsync('v', true, roles => roles.HasFlag(UserRole.VoiceEligible), cancellationToken);

    private ValueTask<CommandResult> UserMassDevoiceAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SetUserSelectedModeAsync('v', false, roles => !roles.HasFlag(UserRole.VoiceEligible), cancellationToken);

    private ValueTask<CommandResult> FilterKickAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        KickMatchingMembersAsync(input, matchNick: false, setBan: false, nonOperatorsOnly: false, cancellationToken);

    private ValueTask<CommandResult> FilterKickBanAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        KickMatchingMembersAsync(input, matchNick: false, setBan: true, nonOperatorsOnly: false, cancellationToken);

    private ValueTask<CommandResult> FindNickKickAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        KickMatchingMembersAsync(input, matchNick: true, setBan: false, nonOperatorsOnly: false, cancellationToken);

    private ValueTask<CommandResult> KickNonOperatorsAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        KickMatchingMembersAsync(input, matchNick: false, setBan: false, nonOperatorsOnly: true, cancellationToken);

    private async ValueTask<CommandResult> KickMatchingMembersAsync(
        CommandInput input,
        bool matchNick,
        bool setBan,
        bool nonOperatorsOnly,
        CancellationToken cancellationToken)
    {
        if (!TryGetOperatorChannel(out var session, out var channel, out var failure))
        {
            return failure;
        }

        if (!TryGetUserDirectory(out _, out var directory, out failure))
        {
            return failure;
        }

        string pattern;
        string reason;
        if (nonOperatorsOnly)
        {
            pattern = "*";
            reason = ResolveKickMessage(string.IsNullOrWhiteSpace(input.RawArguments) ? null : input.RawArguments);
        }
        else
        {
            if (input.Arguments.Count == 0)
            {
                return CommandResult.Failure($"Usage: /{(setBan ? "filterkickban" : matchNick ? "findnickkick" : "filterkick")} <wildcard> [reason]");
            }

            pattern = input.Arguments[0];
            reason = ResolveKickMessage(TrySplitFirst(input.RawArguments, out _, out var supplied) ? supplied : null);
        }

        var comparer = new IrcNameComparer(session!.State.CaseMapping);
        var targets = channel!.Members.Where(member =>
        {
            if (comparer.Equals(member.Nickname, session.CurrentNickname) ||
                (nonOperatorsOnly && HasOperatorPrivilege(session.Features, member)))
            {
                return false;
            }

            var value = matchNick ? member.Nickname : member.FullMask;
            if (value is null || !NetworkUserDirectory.WildcardMatches(pattern, value, session.State.CaseMapping))
            {
                return false;
            }

            var match = member.FullMask is null ? null : directory!.Match(member.FullMask, session.State.CaseMapping);
            var roles = match?.User?.EffectiveRoles(channel.Name, session.State.CaseMapping) ?? UserRole.None;
            return match?.Conflict != true && !roles.HasFlag(UserRole.Protected);
        }).Select(member => member.Nickname).ToArray();

        if (targets.Length == 0)
        {
            return CommandResult.Success("No unprotected channel members matched.");
        }

        if (setBan)
        {
            if (!pattern.Contains('!') || !pattern.Contains('@'))
            {
                return CommandResult.Failure("/filterkickban requires a nick!user@host wildcard mask.");
            }

            await session.SendAsync("MODE", [channel.Name, "+b", pattern], cancellationToken: cancellationToken);
        }

        foreach (var target in targets)
        {
            await session.SendAsync("KICK", [channel.Name, target, reason], IrcOutboundPriority.Bulk, cancellationToken);
        }

        return CommandResult.Success($"Queued {targets.Length} protected-aware kick(s) in {channel.Name}.");
    }

    private ValueTask<CommandResult> CommonOpAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        CommonMemberOperationAsync(input, op: true, ban: false, kick: false, cancellationToken);

    private ValueTask<CommandResult> CommonBanAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        CommonMemberOperationAsync(input, op: false, ban: true, kick: false, cancellationToken);

    private ValueTask<CommandResult> CommonKickAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        CommonMemberOperationAsync(input, op: false, ban: false, kick: true, cancellationToken);

    private ValueTask<CommandResult> CommonKickBanAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        CommonMemberOperationAsync(input, op: false, ban: true, kick: true, cancellationToken);

    private async ValueTask<CommandResult> CommonMemberOperationAsync(
        CommandInput input,
        bool op,
        bool ban,
        bool kick,
        CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        if (session is null)
        {
            return failure;
        }

        if (input.Arguments.Count == 0)
        {
            return CommandResult.Failure($"Usage: /{(op ? "cop" : ban && kick ? "ckb" : ban ? "cban" : "ckick")} <nick> [reason]");
        }

        if (!TryGetUserDirectory(out _, out var directory, out failure))
        {
            return failure;
        }

        var nickname = input.Arguments[0];
        var reason = ResolveKickMessage(TrySplitFirst(input.RawArguments, out _, out var supplied) ? supplied : null);
        var eligible = session.State.Channels.Where(channel =>
            channel.TryGetMember(nickname, out _) &&
            channel.TryGetMember(session.CurrentNickname, out var self) &&
            HasOperatorPrivilege(session.Features, self!)).ToArray();
        var changed = 0;
        foreach (var channel in eligible)
        {
            channel.TryGetMember(nickname, out var member);
            if (!op)
            {
                if (member!.FullMask is null)
                {
                    continue;
                }

                var match = directory!.Match(member.FullMask, session.State.CaseMapping);
                var roles = match.User?.EffectiveRoles(channel.Name, session.State.CaseMapping) ?? UserRole.None;
                if (match.Conflict || roles.HasFlag(UserRole.Protected))
                {
                    continue;
                }
            }

            if (op)
            {
                if (!member!.PrefixModes.Contains('o'))
                {
                    await session.SendAsync("MODE", [channel.Name, "+o", member.Nickname], cancellationToken: cancellationToken);
                    changed++;
                }
                continue;
            }

            if (ban)
            {
                if (string.IsNullOrWhiteSpace(member!.Host))
                {
                    continue;
                }

                await session.SendAsync("MODE", [channel.Name, "+b", BanmaskFormatter.Create(member, _preferences.BanmaskStyle)], cancellationToken: cancellationToken);
                if (!kick && member.PrefixModes.Contains('o'))
                {
                    await session.SendAsync("MODE", [channel.Name, "-o", member.Nickname], cancellationToken: cancellationToken);
                }
            }

            if (kick)
            {
                await session.SendAsync("KICK", [channel.Name, member!.Nickname, reason], IrcOutboundPriority.Bulk, cancellationToken);
            }
            changed++;
        }

        return CommandResult.Success($"Applied the common-channel operation in {changed} channel(s); ineligible and protected channels were skipped.");
    }

    private async ValueTask<CommandResult> MassInviteAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        var sourceName = ActiveChannel();
        if (session is null || sourceName is null || input.Arguments.Count != 1 ||
            !session.State.TryGetChannel(sourceName, out var source) ||
            !session.State.TryGetChannel(input.Arguments[0], out var target))
        {
            return session is null ? failure : CommandResult.Failure("Usage from a joined channel: /massinvite <other-joined-channel>");
        }

        if (!target!.TryGetMember(session.CurrentNickname, out var self) || !HasOperatorPrivilege(session.Features, self!))
        {
            return CommandResult.Failure($"You must be an operator in {target.Name}.");
        }

        var comparer = new IrcNameComparer(session.State.CaseMapping);
        var targets = source!.Members
            .Where(member => !comparer.Equals(member.Nickname, session.CurrentNickname))
            .Where(member => !target.TryGetMember(member.Nickname, out _))
            .Select(member => member.Nickname)
            .ToArray();
        foreach (var nickname in targets)
        {
            await session.SendAsync("INVITE", [nickname, target.Name], IrcOutboundPriority.Bulk, cancellationToken);
        }

        return CommandResult.Success($"Queued {targets.Length} invite(s) from {source.Name} to {target.Name}.");
    }

    private async ValueTask<CommandResult> InviteAllAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        if (session is null)
        {
            return failure;
        }

        if (input.Arguments.Count != 1)
        {
            return CommandResult.Failure("Usage: /inviteall <nick>");
        }

        var nickname = input.Arguments[0];
        var channels = session.State.Channels.Where(channel =>
            !channel.TryGetMember(nickname, out _) &&
            channel.TryGetMember(session.CurrentNickname, out var self) &&
            HasOperatorPrivilege(session.Features, self!)).ToArray();
        foreach (var channel in channels)
        {
            await session.SendAsync("INVITE", [nickname, channel.Name], IrcOutboundPriority.Bulk, cancellationToken);
        }

        return CommandResult.Success($"Queued invites for {nickname} to {channels.Length} channel(s).");
    }

    private ValueTask<CommandResult> OperatorWallAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SendOperatorWallAsync(input.RawArguments, notice: true, cancellationToken);

    private ValueTask<CommandResult> OperatorWallMessageAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SendOperatorWallAsync(input.RawArguments, notice: false, cancellationToken);

    private ValueTask<CommandResult> VoiceNoticeAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SendMemberGroupAsync(input.RawArguments, "voicenotice", notice: true,
            (session, member, _) => member.PrefixModes.Contains('v') || HasOperatorPrivilege(session.Features, member), cancellationToken);

    private ValueTask<CommandResult> VoiceMessageAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SendMemberGroupAsync(input.RawArguments, "voicemsg", notice: false,
            (session, member, _) => member.PrefixModes.Contains('v') || HasOperatorPrivilege(session.Features, member), cancellationToken);

    private ValueTask<CommandResult> NonOperatorNoticeAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SendMemberGroupAsync(input.RawArguments, "nonopnotice", notice: true,
            (session, member, _) => !HasOperatorPrivilege(session.Features, member), cancellationToken);

    private ValueTask<CommandResult> NonOperatorMessageAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SendMemberGroupAsync(input.RawArguments, "nonopmsg", notice: false,
            (session, member, _) => !HasOperatorPrivilege(session.Features, member), cancellationToken);

    private ValueTask<CommandResult> UserWallAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SendMemberGroupAsync(input.RawArguments, "userwall", notice: true,
            (session, member, directory) =>
            {
                if (!HasOperatorPrivilege(session.Features, member))
                {
                    return false;
                }

                var match = member.FullMask is null ? null : directory.Match(member.FullMask, session.State.CaseMapping);
                var roles = match?.User?.EffectiveRoles(ActiveChannel(), session.State.CaseMapping) ?? UserRole.None;
                return match?.Conflict != true && !roles.HasFlag(UserRole.Bot);
            }, cancellationToken);

    private async ValueTask<CommandResult> SendMemberGroupAsync(
        string text,
        string command,
        bool notice,
        Func<IrcNetworkSession, ChannelMemberState, NetworkUserDirectory, bool> predicate,
        CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        var channelName = ActiveChannel();
        if (session is null || channelName is null || !session.State.TryGetChannel(channelName, out var channel))
        {
            return session is null ? failure : CommandResult.Failure("This command requires an active joined channel.");
        }

        if (!channel!.NamesSynchronized || string.IsNullOrWhiteSpace(text))
        {
            return CommandResult.Failure($"Usage after NAMES synchronization: /{command} <text>");
        }

        if (!TryGetUserDirectory(out _, out var directory, out failure))
        {
            return failure;
        }

        var comparer = new IrcNameComparer(session.State.CaseMapping);
        var targets = channel.Members
            .Where(member => !comparer.Equals(member.Nickname, session.CurrentNickname))
            .Where(member => predicate(session, member, directory!))
            .Select(member => member.Nickname)
            .ToArray();
        foreach (var target in targets)
        {
            await session.SendAsync(
                notice ? "NOTICE" : "PRIVMSG",
                [target, text],
                IrcOutboundPriority.Bulk,
                cancellationToken);
        }

        return CommandResult.Success($"Sent {(notice ? "notices" : "messages")} to {targets.Length} member(s) in {channel.Name}.");
    }

    private async ValueTask<CommandResult> SendOperatorWallAsync(string text, bool notice, CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        var channelName = ActiveChannel();
        if (session is null || channelName is null || !session.State.TryGetChannel(channelName, out var channel))
        {
            return session is null ? failure : CommandResult.Failure("This command requires an active joined channel.");
        }

        if (!channel!.NamesSynchronized || string.IsNullOrWhiteSpace(text))
        {
            return CommandResult.Failure($"Usage after NAMES synchronization: /{(notice ? "wall" : "wallmsg")} <text>");
        }

        var comparer = new IrcNameComparer(session.State.CaseMapping);
        var targets = channel.Members
            .Where(member => HasOperatorPrivilege(session.Features, member))
            .Where(member => !comparer.Equals(member.Nickname, session.CurrentNickname))
            .Select(member => member.Nickname)
            .ToArray();
        foreach (var target in targets)
        {
            await session.SendAsync(
                notice ? "NOTICE" : "PRIVMSG",
                [target, text],
                IrcOutboundPriority.Bulk,
                cancellationToken);
        }

        return CommandResult.Success($"Sent {(notice ? "notice" : "message")} wall to {targets.Length} operator(s) in {channel.Name}.");
    }

    private async ValueTask<CommandResult> SetUserSelectedModeAsync(
        char mode,
        bool adding,
        Func<UserRole, bool> rolePredicate,
        CancellationToken cancellationToken)
    {
        if (!TryGetOperatorChannel(out var session, out var channel, out var failure))
        {
            return failure;
        }

        if (!TryGetUserDirectory(out _, out var directory, out failure))
        {
            return failure;
        }

        var comparer = new IrcNameComparer(session!.State.CaseMapping);
        var targets = channel!.Members.Where(member =>
        {
            if (comparer.Equals(member.Nickname, session.CurrentNickname) || member.FullMask is null)
            {
                return false;
            }

            var match = directory!.Match(member.FullMask, session.State.CaseMapping);
            var roles = match.User?.EffectiveRoles(channel.Name, session.State.CaseMapping) ?? UserRole.None;
            var currentlySet = member.PrefixModes.Contains(mode);
            return !match.Conflict && rolePredicate(roles) && (adding ? !currentlySet : currentlySet);
        }).Select(member => member.Nickname).ToArray();
        return await SendModeBatchesAsync(session, channel, mode, adding, targets, cancellationToken);
    }

    private bool TryGetUserDirectory(
        out IrcNetworkSession? session,
        out NetworkUserDirectory? directory,
        out CommandResult failure)
    {
        session = ActiveSession();
        directory = null;
        if (session is null)
        {
            failure = CommandResult.Failure("Not connected. Use /server first.");
            return false;
        }

        try
        {
            var profile = EnsureProfileFor(session, out _);
            directory = _userAndChannelPolicy
                .GetDirectory(profile.Id, () => _userDirectoryStore.Load(profile.Id))
                .DeepCopy();

            failure = CommandResult.Success();
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            failure = CommandResult.Failure(exception.Message);
            return false;
        }
    }

    private void SaveUserDirectory(NetworkUserDirectory directory)
    {
        _userDirectoryStore.Save(directory);
        _userAndChannelPolicy.ReplaceDirectory(directory);
    }

    private static (string Mask, string? Derivation) ResolveUserMask(IrcNetworkSession session, string maskOrNickname)
    {
        if (maskOrNickname.Contains('!') && maskOrNickname.Contains('@'))
        {
            return (maskOrNickname, null);
        }

        var comparer = new IrcNameComparer(session.State.CaseMapping);
        var members = session.State.Channels
            .SelectMany(channel => channel.Members)
            .Where(member => comparer.Equals(member.Nickname, maskOrNickname))
            .GroupBy(member => member.FullMask, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (members.Length != 1 || members[0].Username is null || members[0].Host is null)
        {
            throw new InvalidOperationException(
                $"'{maskOrNickname}' is not one unambiguous visible nick with known user and host; supply nick!user@host explicitly.");
        }

        return ($"*!{members[0].Username}@{members[0].Host}", $"Derived from {members[0].FullMask}.");
    }

    private static PresentationBlock FormatUserRecord(UserRecord user, string? matchedMask)
    {
        var fields = new List<PresentationField>
        {
            new("Hostmasks", user.Hostmasks.Count == 0 ? "none" : string.Join(", ", user.Hostmasks)),
            new("Flags", UserRoleParser.FormatFlags(user.Roles))
        };
        var roles = UserRoleParser.FormatEligibility(user.Roles);
        if (roles != "none") fields.Add(new PresentationField("Roles", roles));
        if (user.ChannelRoles.Count > 0)
        {
            fields.Add(new PresentationField("Channels", string.Join(", ", user.ChannelRoles
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}: {UserRoleParser.Format(pair.Value)}"))));
        }
        if (user.Comment.Length > 0) fields.Add(new PresentationField("Infoline", user.Comment));
        if (user.ChannelComments.Count > 0)
        {
            fields.Add(new PresentationField(
                "Channel infolines",
                string.Join(", ", user.ChannelComments
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => $"{pair.Key}: {pair.Value}"))));
        }
        if (matchedMask is not null) fields.Add(new PresentationField("Matched", matchedMask));
        return new PresentationBlock("UWHOIS:", fields, TitleHighlight: user.Handle);
    }

    internal static IReadOnlyList<IReadOnlyList<string>> UserRows(
        UserRecord user,
        IReadOnlyList<string> visibleFullMasks,
        IrcCaseMapping mapping)
    {
        var masks = user.Hostmasks
            .OrderByDescending(mask => visibleFullMasks.Any(fullMask =>
                NetworkUserDirectory.WildcardMatches(mask, fullMask, mapping)))
            .ThenBy(mask => mask, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (masks.Length == 0) masks = ["none"];

        var channels = user.ChannelRoles.Count == 0
            ? "none"
            : string.Join(", ", user.ChannelRoles.Keys.OrderBy(channel => channel, StringComparer.OrdinalIgnoreCase));
        return masks.Select((mask, index) => (IReadOnlyList<string>)(index == 0
            ? [user.Handle, mask, UserRoleParser.FormatFlags(user.Roles), UserRoleParser.FormatEligibility(user.Roles), channels]
            : [string.Empty, mask, string.Empty, string.Empty, string.Empty])).ToArray();
    }

}
