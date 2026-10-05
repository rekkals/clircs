using System.Globalization;
using System.Text;
using Clircs.Protocol;

namespace Clircs.Sessions;

internal static class IrcTextSegmenter
{
    internal static IReadOnlyList<string> Split(
        string text,
        int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (maximumBytes <= 0)
        {
            throw new IrcProtocolException(
                "The message cannot fit within the IRC line limit.");
        }

        if (text.Length == 0 ||
            IrcTextEncoding.Encode(text).Length <= maximumBytes)
        {
            return [text];
        }

        var tokens = Tokenize(text);
        var segments = new List<string>();
        var start = 0;

        while (start < tokens.Count)
        {
            var prefix = start == 0
                ? string.Empty
                : FormattingPrefix(tokens[start].StyleBefore);
            var prefixBytes = IrcTextEncoding.Encode(prefix).Length;
            var availableBytes = maximumBytes - prefixBytes;

            if (availableBytes <= 0)
            {
                throw new IrcProtocolException(
                    "The message cannot fit within the IRC line limit.");
            }

            var end = start;
            var usedBytes = 0;
            var lastWhitespaceEnd = -1;

            while (end < tokens.Count &&
                   usedBytes + tokens[end].ByteCount <= availableBytes)
            {
                usedBytes += tokens[end].ByteCount;
                end++;

                if (tokens[end - 1].IsWhitespace)
                {
                    lastWhitespaceEnd = end;
                }
            }

            int segmentEnd;
            if (end == tokens.Count)
            {
                segmentEnd = end;
            }
            else if (lastWhitespaceEnd > start)
            {
                segmentEnd = lastWhitespaceEnd;
            }
            else if (end > start)
            {
                segmentEnd = end;
            }
            else
            {
                throw new IrcProtocolException(
                    "One character cannot fit within the IRC line limit.");
            }

            var segment = new StringBuilder(prefix);
            for (var index = start; index < segmentEnd; index++)
            {
                segment.Append(tokens[index].Text);
            }

            var value = segment.ToString();
            if (IrcTextEncoding.Encode(value).Length > maximumBytes)
            {
                throw new IrcProtocolException(
                    "The message could not be split within the IRC line limit.");
            }

            segments.Add(value);
            start = segmentEnd;
        }

        return segments;
    }

    private static IReadOnlyList<TextToken> Tokenize(string text)
    {
        var tokens = new List<TextToken>();
        var style = new SegmentStyle();
        var index = 0;

        while (index < text.Length)
        {
            var styleBefore = style;
            var formattingLength =
                FormattingTokenLength(text, index);

            string tokenText;
            if (formattingLength > 0)
            {
                tokenText = text.Substring(index, formattingLength);
                style = ApplyFormattingToken(tokenText, style);
                index += formattingLength;
            }
            else
            {
                tokenText =
                    StringInfo.GetNextTextElement(text, index);
                index += tokenText.Length;
            }

            tokens.Add(new TextToken(
                tokenText,
                IrcTextEncoding.Encode(tokenText).Length,
                tokenText.All(char.IsWhiteSpace),
                styleBefore));
        }

        return tokens;
    }

    private static int FormattingTokenLength(
        string text,
        int index)
    {
        switch (text[index])
        {
            case '\u0002':
            case '\u000f':
            case '\u0011':
            case '\u0016':
            case '\u001d':
            case '\u001f':
                return 1;

            case '\u0003':
                var cursor = index + 1;
                if (!TrySkipColor(text, ref cursor))
                {
                    return 1;
                }

                if (cursor < text.Length &&
                    text[cursor] == ',')
                {
                    var backgroundCursor = cursor + 1;
                    if (TrySkipColor(
                            text,
                            ref backgroundCursor))
                    {
                        cursor = backgroundCursor;
                    }
                }

                return cursor - index;

            default:
                return 0;
        }
    }

    private static bool TrySkipColor(
        string text,
        ref int cursor)
    {
        if (cursor >= text.Length ||
            !char.IsAsciiDigit(text[cursor]))
        {
            return false;
        }

        cursor++;
        if (cursor < text.Length &&
            char.IsAsciiDigit(text[cursor]))
        {
            cursor++;
        }

        return true;
    }

    private static SegmentStyle ApplyFormattingToken(
        string token,
        SegmentStyle style)
    {
        switch (token[0])
        {
            case '\u0002':
                return style with { Bold = !style.Bold };

            case '\u0003':
                var cursor = 1;
                var foreground = ReadColor(token, ref cursor);
                int? background = null;

                if (foreground is not null &&
                    cursor < token.Length &&
                    token[cursor] == ',')
                {
                    cursor++;
                    background = ReadColor(token, ref cursor);
                }

                return foreground is null
                    ? style with
                    {
                        Foreground = null,
                        Background = null
                    }
                    : style with
                    {
                        Foreground = foreground,
                        Background = background
                    };

            case '\u000f':
                return new SegmentStyle();

            case '\u0011':
                return style with
                {
                    Monospace = !style.Monospace
                };

            case '\u0016':
                return style with
                {
                    Reverse = !style.Reverse
                };

            case '\u001d':
                return style with
                {
                    Italic = !style.Italic
                };

            case '\u001f':
                return style with
                {
                    Underline = !style.Underline
                };

            default:
                return style;
        }
    }

    private static int? ReadColor(
        string token,
        ref int cursor)
    {
        if (cursor >= token.Length ||
            !char.IsAsciiDigit(token[cursor]))
        {
            return null;
        }

        var color = token[cursor++] - '0';
        if (cursor < token.Length &&
            char.IsAsciiDigit(token[cursor]))
        {
            color = color * 10 + token[cursor++] - '0';
        }

        return color;
    }

    private static string FormattingPrefix(
        SegmentStyle style)
    {
        var prefix = new StringBuilder();

        if (style.Foreground is { } foreground)
        {
            prefix.Append('\u0003');
            prefix.Append(
                foreground.ToString(
                    "00",
                    CultureInfo.InvariantCulture));

            if (style.Background is { } background)
            {
                prefix.Append(',');
                prefix.Append(
                    background.ToString(
                        "00",
                        CultureInfo.InvariantCulture));
            }

            // Prevent the first visible characters in the continuation
            // from being consumed as more color parameters.
            prefix.Append('\u0002');
            prefix.Append('\u0002');
        }

        if (style.Monospace)
        {
            prefix.Append('\u0011');
        }

        if (style.Bold)
        {
            prefix.Append('\u0002');
        }

        if (style.Italic)
        {
            prefix.Append('\u001d');
        }

        if (style.Underline)
        {
            prefix.Append('\u001f');
        }

        if (style.Reverse)
        {
            prefix.Append('\u0016');
        }

        return prefix.ToString();
    }

    private sealed record SegmentStyle(
        int? Foreground = null,
        int? Background = null,
        bool Bold = false,
        bool Italic = false,
        bool Underline = false,
        bool Reverse = false,
        bool Monospace = false);

    private sealed record TextToken(
        string Text,
        int ByteCount,
        bool IsWhitespace,
        SegmentStyle StyleBefore);
}
