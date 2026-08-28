using System.Text.Json;
using Clircs.Infrastructure;

namespace Clircs.ConsoleClient;

internal sealed record AppearanceSettings(
    string Theme,
    string JoinHostmasks,
    string PartHostmasks,
    string QuitHostmasks,
    IReadOnlyDictionary<string, string> OutputRoutes,
    bool AutoRejoinOnKick = false,
    string? DefaultKickMessage = null,
    string? DefaultQuitMessage = null,
    string? DefaultTopicMessage = null,
    bool AnnounceUserInfoOnJoin = false,
    bool HighlightNickname = true,
    string DefaultBanmask = "host",
    string? Nickname = null,
    string? AlternateNickname = null,
    string? Username = null,
    string? RealName = null,
    bool CloneDetection = true,
    bool NetworkReconnect = true,
    bool KillReconnect = true,
    bool AwayLogging = false,
    string DccAddress = "auto",
    string DccPorts = "random",
    string? DccDownloads = null,
    string AwayMessage = "away");

internal sealed class AppearanceSettingsStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };
    private readonly DurableFileWriter _files;

    public AppearanceSettingsStore(string path, DurableFileWriter? files = null)
    {
        _path = Path.GetFullPath(path);
        _files = files ?? DurableFileWriter.Shared;
    }

    public string? LoadError { get; private set; }

    public AppearanceSettings Load()
    {
        if (!File.Exists(_path))
        {
            return Defaults();
        }
        try
        {
            var stored = JsonSerializer.Deserialize<StoredAppearance>(File.ReadAllText(_path), _options)
                ?? throw new InvalidDataException("Appearance settings are empty.");
            if (stored.SchemaVersion != 1)
            {
                throw new InvalidDataException($"Unsupported appearance schema {stored.SchemaVersion}.");
            }
            return new AppearanceSettings(
                stored.Theme,
                stored.JoinHostmasks,
                stored.PartHostmasks,
                stored.QuitHostmasks,
                stored.OutputRoutes,
                stored.AutoRejoinOnKick,
                EmptyToNull(stored.DefaultKickMessage),
                EmptyToNull(stored.DefaultQuitMessage),
                EmptyToNull(stored.DefaultTopicMessage),
                stored.AnnounceUserInfoOnJoin,
                stored.HighlightNickname,
                string.IsNullOrWhiteSpace(stored.DefaultBanmask) ? "host" : stored.DefaultBanmask,
                EmptyToNull(stored.Nickname),
                EmptyToNull(stored.AlternateNickname),
                EmptyToNull(stored.Username),
                EmptyToNull(stored.RealName),
                stored.CloneDetection,
                stored.NetworkReconnect,
                stored.KillReconnect,
                stored.AwayLogging,
                string.IsNullOrWhiteSpace(stored.DccAddress) ? "auto" : stored.DccAddress,
                string.IsNullOrWhiteSpace(stored.DccPorts) ? "random" : stored.DccPorts,
                EmptyToNull(stored.DccDownloads),
                string.IsNullOrWhiteSpace(stored.AwayMessage) ? "away" : stored.AwayMessage);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            LoadError = $"Appearance settings '{_path}' are invalid and were left untouched: {exception.Message}";
            return Defaults();
        }
    }

    public void Save(AppearanceSettings settings)
    {
        if (LoadError is not null)
        {
            throw new InvalidOperationException(LoadError);
        }
        var stored = new StoredAppearance
        {
            SchemaVersion = 1,
            Theme = settings.Theme,
            JoinHostmasks = settings.JoinHostmasks,
            PartHostmasks = settings.PartHostmasks,
            QuitHostmasks = settings.QuitHostmasks,
            AutoRejoinOnKick = settings.AutoRejoinOnKick,
            DefaultKickMessage = settings.DefaultKickMessage,
            DefaultQuitMessage = settings.DefaultQuitMessage,
            DefaultTopicMessage = settings.DefaultTopicMessage,
            AnnounceUserInfoOnJoin = settings.AnnounceUserInfoOnJoin,
            HighlightNickname = settings.HighlightNickname,
            DefaultBanmask = settings.DefaultBanmask,
            Nickname = settings.Nickname,
            AlternateNickname = settings.AlternateNickname,
            Username = settings.Username,
            RealName = settings.RealName,
            CloneDetection = settings.CloneDetection,
            NetworkReconnect = settings.NetworkReconnect,
            KillReconnect = settings.KillReconnect,
            AwayLogging = settings.AwayLogging,
            DccAddress = settings.DccAddress,
            DccPorts = settings.DccPorts,
            DccDownloads = settings.DccDownloads,
            AwayMessage = settings.AwayMessage,
            OutputRoutes = new Dictionary<string, string>(settings.OutputRoutes, StringComparer.OrdinalIgnoreCase)
        };
        _files.WriteText(_path, JsonSerializer.Serialize(stored, _options));
    }

    private static AppearanceSettings Defaults() => new(
        "clircs", "userhost", "userhost", "userhost",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["who"] = "active",
            ["whois"] = "active",
            ["whowas"] = "active",
            ["ctcp"] = "active",
            ["notice"] = "active",
            ["invite"] = "active",
            ["links"] = "status",
            ["list"] = "dedicated",
            ["dns"] = "active",
            ["messageguard"] = "active"
        });

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class StoredAppearance
    {
        public int SchemaVersion { get; set; }
        public string Theme { get; set; } = "clircs";
        public string JoinHostmasks { get; set; } = "userhost";
        public string PartHostmasks { get; set; } = "userhost";
        public string QuitHostmasks { get; set; } = "userhost";
        public bool AutoRejoinOnKick { get; set; }
        public string? DefaultKickMessage { get; set; }
        public string? DefaultQuitMessage { get; set; }
        public string? DefaultTopicMessage { get; set; }
        public bool AnnounceUserInfoOnJoin { get; set; }
        public bool HighlightNickname { get; set; } = true;
        public string DefaultBanmask { get; set; } = "host";
        public string? Nickname { get; set; }
        public string? AlternateNickname { get; set; }
        public string? Username { get; set; }
        public string? RealName { get; set; }
        public bool CloneDetection { get; set; } = true;
        public bool NetworkReconnect { get; set; } = true;
        public bool KillReconnect { get; set; } = true;
        public bool AwayLogging { get; set; }
        public string DccAddress { get; set; } = "auto";
        public string DccPorts { get; set; } = "random";
        public string? DccDownloads { get; set; }
        public string AwayMessage { get; set; } = "away";
        public Dictionary<string, string> OutputRoutes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
