namespace Clircs.ConsoleClient;

internal static class InputFormattingControls
{
    internal const char Bold = '\x02';
    internal const char Color = '\x03';
    internal const char Reset = '\x0F';
    internal const char Reverse = '\x16';
    internal const char Italic = '\x1D';
    internal const char Underline = '\x1F';

    internal static bool TryTranslate(ConsoleKeyInfo key, out char control)
    {
        control = key.Key switch
        {
            ConsoleKey.B when key.Modifiers.HasFlag(ConsoleModifiers.Control) => Bold,
            ConsoleKey.K when key.Modifiers.HasFlag(ConsoleModifiers.Control) => Color,
            ConsoleKey.O when key.Modifiers.HasFlag(ConsoleModifiers.Control) => Reset,
            ConsoleKey.R when key.Modifiers.HasFlag(ConsoleModifiers.Control) => Reverse,
            ConsoleKey.I when key.Modifiers.HasFlag(ConsoleModifiers.Control) => Italic,
            ConsoleKey.U when key.Modifiers.HasFlag(ConsoleModifiers.Control) => Underline,
            _ => '\0'
        };
        return control != '\0';
    }

    internal static string ToDisplayText(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return string.Create(input.Length, input, static (display, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                display[index] = source[index] switch
                {
                    Bold => '\u2402',
                    Color => '\u2403',
                    Reset => '\u240F',
                    Reverse => '\u2416',
                    Italic => '\u241D',
                    Underline => '\u241F',
                    var character => character
                };
            }
        });
    }
}
