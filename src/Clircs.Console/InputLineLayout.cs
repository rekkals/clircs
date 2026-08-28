namespace Clircs.ConsoleClient;

internal sealed record InputLineLayout(
    string Prompt,
    string Text,
    int ViewStart,
    int CursorColumn);

internal static class InputLineLayouter
{
    public static InputLineLayout Calculate(
        string prompt,
        string input,
        int cursor,
        int width,
        int previousViewStart)
    {
        prompt ??= string.Empty;
        input ??= string.Empty;
        width = Math.Max(2, width);
        cursor = Math.Clamp(cursor, 0, input.Length);
        var visiblePrompt = prompt.Length <= width - 2 ? prompt : prompt[..Math.Max(0, width - 2)];
        var available = Math.Max(1, width - visiblePrompt.Length - 1);
        var start = Math.Clamp(previousViewStart, 0, input.Length);
        if (cursor < start) start = cursor;
        if (cursor > start + available) start = cursor - available;
        if (input.Length - start < available) start = Math.Max(0, input.Length - available);
        var visibleLength = Math.Min(available, input.Length - start);
        var visibleText = visibleLength == 0 ? string.Empty : input.Substring(start, visibleLength);
        var cursorColumn = Math.Min(width - 1, visiblePrompt.Length + cursor - start);
        return new InputLineLayout(visiblePrompt, visibleText, start, cursorColumn);
    }
}
