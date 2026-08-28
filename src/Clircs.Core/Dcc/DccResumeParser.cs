using System.Globalization;
using System.Text;

namespace Clircs.Dcc;

public enum DccResumeOperation
{
    Resume,
    Accept
}

public sealed record DccResumeMessage(
    DccResumeOperation Operation,
    string Filename,
    int Port,
    long Position,
    string? PassiveToken,
    string RawPayload)
{
    public bool IsPassive => Port == 0 && !string.IsNullOrWhiteSpace(PassiveToken);
}

public static class DccResumeParser
{
    public static bool TryParse(string payload, out DccResumeMessage? message, out string? error)
    {
        message = null;
        error = null;
        var tokens = Tokenize(payload, out error);
        if (tokens is null) return false;
        if (tokens.Count < 5 || !tokens[0].Equals("DCC", StringComparison.OrdinalIgnoreCase))
        {
            error = "The CTCP payload is not a DCC resume message";
            return false;
        }

        DccResumeOperation operation;
        if (tokens[1].Equals("RESUME", StringComparison.OrdinalIgnoreCase))
            operation = DccResumeOperation.Resume;
        else if (tokens[1].Equals("ACCEPT", StringComparison.OrdinalIgnoreCase))
            operation = DccResumeOperation.Accept;
        else
        {
            error = "The DCC message is not RESUME or ACCEPT";
            return false;
        }

        var passive = tokens.Count >= 6 && tokens[^3] == "0";
        var filenameEnd = passive ? tokens.Count - 3 : tokens.Count - 2;
        if (filenameEnd <= 2)
        {
            error = "The DCC resume message does not contain a filename";
            return false;
        }
        var filename = string.Join(' ', tokens.Skip(2).Take(filenameEnd - 2));
        if (string.IsNullOrWhiteSpace(filename) || filename is "." or ".." ||
            Path.IsPathRooted(filename) || filename.IndexOfAny(['/', '\\', ':']) >= 0 ||
            filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "The DCC resume filename is unsafe";
            return false;
        }

        var portToken = tokens[filenameEnd];
        var positionToken = tokens[filenameEnd + 1];
        if (!int.TryParse(portToken, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 0 or > 65535)
        {
            error = "The DCC resume port is invalid";
            return false;
        }
        if (!long.TryParse(positionToken, NumberStyles.None, CultureInfo.InvariantCulture, out var position) ||
            position < 0)
        {
            error = "The DCC resume position is invalid";
            return false;
        }

        string? token = null;
        if (passive)
        {
            token = tokens[^1];
            if (!uint.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                error = "The passive DCC token is invalid";
                return false;
            }
        }
        else if (port == 0 || tokens.Count != filenameEnd + 2)
        {
            error = "Passive DCC resume requires a token";
            return false;
        }

        message = new DccResumeMessage(operation, filename, port, position, token, payload);
        return true;
    }

    public static string Format(
        DccResumeOperation operation,
        string filename,
        int port,
        long position,
        string? passiveToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (position < 0) throw new ArgumentOutOfRangeException(nameof(position));
        if (port == 0 && string.IsNullOrWhiteSpace(passiveToken))
            throw new ArgumentException("Passive DCC resume requires a token", nameof(passiveToken));
        var wireFilename = filename.Any(char.IsWhiteSpace) ? $"\"{filename}\"" : filename;
        var command = operation.ToString().ToUpperInvariant();
        return $"DCC {command} {wireFilename} {port} {position}" +
            (port == 0 ? $" {passiveToken}" : string.Empty);
    }

    private static IReadOnlyList<string>? Tokenize(string payload, out string? error)
    {
        error = null;
        var result = new List<string>();
        var token = new StringBuilder();
        var quoted = false;
        foreach (var character in payload)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (token.Length > 0)
                {
                    result.Add(token.ToString());
                    token.Clear();
                }
                continue;
            }
            if (char.IsControl(character))
            {
                error = "The DCC resume message contains control characters";
                return null;
            }
            token.Append(character);
        }
        if (quoted)
        {
            error = "The DCC resume message contains an unterminated quote";
            return null;
        }
        if (token.Length > 0) result.Add(token.ToString());
        return result;
    }
}
