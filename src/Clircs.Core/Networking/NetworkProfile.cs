using System.Collections.ObjectModel;
using Clircs.Identity;

namespace Clircs.Networking;

public sealed record ReconnectPolicy(int MaximumAttempts, TimeSpan InitialDelay, TimeSpan MaximumDelay)
{
    public static ReconnectPolicy Default { get; } = new(99, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(2));

    public ReconnectPolicy Validate()
    {
        if (MaximumAttempts is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumAttempts), "Reconnect attempts must be from 0 through 100.");
        }

        if (InitialDelay < TimeSpan.FromSeconds(1) || InitialDelay > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(InitialDelay), "The initial reconnect delay must be from 1 second through 10 minutes.");
        }

        if (MaximumDelay < InitialDelay || MaximumDelay > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumDelay), "The maximum reconnect delay must be at least the initial delay and no more than 1 hour.");
        }

        return this;
    }
}

public sealed record SaslProfileSettings
{
    public SaslProfileSettings(string username, bool Required = true)
        : this(SaslMechanisms.Plain, username, null, Required) { }

    private SaslProfileSettings(string mechanism, string? authorizationIdentity, string? clientCertificatePath, bool required)
    {
        Mechanism = mechanism;
        AuthorizationIdentity = authorizationIdentity;
        ClientCertificatePath = clientCertificatePath;
        Required = required;
    }

    public string Mechanism { get; }
    public string? AuthorizationIdentity { get; }
    public string Username => Mechanism == SaslMechanisms.Plain ? AuthorizationIdentity! : string.Empty;
    public string? ClientCertificatePath { get; }
    public bool Required { get; }

    public static SaslProfileSettings External(string clientCertificatePath,
        string? authorizationIdentity = null, bool required = true) =>
        new(SaslMechanisms.External, authorizationIdentity, clientCertificatePath, required);

    public SaslProfileSettings Validate()
    {
        if (Mechanism == SaslMechanisms.Plain && string.IsNullOrWhiteSpace(AuthorizationIdentity))
            throw new ArgumentException("The SASL account is required.", nameof(AuthorizationIdentity));
        if (AuthorizationIdentity is not null &&
            (AuthorizationIdentity.Length > 256 || AuthorizationIdentity.IndexOfAny(['\r', '\n', '\0']) >= 0))
        {
            throw new ArgumentException("The SASL identity must contain no more than 256 characters without CR, LF, or NUL.", nameof(AuthorizationIdentity));
        }
        if (Mechanism == SaslMechanisms.External)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ClientCertificatePath);
            if (ClientCertificatePath.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new ArgumentException("The client certificate path cannot contain CR, LF, or NUL.", nameof(ClientCertificatePath));
        }
        else if (Mechanism != SaslMechanisms.Plain)
            throw new ArgumentException($"Unsupported SASL mechanism '{Mechanism}'.", nameof(Mechanism));
        return this;
    }
}

public sealed class NetworkProfile
{
    public NetworkProfile(
        NetworkProfileId id,
        string displayName,
        IEnumerable<IrcEndpoint> endpoints,
        IrcIdentity identity,
        IEnumerable<string>? autojoinChannels = null,
        ReconnectPolicy? reconnect = null,
        string? networkName = null,
        IEnumerable<string>? notifyNicknames = null,
        string userModes = "+i",
        SaslProfileSettings? sasl = null)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("A network profile ID is required.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var normalizedName = displayName.Trim();
        if (normalizedName.IndexOfAny(['/', '\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("A network name cannot contain slash, CR, LF, or NUL.", nameof(displayName));
        }

        ArgumentNullException.ThrowIfNull(endpoints);
        var endpointArray = endpoints.ToArray();

        ArgumentNullException.ThrowIfNull(identity);
        var normalizedNetworkName = string.IsNullOrWhiteSpace(networkName) ? null : networkName.Trim();
        if (normalizedNetworkName?.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("A server-advertised network name cannot contain CR, LF, or NUL.", nameof(networkName));
        }

        var channels = (autojoinChannels ?? [])
            .Select(channel => channel.Trim())
            .Where(channel => channel.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (channels.Any(channel => channel.IndexOfAny([' ', ',', '\r', '\n', '\0']) >= 0))
        {
            throw new ArgumentException("Autojoin channels must be individual IRC channel tokens.", nameof(autojoinChannels));
        }
        var notify = (notifyNicknames ?? [])
            .Select(nickname => nickname.Trim())
            .Where(nickname => nickname.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (notify.Any(nickname => nickname.IndexOfAny([' ', ',', '\r', '\n', '\0']) >= 0))
        {
            throw new ArgumentException("Notify entries must be individual IRC nicknames.", nameof(notifyNicknames));
        }
        userModes = NormalizeUserModes(userModes);
        sasl = sasl?.Validate();
        if (sasl is not null && endpointArray.Any(endpoint => !endpoint.UseTls))
        {
            throw new ArgumentException($"Every endpoint in a SASL {sasl.Mechanism} profile must use TLS.", nameof(endpoints));
        }

        Id = id;
        DisplayName = normalizedName;
        Endpoints = new ReadOnlyCollection<IrcEndpoint>(endpointArray);
        Identity = identity;
        NetworkName = normalizedNetworkName;
        AutojoinChannels = new ReadOnlyCollection<string>(channels);
        NotifyNicknames = new ReadOnlyCollection<string>(notify);
        Reconnect = (reconnect ?? ReconnectPolicy.Default).Validate();
        UserModes = userModes;
        Sasl = sasl;
    }

    public NetworkProfileId Id { get; }

    public string DisplayName { get; }

    public IReadOnlyList<IrcEndpoint> Endpoints { get; }

    public IrcIdentity Identity { get; }

    public string? NetworkName { get; }

    public IReadOnlyList<string> AutojoinChannels { get; }

    public IReadOnlyList<string> NotifyNicknames { get; }

    public ReconnectPolicy Reconnect { get; }

    public string UserModes { get; }

    public SaslProfileSettings? Sasl { get; }

    public bool IsConfigured => Endpoints.Count > 0;

    public IrcConnectionOptions CreateConnectionOptions(int endpointIndex = 0)
        => CreateConnectionOptions(Identity, endpointIndex);

    public IrcConnectionOptions CreateConnectionOptions(IrcIdentity identity, int endpointIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (Endpoints.Count == 0)
        {
            throw new InvalidOperationException(
                $"Network profile {DisplayName} has no server endpoint. Configure one with /network add {DisplayName} <host> [port] [--tls].");
        }
        if (endpointIndex < 0 || endpointIndex >= Endpoints.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(endpointIndex));
        }

        return new IrcConnectionOptions(Endpoints[endpointIndex], identity);
    }

    public NetworkProfile WithAutojoin(IEnumerable<string> channels) =>
        new(Id, DisplayName, Endpoints, Identity, channels, Reconnect, NetworkName, NotifyNicknames, UserModes, Sasl);

    public NetworkProfile WithNotify(IEnumerable<string> nicknames) =>
        new(Id, DisplayName, Endpoints, Identity, AutojoinChannels, Reconnect, NetworkName, nicknames, UserModes, Sasl);

    public NetworkProfile WithNetworkName(string networkName) =>
        new(Id, DisplayName, Endpoints, Identity, AutojoinChannels, Reconnect, networkName, NotifyNicknames, UserModes, Sasl);

    public NetworkProfile WithUserModes(string userModes) =>
        new(Id, DisplayName, Endpoints, Identity, AutojoinChannels, Reconnect, NetworkName, NotifyNicknames, userModes, Sasl);

    public NetworkProfile WithIdentity(IrcIdentity identity) =>
        new(Id, DisplayName, Endpoints, identity, AutojoinChannels, Reconnect, NetworkName, NotifyNicknames, UserModes, Sasl);

    public NetworkProfile WithSasl(SaslProfileSettings? sasl) =>
        new(Id, DisplayName, Endpoints, Identity, AutojoinChannels, Reconnect, NetworkName, NotifyNicknames, UserModes, sasl);

    public NetworkProfile WithEndpoint(IrcEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (Endpoints.Any(existing =>
            existing.Port == endpoint.Port &&
            existing.UseTls == endpoint.UseTls &&
            existing.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase)))
        {
            return this;
        }

        return new NetworkProfile(
            Id,
            DisplayName,
            [.. Endpoints, endpoint],
            Identity,
            AutojoinChannels,
            Reconnect,
            NetworkName,
            NotifyNicknames,
            UserModes,
            Sasl);
    }

    public NetworkProfile WithoutEndpoint(IrcEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var remaining = Endpoints.Where(existing =>
            existing.Port != endpoint.Port ||
            existing.UseTls != endpoint.UseTls ||
            !existing.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (remaining.Length == Endpoints.Count)
        {
            return this;
        }

        return new NetworkProfile(
            Id,
            DisplayName,
            remaining,
            Identity,
            AutojoinChannels,
            Reconnect,
            NetworkName,
            NotifyNicknames,
            UserModes,
            Sasl);
    }

    public static string NormalizeUserModes(string? value)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase) || value.Equals("off", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        if (value.Length == 0) return string.Empty;
        if (value.Length < 2 || value[0] is not '+' and not '-' || value.Skip(1).Any(character => !char.IsLetter(character)))
            throw new ArgumentException("User modes must look like +i or +iw, or be none.", nameof(value));
        return value;
    }
}
