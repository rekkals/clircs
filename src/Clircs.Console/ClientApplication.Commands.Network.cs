using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Clircs.Commands;
using Clircs.Dcc;
using Clircs.Identity;
using Clircs.Networking;
using Clircs.Protocol;
using Clircs.Sessions;
using Clircs.State;

namespace Clircs.ConsoleClient;

// Owns server connections, network profiles, windows, and autojoin commands.
internal sealed partial class ClientApplication
{
    private async ValueTask<CommandResult> ServerAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (input.Arguments.Count == 0)
        {
            return CommandResult.Failure("Usage: /server <host|profile> [port] [--tls] [--new] [--password]");
        }

        var useTls = input.Arguments.Contains("--tls", StringComparer.OrdinalIgnoreCase);
        var wantsNew = input.Arguments.Contains("--new", StringComparer.OrdinalIgnoreCase);
        var promptPassword = input.Arguments.Contains("--password", StringComparer.OrdinalIgnoreCase);
        var unknownOption = input.Arguments.FirstOrDefault(argument => argument.StartsWith("--", StringComparison.Ordinal) &&
            !argument.Equals("--tls", StringComparison.OrdinalIgnoreCase) &&
            !argument.Equals("--new", StringComparison.OrdinalIgnoreCase) &&
            !argument.Equals("--password", StringComparison.OrdinalIgnoreCase));
        if (unknownOption is not null)
        {
            return CommandResult.Failure($"Unknown /server option: {unknownOption}");
        }

        var positional = input.Arguments.Where(argument => !argument.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (positional.Length is < 1 or > 2)
        {
            return CommandResult.Failure("Usage: /server <host|profile> [port] [--tls] [--new] [--password]");
        }

        var profile = positional.Length == 1 ? _profileStore.Find(positional[0]) : null;
        IrcConnectionOptions options;
        string? displayName = null;
        NetworkProfileId? profileId = null;
        if (profile is not null)
        {
            if (useTls)
            {
                return CommandResult.Failure("A saved profile already defines TLS. Use /server <profile> [--new].");
            }
            if (!profile.IsConfigured)
            {
                return CommandResult.Failure(
                    $"Network profile {profile.DisplayName} has no server endpoint. Configure one with /network add {profile.DisplayName} <host> [port] [--tls].");
            }

            try
            {
                options = ConnectionOptionsForProfile(profile, CurrentIdentity());
            }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or ArgumentException)
            {
                return CommandResult.Failure(exception.Message);
            }
            displayName = profile.DisplayName;
            profileId = profile.Id;
        }
        else
        {
            var port = useTls ? 6697 : 6667;
            if (positional.Length == 2 && (!int.TryParse(positional[1], out port) || port is < 1 or > 65535))
            {
                return CommandResult.Failure("The server port must be a number from 1 through 65535.");
            }

            options = new IrcConnectionOptions(
                new IrcEndpoint(positional[0], port, useTls),
                new IrcIdentity([_preferences.Nickname, _preferences.AlternateNickname], _preferences.Username, _preferences.RealName));
        }
        if (promptPassword)
        {
            var password = _presenter.ReadSecret("Server password (Esc cancels): ");
            if (password is null)
            {
                return CommandResult.Failure("Connection canceled.");
            }
            options = options with { Password = password };
        }

        RememberRecentConnection(options, profileId);
        var active = ActiveSession();
        int? replacementStatusNumber = null;
        if (!wantsNew && active is not null)
        {
            replacementStatusNumber = BufferNumber(active.State.StatusBuffer.Id);
            await CloseSessionAsync(active, "Changing servers");
        }

        await StartSessionAsync(options, cancellationToken, displayName, profileId, replacementStatusNumber);
        return CommandResult.Success();
    }

    private ValueTask<CommandResult> NetworkAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        var sessions = SessionsSnapshot();
        var profiles = _profileStore.Entries;
        var operation = input.Arguments.Count == 0 ? "list" : input.Arguments[0].ToLowerInvariant();
        switch (operation)
        {
            case "list":
                if (sessions.Length == 0)
                {
                    return ValueTask.FromResult(CommandResult.Success(
                        "No live network sessions. Use /network profiles to inspect saved profiles."));
                }

                var networkRows = sessions.Select((session, index) => (IReadOnlyList<string>)new[]
                {
                    (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _windowStates.IsActiveSession(session.State.Id) ? "*" : string.Empty,
                    ProfileFor(session)?.DisplayName ?? session.Features.NetworkName ?? session.State.DisplayName,
                    ConnectionStatusLabel(session),
                    session.Options.Endpoint.ToString(),
                    session.CurrentNickname
                }).ToArray();
                return ValueTask.FromResult(CommandResult.Success(new PresentationBlock(
                    "Networks",
                    Table: new PresentationTable(
                        ["No.", "", "Network", "State", "Server", "Nick"],
                        networkRows),
                    Summary: null)));
            case "profiles":
                if (input.Arguments.Count != 1)
                {
                    return ValueTask.FromResult(CommandResult.Failure("Usage: /network profiles"));
                }
                if (profiles.Count == 0)
                {
                    return ValueTask.FromResult(CommandResult.Success("No saved network profiles."));
                }
                var profileRows = NetworkProfileRows(profiles);
                return ValueTask.FromResult(CommandResult.Success(new PresentationBlock(
                    "Network Profiles",
                    Table: new PresentationTable(
                        ["Network", "Server(s)", "Nick", "SASL"],
                        profileRows))));
            case "use":
                if (input.Arguments.Count != 2)
                {
                    return ValueTask.FromResult(CommandResult.Failure("Usage: /network use <name-or-number>"));
                }

                var selected = FindSession(input.Arguments[1], sessions);
                return ValueTask.FromResult(selected is null
                    ? CommandResult.Failure($"No live network matches '{input.Arguments[1]}'.")
                    : SwitchTo(selected, selected.State.StatusBuffer));
            case "status":
                if (input.Arguments.Count > 2)
                {
                    return ValueTask.FromResult(CommandResult.Failure("Usage: /network status [name-or-number]"));
                }

                var statusSession = input.Arguments.Count > 1
                    ? FindSession(input.Arguments[1], sessions)
                    : ActiveSession();
                if (statusSession is not null)
                {
                    return ValueTask.FromResult(SessionStatus(statusSession));
                }

                var statusProfile = input.Arguments.Count > 1 ? _profileStore.Find(input.Arguments[1]) : null;
                return ValueTask.FromResult(statusProfile is null
                    ? CommandResult.Failure("No matching live network session or saved profile.")
                    : ProfileStatus(statusProfile));
            case "add":
                var addArguments = input.Arguments.Skip(1).ToArray();
                var addTls = addArguments.Contains("--tls", StringComparer.OrdinalIgnoreCase);
                var badOption = addArguments.FirstOrDefault(argument => argument.StartsWith("--", StringComparison.Ordinal) &&
                    !argument.Equals("--tls", StringComparison.OrdinalIgnoreCase));
                var addPositionals = addArguments.Where(argument => !argument.StartsWith("--", StringComparison.Ordinal)).ToArray();
                if (badOption is not null || addPositionals.Length is < 2 or > 3)
                {
                    return ValueTask.FromResult(CommandResult.Failure(
                        "Usage: /network add <name> <host> [port] [--tls]"));
                }

                var addPort = addTls ? 6697 : 6667;
                if (addPositionals.Length == 3 &&
                    (!int.TryParse(addPositionals[2], out addPort) || addPort is < 1 or > 65535))
                {
                    return ValueTask.FromResult(CommandResult.Failure("The server port must be a number from 1 through 65535."));
                }

                try
                {
                    var endpoint = new IrcEndpoint(addPositionals[1], addPort, addTls);
                    var existing = _profileStore.Find(addPositionals[0]);
                    if (existing is not null)
                    {
                        if (existing.IsConfigured)
                        {
                            return ValueTask.FromResult(CommandResult.Failure(
                                $"A configured network profile named '{existing.DisplayName}' already exists."));
                        }
                        var configured = existing.WithEndpoint(endpoint);
                        _profileStore.Replace(configured);
                        return ValueTask.FromResult(CommandResult.Success(
                            $"Configured network profile {configured.DisplayName}. Connect with /server {configured.DisplayName}."));
                    }
                    var added = new NetworkProfile(
                        NetworkProfileId.New(),
                        addPositionals[0],
                        [endpoint],
                        new IrcIdentity([_preferences.Nickname, _preferences.AlternateNickname], _preferences.Username, _preferences.RealName));
                    _profileStore.Add(added);
                    return ValueTask.FromResult(CommandResult.Success(
                        $"Saved network profile {added.DisplayName}. Connect with /server {added.DisplayName}."));
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    return ValueTask.FromResult(CommandResult.Failure(exception.Message));
                }
            case "remove":
                if (input.Arguments.Count != 2)
                {
                    return ValueTask.FromResult(CommandResult.Failure("Usage: /network remove <name>"));
                }

                var removeProfile = _profileStore.Find(input.Arguments[1]);
                if (removeProfile is null)
                {
                    return ValueTask.FromResult(CommandResult.Failure($"No saved network profile matches '{input.Arguments[1]}'."));
                }

                if (_liveSessions.UsesProfile(removeProfile.Id))
                {
                    return ValueTask.FromResult(CommandResult.Failure(
                        $"Disconnect live sessions using {removeProfile.DisplayName} before removing its profile."));
                }

                try
                {
                    _profileStore.Remove(removeProfile.DisplayName);
                    _networkCredentials.Remove(removeProfile.Id);
                    return ValueTask.FromResult(CommandResult.Success($"Removed network profile {removeProfile.DisplayName}."));
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    return ValueTask.FromResult(CommandResult.Failure(exception.Message));
                }
            case "sasl":
                return ValueTask.FromResult(ConfigureNetworkSasl(input));
            default:
                return ValueTask.FromResult(CommandResult.Failure("Usage: /network list|profiles|add|remove|use|status|sasl"));
        }
    }

    internal static IReadOnlyList<IReadOnlyList<string>> NetworkProfileRows(IEnumerable<NetworkProfile> profiles)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var profile in profiles)
        {
            var sasl = profile.Sasl is null
                ? "off"
                : $"{profile.Sasl.Mechanism} ({(profile.Sasl.Required ? "required" : "optional")})";
            var servers = profile.IsConfigured
                ? profile.Endpoints.Select(endpoint => endpoint.ToString()).ToArray()
                : ["[no server configured]"];

            rows.Add([profile.DisplayName, servers[0], profile.Identity.Nicknames[0], sasl]);
            rows.AddRange(servers.Skip(1).Select(server => (IReadOnlyList<string>)[string.Empty, server, string.Empty, string.Empty]));
        }

        return rows;
    }

    private CommandResult ConfigureNetworkSasl(CommandInput input)
    {
        if (input.Arguments.Count < 2 || input.Arguments.Count > 5)
        {
            return CommandResult.Failure(
                "Usage: /network sasl <profile> [<account> [required|optional]|plain <account> [required|optional]|external <certificate.pfx> [required|optional]|off]");
        }

        var profile = _profileStore.Find(input.Arguments[1]);
        if (profile is null)
        {
            return CommandResult.Failure($"No saved network profile matches '{input.Arguments[1]}'");
        }

        if (input.Arguments.Count == 2)
        {
            if (profile.Sasl is null)
            {
                return CommandResult.Success($"SASL is off for {profile.DisplayName}");
            }
            return CommandResult.Success(new PresentationBlock(
                $"SASL: {profile.DisplayName}",
                profile.Sasl.Mechanism == SaslMechanisms.Plain
                    ? [
                        new PresentationField("Mechanism", SaslMechanisms.Plain),
                        new PresentationField("Account", profile.Sasl.Username!),
                        new PresentationField("Failure policy", profile.Sasl.Required ? "disconnect" : "continue unidentified"),
                        new PresentationField("Password", _networkCredentials.HasSaslSecret(profile.Id) ? "encrypted and saved" : "missing")
                    ]
                    : [
                        new PresentationField("Mechanism", SaslMechanisms.External),
                        new PresentationField("Certificate", profile.Sasl.ClientCertificatePath!),
                        new PresentationField("Authorization identity", profile.Sasl.AuthorizationIdentity ?? "certificate identity"),
                        new PresentationField("Failure policy", profile.Sasl.Required ? "disconnect" : "continue unidentified"),
                        new PresentationField("Certificate password", _networkCredentials.HasSaslSecret(profile.Id) ? "encrypted and saved" : "missing")
                    ]));
        }

        if (input.Arguments[2].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            if (input.Arguments.Count != 3)
            {
                return CommandResult.Failure("Usage: /network sasl <profile> off");
            }
            try
            {
                _profileStore.Replace(profile.WithSasl(null));
                _networkCredentials.Remove(profile.Id);
                return CommandResult.Success($"SASL disabled for {profile.DisplayName}");
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return CommandResult.Failure(exception.Message);
            }
        }

        var explicitMechanism = input.Arguments[2].ToLowerInvariant();
        var isExternal = explicitMechanism == "external";
        var isExplicitPlain = explicitMechanism == "plain";
        var valueIndex = isExternal || isExplicitPlain ? 3 : 2;
        if (input.Arguments.Count <= valueIndex)
        {
            return CommandResult.Failure(isExternal
                ? "Usage: /network sasl <profile> external <certificate.pfx> [required|optional]"
                : "Usage: /network sasl <profile> plain <account> [required|optional]");
        }
        var policyIndex = valueIndex + 1;
        if (input.Arguments.Count > policyIndex + 1)
            return CommandResult.Failure("Too many SASL configuration arguments");
        var policy = input.Arguments.Count == policyIndex + 1 ? input.Arguments[policyIndex].ToLowerInvariant() : "required";
        if (policy is not ("required" or "optional"))
        {
            return CommandResult.Failure(
                isExternal
                    ? "Usage: /network sasl <profile> external <certificate.pfx> [required|optional]"
                    : "Usage: /network sasl <profile> [plain] <account> [required|optional]");
        }
        if (!profile.IsConfigured || profile.Endpoints.Any(endpoint => !endpoint.UseTls))
        {
            return CommandResult.Failure($"SASL {(isExternal ? "EXTERNAL" : "PLAIN")} requires every server endpoint in the profile to use TLS");
        }

        SaslProfileSettings settings;
        try
        {
            settings = isExternal
                ? SaslProfileSettings.External(Path.GetFullPath(input.Arguments[valueIndex]), required: policy == "required").Validate()
                : new SaslProfileSettings(input.Arguments[valueIndex], policy == "required").Validate();
        }
        catch (ArgumentException exception)
        {
            return CommandResult.Failure(exception.Message);
        }

        var secret = _presenter.ReadSecret(isExternal
            ? "PKCS#12 certificate password (blank if none; Esc cancels): "
            : $"SASL password for {settings.Username} (Esc cancels): ");
        if (secret is null) return CommandResult.Failure("SASL configuration canceled");

        string? certificateFingerprint = null;
        if (isExternal)
        {
            try
            {
                using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                    settings.ClientCertificatePath!, secret, X509KeyStorageFlags.EphemeralKeySet);
                if (!certificate.HasPrivateKey)
                    return CommandResult.Failure("The TLS client certificate does not contain a private key");
                certificateFingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
            {
                return CommandResult.Failure($"Could not load the TLS client certificate: {exception.Message}");
            }
        }

        string? previousSecret = null;
        try
        {
            previousSecret = _networkCredentials.GetSaslSecret(profile.Id);
            _networkCredentials.SetSaslSecret(profile.Id, secret, allowEmpty: isExternal);
            try
            {
                _profileStore.Replace(profile.WithSasl(settings));
            }
            catch
            {
                if (previousSecret is null) _networkCredentials.Remove(profile.Id);
                else _networkCredentials.SetSaslSecret(profile.Id, previousSecret, allowEmpty: true);
                throw;
            }
            return isExternal
                ? CommandResult.Success(new PresentationBlock(
                    $"SASL EXTERNAL: {profile.DisplayName}",
                    [
                        new PresentationField("Certificate", settings.ClientCertificatePath!),
                        new PresentationField("SHA-256 fingerprint", certificateFingerprint!),
                        new PresentationField("Failure policy", policy),
                        new PresentationField("Next", "Associate the fingerprint with your IRC account, then reconnect")
                    ]))
                : CommandResult.Success(
                    $"SASL PLAIN configured for {profile.DisplayName} as {settings.Username} ({policy}); reconnect to authenticate");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return CommandResult.Failure(exception.Message);
        }
    }

    private IrcConnectionOptions ConnectionOptionsForProfile(NetworkProfile profile, IrcIdentity identity) =>
        ApplyProfileSasl(profile, profile.CreateConnectionOptions(identity));

    private IrcConnectionOptions ApplyProfileSasl(NetworkProfile profile, IrcConnectionOptions options)
    {
        if (profile.Sasl is null) return options with { Sasl = null };
        var secret = _networkCredentials.GetSaslSecret(profile.Id);
        if (secret is null)
        {
            throw new InvalidOperationException(
                $"SASL {profile.Sasl.Mechanism} is configured for {profile.DisplayName}, but its encrypted secret is missing; configure SASL again");
        }
        return (options with
        {
            Sasl = profile.Sasl.Mechanism == SaslMechanisms.Plain
                ? new SaslAuthentication(profile.Sasl.Username!, secret, profile.Sasl.Required)
                : SaslAuthentication.External(
                    new TlsClientCertificate(profile.Sasl.ClientCertificatePath!, secret),
                    profile.Sasl.AuthorizationIdentity,
                    profile.Sasl.Required)
        }).Validate();
    }

    private async ValueTask<CommandResult> DisconnectAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        var session = ActiveSession();
        if (session is null)
        {
            return CommandResult.Failure("Not connected.");
        }

        var reason = input.RawArguments.Length == 0 ? "Leaving" : input.RawArguments;
        CancelReconnect(session.State.Id);
        await session.DisconnectAsync(reason, cancellationToken);
        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> ReconnectAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        var active = ActiveSession();
        if (input.Arguments.Count == 1 && input.Arguments[0].Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            if (active is null)
            {
                return CommandResult.Failure("No network session is available.");
            }
            return CancelReconnect(active.State.Id)
                ? CommandResult.Success("Automatic reconnect canceled. The network session remains offline.")
                : CommandResult.Failure("This network is not waiting to reconnect.");
        }
        if (input.Arguments.Count != 0)
        {
            return CommandResult.Failure("Usage: /reconnect [cancel]");
        }
        var recent = RecentConnectionSnapshot();
        var profileId = active is not null ? ProfileIdFor(active) : recent?.ProfileId;
        var profile = profileId is { } id ? _profileStore.Find(id) : null;
        IrcConnectionOptions? options;
        try
        {
            options = profile is null
                ? active?.Options ?? recent?.Options
                : ConnectionOptionsForProfile(profile, CurrentIdentity());
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or ArgumentException)
        {
            return CommandResult.Failure(exception.Message);
        }
        if (options is null)
        {
            return CommandResult.Failure("No previous server is available. Use /server first.");
        }

        if (active is not null)
        {
            CancelReconnect(active.State.Id);
            var reconnectOptions = ConnectionRouteFor(active);
            StartSessionWork(
                active,
                "manual reconnect",
                () => ReconnectSessionManuallyAsync(active, reconnectOptions, cancellationToken));
            return CommandResult.Success("Reconnecting.");
        }

        await StartSessionAsync(options, cancellationToken, profile?.DisplayName, profile?.Id);
        return CommandResult.Success();
    }

    private async Task ReconnectSessionManuallyAsync(
        IrcNetworkSession session,
        IrcConnectionOptions options,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(ConnectionAttemptTimeout);
        try
        {
            await session.ReconnectAsync(options, timeout.Token);
            PublishStatus(session, SessionEventKind.Status, "Reconnected.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            if (FindSession(session.State.Id) is not null)
            {
                PublishStatus(session, SessionEventKind.Error, $"Reconnect failed: {exception.Message}");
            }
        }
    }

    private async ValueTask<CommandResult> QuitAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        _exitRequested = true;
        await CloseAllSessionsAsync(ResolveQuitMessage(input.RawArguments.Length == 0 ? null : input.RawArguments));
        return CommandResult.Success("Goodbye.");
    }

    private async ValueTask<CommandResult> NickAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        if (input.Arguments.Count != 1)
        {
            return CommandResult.Failure("Usage: /nick <nickname>");
        }

        var session = ActiveSession();
        var requestedNickname = input.Arguments[0];
        if (session is null)
        {
            _preferences.Nickname = requestedNickname;
            return CommandResult.Success($"Default nickname set to {_preferences.Nickname}.");
        }

        await session.SendNicknameAsync(requestedNickname, cancellationToken);
        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> AwayAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        if (session is null)
        {
            return failure;
        }

        await session.SendAsync("AWAY", [input.RawArguments.Length == 0 ? _preferences.AwayMessage : input.RawArguments], cancellationToken: cancellationToken);
        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> BackAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        if (session is null)
        {
            return failure;
        }

        await session.SendAsync("AWAY", [string.Empty], cancellationToken: cancellationToken);
        return CommandResult.Success();
    }

    private ValueTask<CommandResult> AwayLogAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        if (input.Arguments.Count == 0 ||
            input.Arguments[0].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(CommandResult.Success(
                $"Away-message recording is {(_preferences.AwayLogging ? "on" : "off")}."));
        }
        if (input.Arguments.Count != 1 || !TryParseOnOff(input.Arguments[0], out var enabled))
        {
            return ValueTask.FromResult(CommandResult.Failure("Usage: /awaylog on|off|status"));
        }
        try
        {
            _preferences.AwayLogging = enabled;
            SaveAppearanceSettings();
            return ValueTask.FromResult(CommandResult.Success(
                $"Away-message recording turned {(enabled ? "on" : "off")}."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ValueTask.FromResult(CommandResult.Failure(exception.Message));
        }
    }

    private ValueTask<CommandResult> AwayMessagesAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        if (session is null) return ValueTask.FromResult(failure);
        try
        {
            var network = AwayNetwork(session);
            var networkKey = network.Key;
            var networkName = network.Name;
            var operation = input.Arguments.Count == 0 ? "list" : input.Arguments[0].ToLowerInvariant();
            switch (operation)
            {
                case "list":
                {
                    if (input.Arguments.Count != 0 && input.Arguments.Count != 1)
                    {
                        return ValueTask.FromResult(CommandResult.Failure(
                            "Usage: /messages [list|read <nick>|delete <nick>|clear]"));
                    }
                    return ValueTask.FromResult(OpenAwayMessageIndex(session, networkKey, networkName));
                }
                case "read":
                {
                    if (input.Arguments.Count != 2)
                    {
                        return ValueTask.FromResult(CommandResult.Failure("Usage: /messages read <nick>"));
                    }
                    var nickname = input.Arguments[1];
                    var entries = _awayMessageStore.ForNetwork(networkKey)
                        .Where(entry => entry.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    if (entries.Length == 0)
                    {
                        return ValueTask.FromResult(CommandResult.Failure(
                            $"No saved away messages from {nickname} on {networkName}."));
                    }
                    _awayMessageStore.MarkRead(networkKey, nickname);
                    SwitchTo(session, AwayMessagesBuffer(session));
                    var rows = entries.Select(entry => (IReadOnlyList<string>)new[]
                    {
                        entry.ReceivedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                        entry.Type,
                        entry.Text
                    }).ToArray();
                    var address = entries[^1].Username is null && entries[^1].Host is null
                        ? nickname
                        : $"{nickname}!{entries[^1].Username ?? "*"}@{entries[^1].Host ?? "*"}";
                    return ValueTask.FromResult(CommandResult.Success(new PresentationBlock(
                        $"MESSAGES: {nickname}",
                        [new PresentationField("Address", address)],
                        new PresentationTable(["Received", "Type", "Message"], rows))));
                }
                case "delete":
                {
                    if (input.Arguments.Count != 2)
                    {
                        return ValueTask.FromResult(CommandResult.Failure("Usage: /messages delete <nick>"));
                    }
                    var removed = _awayMessageStore.Delete(networkKey, input.Arguments[1]);
                    return ValueTask.FromResult(CommandResult.Success(removed == 0
                        ? $"No saved away messages from {input.Arguments[1]}."
                        : $"Deleted {removed} saved away message{(removed == 1 ? string.Empty : "s")} from {input.Arguments[1]}."));
                }
                case "clear":
                {
                    if (input.Arguments.Count != 1)
                    {
                        return ValueTask.FromResult(CommandResult.Failure("Usage: /messages clear"));
                    }
                    var removed = _awayMessageStore.Delete(networkKey);
                    return ValueTask.FromResult(CommandResult.Success(
                        removed == 0 ? $"No saved away messages for {networkName}." :
                        $"Deleted {removed} saved away message{(removed == 1 ? string.Empty : "s")} for {networkName}."));
                }
                default:
                    return ValueTask.FromResult(CommandResult.Failure(
                        "Usage: /messages [list|read <nick>|delete <nick>|clear]"));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ValueTask.FromResult(CommandResult.Failure(exception.Message));
        }
    }

    private CommandResult OpenAwayMessageIndex(IrcNetworkSession session, string networkKey, string networkName)
    {
        var entries = _awayMessageStore.ForNetwork(networkKey);
        if (entries.Count == 0)
        {
            return CommandResult.Success($"No saved away messages for {networkName}.");
        }
        SwitchTo(session, AwayMessagesBuffer(session));
        var rows = entries
            .GroupBy(entry => entry.Nickname, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => (IReadOnlyList<string>)new[]
            {
                group.Key,
                group.Count().ToString(),
                group.Count(entry => !entry.Read).ToString(),
                group.Max(entry => entry.ReceivedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            }).ToArray();
        return CommandResult.Success(new PresentationBlock(
            $"AWAY MESSAGES: {networkName}",
            Table: new PresentationTable(["Nick", "Messages", "Unread", "Last received"], rows),
            Summary: "Use /messages read <nick>, /messages delete <nick>, or /messages clear."));
    }

    private static BufferState AwayMessagesBuffer(IrcNetworkSession session) =>
        session.State.GetOrCreateBuffer(BufferKind.Results, "=messages");

    private (string Key, string Name) AwayNetwork(IrcNetworkSession session)
    {
        var profile = ProfileFor(session);
        var name = session.Features.NetworkName ?? profile?.NetworkName ?? profile?.DisplayName ??
            session.State.DisplayName;
        return ($"network:{name.Trim().ToLowerInvariant()}", name);
    }

    private void RecordAwayMessage(SessionEvent sessionEvent)
    {
        if (!_preferences.AwayLogging ||
            sessionEvent.Kind is not (SessionEventKind.Message or SessionEventKind.Action) ||
            sessionEvent.Fields?.GetValueOrDefault("private") != "true" ||
            sessionEvent.Fields.GetValueOrDefault("replay") == "true" ||
            sessionEvent.Fields.GetValueOrDefault("nick") is not { } nickname ||
            sessionEvent.Fields.GetValueOrDefault("message") is not { } message ||
            FindSession(sessionEvent.NetworkSessionId) is not { } session ||
            !session.State.IsAway)
        {
            return;
        }
        try
        {
            var network = AwayNetwork(session);
            _awayMessageStore.Add(new AwayMessageEntry(
                Guid.NewGuid(),
                network.Key,
                network.Name,
                nickname,
                sessionEvent.Fields.GetValueOrDefault("username"),
                sessionEvent.Fields.GetValueOrDefault("host"),
                sessionEvent.Kind == SessionEventKind.Action ? "action" : "message",
                message,
                sessionEvent.Timestamp,
                Read: false));

            var acknowledge = _sessionTransientState.TryAcknowledgeAwaySender(
                session.State.Id,
                nickname,
                new IrcNameComparer(session.State.CaseMapping));
            if (acknowledge)
            {
                StartSessionWork(
                    session,
                    "away-message acknowledgement",
                    () => AcknowledgeAwayMessageAsync(session, nickname));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PublishStatus(session, SessionEventKind.Error, $"Could not record away message: {exception.Message}");
        }
    }

    private async Task AcknowledgeAwayMessageAsync(IrcNetworkSession session, string nickname)
    {
        try
        {
            await session.SendAsync(
                "NOTICE",
                [nickname, "Your message has been recorded; I am currently away."],
                IrcOutboundPriority.Automation,
                SessionWorkToken(session));
        }
        catch (OperationCanceledException) when (SessionWorkToken(session).IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            PublishStatus(session, SessionEventKind.Error,
                $"Could not acknowledge the away message from {nickname}: {exception.Message}");
        }
    }

    private ValueTask<CommandResult> BufferAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        var entries = OrderedBuffers().ToArray();
        if (entries.Length == 0)
        {
            return ValueTask.FromResult(CommandResult.Failure("Not connected."));
        }

        if (input.Arguments.Count == 0)
        {
            var rows = entries.Select(entry => (IReadOnlyList<string>)new[]
            {
                BufferNumber(entry.Buffer.Id).ToString(),
                _windowStates.IsActiveBuffer(entry.Buffer.Id) ? "*" : string.Empty,
                ProfileFor(entry.Session)?.DisplayName ?? entry.Session.Features.NetworkName ?? entry.Session.State.DisplayName,
                WindowTarget(entry.Buffer),
                entry.Buffer.Kind.ToString(),
                ActivitySuffix(entry.Buffer.Id).Trim()
            }).ToArray();
            return ValueTask.FromResult(CommandResult.Success(new PresentationBlock(
                "Active Windows",
                Table: new PresentationTable(["No.", "", "Network", "Target", "Type", "Activity"], rows))));
        }

        (IrcNetworkSession Session, BufferState Buffer)? selected = null;
        if (int.TryParse(input.Arguments[0], out var number) && number >= 1)
        {
            var match = entries.FirstOrDefault(entry => BufferNumber(entry.Buffer.Id) == number);
            if (match.Buffer is not null)
            {
                selected = match;
            }
        }
        else
        {
            selected = FindBuffer(input.Arguments[0]);
        }

        return ValueTask.FromResult(selected is null
            ? CommandResult.Failure($"No buffer matches '{input.Arguments[0]}'.")
            : SwitchTo(selected.Value.Session, selected.Value.Buffer));
    }

    private ValueTask<CommandResult> NextBufferAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(MoveBuffer(1));

    internal static string WindowTarget(BufferState buffer) =>
        buffer.Kind == BufferKind.Status ? "-" : buffer.Name;

    private ValueTask<CommandResult> PreviousBufferAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(MoveBuffer(-1));

    internal enum BufferCloseAction
    {
        Refuse,
        CloseImmediately,
        PartThenClose
    }

    internal static BufferCloseAction CloseActionFor(BufferKind kind, bool joinedChannel) => kind switch
    {
        BufferKind.Status => BufferCloseAction.Refuse,
        BufferKind.Channel when joinedChannel => BufferCloseAction.PartThenClose,
        _ => BufferCloseAction.CloseImmediately
    };

    private async ValueTask<CommandResult> CloseBufferAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        var force = input.Arguments.Count == 1 &&
            input.Arguments[0].Equals("--force", StringComparison.OrdinalIgnoreCase);
        if (input.Arguments.Count != 0 && !force)
        {
            return CommandResult.Failure("Usage: /close [--force]");
        }
        var active = ActiveBuffer();
        if (active is null)
        {
            return CommandResult.Failure("No active window.");
        }

        var session = ActiveSession()!;
        if (active.Kind == BufferKind.Status)
        {
            var reconnecting = _liveSessions.IsReconnecting(session.State.Id);
            var busy = reconnecting || session.ConnectionState is not
                (IrcConnectionState.Disconnected or IrcConnectionState.Failed);
            if (busy && !force)
            {
                return CommandResult.Failure(
                    "This network session is connected. Use /close --force to disconnect it and close all associated windows.");
            }

            await CloseSessionAsync(session, "Closing network session");
            RedrawActiveBuffer();
            return CommandResult.Success();
        }
        var joinedChannel = active.Kind == BufferKind.Channel &&
            session.State.TryGetChannel(active.Name, out var channel) &&
            channel!.TryGetMember(session.CurrentNickname, out _);
        var action = CloseActionFor(active.Kind, joinedChannel);
        if (active.Kind == BufferKind.DccChat)
        {
            await CloseDccChatBufferAsync(session, active);
            return CommandResult.Success();
        }
        if (action == BufferCloseAction.PartThenClose)
        {
            await session.SendAsync("PART", [active.Name], cancellationToken: cancellationToken);
            return CommandResult.Success();
        }

        CloseLocalBuffer(session, active);
        return CommandResult.Success();
    }

    private void CloseLocalBuffer(IrcNetworkSession session, BufferState buffer)
    {
        lock (_windowTransactionGate)
        {
            SelectPreviousBufferUnsafe(buffer.Id, session);
            _windowStates.Remove(buffer.Id);
            session.State.RemoveBuffer(buffer.Id);
        }

        _presenter.ForgetInputHistory(buffer.Id);
        RedrawActiveBuffer();
    }

    private async Task CloseDccChatBufferAsync(IrcNetworkSession session, BufferState buffer)
    {
        if (_dcc.RequestIdForChatBuffer(buffer.Id) is not { } requestId)
        {
            CloseLocalBuffer(session, buffer);
            return;
        }

        if (_dcc.Requests.TryGet(requestId, out var request))
        {
            if (request!.State is DccRequestState.Pending or DccRequestState.Connecting)
            {
                CancelDccExpiration(requestId);
                CancelDccChatConnection(requestId);
                await StopDccChatListenerAsync(requestId);
                if (_dcc.Requests.TryTransition(requestId, DccRequestState.Cancelled,
                        $"The DCC {DccProtocolName(request.Offer)} request was canceled locally", out var cancelled))
                {
                    PublishDccState(cancelled!,
                        $"DCC {DccProtocolName(request.Offer)} request #{requestId} with {request.Sender} was canceled");
                }
            }
            else if (request.State == DccRequestState.Connected)
            {
                await EndDccChatAsync(requestId, DccRequestState.Closed,
                    $"DCC {DccProtocolName(request.Offer)} closed locally");
            }
        }
        _dcc.ClearChatBuffer(requestId, buffer.Id);
        CloseLocalBuffer(session, buffer);
    }

    private void SelectPreviousBufferUnsafe(BufferId closingBufferId, IrcNetworkSession fallbackSession)
    {
        var closingNumber = _windowStates.NumberOr(closingBufferId, int.MaxValue);
        var previous = _liveSessions.SessionSnapshot()
            .SelectMany(candidateSession => candidateSession.State.Buffers
                .Where(candidateBuffer => candidateBuffer.Id != closingBufferId)
                .Select(candidateBuffer => (
                    Session: candidateSession,
                    Buffer: candidateBuffer,
                    Number: AssignBufferNumberUnsafe(candidateBuffer.Id))))
            .Where(candidate => candidate.Number < closingNumber)
            .OrderByDescending(candidate => candidate.Number)
            .FirstOrDefault();

        if (previous.Buffer is not null)
        {
            _windowStates.Activate(previous.Session.State.Id, previous.Buffer.Id);
            return;
        }

        _windowStates.Activate(fallbackSession.State.Id, fallbackSession.State.StatusBuffer.Id);
    }

    internal static int? PreviousBufferNumber(int closingNumber, IEnumerable<int> openNumbers) =>
        openNumbers.Where(number => number < closingNumber).Select(number => (int?)number).Max();

    private ValueTask<CommandResult> ClearAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        _presenter.Clear();
        return ValueTask.FromResult(CommandResult.Success());
    }

    private async ValueTask<CommandResult> NotifyAsync(CommandContext context, CommandInput input, CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        if (session is null) return failure;
        NetworkProfile profile;
        try { profile = EnsureProfileFor(session, out _); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { return CommandResult.Failure(exception.Message); }

        if (input.Arguments.Count == 0 || input.Arguments.Count == 1 && input.Arguments[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var online = _liveSessions.Runtime(session)?.Notify.OnlineSnapshot(IrcNameComparerFor(session)) ?? [];
            return profile.NotifyNicknames.Count == 0
                ? CommandResult.Success($"No notify entries for {profile.DisplayName}.")
                : CommandResult.Success(new PresentationBlock(
                    $"Notify: {profile.DisplayName}",
                    profile.NotifyNicknames.OrderBy(nick => nick, StringComparer.OrdinalIgnoreCase)
                        .Select(nick => new PresentationField(nick, online.Contains(nick) ? "online" : "offline")).ToArray()));
        }

        var operation = input.Arguments[0].ToLowerInvariant();
        IReadOnlyList<string> nicknames;
        if (operation is "add" or "+" or "remove" or "delete" or "-") nicknames = input.Arguments.Skip(1).ToArray();
        else if (input.Arguments.Count == 1 && input.Arguments[0].Length > 1 && input.Arguments[0][0] is '+' or '-')
        {
            operation = input.Arguments[0][0] == '+' ? "add" : "remove";
            nicknames = [input.Arguments[0][1..]];
        }
        else return CommandResult.Failure("Usage: /notify [list|add <nick...>|remove <nick...>|+nick|-nick]");

        if (nicknames.Count == 0 || nicknames.Any(nick => !IsValidNotifyNickname(session, nick)))
            return CommandResult.Failure("Notify entries must be individual nicknames without spaces, commas, or channel prefixes.");

        var updated = profile.NotifyNicknames.ToList();
        if (operation is "add" or "+")
        {
            foreach (var nickname in nicknames)
                if (!updated.Contains(nickname, StringComparer.OrdinalIgnoreCase)) updated.Add(nickname);
        }
        else updated.RemoveAll(existing => nicknames.Contains(existing, StringComparer.OrdinalIgnoreCase));

        try
        {
            profile = profile.WithNotify(updated);
            _profileStore.Replace(profile);
            var runtime = RuntimeFor(session);
            if (runtime is not null && session.ConnectionState == IrcConnectionState.Online)
            {
                await runtime.Notify.RefreshAsync(
                    token => RequestNotifyStatusAsync(session, profile, runtime.Notify, token).AsTask(),
                    cancellationToken);
            }
            if (profile.NotifyNicknames.Count > 0) StartNotifyMonitor(session);
            else runtime?.Notify.Stop();
            return CommandResult.Success($"Notify list for {profile.DisplayName}: " +
                (profile.NotifyNicknames.Count == 0 ? "empty" : string.Join(", ", profile.NotifyNicknames)));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { return CommandResult.Failure(exception.Message); }
    }

    private static bool IsValidNotifyNickname(IrcNetworkSession session, string nickname) =>
        !string.IsNullOrWhiteSpace(nickname) && nickname.Length <= 64 && !session.Features.IsChannel(nickname) &&
        nickname.IndexOfAny([' ', ',', '\r', '\n', '\0']) < 0;

    private async ValueTask<CommandResult> AcceptAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        var session = RequireSession(out var failure);
        if (session is null) return failure;
        if (session.ConnectionState != IrcConnectionState.Online)
            return CommandResult.Failure("Not connected to a server");

        if (input.Arguments.Count == 0)
        {
            await session.SendAsync("ACCEPT", ["*"], IrcOutboundPriority.Interactive, cancellationToken);
            return CommandResult.Success();
        }

        var entries = input.Arguments
            .SelectMany(argument => argument.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();
        if (entries.Length == 0 || entries.Any(entry =>
                !IsValidNotifyNickname(session, entry[0] == '-' ? entry[1..] : entry)))
            return CommandResult.Failure("Usage: /accept [[-]nick,...]");

        await session.SendAsync("ACCEPT", [string.Join(',', entries)], IrcOutboundPriority.Interactive, cancellationToken);
        return CommandResult.Success();
    }

    private async ValueTask<CommandResult> AutojoinRemoveAliasAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        if (input.Arguments.Count == 0 && ActiveChannel() is null)
        {
            return await HelpAsync(
                context,
                new CommandInput("help", ["rj"], "rj"),
                cancellationToken);
        }

        return await AutojoinAsync(
            context,
            new CommandInput("autojoin", ["remove", .. input.Arguments], $"remove {input.RawArguments}".TrimEnd()),
            cancellationToken);
    }

    private async ValueTask<CommandResult> AutojoinAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        if (input.Arguments.Count == 0)
        {
            if (ActiveChannel() is not { } activeChannel)
            {
                return await HelpAsync(
                    context,
                    new CommandInput("help", ["autojoin"], "autojoin"),
                    cancellationToken);
            }
            input = new CommandInput("autojoin", ["add", activeChannel], $"add {activeChannel}");
        }

        var session = RequireSession(out var failure);
        if (session is null)
        {
            return failure;
        }

        NetworkProfile profile;
        bool createdProfile;
        try
        {
            profile = EnsureProfileFor(session, out createdProfile);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return CommandResult.Failure(exception.Message);
        }

        if (session.Features.IsChannel(input.Arguments[0]))
        {
            input = new CommandInput("autojoin", ["add", input.Arguments[0]], $"add {input.Arguments[0]}");
        }

        var creationMessage = createdProfile ? $"Created saved network profile {profile.DisplayName}. " : string.Empty;
        var operation = input.Arguments[0].ToLowerInvariant();
        switch (operation)
        {
            case "list":
                return profile.AutojoinChannels.Count == 0
                    ? CommandResult.Success($"{creationMessage}{profile.DisplayName} has no autojoin channels.")
                    : CommandResult.Success($"{creationMessage}Autojoin for {profile.DisplayName}: {string.Join(", ", profile.AutojoinChannels)}");
            case "add":
                var addChannel = input.Arguments.Count > 1 ? input.Arguments[1] : ActiveChannel();
                if (input.Arguments.Count > 2 || addChannel is null || !session.Features.IsChannel(addChannel))
                {
                    return CommandResult.Failure("Usage: /autojoin add [channel] or /aj [channel]");
                }

                var alreadySaved = profile.AutojoinChannels.Contains(addChannel, StringComparer.OrdinalIgnoreCase);
                if (!alreadySaved)
                {
                    var saved = SaveAutojoin(profile, [.. profile.AutojoinChannels, addChannel],
                        $"{creationMessage}Added {addChannel} to autojoin for {profile.DisplayName}.");
                    if (!saved.Succeeded)
                    {
                        return saved;
                    }
                }

                var joined = session.State.TryGetChannel(addChannel, out _);
                if (session.ConnectionState == IrcConnectionState.Online && !joined)
                {
                    await SendJoinAsync(session, [addChannel], IrcOutboundPriority.Interactive, cancellationToken,
                        ActiveBuffer()?.Id ?? session.State.StatusBuffer.Id);
                    return CommandResult.Success(alreadySaved
                        ? $"{addChannel} is already in autojoin for {profile.DisplayName}; joining it now."
                        : $"{creationMessage}Added {addChannel} to autojoin for {profile.DisplayName}; joining it now.");
                }

                return CommandResult.Success(alreadySaved
                    ? $"{addChannel} is already in autojoin for {profile.DisplayName}."
                    : $"{creationMessage}Added {addChannel} to autojoin for {profile.DisplayName}.");
            case "remove":
                var removeChannel = input.Arguments.Count > 1 ? input.Arguments[1] : ActiveChannel();
                if (input.Arguments.Count > 2 || removeChannel is null)
                {
                    return CommandResult.Failure("Usage: /autojoin remove [channel] or /rj [channel]");
                }

                if (!profile.AutojoinChannels.Contains(removeChannel, StringComparer.OrdinalIgnoreCase))
                {
                    return CommandResult.Failure($"{removeChannel} is not in autojoin for {profile.DisplayName}.");
                }

                return SaveAutojoin(
                    profile,
                    profile.AutojoinChannels.Where(channel => !channel.Equals(removeChannel, StringComparison.OrdinalIgnoreCase)),
                    $"Removed {removeChannel} from autojoin for {profile.DisplayName}.");
            case "run":
                if (input.Arguments.Count != 1)
                {
                    return CommandResult.Failure("Usage: /autojoin run");
                }

                await RunAutojoinAsync(session, profile, cancellationToken);
                return CommandResult.Success(createdProfile ? creationMessage.TrimEnd() : null);
            case "clear":
                if (input.Arguments.Count != 2 || !input.Arguments[1].Equals("--force", StringComparison.OrdinalIgnoreCase))
                {
                    return CommandResult.Failure("Usage: /autojoin clear --force");
                }

                return SaveAutojoin(profile, [], $"{creationMessage}Cleared autojoin for {profile.DisplayName}.");
            default:
                return CommandResult.Failure("Usage: /autojoin list|add|remove|run|clear");
        }
    }

}
