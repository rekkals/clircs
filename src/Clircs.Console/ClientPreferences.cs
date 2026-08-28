using Clircs.Transport;
using Clircs.Users;

namespace Clircs.ConsoleClient;

internal sealed class ClientPreferences
{
    public ClientPreferences(string nickname, string username, string dccDownloads)
    {
        Nickname = nickname;
        AlternateNickname = $"{nickname}_";
        Username = username;
        DccDownloads = dccDownloads;
    }

    public string Nickname { get; set; }
    public string AlternateNickname { get; set; }
    public string Username { get; set; }
    public string RealName { get; set; } = "clircs user";
    public string AwayMessage { get; set; } = "away";
    public HostmaskVisibility JoinHostmasks { get; set; } = HostmaskVisibility.UserHost;
    public HostmaskVisibility PartHostmasks { get; set; } = HostmaskVisibility.UserHost;
    public HostmaskVisibility QuitHostmasks { get; set; } = HostmaskVisibility.UserHost;
    public bool AutoRejoinOnKick { get; set; }
    public bool AnnounceUserInfoOnJoin { get; set; }
    public string? DefaultKickMessage { get; set; }
    public string? DefaultQuitMessage { get; set; }
    public string? DefaultTopicMessage { get; set; }
    public bool HighlightNickname { get; set; } = true;
    public bool CloneDetection { get; set; } = true;
    public bool NetworkReconnect { get; set; } = true;
    public bool KillReconnect { get; set; } = true;
    public bool AwayLogging { get; set; }
    public string DccAddress { get; set; } = "auto";
    public DccPortRange DccPorts { get; set; } = DccPortRange.Random;
    public string DccDownloads { get; set; }
    public BanmaskStyle BanmaskStyle { get; set; } = BanmaskStyle.Host;
}
