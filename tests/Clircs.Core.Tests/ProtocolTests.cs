using System.Text;
using Clircs.Protocol;
using Clircs.Protocol.Testing;

namespace Clircs.Core.Tests;

internal static class ProtocolTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("parser handles prefix, command, middle, and trailing parameters", ParserHandlesMessage);
        suite.Add("parser preserves an empty trailing parameter", ParserPreservesEmptyTrailing);
        suite.Add("parser rejects line injection", ParserRejectsLineInjection);
        suite.Add("framer handles split TCP input", FramerHandlesSplitInput);
        suite.Add("framer accepts the maximum payload", FramerAcceptsMaximumPayload);
        suite.Add("framer discards an oversized payload and recovers", FramerDiscardsOversizedPayloadAndRecovers);
        suite.Add("framer discards split oversized input exactly once", FramerDiscardsSplitOversizedInput);
        suite.Add("builder emits a bounded CRLF line", BuilderEmitsWireLine);
        suite.Add("encoding falls back to Windows-1252", EncodingFallsBack);
        suite.Add("transcript harness ignores outbound and comments", TranscriptHarnessReplaysInbound);
        suite.Add("RFC1459 case mapping follows traditional equivalences", Rfc1459CaseMapping);
        suite.Add("strict RFC1459 keeps caret and tilde distinct", StrictRfc1459CaseMapping);
    }

    private static void ParserHandlesMessage()
    {
        var message = IrcMessageParser.Parse(":nick!user@example PRIVMSG #clirc :hello there");
        Assert.Equal("nick!user@example", message.Prefix!);
        Assert.Equal("PRIVMSG", message.Command);
        Assert.Equal(2, message.Parameters.Count);
        Assert.Equal("#clirc", message.Parameters[0]);
        Assert.Equal("hello there", message.Parameters[1]);
    }

    private static void ParserPreservesEmptyTrailing()
    {
        var message = IrcMessageParser.Parse("NOTICE nick :");
        Assert.Equal(2, message.Parameters.Count);
        Assert.Equal(string.Empty, message.Parameters[1]);
    }

    private static void ParserRejectsLineInjection() =>
        Assert.Throws<IrcProtocolException>(() => IrcMessageParser.Parse("PING x\r\nOPER bad"));

    private static void FramerHandlesSplitInput()
    {
        var framer = new IrcLineFramer();

        var first = framer.Push("PING :ser"u8);
        Assert.Equal(0, first.Lines.Count);
        Assert.Equal(0, first.DiscardedOversizedLineCount);

        var second = framer.Push("ver\r\nNOTICE me :hi\r\n"u8);
        Assert.Equal(2, second.Lines.Count);
        Assert.Equal(0, second.DiscardedOversizedLineCount);
        Assert.Equal("PING :server", Encoding.UTF8.GetString(second.Lines[0]));
        Assert.Equal("NOTICE me :hi", Encoding.UTF8.GetString(second.Lines[1]));
    }

    private static void FramerAcceptsMaximumPayload()
    {
        var input = Enumerable
            .Repeat((byte)'x', IrcLineFramer.MaximumPayloadBytes)
            .Concat("\r\n"u8.ToArray())
            .ToArray();

        var result = new IrcLineFramer().Push(input);

        Assert.Equal(1, result.Lines.Count);
        Assert.Equal(IrcLineFramer.MaximumPayloadBytes, result.Lines[0].Length);
        Assert.Equal(0, result.DiscardedOversizedLineCount);
    }

    private static void FramerDiscardsOversizedPayloadAndRecovers()
    {
        var input = Enumerable
            .Repeat((byte)'x', IrcLineFramer.MaximumPayloadBytes + 1)
            .Concat("\r\nPING :server\r\n"u8.ToArray())
            .ToArray();

        var result = new IrcLineFramer().Push(input);

        Assert.Equal(1, result.DiscardedOversizedLineCount);
        Assert.Equal(1, result.Lines.Count);
        Assert.Equal("PING :server", Encoding.UTF8.GetString(result.Lines[0]));
    }

    private static void FramerDiscardsSplitOversizedInput()
    {
        var framer = new IrcLineFramer();
        var beginning = Enumerable
            .Repeat((byte)'x', IrcLineFramer.MaximumPayloadBytes + 1)
            .ToArray();

        var first = framer.Push(beginning);
        Assert.Equal(0, first.Lines.Count);
        Assert.Equal(0, first.DiscardedOversizedLineCount);

        var second = framer.Push("more garbage\r\nNOTICE me :still connected\r\n"u8);

        Assert.Equal(1, second.DiscardedOversizedLineCount);
        Assert.Equal(1, second.Lines.Count);
        Assert.Equal(
            "NOTICE me :still connected",
            Encoding.UTF8.GetString(second.Lines[0]));
    }

    private static void BuilderEmitsWireLine()
    {
        var line = IrcLineBuilder.Build("privmsg", "#clirc", "hello there");
        Assert.Equal("PRIVMSG #clirc :hello there\r\n", Encoding.UTF8.GetString(line));
    }

    private static void EncodingFallsBack()
    {
        var decoded = IrcTextEncoding.Decode([(byte)'c', (byte)'a', (byte)'f', 0xE9]);
        Assert.Equal("café", decoded);
        Assert.Equal("€", IrcTextEncoding.Decode([0x80]));
    }

    private static void TranscriptHarnessReplaysInbound()
    {
        var messages = new IrcTranscriptHarness().Replay([
            "# registration",
            "> NICK clirc",
            "< :server 001 clirc :Welcome",
            "< PING :server"
        ]);

        Assert.Equal(2, messages.Count);
        Assert.Equal("001", messages[0].Command);
        Assert.Equal("PING", messages[1].Command);
    }

    private static void Rfc1459CaseMapping()
    {
        var comparer = new IrcNameComparer(IrcCaseMapping.Rfc1459);
        Assert.True(comparer.Equals("[Nick]\\^", "{nick}|~"));
        Assert.Equal(comparer.GetHashCode("[Nick]\\^"), comparer.GetHashCode("{nick}|~"));
    }

    private static void StrictRfc1459CaseMapping()
    {
        var comparer = new IrcNameComparer(IrcCaseMapping.StrictRfc1459);
        Assert.True(comparer.Equals("[Nick]\\", "{nick}|"));
        Assert.False(comparer.Equals("nick^", "nick~"));
    }
}
