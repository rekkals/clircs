namespace Clircs.State;

public sealed class ChannelMemberState
{
    private readonly HashSet<char> _prefixModes = [];

    public ChannelMemberState(string nickname, string? username = null, string? host = null, string? realName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);
        Nickname = nickname;
        Username = username;
        Host = host;
        RealName = realName;
    }

    public string Nickname { get; internal set; }

    public string? Username { get; internal set; }

    public string? Host { get; internal set; }

    public string? RealName { get; internal set; }

    public string? FullMask => Username is null || Host is null ? null : $"{Nickname}!{Username}@{Host}";

    public IReadOnlySet<char> PrefixModes => _prefixModes;

    internal void SetIdentity(string? username, string? host, string? realName = null)
    {
        if (!string.IsNullOrWhiteSpace(username))
        {
            Username = username;
        }

        if (!string.IsNullOrWhiteSpace(host))
        {
            Host = host;
        }

        if (!string.IsNullOrWhiteSpace(realName))
        {
            RealName = realName;
        }
    }

    internal void AddPrefixMode(char mode) => _prefixModes.Add(mode);

    internal void RemovePrefixMode(char mode) => _prefixModes.Remove(mode);

    internal void ClearPrefixModes() => _prefixModes.Clear();
}
