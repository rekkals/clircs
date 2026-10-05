namespace Clircs.Protocol;

internal static class IrcRelayTextBudget
{
    internal static int MaximumTextBytes(
        string command,
        string target,
        string nickname,
        string username,
        string host,
        int contentFramingBytes = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        return MaximumTextBytes(
            command,
            target,
            nickname,
            IrcTextEncoding.Encode(username).Length,
            IrcTextEncoding.Encode(host).Length,
            contentFramingBytes);
    }

    internal static int MaximumTextBytes(
        string command,
        string target,
        string nickname,
        int usernameBytes,
        int hostBytes,
        int contentFramingBytes = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(usernameBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hostBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(contentFramingBytes);

        var relayFixedText =
            $":{nickname}!@ {command} {target} :";
        var relayOverhead =
            IrcTextEncoding.Encode(relayFixedText).Length +
            usernameBytes +
            hostBytes +
            contentFramingBytes;

        return Math.Max(
            0,
            IrcLineFramer.MaximumPayloadBytes - relayOverhead);
    }
}
