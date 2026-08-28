using System.Net;
using System.Net.Sockets;
using System.Text;
using Clircs.Commands;
using Clircs.Dcc;
using Clircs.Networking;
using Clircs.Protocol;
using Clircs.Scripting;
using Clircs.Sessions;
using Clircs.State;
using Clircs.Transport;

namespace Clircs.ConsoleClient;

// Owns IRC queries, settings, themes, scripts, and debug commands.
internal sealed partial class ClientApplication
{
    private ValueTask<CommandResult> NamesAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SendSimpleAsync("NAMES", input.Arguments.Count == 0 && ActiveChannel() is { } channel ? [channel] : input.Arguments, "Usage: /names <channel>", cancellationToken);

    private ValueTask<CommandResult> WhoAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SendWhoAsync(input.Arguments.Count == 0 && ActiveChannel() is { } channel ? [channel] : input.Arguments, cancellationToken);

    private async ValueTask<CommandResult> ClonesAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        if (input.Arguments.Count > 1)
            return CommandResult.Failure("Usage: /clones [channel]");
        var session = RequireSession(out var failure);
        if (session is null) return failure;
        var channelName = input.Arguments.Count == 1 ? input.Arguments[0] : ActiveChannel();
        if (string.IsNullOrWhiteSpace(channelName) || !session.Features.IsChannel(channelName))
            return CommandResult.Failure("Use /clones in a channel window, or specify a joined channel.");
        if (!session.State.TryGetChannel(channelName, out var channel))
            return CommandResult.Failure($"You are not joined to {channelName}.");

        try
        {
            await RefreshCloneDataAsync(session, channel!, cancellationToken);
            return ClonePresentation(channel!);
        }
        catch (TimeoutException)
        {
            return CommandResult.Failure($"Clone scan for {channelName} timed out.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            return CommandResult.Failure($"Clone scan failed: {exception.Message}");
        }
    }

    private ValueTask<CommandResult> WhoisAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SendWhoisAsync(input.Arguments, includeIdle: false, cancellationToken);

    private ValueTask<CommandResult> IdleWhoisAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SendWhoisAsync(input.Arguments, includeIdle: true, cancellationToken);

    private ValueTask<CommandResult> WhowasAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SendRoutedSimpleAsync("WHOWAS", "whowas", input.Arguments, "Usage: /whowas <nick>", cancellationToken);

    private ValueTask<CommandResult> MotdAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SendSimpleAsync("MOTD", input.Arguments, null, cancellationToken, allowEmpty: true);

    private ValueTask<CommandResult> LinksAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SendRoutedSimpleAsync("LINKS", "links", input.Arguments, "Usage: /links [server-mask]", cancellationToken, allowEmpty: true);

    private ValueTask<CommandResult> ListAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        SendRoutedSimpleAsync("LIST", "list", input.Arguments, "Usage: /list [filters]", cancellationToken, allowEmpty: true);

    private ValueTask<CommandResult> DnsAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        if (input.Arguments.Count != 1)
        {
            return ValueTask.FromResult(CommandResult.Failure("Usage: /dns <hostname|IP>"));
        }

        var requested = input.Arguments[0].Trim();
        var session = context.NetworkSessionId is { } sessionId ? FindSession(sessionId) : null;
        var target = session is null
            ? requested
            : ResolveDnsLookupTarget(session.State, requested);
        if (session is not null)
        {
            TrackOutputRequest(session, "dns");
            StartSessionWork(
                session,
                $"DNS lookup for {target}",
                () => ResolveDnsAsync(context, session, target, SessionWorkToken(session)));
        }
        else
        {
            _applicationWork.TryStart(
                $"DNS lookup for {target}",
                () => ResolveDnsAsync(context, null, target, _applicationWork.Token),
                LogUnexpectedApplicationWorkFailure);
        }

        return ValueTask.FromResult(CommandResult.Success());
    }

    private async Task ResolveDnsAsync(
        CommandContext context,
        IrcNetworkSession? session,
        string target,
        CancellationToken cancellationToken)
    {
        string text;
        try
        {
            var entry = await Dns.GetHostEntryAsync(target, cancellationToken).ConfigureAwait(false);
            text = DnsResultText(target, entry);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (
            exception is SocketException or ArgumentException or InvalidOperationException)
        {
            text = $"Unable to resolve {target}";
        }

        if (session is not null && context.BufferId is { } bufferId)
        {
            OnSessionEvent(new SessionEvent(
                session.State.Id,
                bufferId,
                SessionEventKind.ChannelSync,
                text,
                DateTimeOffset.Now,
                new Dictionary<string, string?>
                {
                    ["outputFamily"] = "dns",
                    ["routeConfigured"] = "true",
                    ["outputEnd"] = "true"
                }));
            return;
        }

        DisplayCommandResult(CommandResult.Success(text), context);
    }

    internal static string ResolveDnsLookupTarget(NetworkSessionState state, string requested)
    {
        var comparer = new IrcNameComparer(state.CaseMapping);
        var hosts = state.Channels
            .SelectMany(channel => channel.Members)
            .Where(member => comparer.Equals(member.Nickname, requested))
            .Select(member => member.Host)
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return hosts.Length == 1 ? hosts[0]! : requested;
    }

    internal static string DnsResultText(string requested, IPHostEntry entry)
    {
        string result;
        if (IPAddress.TryParse(requested, out _))
        {
            result = entry.HostName;
        }
        else
        {
            result = string.Join(", ", entry.AddressList
                .Distinct()
                .Select(address => address.ToString()));
        }
        return string.IsNullOrWhiteSpace(result)
            ? $"Unable to resolve {requested}"
            : $"DNS: Resolved {requested} to {result}";
    }

    private ValueTask<CommandResult> SetAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (input.Arguments.Count == 0)
        {
            var fields = new List<PresentationField>
            {
                new("nickname", _preferences.Nickname),
                new("altnick", _preferences.AlternateNickname),
                new("username", _preferences.Username),
                new("realname", _preferences.RealName),
                new("awaymsg", _preferences.AwayMessage),
                new("kickmsg", _preferences.DefaultKickMessage ?? "random"),
                new("quitmsg", _preferences.DefaultQuitMessage ?? "random"),
                new("topicmsg", _preferences.DefaultTopicMessage ?? "random"),
                new("banmask", BanmaskFormatter.Name(_preferences.BanmaskStyle)),
                new("clonedetect", _preferences.CloneDetection ? "on" : "off"),
                new("highlight", _preferences.HighlightNickname ? "on" : "off"),
                new("joininfo", _preferences.AnnounceUserInfoOnJoin ? "on" : "off"),
                new("hostmasks.join", FormatHostmaskVisibility(_preferences.JoinHostmasks)),
                new("hostmasks.part", FormatHostmaskVisibility(_preferences.PartHostmasks)),
                new("hostmasks.quit", FormatHostmaskVisibility(_preferences.QuitHostmasks)),
                new("kickrejoin", _preferences.AutoRejoinOnKick ? "on" : "off"),
                new("dcc.address", _preferences.DccAddress),
                new("dcc.ports", _preferences.DccPorts.ToString()),
                new("dcc.downloads", _preferences.DccDownloads)
            };
            fields.Add(new PresentationField("network.reconnect", _preferences.NetworkReconnect ? "on" : "off"));
            fields.Add(new PresentationField("kill.reconnect", _preferences.KillReconnect ? "on" : "off"));
            var activeProfile = ActiveSession() is { } activeSession ? ProfileFor(activeSession) : null;
            fields.Add(new PresentationField("usermodes", activeProfile is null
                ? "+i (new-network default)"
                : activeProfile.UserModes.Length == 0 ? "none" : activeProfile.UserModes));
            fields.AddRange(OutputRoutingCoordinator.SettingOrder
                .Where(_outputRouting.Supports)
                .Select(key => new PresentationField(
                    key == "messageguard" ? "messageguard" : $"{key}.output",
                    FormatOutputDestination(_outputRouting.DestinationFor(key), key == "notice"))));
            return ValueTask.FromResult(CommandResult.Success(new PresentationBlock("Client Settings", fields)));
        }

        if (input.Arguments.Count < 2)
        {
            return ValueTask.FromResult(CommandResult.Failure(
                "Usage: /set <setting> <value>. Use /help set for the available settings."));
        }

        var requestedSetting = input.Arguments[0];
        var setting = CanonicalSettingName(requestedSetting);
        var value = input.RawArguments[(input.RawArguments.IndexOf(' ') + 1)..];
        if (setting == "usermodes")
        {
            var session = RequireSession(out var failure);
            if (session is null) return ValueTask.FromResult(failure);
            try
            {
                var modes = NetworkProfile.NormalizeUserModes(value);
                var profile = EnsureProfileFor(session, out var created).WithUserModes(modes);
                _profileStore.Replace(profile);
                AssociateProfile(session, profile);
                return ValueTask.FromResult(CommandResult.Success(
                    $"usermodes for {profile.DisplayName} changed to {(modes.Length == 0 ? "none" : modes)}." +
                    (created ? " A network profile was created automatically." : string.Empty)));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return ValueTask.FromResult(CommandResult.Failure(exception.Message));
            }
        }
        var previousSettings = CaptureAppearanceSettings();
        switch (setting)
        {
            case "nickname":
                _preferences.Nickname = value;
                break;
            case "altnick":
                _preferences.AlternateNickname = value;
                break;
            case "username":
                _preferences.Username = value;
                break;
            case "realname":
                _preferences.RealName = value;
                break;
            case "awaymsg":
                _preferences.AwayMessage = value;
                break;
            case "kickmsg":
                var kickDefault = ParseOptionalDefault(value);
                if (kickDefault?.Length > 300) return ValueTask.FromResult(CommandResult.Failure("A default message cannot exceed 300 characters."));
                _preferences.DefaultKickMessage = kickDefault;
                break;
            case "quitmsg":
                var quitDefault = ParseOptionalDefault(value);
                if (quitDefault?.Length > 300) return ValueTask.FromResult(CommandResult.Failure("A default message cannot exceed 300 characters."));
                _preferences.DefaultQuitMessage = quitDefault;
                break;
            case "topicmsg":
                var topicDefault = ParseOptionalDefault(value);
                if (topicDefault?.Length > 300) return ValueTask.FromResult(CommandResult.Failure("A default message cannot exceed 300 characters."));
                _preferences.DefaultTopicMessage = topicDefault;
                break;
            case "banmask":
                if (!BanmaskFormatter.TryParse(value, out var requestedBanmask))
                {
                    return ValueTask.FromResult(CommandResult.Failure(
                        "banmask must be host, userhost, nick-userhost, or wildcard-host."));
                }
                _preferences.BanmaskStyle = requestedBanmask;
                value = BanmaskFormatter.Name(_preferences.BanmaskStyle);
                break;
            case "highlight":
                if (!TryParseOnOff(value, out var highlightNickname))
                {
                    return ValueTask.FromResult(CommandResult.Failure("highlight must be on or off."));
                }
                _preferences.HighlightNickname = highlightNickname;
                break;
            case "clonedetect":
                if (!TryParseOnOff(value, out var cloneDetection))
                {
                    return ValueTask.FromResult(CommandResult.Failure("clonedetect must be on or off."));
                }
                _preferences.CloneDetection = cloneDetection;
                break;
            case "joininfo":
                if (!TryParseOnOff(value, out var announceInfo))
                {
                    return ValueTask.FromResult(CommandResult.Failure("joininfo must be on or off."));
                }
                _preferences.AnnounceUserInfoOnJoin = announceInfo;
                break;
            case "kickrejoin":
                if (!TryParseOnOff(value, out var autoRejoin))
                {
                    return ValueTask.FromResult(CommandResult.Failure("kickrejoin must be on or off."));
                }
                _preferences.AutoRejoinOnKick = autoRejoin;
                break;
            case "network.reconnect":
                if (!TryParseOnOff(value, out var networkReconnect))
                {
                    return ValueTask.FromResult(CommandResult.Failure("network.reconnect must be on or off."));
                }
                _preferences.NetworkReconnect = networkReconnect;
                break;
            case "kill.reconnect":
                if (!TryParseOnOff(value, out var killReconnect))
                {
                    return ValueTask.FromResult(CommandResult.Failure("kill.reconnect must be on or off."));
                }
                _preferences.KillReconnect = killReconnect;
                break;
            case "dcc.address":
                if (!IsValidDccAddressSetting(value))
                {
                    return ValueTask.FromResult(CommandResult.Failure(
                        "dcc.address must be auto, an IPv4 or IPv6 address, or a hostname without spaces."));
                }
                _preferences.DccAddress = value.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? "auto"
                    : value.ToLowerInvariant();
                value = _preferences.DccAddress;
                break;
            case "dcc.ports":
                if (!DccPortRange.TryParse(value, out var dccPorts))
                {
                    return ValueTask.FromResult(CommandResult.Failure(
                        "dcc.ports must be random, one port, or a range such as 50000-50009."));
                }
                _preferences.DccPorts = dccPorts;
                value = _preferences.DccPorts.ToString();
                break;
            case "dcc.downloads":
                try
                {
                    var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
                    if (string.IsNullOrWhiteSpace(expanded) || expanded.IndexOfAny(['\r', '\n', '\0']) >= 0)
                    {
                        throw new ArgumentException("The download folder is empty or invalid.");
                    }
                    _preferences.DccDownloads = Path.GetFullPath(expanded);
                    value = _preferences.DccDownloads;
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return ValueTask.FromResult(CommandResult.Failure($"dcc.downloads is invalid: {exception.Message}"));
                }
                break;
            case "who.output":
            case "whois.output":
            case "whowas.output":
            case "ctcp.output":
            case "notice.output":
            case "invite.output":
            case "links.output":
            case "list.output":
            case "dns.output":
            case "messageguard":
                if (!TryParseOutputDestination(value, out var destination))
                {
                    return ValueTask.FromResult(CommandResult.Failure(
                        setting == "notice.output"
                            ? "notice.output must be active, status, or window."
                            : "Output destination must be active, status, or dedicated."));
                }

                _outputRouting.TrySetDestination(
                    setting == "messageguard" ? setting : setting[..^".output".Length],
                    destination);
                break;
            case "hostmasks":
            case "hostmasks.join":
            case "hostmasks.part":
            case "hostmasks.quit":
                if (!TryParseHostmaskVisibility(value, out var visibility))
                {
                    return ValueTask.FromResult(CommandResult.Failure("Hostmask visibility must be userhost, host, or off."));
                }
                var hostmaskSetting = setting;
                if (hostmaskSetting is "hostmasks" or "hostmasks.join") _preferences.JoinHostmasks = visibility;
                if (hostmaskSetting is "hostmasks" or "hostmasks.part") _preferences.PartHostmasks = visibility;
                if (hostmaskSetting is "hostmasks" or "hostmasks.quit") _preferences.QuitHostmasks = visibility;
                _presenter.SetHostmaskVisibility(_preferences.JoinHostmasks, _preferences.PartHostmasks, _preferences.QuitHostmasks);
                break;
            default:
                return ValueTask.FromResult(CommandResult.Failure(
                    $"Unknown setting: {requestedSetting}. Use /help set for the available settings."));
        }

        try
        {
            SaveAppearanceSettings();
            return ValueTask.FromResult(CommandResult.Success($"{setting} changed to {value}."));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ApplyAppearanceSettings(previousSettings);
            return ValueTask.FromResult(CommandResult.Failure(exception.Message));
        }
    }

    private static string CanonicalSettingName(string setting) => setting.ToLowerInvariant() switch
    {
        "identity.nick" => "nickname",
        "identity.altnick" => "altnick",
        "identity.username" => "username",
        "identity.realname" => "realname",
        "away.defaultmessage" => "awaymsg",
        "message.kick" => "kickmsg",
        "message.quit" => "quitmsg",
        "message.topic" => "topicmsg",
        "ban.mask" => "banmask",
        "clone.detect" => "clonedetect",
        "highlight.nickname" => "highlight",
        "userlist.infoonjoin" => "joininfo",
        "channel.rejoinonkick" => "kickrejoin",
        "net.reconnect" => "network.reconnect",
        "output.hostmasks" => "hostmasks",
        "output.hostmasks.join" => "hostmasks.join",
        "output.hostmasks.part" => "hostmasks.part",
        "output.hostmasks.quit" => "hostmasks.quit",
        "output.who" => "who.output",
        "output.whois" => "whois.output",
        "output.whowas" => "whowas.output",
        "output.ctcp" => "ctcp.output",
        "output.notice" => "notice.output",
        "output.invite" => "invite.output",
        "output.links" => "links.output",
        "output.list" => "list.output",
        "output.dns" => "dns.output",
        "output.messageguard" => "messageguard",
        var canonical => canonical
    };

    private ValueTask<CommandResult> ThemeAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (input.Arguments.Count == 0)
        {
            return ValueTask.FromResult(CommandResult.Success(ThemeOverview(
                _presenter.Theme.Name,
                _themeManager.Themes.Select(theme => theme.Name))));
        }

        if (input.Arguments.Count == 1 && input.Arguments[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(CommandResult.Success(
                $"Themes: {string.Join(", ", _themeManager.Themes.Select(theme => theme.Name + (theme.Name.Equals(_presenter.Theme.Name, StringComparison.OrdinalIgnoreCase) ? "*" : string.Empty)))}"));
        }

        if (input.Arguments.Count == 1 && input.Arguments[0].Equals("reload", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var currentName = _presenter.Theme.Name;
                _themeManager.Reload();
                var fellBack = false;
                if (_themeManager.TryGet(currentName, out var reloadedTheme))
                {
                    _presenter.SetTheme(reloadedTheme!);
                }
                else
                {
                    _themeManager.TryGet("clircs", out reloadedTheme);
                    _presenter.SetTheme(reloadedTheme!);
                    fellBack = true;
                }
                RefreshWindowChrome();
                SaveAppearanceSettings();
                return ValueTask.FromResult(CommandResult.Success(
                    $"Reloaded themes from {_themeManager.DirectoryPath}." +
                    (fellBack ? $" Theme '{currentName}' is unavailable; using clircs." : string.Empty) +
                    (_themeManager.Errors.Count == 0 ? string.Empty : $" {_themeManager.Errors.Count} theme(s) had errors:\n{string.Join('\n', _themeManager.Errors)}")));
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return ValueTask.FromResult(CommandResult.Failure(exception.Message));
            }
        }

        if (input.Arguments.Count == 2 && input.Arguments[0].Equals("use", StringComparison.OrdinalIgnoreCase))
        {
            var requested = input.Arguments[1].Equals("color", StringComparison.OrdinalIgnoreCase) ? "clircs" : input.Arguments[1];
            if (_themeManager.TryGet(requested, out var theme))
            {
                _presenter.SetTheme(theme!);
                RefreshWindowChrome();
                try
                {
                    SaveAppearanceSettings();
                    return ValueTask.FromResult(CommandResult.Success($"Theme set to {theme!.Name}."));
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    return ValueTask.FromResult(CommandResult.Failure(exception.Message));
                }
            }
            return ValueTask.FromResult(CommandResult.Failure($"No theme named '{requested}'. Use /theme list."));
        }

        return ValueTask.FromResult(CommandResult.Failure("Usage: /theme list|reload|use <name>"));
    }

    internal static PresentationBlock ThemeOverview(string current, IEnumerable<string> themes)
    {
        var available = themes
            .Where(theme => !theme.Equals(current, StringComparison.OrdinalIgnoreCase))
            .OrderBy(theme => theme, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new PresentationBlock(
            "Theme",
            [
                new PresentationField("Current", current),
                new PresentationField("Available", available.Length == 0 ? "none" : string.Join(", ", available))
            ],
            Summary: "Use: /theme list|reload|use <name>");
    }

    private ValueTask<CommandResult> TlsAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (input.Arguments.Count == 0 ||
            input.Arguments.Count == 1 && input.Arguments[0].Equals("pins", StringComparison.OrdinalIgnoreCase))
        {
            var pins = _tlsCertificatePolicy.Pins;
            if (pins.Count == 0)
            {
                return ValueTask.FromResult(CommandResult.Success("No TLS certificates are currently trusted."));
            }

            var now = DateTimeOffset.UtcNow;
            var fields = new List<PresentationField>();
            for (var index = 0; index < pins.Count; index++)
            {
                var pin = pins[index];
                var validity = pin.ValidFromUtc is null || pin.ValidUntilUtc is null
                    ? "validity unavailable (pin created by an older clircs build)"
                    : now < pin.ValidFromUtc.Value
                        ? "not yet valid"
                        : now > pin.ValidUntilUtc.Value ? "expired" : "currently valid";
                fields.Add(new PresentationField($"Certificate {index + 1}", $"{pin.Host}:{pin.Port}"));
                fields.Add(new PresentationField("Status", validity));
                fields.Add(new PresentationField("Subject", TerminalTextSanitizer.Sanitize(pin.Subject)));
                fields.Add(new PresentationField("Issuer", TerminalTextSanitizer.Sanitize(pin.Issuer ?? "unavailable")));
                fields.Add(new PresentationField("Valid from", pin.ValidFromUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "unavailable"));
                fields.Add(new PresentationField("Valid until", pin.ValidUntilUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "unavailable"));
                fields.Add(new PresentationField("SHA-256", FormatTlsFingerprint(pin.Sha256Fingerprint)));
                fields.Add(new PresentationField("Trusted on", pin.TrustedAtUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'")));
                fields.Add(new PresentationField("Revoke", $"/tls forget {pin.Host} {pin.Port}"));
            }
            return ValueTask.FromResult(CommandResult.Success(new PresentationBlock("Trusted TLS certificates", fields)));
        }

        if (input.Arguments.Count == 3 &&
            input.Arguments[0].Equals("forget", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(input.Arguments[2], out var port) &&
            port is >= 1 and <= 65535)
        {
            try
            {
                return ValueTask.FromResult(_tlsCertificatePolicy.Forget(input.Arguments[1], port)
                    ? CommandResult.Success($"Forgot the TLS certificate pin for {input.Arguments[1]}:{port}.")
                    : CommandResult.Failure($"No TLS certificate pin exists for {input.Arguments[1]}:{port}."));
            }
            catch (InvalidOperationException exception)
            {
                return ValueTask.FromResult(CommandResult.Failure(exception.Message));
            }
        }

        return ValueTask.FromResult(CommandResult.Failure("Usage: /tls pins or /tls forget <host> <port>"));
    }

    private async ValueTask<CommandResult> ScriptAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        var operation = input.Arguments.Count == 0 ? "list" : input.Arguments[0].ToLowerInvariant();
        try
        {
            switch (operation)
            {
                case "list":
                {
                    var scripts = _scriptManager.List();
                    if (scripts.Count == 0)
                    {
                        return CommandResult.Success($"No scripts installed. Script directory: {_scriptManager.ScriptsDirectory}");
                    }

                    var lines = scripts.Select(script =>
                        $"{script.Id} {script.Version} [{script.Status}] permissions: " +
                        (script.GrantedPermissions.Count == 0
                            ? "none"
                            : string.Join(',', script.GrantedPermissions.Select(FormatPermission))));
                    return CommandResult.Success(string.Join(Environment.NewLine, lines));
                }

                case "load" when input.Arguments.Count == 2:
                {
                    var script = await _scriptManager.LoadAsync(input.Arguments[1], cancellationToken);
                    return CommandResult.Success($"Loaded {script.Name} {script.Version} ({script.Id}).");
                }

                case "unload" when input.Arguments.Count == 2:
                    return await _scriptManager.UnloadAsync(input.Arguments[1], cancellationToken)
                        ? CommandResult.Success($"Unloaded script '{input.Arguments[1]}'.")
                        : CommandResult.Failure($"Script '{input.Arguments[1]}' is not loaded.");

                case "reload" when input.Arguments.Count == 2:
                {
                    var script = await _scriptManager.ReloadAsync(input.Arguments[1], cancellationToken);
                    return CommandResult.Success($"Reloaded {script.Name} {script.Version} ({script.Id}).");
                }

                case "errors":
                {
                    var errors = _scriptManager.Errors.TakeLast(20).ToArray();
                    return errors.Length == 0
                        ? CommandResult.Success("No script errors recorded.")
                        : CommandResult.Success(string.Join(Environment.NewLine, errors.Select(error =>
                            $"{error.Timestamp:u} {error.ScriptId} {error.Operation}: {error.Message}")));
                }

                case "permissions" when input.Arguments.Count is 1 or 2:
                {
                    var scripts = _scriptManager.List();
                    if (input.Arguments.Count == 2)
                    {
                        scripts = scripts.Where(script => script.Id.Equals(input.Arguments[1], StringComparison.OrdinalIgnoreCase)).ToArray();
                    }

                    if (scripts.Count == 0)
                    {
                        return CommandResult.Failure("No matching installed script.");
                    }

                    return CommandResult.Success(string.Join(Environment.NewLine, scripts.Select(script =>
                        $"{script.Id}: requested [{string.Join(',', script.RequestedPermissions.Select(FormatPermission))}] " +
                        $"granted [{string.Join(',', script.GrantedPermissions.Select(FormatPermission))}]")));
                }

                case "permissions" when input.Arguments.Count == 4:
                {
                    if (!Enum.TryParse<ScriptPermission>(input.Arguments[2], true, out var permission))
                    {
                        return CommandResult.Failure($"Unknown script permission '{input.Arguments[2]}'.");
                    }

                    var enabled = input.Arguments[3].Equals("on", StringComparison.OrdinalIgnoreCase);
                    if (!enabled && !input.Arguments[3].Equals("off", StringComparison.OrdinalIgnoreCase))
                    {
                        return CommandResult.Failure("Permission state must be on or off.");
                    }

                    await _scriptManager.SetPermissionAsync(input.Arguments[1], permission, enabled, cancellationToken);
                    return CommandResult.Success(
                        $"{FormatPermission(permission)} permission {(enabled ? "granted to" : "revoked from")} '{input.Arguments[1]}'.");
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return CommandResult.Failure(exception.Message);
        }

        return CommandResult.Failure(
            "Usage: /script list|load <id>|unload <id>|reload <id>|errors|permissions [id [permission on|off]]");
    }

    private static string FormatPermission(ScriptPermission permission) => permission.ToString().ToLowerInvariant();

    private void PrintScriptOutput(string scriptId, string text) =>
        _presenter.Result($"[{scriptId}] {TerminalTextSanitizer.Sanitize(text)}");

    private IDisposable RegisterScriptCommand(ScriptCommandRegistration registration)
    {
        var definition = new CommandDefinition(
            registration.Name,
            registration.Aliases,
            $"/{registration.Name} [arguments]",
            $"{registration.Summary} [script: {registration.ScriptId}]",
            (context, input, cancellationToken) => registration.Handler(context, input.Arguments, cancellationToken));
        _commands.Register(definition);
        return new CallbackDisposable(() => _commands.Unregister(definition));
    }

    private void QueueScriptCommand(string scriptId, CommandContext context, string commandLine)
    {
        _ = ExecuteQueuedScriptCommandAsync(scriptId, context, commandLine);
    }

    private async Task ExecuteQueuedScriptCommandAsync(string scriptId, CommandContext context, string commandLine)
    {
        try
        {
            var parsed = CommandLineParser.Parse(commandLine);
            if (parsed is not CommandInput command)
            {
                PrintScriptOutput(scriptId, "Only slash commands may be requested");
                return;
            }

            var result = await _commandExecution.ExecuteAsync(context, command, _lifetime.Token);
            if (result.Presentation is not null || !string.IsNullOrWhiteSpace(result.Message))
            {
                DisplayCommandResult(result, context);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedCommandFailure(exception))
        {
            PrintScriptOutput(scriptId, $"Command request failed: {exception.Message}");
        }
        catch (Exception exception)
        {
            LogUnexpectedCommandFailure(commandLine, context, exception);
            PrintScriptOutput(scriptId, "Command request failed unexpectedly; details were written to the clircs error log");
        }
    }

    private void SetScriptHeader(string scriptId, ScriptHeaderContribution contribution)
    {
        lock (_scriptHeaderGate)
        {
            _scriptHeaders[(scriptId, contribution.Id)] = contribution with
            {
                Text = TerminalTextSanitizer.Sanitize(contribution.Text)
            };
        }
        RefreshWindowChrome();
    }

    private void ClearScriptHeader(string scriptId, string itemId)
    {
        lock (_scriptHeaderGate)
        {
            _scriptHeaders.Remove((scriptId, itemId));
        }
        RefreshWindowChrome();
    }

    private void ClearScriptHeaders(string scriptId)
    {
        lock (_scriptHeaderGate)
        {
            foreach (var key in _scriptHeaders.Keys.Where(key =>
                key.ScriptId.Equals(scriptId, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                _scriptHeaders.Remove(key);
            }
        }
        RefreshWindowChrome();
    }

    private string? ReadScriptSecret(string scriptId, string label) =>
        _presenter.ReadSecret($"[{scriptId}] {TerminalTextSanitizer.Sanitize(label)} (Esc cancels): ");

    private ValueTask<CommandResult> DebugAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (input.Arguments.Count != 0)
        {
            return ValueTask.FromResult(CommandResult.Failure("Usage: /debug"));
        }

        var session = RequireSession(out var failure);
        if (session is null)
        {
            return ValueTask.FromResult(failure);
        }

        var buffer = session.State.GetOrCreateBuffer(BufferKind.Diagnostics, "=debug");
        return ValueTask.FromResult(SwitchTo(session, buffer));
    }

    internal static string FormatWireDebugLine(IrcWireLine wireLine)
    {
        var direction = wireLine.Direction == IrcWireDirection.Received ? "<<" : ">>";
        var visible = new StringBuilder(wireLine.Line.Length);
        foreach (var character in wireLine.Line)
        {
            if (TerminalTextSanitizer.IsBidirectionalControl(character))
            {
                visible.Append($"\\u{(int)character:X4}");
            }
            else if (character < ' ' || character == '\u007f')
            {
                visible.Append($"\\x{(int)character:X2}");
            }
            else
            {
                visible.Append(character);
            }
        }
        return $"{direction} {visible}";
    }

    private async ValueTask<CommandResult> SendSimpleAsync(
        string command,
        IReadOnlyList<string> arguments,
        string? usage,
        CancellationToken cancellationToken,
        bool allowEmpty = false)
    {
        var session = RequireSession(out var failure);
        if (session is null)
        {
            return failure;
        }

        if (!allowEmpty && arguments.Count == 0)
        {
            return CommandResult.Failure(usage ?? $"Usage: /{command.ToLowerInvariant()} <arguments>");
        }

        await session.SendAsync(command, arguments, cancellationToken: cancellationToken);
        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> SendWhoAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        if (session is null) return failure;
        if (arguments.Count == 0) return CommandResult.Failure("Usage: /who <mask-or-channel>");

        var requestId = session.BeginWhoRequest(arguments);
        TrackOutputRequest(session, "who", requestId);
        try
        {
            await session.SendAsync("WHO", arguments, cancellationToken: cancellationToken);
            return CommandResult.Success();
        }
        catch
        {
            session.CancelWhoRequest(requestId);
            CancelOutputRequest(session, "who", requestId);
            throw;
        }
    }

    private async ValueTask<CommandResult> SendWhoisAsync(
        IReadOnlyList<string> arguments,
        bool includeIdle,
        CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        if (session is null) return failure;
        if (arguments.Count != 1)
        {
            return CommandResult.Failure(includeIdle ? "Usage: /iwhois <nick>" : "Usage: /whois <nick>");
        }

        var nickname = arguments[0];
        var wireArguments = includeIdle ? new[] { nickname, nickname } : new[] { nickname };
        var requestId = session.BeginWhoisRequest(nickname, includeIdle);
        TrackOutputRequest(session, "whois", requestId);
        try
        {
            await session.SendAsync("WHOIS", wireArguments, cancellationToken: cancellationToken);
            return CommandResult.Success();
        }
        catch
        {
            session.CancelWhoisRequest(requestId);
            CancelOutputRequest(session, "whois", requestId);
            throw;
        }
    }

    private async ValueTask<CommandResult> SendRoutedSimpleAsync(
        string command,
        string family,
        IReadOnlyList<string> arguments,
        string usage,
        CancellationToken cancellationToken,
        bool allowEmpty = false)
    {
        var session = RequireSession(out var failure);
        if (session is null)
        {
            return failure;
        }

        if (!allowEmpty && arguments.Count == 0)
        {
            return CommandResult.Failure(usage);
        }

        if (!TryTrackExclusiveOutputRequest(session, family))
        {
            return CommandResult.Failure($"A {command} request is already in progress");
        }
        try
        {
            await session.SendAsync(command, arguments, cancellationToken: cancellationToken);
            return CommandResult.Success();
        }
        catch
        {
            CancelOutputRequest(session, family);
            throw;
        }
    }

}
