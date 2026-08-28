namespace Clircs.Sessions;

public static class TerminalTextSanitizer
{
    public static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return IrcTextFormatting.ToPlainText(value);
    }

    public static bool IsBidirectionalControl(char value) => value is
        '\u061c' or
        '\u200e' or '\u200f' or
        >= '\u202a' and <= '\u202e' or
        >= '\u2066' and <= '\u2069';
}
