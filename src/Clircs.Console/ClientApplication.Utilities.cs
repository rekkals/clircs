using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Clircs.Commands;
using Clircs.Dcc;
using Clircs.Identity;
using Clircs.Networking;
using Clircs.Sessions;
using Clircs.Transport;

namespace Clircs.ConsoleClient;

// Owns shared application helpers for status, appearance, formatting, and lookup.
internal sealed partial class ClientApplication
{
    private CommandResult SessionStatus(IrcNetworkSession session)
    {
        var profile = ProfileFor(session);
        var upstreamTls = session.State.BouncerName is null
            ? session.State.ClientTransportTls
            : session.State.UpstreamTls;
        var fields = new List<PresentationField>
        {
            new("Network", profile?.DisplayName ?? session.Features.NetworkName ?? session.State.DisplayName),
            new("Connection", ConnectionStatusLabel(session)),
            new("IRC server", session.State.ServerName ?? session.Options.Endpoint.Host),
            new("IRC network TLS", upstreamTls switch
            {
                true => "enabled",
                false => "disabled",
                null => "not confirmed"
            }),
            new("Nickname", session.CurrentNickname)
        };
        if (session.State.BouncerName is null)
        {
            fields.Insert(4, new PresentationField("Server endpoint", session.Options.Endpoint.ToString()));
        }
        else
        {
            fields.Insert(4, new PresentationField("Bouncer endpoint", session.Options.Endpoint.ToString()));
            fields.Insert(5, new PresentationField(
                "Bouncer TLS",
                session.State.ClientTransportTls ? "enabled" : "disabled"));
            fields.Insert(6, new PresentationField("Connected through", session.State.BouncerName));
        }
        if (profile is null)
        {
            fields.Add(new PresentationField("Profile", "temporary connection"));
        }
        else
        {
            fields.Add(new PresentationField("Profile", profile.DisplayName));
            fields.Add(new PresentationField("Autojoin", profile.AutojoinChannels.Count == 0 ? "none" : string.Join(", ", profile.AutojoinChannels)));
            fields.Add(new PresentationField("User modes", profile.UserModes.Length == 0 ? "none" : profile.UserModes));
            fields.Add(new PresentationField("SASL", profile.Sasl is null
                ? "off"
                : profile.Sasl.Mechanism == SaslMechanisms.Plain
                    ? $"PLAIN as {profile.Sasl.Username} ({(profile.Sasl.Required ? "required" : "optional")})"
                    : $"EXTERNAL ({(profile.Sasl.Required ? "required" : "optional")})"));
            fields.Add(new PresentationField("Reconnect limit", profile.Reconnect.MaximumAttempts.ToString(CultureInfo.InvariantCulture)));
        }
        if (session.State.AccountName is not null)
        {
            fields.Add(new PresentationField("Authenticated account", session.State.AccountName));
        }
        return CommandResult.Success(new PresentationBlock("Network status", fields));
    }

    private CommandResult ProfileStatus(NetworkProfile profile) => CommandResult.Success(new PresentationBlock(
        $"Network profile: {profile.DisplayName}",
        [
            new PresentationField("Servers", profile.IsConfigured ? string.Join(", ", profile.Endpoints) : "[unconfigured]"),
            new PresentationField("Nicknames", string.Join(", ", profile.Identity.Nicknames)),
            new PresentationField("Username", profile.Identity.Username),
            new PresentationField("Autojoin", profile.AutojoinChannels.Count == 0 ? "none" : string.Join(", ", profile.AutojoinChannels)),
            new PresentationField("User modes", profile.UserModes.Length == 0 ? "none" : profile.UserModes),
            new PresentationField("Notify", profile.NotifyNicknames.Count == 0 ? "none" : string.Join(", ", profile.NotifyNicknames)),
            new PresentationField("SASL", profile.Sasl is null
                ? "off"
                : profile.Sasl.Mechanism == SaslMechanisms.Plain
                    ? $"PLAIN as {profile.Sasl.Username} ({(profile.Sasl.Required ? "required" : "optional")}; " +
                      $"password {(_networkCredentials.HasSaslSecret(profile.Id) ? "saved" : "missing")})"
                    : $"EXTERNAL ({(profile.Sasl.Required ? "required" : "optional")}; certificate password " +
                      $"{(_networkCredentials.HasSaslSecret(profile.Id) ? "saved" : "missing")})"),
            new PresentationField("Reconnect limit", profile.Reconnect.MaximumAttempts.ToString(CultureInfo.InvariantCulture))
        ]));

    internal static string PrefixCountLabel(char mode, char symbol) => mode switch
    {
        'Y' => "sys",
        'q' => "own",
        'a' => "adm",
        'o' => "ops",
        'h' => "hlf",
        'v' => "voc",
        _ => symbol.ToString()
    };

    private static string FormatTlsFingerprint(string fingerprint)
    {
        var normalized = TlsCertificateInfo.NormalizeFingerprint(fingerprint);
        return string.Join(':', Enumerable.Range(0, normalized.Length / 2)
            .Select(index => normalized.Substring(index * 2, 2)));
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true;
        _exitRequested = true;
        _lifetime.Cancel();
    }

    private static bool TrySplitFirst(string value, out string first, out string rest)
    {
        var separator = value.IndexOf(' ');
        if (separator <= 0)
        {
            first = value;
            rest = string.Empty;
            return false;
        }

        first = value[..separator];
        rest = value[(separator + 1)..].TrimStart();
        return rest.Length > 0;
    }

    private static string CreateDefaultNickname()
    {
        var sanitized = new string(Environment.UserName.Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-').ToArray());
        return sanitized.Length == 0 ? $"clircs{Environment.ProcessId % 10000}" : sanitized[..Math.Min(20, sanitized.Length)];
    }

    private static string CreateDefaultUsername()
    {
        var sanitized = new string(Environment.UserName.Where(char.IsAsciiLetterOrDigit).ToArray());
        return sanitized.Length == 0 ? "clircs" : sanitized[..Math.Min(12, sanitized.Length)].ToLowerInvariant();
    }

    private bool IsUnread(BufferId id)
        => _windowStates.IsUnread(id);

    private int BufferNumber(BufferId id)
        => AssignBufferNumberUnsafe(id);

    private int AssignBufferNumberUnsafe(BufferId id)
    {
        return _windowStates.AssignNumber(id);
    }

    private string ActivitySuffix(BufferId id)
        => string.Join(',', _windowStates.UnreadKinds(id)
            .OrderByDescending(ActivityPriority)
            .Select(ActivityName));

    private static int ActivityPriority(SessionEventKind kind) => kind switch
    {
        SessionEventKind.Error => 100,
        SessionEventKind.Highlight => 95,
        SessionEventKind.Message => 90,
        SessionEventKind.Action => 80,
        SessionEventKind.Notice => 70,
        SessionEventKind.Protection => 70,
        SessionEventKind.Nick => 60,
        SessionEventKind.Topic => 50,
        SessionEventKind.Mode => 40,
        SessionEventKind.Join or SessionEventKind.Part => 30,
        SessionEventKind.Server => 20,
        _ => 10
    };

    private static string ActivityName(SessionEventKind kind) => kind.ToString().ToLowerInvariant();

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        duration = default;
        if (value.Length < 2 || !double.TryParse(value[..^1], System.Globalization.NumberStyles.AllowDecimalPoint,
            System.Globalization.CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            return false;
        }

        duration = char.ToLowerInvariant(value[^1]) switch
        {
            's' => TimeSpan.FromSeconds(amount),
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            _ => default
        };
        return duration >= TimeSpan.FromSeconds(1) && duration <= TimeSpan.FromDays(30);
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalDays >= 1
        ? $"{duration.TotalDays:0.##}d"
        : duration.TotalHours >= 1
            ? $"{duration.TotalHours:0.##}h"
            : duration.TotalMinutes >= 1
                ? $"{duration.TotalMinutes:0.##}m"
                : $"{duration.TotalSeconds:0.##}s";

    private static bool TryParseOutputDestination(string value, out OutputDestination destination)
    {
        if (value.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            destination = OutputDestination.Active;
            return true;
        }

        if (value.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            destination = OutputDestination.Status;
            return true;
        }

        if (value.Equals("dedicated", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("window", StringComparison.OrdinalIgnoreCase))
        {
            destination = OutputDestination.Dedicated;
            return true;
        }

        destination = default;
        return false;
    }

    private static string FormatOutputDestination(OutputDestination destination, bool callDedicatedWindow = false) => destination switch
    {
        OutputDestination.Active => "active",
        OutputDestination.Status => "status",
        OutputDestination.Dedicated => callDedicatedWindow ? "window" : "dedicated",
        _ => throw new ArgumentOutOfRangeException(nameof(destination))
    };

    private static bool TryParseHostmaskVisibility(string value, out HostmaskVisibility visibility)
    {
        if (value.Equals("full", StringComparison.OrdinalIgnoreCase))
        {
            visibility = HostmaskVisibility.UserHost;
            return true;
        }
        if (value.Equals("userhost", StringComparison.OrdinalIgnoreCase))
        {
            visibility = HostmaskVisibility.UserHost;
            return true;
        }
        if (value.Equals("host", StringComparison.OrdinalIgnoreCase))
        {
            visibility = HostmaskVisibility.Host;
            return true;
        }
        if (value.Equals("off", StringComparison.OrdinalIgnoreCase) || value.Equals("nick", StringComparison.OrdinalIgnoreCase))
        {
            visibility = HostmaskVisibility.Off;
            return true;
        }
        visibility = default;
        return false;
    }

    private static string FormatHostmaskVisibility(HostmaskVisibility visibility) => visibility switch
    {
        HostmaskVisibility.Full => "userhost",
        HostmaskVisibility.UserHost => "userhost",
        HostmaskVisibility.Host => "host",
        HostmaskVisibility.Off => "off",
        _ => throw new ArgumentOutOfRangeException(nameof(visibility))
    };

    private static bool TryParseOnOff(string value, out bool enabled)
    {
        if (value.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            enabled = true;
            return true;
        }
        if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            enabled = false;
            return true;
        }
        enabled = false;
        return false;
    }

    private static string? ParseOptionalDefault(string value) =>
        value.Equals("none", StringComparison.OrdinalIgnoreCase) || value.Equals("random", StringComparison.OrdinalIgnoreCase)
            ? null
            : TerminalTextSanitizer.Sanitize(value).Trim();

    private static bool IsValidDccAddressSetting(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 253 ||
            value.Any(char.IsWhiteSpace) || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            return false;
        }
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(value, out var address))
        {
            return address.AddressFamily switch
            {
                AddressFamily.InterNetwork => !IPAddress.Any.Equals(address) && !IPAddress.Broadcast.Equals(address),
                AddressFamily.InterNetworkV6 => !IPAddress.IPv6Any.Equals(address) && !IPAddress.IPv6None.Equals(address),
                _ => false
            };
        }
        return Uri.CheckHostName(value) == UriHostNameType.Dns;
    }

    private static string DefaultDccDownloadDirectory(string dataDirectory)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile)
            ? Path.Combine(dataDirectory, "downloads")
            : Path.Combine(profile, "Downloads");
    }

    private static string ResolveDccDownloadDirectory(string? configured, string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(configured)) return DefaultDccDownloadDirectory(dataDirectory);
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return DefaultDccDownloadDirectory(dataDirectory);
        }
    }

    private string ResolveKickMessage(string? supplied) => ResolveMessage(supplied, _preferences.DefaultKickMessage);

    private string ResolveQuitMessage(string? supplied) => ResolveMessage(supplied, _preferences.DefaultQuitMessage);

    private string ResolveTopicMessage(string? supplied) => ResolveMessage(supplied, _preferences.DefaultTopicMessage);

    private string ResolveMessage(string? supplied, string? configured) =>
        !string.IsNullOrWhiteSpace(supplied) ? TerminalTextSanitizer.Sanitize(supplied).Trim() :
        !string.IsNullOrWhiteSpace(configured) ? configured : _quotes.Next();

    private AppearanceSettings CaptureAppearanceSettings() => new(
        _presenter.Theme.Name,
        FormatHostmaskVisibility(_preferences.JoinHostmasks),
        FormatHostmaskVisibility(_preferences.PartHostmasks),
        FormatHostmaskVisibility(_preferences.QuitHostmasks),
        _outputRouting.DestinationSnapshot().ToDictionary(
            entry => entry.Key,
            entry => FormatOutputDestination(entry.Value),
            StringComparer.OrdinalIgnoreCase),
        _preferences.AutoRejoinOnKick,
        _preferences.DefaultKickMessage,
        _preferences.DefaultQuitMessage,
        _preferences.DefaultTopicMessage,
        _preferences.AnnounceUserInfoOnJoin,
        _preferences.HighlightNickname,
        BanmaskFormatter.Name(_preferences.BanmaskStyle),
        _preferences.Nickname,
        _preferences.AlternateNickname,
        _preferences.Username,
        _preferences.RealName,
        _preferences.CloneDetection,
        _preferences.NetworkReconnect,
        _preferences.KillReconnect,
        _preferences.AwayLogging,
        _preferences.DccAddress,
        _preferences.DccPorts.ToString(),
        _preferences.DccDownloads,
        _preferences.AwayMessage);

    private void SaveAppearanceSettings() => _appearanceStore.Save(CaptureAppearanceSettings());

    private void ApplyAppearanceSettings(AppearanceSettings settings)
    {
        _preferences.Nickname = settings.Nickname ?? _preferences.Nickname;
        _preferences.AlternateNickname = settings.AlternateNickname ?? $"{_preferences.Nickname}_";
        _preferences.Username = settings.Username ?? _preferences.Username;
        _preferences.RealName = settings.RealName ?? _preferences.RealName;
        _preferences.AwayMessage = settings.AwayMessage;
        if (_themeManager.TryGet(settings.Theme, out var theme)) _presenter.SetTheme(theme!);
        if (TryParseHostmaskVisibility(settings.JoinHostmasks, out var join)) _preferences.JoinHostmasks = join;
        if (TryParseHostmaskVisibility(settings.PartHostmasks, out var part)) _preferences.PartHostmasks = part;
        if (TryParseHostmaskVisibility(settings.QuitHostmasks, out var quit)) _preferences.QuitHostmasks = quit;
        foreach (var route in settings.OutputRoutes)
        {
            if (TryParseOutputDestination(route.Value, out var destination))
                _outputRouting.TrySetDestination(route.Key, destination);
        }
        _preferences.AutoRejoinOnKick = settings.AutoRejoinOnKick;
        _preferences.DefaultKickMessage = settings.DefaultKickMessage;
        _preferences.DefaultQuitMessage = settings.DefaultQuitMessage;
        _preferences.DefaultTopicMessage = settings.DefaultTopicMessage;
        _preferences.AnnounceUserInfoOnJoin = settings.AnnounceUserInfoOnJoin;
        _preferences.HighlightNickname = settings.HighlightNickname;
        _preferences.CloneDetection = settings.CloneDetection;
        _preferences.NetworkReconnect = settings.NetworkReconnect;
        _preferences.KillReconnect = settings.KillReconnect;
        _preferences.AwayLogging = settings.AwayLogging;
        _preferences.DccAddress = settings.DccAddress;
        if (DccPortRange.TryParse(settings.DccPorts, out var ports)) _preferences.DccPorts = ports;
        _preferences.DccDownloads = settings.DccDownloads ?? _preferences.DccDownloads;
        if (BanmaskFormatter.TryParse(settings.DefaultBanmask, out var mask)) _preferences.BanmaskStyle = mask;
        _presenter.SetHostmaskVisibility(_preferences.JoinHostmasks, _preferences.PartHostmasks, _preferences.QuitHostmasks);
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        private Action? _callback = callback;

        public void Dispose() => Interlocked.Exchange(ref _callback, null)?.Invoke();
    }

}

internal enum OutputDestination
{
    Active,
    Status,
    Dedicated
}
