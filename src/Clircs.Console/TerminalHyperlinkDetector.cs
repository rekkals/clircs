namespace Clircs.ConsoleClient;

internal sealed record TerminalHyperlinkSpan(int Start, int Length, string Target);

internal static class TerminalHyperlinkDetector
{
    private static readonly string[] Prefixes = ["https://", "http://", "www."];
    private static readonly char[] SimpleTrailingPunctuation = ['.', ',', ';', ':', '!', '?'];

    public static IReadOnlyList<TerminalHyperlinkSpan> Find(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var links = new List<TerminalHyperlinkSpan>();
        for (var index = 0; index < text.Length; index++)
        {
            var prefix = Prefixes.FirstOrDefault(candidate =>
                text.AsSpan(index).StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
            if (prefix is null || !IsBoundary(text, index)) continue;

            var end = index + prefix.Length;
            while (end < text.Length && !IsTerminator(text[end])) end++;
            end = TrimTrailingPunctuation(text, index, end);
            if (end <= index + prefix.Length)
            {
                index += prefix.Length - 1;
                continue;
            }

            var displayed = text[index..end];
            var target = prefix.Equals("www.", StringComparison.OrdinalIgnoreCase)
                ? $"https://{displayed}"
                : displayed;
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https")
            {
                links.Add(new TerminalHyperlinkSpan(index, end - index, uri.AbsoluteUri));
                index = end - 1;
            }
        }
        return links;
    }

    private static bool IsBoundary(string text, int index) => index == 0 ||
        char.IsWhiteSpace(text[index - 1]) || text[index - 1] is '(' or '[' or '{' or '<' or '\'' or '"';

    private static bool IsTerminator(char value) => char.IsWhiteSpace(value) || char.IsControl(value) || value is '<' or '>' or '"';

    private static int TrimTrailingPunctuation(string text, int start, int end)
    {
        while (end > start && SimpleTrailingPunctuation.Contains(text[end - 1])) end--;
        end = TrimUnbalanced(text, start, end, '(', ')');
        end = TrimUnbalanced(text, start, end, '[', ']');
        return TrimUnbalanced(text, start, end, '{', '}');
    }

    private static int TrimUnbalanced(string text, int start, int end, char opening, char closing)
    {
        while (end > start && text[end - 1] == closing &&
               text.AsSpan(start, end - start).Count(closing) > text.AsSpan(start, end - start).Count(opening))
        {
            end--;
        }
        return end;
    }
}
