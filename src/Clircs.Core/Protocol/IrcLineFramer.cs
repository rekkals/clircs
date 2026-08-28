namespace Clircs.Protocol;

public sealed class IrcLineFramer
{
    public const int MaximumPayloadBytes = 510;

    private readonly List<byte> _pending = [];

    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> bytes)
    {
        var lines = new List<byte[]>();

        foreach (var value in bytes)
        {
            if (value == (byte)'\n')
            {
                var payloadLength = _pending.Count;
                if (payloadLength > 0 && _pending[^1] == (byte)'\r')
                {
                    payloadLength--;
                }

                if (payloadLength > MaximumPayloadBytes)
                {
                    _pending.Clear();
                    throw new IrcProtocolException($"An IRC line exceeded {MaximumPayloadBytes} payload bytes.");
                }

                lines.Add(_pending.Take(payloadLength).ToArray());
                _pending.Clear();
                continue;
            }

            _pending.Add(value);
            if (_pending.Count > MaximumPayloadBytes + 1)
            {
                _pending.Clear();
                throw new IrcProtocolException($"An IRC line exceeded {MaximumPayloadBytes} payload bytes.");
            }
        }

        return lines;
    }

    public void Reset() => _pending.Clear();
}
