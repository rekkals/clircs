using System.Text;

namespace Clircs.Protocol;

public static class IrcMessageParser
{
    public static IrcMessage Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.Length == 0)
        {
            throw new IrcProtocolException("An IRC line cannot be empty.");
        }

        if (line.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new IrcProtocolException("An IRC line cannot contain CR, LF, or NUL characters.");
        }

        var position = 0;
        var tags = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (line[position] == '@')
        {
            var tagsEnd = line.IndexOf(' ');
            if (tagsEnd <= 1)
            {
                throw new IrcProtocolException(
                    "IRC message tags must be followed by a command.");
            }

            ParseTags(line[1..tagsEnd], tags);
            position = SkipSpaces(line, tagsEnd);
        }

        if (position >= line.Length)
        {
            throw new IrcProtocolException("An IRC line is missing its command.");
        }

        string? prefix = null;
        if (line[position] == ':')
        {
            var prefixEnd = line.IndexOf(' ', position);
            if (prefixEnd <= position + 1)
            {
                throw new IrcProtocolException("An IRC prefix must be followed by a command.");
            }

            prefix = line[(position + 1)..prefixEnd];
            position = SkipSpaces(line, prefixEnd);
        }

        if (position >= line.Length)
        {
            throw new IrcProtocolException("An IRC line is missing its command.");
        }

        var commandEnd = line.IndexOf(' ', position);
        string command;
        if (commandEnd < 0)
        {
            command = line[position..];
            return new IrcMessage(prefix, command, [], tags);
        }

        command = line[position..commandEnd];
        position = SkipSpaces(line, commandEnd);
        var parameters = new List<string>();

        while (position < line.Length)
        {
            if (line[position] == ':')
            {
                parameters.Add(line[(position + 1)..]);
                break;
            }

            var parameterEnd = line.IndexOf(' ', position);
            if (parameterEnd < 0)
            {
                parameters.Add(line[position..]);
                break;
            }

            parameters.Add(line[position..parameterEnd]);
            position = SkipSpaces(line, parameterEnd);
        }

        return new IrcMessage(prefix, command, parameters, tags);
    }

    public static bool TryParse(string line, out IrcMessage? message)
    {
        try
        {
            message = Parse(line);
            return true;
        }
        catch (IrcProtocolException)
        {
            message = null;
            return false;
        }
        catch (ArgumentException)
        {
            message = null;
            return false;
        }
    }

    private static void ParseTags(
        string value,
        IDictionary<string, string?> tags)
    {
        foreach (var token in value.Split(';'))
        {
            var separator = token.IndexOf('=');
            var key = separator < 0 ? token : token[..separator];
            if (key.Length == 0)
            {
                throw new IrcProtocolException(
                    "An IRC message tag must have a key.");
            }

            var unescaped = separator < 0
                ? null
                : UnescapeTagValue(token[(separator + 1)..]);

            // Missing and empty values are the same under the IRCv3 specs.
            // Later duplicates replace earlier ones.
            tags[key] = string.IsNullOrEmpty(unescaped) ? null : unescaped;
        }
    }

    private static string UnescapeTagValue(string value)
    {
        if (!value.Contains('\\'))
        {
            return value;
        }

        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\')
            {
                result.Append(value[index]);
                continue;
            }

            index++;
            if (index >= value.Length)
            {
                break;
            }

            result.Append(value[index] switch
            {
                ':' => ';',
                's' => ' ',
                '\\' => '\\',
                'r' => '\r',
                'n' => '\n',
                _ => value[index]
            });
        }

        return result.ToString();
    }

    private static int SkipSpaces(string line, int position)
    {
        while (position < line.Length && line[position] == ' ')
        {
            position++;
        }

        return position;
    }
}
