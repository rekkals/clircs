using Clircs.State;

namespace Clircs.ConsoleClient;

internal enum BanmaskStyle
{
    Host,
    UserHost,
    NickUserHost,
    WildcardHost
}

internal static class BanmaskFormatter
{
    public static bool TryParse(string value, out BanmaskStyle style)
    {
        style = value.ToLowerInvariant() switch
        {
            "host" => BanmaskStyle.Host,
            "userhost" => BanmaskStyle.UserHost,
            "nick-userhost" or "full" => BanmaskStyle.NickUserHost,
            "wildcard-host" or "domain" => BanmaskStyle.WildcardHost,
            _ => (BanmaskStyle)(-1)
        };
        return Enum.IsDefined(style);
    }

    public static string Name(BanmaskStyle style) => style switch
    {
        BanmaskStyle.Host => "host",
        BanmaskStyle.UserHost => "userhost",
        BanmaskStyle.NickUserHost => "nick-userhost",
        BanmaskStyle.WildcardHost => "wildcard-host",
        _ => throw new ArgumentOutOfRangeException(nameof(style))
    };

    public static string Create(ChannelMemberState member, BanmaskStyle style)
    {
        if (string.IsNullOrWhiteSpace(member.Username) || string.IsNullOrWhiteSpace(member.Host))
        {
            throw new InvalidOperationException($"No synchronized user and host are known for {member.Nickname}.");
        }
        return style switch
        {
            BanmaskStyle.Host => $"*!*@{member.Host}",
            BanmaskStyle.UserHost => $"*!{member.Username}@{member.Host}",
            BanmaskStyle.NickUserHost => $"{member.Nickname}!{member.Username}@{member.Host}",
            BanmaskStyle.WildcardHost => $"*!*@{WildcardHost(member.Host)}",
            _ => throw new ArgumentOutOfRangeException(nameof(style))
        };
    }

    private static string WildcardHost(string host)
    {
        if (System.Net.IPAddress.TryParse(host, out _)) return host;
        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return labels.Length >= 3 ? $"*.{string.Join('.', labels.Skip(1))}" : host;
    }
}
