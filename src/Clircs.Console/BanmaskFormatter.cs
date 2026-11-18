using Clircs.State;

namespace Clircs.ConsoleClient;

internal enum BanmaskStyle
{
    Host,
    UserHost,
    NickUserHost,
    WildcardHost,
    Nick
}

internal static class BanmaskFormatter
{
    public static bool TryParse(string value, out BanmaskStyle style)
    {
        style = value.ToLowerInvariant() switch
        {
            "nick" => BanmaskStyle.Nick,
            "host" => BanmaskStyle.Host,
            "userhost" => BanmaskStyle.UserHost,
            "full" => BanmaskStyle.NickUserHost,
            "wildcard" => BanmaskStyle.WildcardHost,
            _ => (BanmaskStyle)(-1)
        };
        return Enum.IsDefined(style);
    }

    public static string Name(BanmaskStyle style) => style switch
    {
        BanmaskStyle.Nick => "nick",
        BanmaskStyle.Host => "host",
        BanmaskStyle.UserHost => "userhost",
        BanmaskStyle.NickUserHost => "full",
        BanmaskStyle.WildcardHost => "wildcard",
        _ => throw new ArgumentOutOfRangeException(nameof(style))
    };

    public static string Create(ChannelMemberState member, BanmaskStyle style)
    {
        if (style == BanmaskStyle.Nick)
        {
            return $"{member.Nickname}!*@*";
        }

        if (string.IsNullOrWhiteSpace(member.Username) || string.IsNullOrWhiteSpace(member.Host))
        {
            throw new InvalidOperationException($"{member.Nickname}'s user and host is not known.");
        }

        return style switch
        {
            BanmaskStyle.Host => $"*!*@{member.Host}",
            BanmaskStyle.UserHost => $"*!{member.Username}@{member.Host}",
            BanmaskStyle.NickUserHost => $"{member.Nickname}!{member.Username}@{member.Host}",
            BanmaskStyle.WildcardHost => $"*!{WildcardUsername(member.Username)}@{WildcardHost(member.Host)}",
            _ => throw new ArgumentOutOfRangeException(nameof(style))
        };
    }

    private static string WildcardUsername(string username) =>
        username.StartsWith('~') ? $"*{username[1..]}" : $"*{username}";

    private static string WildcardHost(string host)
    {
        if (System.Net.IPAddress.TryParse(host, out _)) return host;
        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return labels.Length >= 3 ? $"*.{string.Join('.', labels.Skip(1))}" : host;
    }
}
