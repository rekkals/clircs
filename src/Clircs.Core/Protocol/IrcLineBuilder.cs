namespace Clircs.Protocol;

public static class IrcLineBuilder
{
    public static byte[] Build(string command, params string[] parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(parameters);

        if (command.Any(char.IsWhiteSpace) || ContainsForbiddenCharacters(command))
        {
            throw new ArgumentException("The IRC command must be a single safe token.", nameof(command));
        }

        if (parameters.Length > IrcMessageParser.MaximumParameterCount)
        {
            throw new ArgumentException($"IRC messages may contain at most {IrcMessageParser.MaximumParameterCount} parameters.", nameof(parameters));
        }

        var parts = new List<string> { command.ToUpperInvariant() };
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index] ?? throw new ArgumentException("IRC parameters cannot be null.", nameof(parameters));
            if (ContainsForbiddenCharacters(parameter))
            {
                throw new ArgumentException("IRC parameters cannot contain CR, LF, or NUL characters.", nameof(parameters));
            }

            var isLast = index == parameters.Length - 1;
            var needsTrailing = parameter.Length == 0 || parameter[0] == ':' || parameter.Any(char.IsWhiteSpace);
            if (!isLast && needsTrailing)
            {
                throw new ArgumentException("Only the final IRC parameter may be empty or contain spaces.", nameof(parameters));
            }

            parts.Add(isLast && needsTrailing ? $":{parameter}" : parameter);
        }

        var payload = IrcTextEncoding.Encode(string.Join(' ', parts));
        if (payload.Length > IrcLineFramer.MaximumPayloadBytes)
        {
            throw new IrcProtocolException($"The encoded IRC line exceeds {IrcLineFramer.MaximumPayloadBytes} payload bytes.");
        }

        var framed = new byte[payload.Length + 2];
        payload.CopyTo(framed, 0);
        framed[^2] = (byte)'\r';
        framed[^1] = (byte)'\n';
        return framed;
    }

    private static bool ContainsForbiddenCharacters(string value) => value.IndexOfAny(['\r', '\n', '\0']) >= 0;
}
