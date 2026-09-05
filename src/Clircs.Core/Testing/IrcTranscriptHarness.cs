using System.Text;

namespace Clircs.Protocol.Testing;

public sealed class IrcTranscriptHarness
{
    public IReadOnlyList<IrcMessage> Replay(IEnumerable<string> transcriptLines)
    {
        ArgumentNullException.ThrowIfNull(transcriptLines);
        var wire = new List<byte>();

        foreach (var transcriptLine in transcriptLines)
        {
            if (string.IsNullOrWhiteSpace(transcriptLine) || transcriptLine.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (!transcriptLine.StartsWith("< ", StringComparison.Ordinal))
            {
                continue;
            }

            wire.AddRange(Encoding.UTF8.GetBytes(transcriptLine[2..]));
            wire.Add((byte)'\r');
            wire.Add((byte)'\n');
        }

        return Feed(wire.ToArray(), 7);
    }

    public IReadOnlyList<IrcMessage> Feed(ReadOnlySpan<byte> bytes, int chunkSize)
    {
        if (chunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        }

        var messages = new List<IrcMessage>();
        var framer = new IrcLineFramer();
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, bytes.Length - offset);
            var framingResult = framer.Push(bytes.Slice(offset, length));

            foreach (var line in framingResult.Lines)
            {
                messages.Add(IrcMessageParser.Parse(IrcTextEncoding.Decode(line)));
            }
        }

        return messages;
    }
}
