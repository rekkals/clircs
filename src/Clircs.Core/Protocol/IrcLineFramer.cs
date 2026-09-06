namespace Clircs.Protocol;

public sealed record IrcLineFramingResult(
    IReadOnlyList<byte[]> Lines,
    int DiscardedOversizedLineCount);

public sealed class IrcLineFramer
{
    public const int MaximumPayloadBytes = 510;

    // IRCv3 message tags may add a whopping 8191 bytes to the traditional 512 limit.
    // Allowing it inbound so extended messages from modern servers/bouncers can't
    // break the connection. Whatever ... I'm still honoring RFC 1459 outbound.
    public const int MaximumInboundPayloadBytes = MaximumPayloadBytes + 8191;

    private readonly List<byte> _pending = [];
    private bool _discardingOversizedLine;

    public IrcLineFramingResult Push(ReadOnlySpan<byte> bytes)
    {
        var lines = new List<byte[]>();
        var discardedOversizedLineCount = 0;

        foreach (var value in bytes)
        {
            if (_discardingOversizedLine)
            {
                if (value == (byte)'\n')
                {
                    _discardingOversizedLine = false;
                }

                continue;
            }

            if (value == (byte)'\n')
            {
                var payloadLength = _pending.Count;
                if (payloadLength > 0 && _pending[^1] == (byte)'\r')
                {
                    payloadLength--;
                }

                if (payloadLength > MaximumInboundPayloadBytes)
                {
                    _pending.Clear();
                    discardedOversizedLineCount++;
                    continue;
                }

                lines.Add(_pending.Take(payloadLength).ToArray());
                _pending.Clear();
                continue;
            }

            _pending.Add(value);
            if (_pending.Count > MaximumInboundPayloadBytes + 1)
            {
                _pending.Clear();
                _discardingOversizedLine = true;
                discardedOversizedLineCount++;
            }
        }

        return new IrcLineFramingResult(lines, discardedOversizedLineCount);
    }

    public void Reset()
    {
        _pending.Clear();
        _discardingOversizedLine = false;
    }
}
