using System.Text;

namespace Clircs.Sessions;

public sealed record IrcTextStyle(
    int? Foreground = null,
    int? Background = null,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    bool Reverse = false);

public sealed record IrcTextRun(string Text, IrcTextStyle Style);

public sealed record IrcFormattedText(string PlainText, IReadOnlyList<IrcTextRun> Runs)
{
    public bool HasFormatting => Runs.Any(run => run.Style != new IrcTextStyle());
}

public static class IrcTextFormatting
{
    public static IrcFormattedText Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var runs = new List<IrcTextRun>();
        var current = new StringBuilder();
        var plain = new StringBuilder(value.Length);
        var style = new IrcTextStyle();

        void Flush()
        {
            if (current.Length == 0) return;
            runs.Add(new IrcTextRun(current.ToString(), style));
            current.Clear();
        }

        void ChangeStyle(IrcTextStyle replacement)
        {
            Flush();
            style = replacement;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (TerminalTextSanitizer.IsBidirectionalControl(character))
            {
                continue;
            }
            switch (character)
            {
                case '\u0002':
                    ChangeStyle(style with { Bold = !style.Bold });
                    continue;
                case '\u0003':
                {
                    Flush();
                    var cursor = index + 1;
                    var foreground = ReadColor(value, ref cursor);
                    int? background = null;
                    if (foreground is not null && cursor < value.Length && value[cursor] == ',' &&
                        cursor + 1 < value.Length && char.IsAsciiDigit(value[cursor + 1]))
                    {
                        cursor++;
                        background = ReadColor(value, ref cursor);
                    }
                    style = foreground is null
                        ? style with { Foreground = null, Background = null }
                        : style with { Foreground = foreground, Background = background };
                    index = cursor - 1;
                    continue;
                }
                case '\u000f':
                    ChangeStyle(new IrcTextStyle());
                    continue;
                case '\u0016':
                    ChangeStyle(style with { Reverse = !style.Reverse });
                    continue;
                case '\u001d':
                    ChangeStyle(style with { Italic = !style.Italic });
                    continue;
                case '\u001f':
                    ChangeStyle(style with { Underline = !style.Underline });
                    continue;
                case '\u0011':
                    // Monospace is already the terminal's natural state.
                    continue;
                case '\u001b':
                case '\u007f':
                    continue;
            }

            if (char.IsControl(character) && character is not '\t') continue;
            current.Append(character);
            plain.Append(character);
        }

        Flush();
        return new IrcFormattedText(plain.ToString(), runs);
    }

    public static string ToPlainText(string value) => Parse(value).PlainText;

    private static int? ReadColor(string value, ref int cursor)
    {
        if (cursor >= value.Length || !char.IsAsciiDigit(value[cursor])) return null;
        var color = value[cursor++] - '0';
        if (cursor < value.Length && char.IsAsciiDigit(value[cursor]))
        {
            color = color * 10 + value[cursor++] - '0';
        }
        return color;
    }
}
