namespace Clircs.Protocol;

public static class IrcMessageParser
{
    public const int MaximumParameterCount = 15;

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
        string? prefix = null;

        // TODO: Need to revisit message-tag handling with bouncers. ZNC is an example of it working
        // right, with independent CAP negotiations from client -> bouncer and bouncer -> server with
        // translation in between. Irssi proxy, however, doesn't negotiate CAP with the connecting
        // client, and just sends upstream-negotiated message tags downstream, which is a problem in
        // the case of the connecting client not supporting, say, IRCv3 features.

        if (line[0] == ':')
        {
            var prefixEnd = line.IndexOf(' ');
            if (prefixEnd <= 1)
            {
                throw new IrcProtocolException("An IRC prefix must be followed by a command.");
            }

            prefix = line[1..prefixEnd];
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
            return new IrcMessage(prefix, command, []);
        }

        command = line[position..commandEnd];
        position = SkipSpaces(line, commandEnd);
        var parameters = new List<string>();

        while (position < line.Length)
        {
            if (parameters.Count == MaximumParameterCount)
            {
                throw new IrcProtocolException($"An IRC message cannot contain more than {MaximumParameterCount} parameters.");
            }

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

        return new IrcMessage(prefix, command, parameters);
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

    private static int SkipSpaces(string line, int position)
    {
        while (position < line.Length && line[position] == ' ')
        {
            position++;
        }

        return position;
    }
}
