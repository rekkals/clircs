using System.Collections.ObjectModel;
using System.Text;

namespace Clircs.Commands;

public abstract record ParsedInput;

public sealed record ChatInput(string Text) : ParsedInput;

public sealed record CommandInput(string Name, IReadOnlyList<string> Arguments, string RawArguments) : ParsedInput;

public static class CommandLineParser
{
    public static ParsedInput Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.StartsWith('/'))
        {
            return new ChatInput(input);
        }

        if (input.StartsWith("//", StringComparison.Ordinal))
        {
            return new ChatInput(input[1..]);
        }

        var content = input[1..].TrimStart();
        if (content.Length == 0)
        {
            throw new CommandLineException("A slash must be followed by a command name.");
        }

        var nameEnd = 0;
        while (nameEnd < content.Length && !char.IsWhiteSpace(content[nameEnd])) nameEnd++;
        if (nameEnd < 0)
        {
            return new CommandInput(NormalizeName(content), Array.Empty<string>(), string.Empty);
        }

        if (nameEnd == content.Length)
        {
            return new CommandInput(NormalizeName(content), Array.Empty<string>(), string.Empty);
        }

        var name = NormalizeName(content[..nameEnd]);
        var rawArguments = content[(nameEnd + 1)..].TrimStart();
        return new CommandInput(name, new ReadOnlyCollection<string>(Tokenize(rawArguments)), rawArguments);
    }

    public static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.TrimStart('/').ToLowerInvariant();
        if (normalized.Length == 0 || normalized.Any(char.IsWhiteSpace))
        {
            throw new CommandLineException("A command name must be one token.");
        }

        return normalized;
    }

    private static string[] Tokenize(string rawArguments)
    {
        if (rawArguments.Length == 0)
        {
            return [];
        }

        var tokens = new List<string>();
        var token = new StringBuilder();
        var quoted = false;
        var tokenStarted = false;

        for (var index = 0; index < rawArguments.Length; index++)
        {
            var character = rawArguments[index];

            if (character == '\\' && quoted && index + 1 < rawArguments.Length &&
                rawArguments[index + 1] is '\\' or '"')
            {
                token.Append(rawArguments[++index]);
                tokenStarted = true;
                continue;
            }

            if (character == '"')
            {
                quoted = !quoted;
                tokenStarted = true;
                continue;
            }

            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (tokenStarted)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                    tokenStarted = false;
                }

                continue;
            }

            token.Append(character);
            tokenStarted = true;
        }

        if (quoted)
        {
            throw new CommandLineException("The command line contains an unfinished quoted argument.");
        }

        if (tokenStarted)
        {
            tokens.Add(token.ToString());
        }

        return tokens.ToArray();
    }
}

public sealed class CommandLineException : Exception
{
    public CommandLineException(string message)
        : base(message)
    {
    }
}
