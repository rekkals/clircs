using System.Text;

namespace Clircs.ConsoleClient;

internal sealed class NicknameCompletion
{
    private CompletionState? _state;

    public void Reset() => _state = null;

    public int? Complete(
        StringBuilder input,
        int cursor,
        Func<string, IReadOnlyList<string>> matchProvider)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(matchProvider);

        if (_state is { } existing)
        {
            var nextIndex = (existing.MatchIndex + 1) % existing.Matches.Count;
            var replacement = Format(existing.Matches[nextIndex], existing.AtMessageStart);
            input.Remove(existing.Start, existing.ReplacementLength);
            input.Insert(existing.Start, replacement);
            _state = existing with { MatchIndex = nextIndex, ReplacementLength = replacement.Length };
            return existing.Start + replacement.Length;
        }

        var start = cursor;
        while (start > 0 && !char.IsWhiteSpace(input[start - 1])) start--;
        var prefix = input.ToString(start, cursor - start);
        if (prefix.Length == 0) return null;

        var matches = matchProvider(prefix)
            .Where(match => !string.IsNullOrWhiteSpace(match))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matches.Length == 0) return null;

        var atMessageStart = start == 0;
        var first = Format(matches[0], atMessageStart);
        input.Remove(start, prefix.Length);
        input.Insert(start, first);
        _state = new CompletionState(start, prefix, matches, 0, first.Length, atMessageStart);
        return start + first.Length;
    }

    private static string Format(string nickname, bool atMessageStart) =>
        atMessageStart ? nickname + ": " : nickname;

    private sealed record CompletionState(
        int Start,
        string Prefix,
        IReadOnlyList<string> Matches,
        int MatchIndex,
        int ReplacementLength,
        bool AtMessageStart);
}
