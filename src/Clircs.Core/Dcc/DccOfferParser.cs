using System.Globalization;
using System.Net;
using System.Text;

namespace Clircs.Dcc;

public static class DccOfferParser
{
    public static bool TryParse(string payload, out DccOffer? offer, out string? error)
    {
        offer = null;
        error = null;
        if (string.IsNullOrWhiteSpace(payload))
        {
            error = "The DCC request is empty.";
            return false;
        }

        var tokens = Tokenize(payload, out error);
        if (tokens is null)
        {
            return false;
        }

        if (tokens.Count < 2 || !tokens[0].Equals("DCC", StringComparison.OrdinalIgnoreCase))
        {
            error = "The CTCP payload is not a DCC request.";
            return false;
        }

        return tokens[1].ToUpperInvariant() switch
        {
            "CHAT" => TryParseChat(payload, tokens, false, out offer, out error),
            "SCHAT" => TryParseChat(payload, tokens, true, out offer, out error),
            "SEND" => TryParseSend(payload, tokens, false, out offer, out error),
            "SSEND" => TryParseSend(payload, tokens, true, out offer, out error),
            _ => Unsupported(tokens[1], out error)
        };
    }

    private static bool TryParseChat(
        string payload,
        IReadOnlyList<string> tokens,
        bool secure,
        out DccOffer? offer,
        out string? error)
    {
        offer = null;
        error = null;
        if (tokens.Count is < 5 or > 6 || !tokens[2].Equals("chat", StringComparison.OrdinalIgnoreCase))
        {
            error = "Malformed DCC CHAT request.";
            return false;
        }

        var passiveToken = tokens.Count == 6 ? tokens[5] : null;
        if (!TryPassiveToken(passiveToken, out error)) return false;
        if (!TryEndpoint(tokens[3], tokens[4], passiveToken, out var address, out var port, out error))
        {
            return false;
        }

        offer = new DccOffer(DccRequestType.Chat, null, address!, port, null, passiveToken, payload, secure);
        return true;
    }

    private static bool TryParseSend(
        string payload,
        IReadOnlyList<string> tokens,
        bool secure,
        out DccOffer? offer,
        out string? error)
    {
        offer = null;
        error = null;
        if (tokens.Count < 6)
        {
            error = "Malformed DCC SEND request.";
            return false;
        }

        var extended = tokens.Count >= 7 &&
            TryEndpointShape(tokens[^4], tokens[^3]) &&
            long.TryParse(tokens[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var extendedSize) &&
            extendedSize >= 0 &&
            TryPassiveToken(tokens[^1], out _);
        var addressIndex = extended ? tokens.Count - 4 : tokens.Count - 3;
        if (addressIndex <= 2)
        {
            error = "The DCC SEND request does not contain a filename.";
            return false;
        }

        var filename = string.Join(' ', tokens.Skip(2).Take(addressIndex - 2));
        if (!IsSafeFilename(filename, out error))
        {
            return false;
        }

        var passiveToken = extended ? tokens[^1] : null;
        if (!TryEndpoint(tokens[addressIndex], tokens[addressIndex + 1], passiveToken,
                out var address, out var port, out error))
        {
            return false;
        }

        var sizeToken = tokens[addressIndex + 2];
        if (!long.TryParse(sizeToken, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size < 0)
        {
            error = "The DCC SEND size is invalid.";
            return false;
        }

        offer = new DccOffer(DccRequestType.Send, filename, address!, port, size, passiveToken, payload, secure);
        return true;
    }

    private static bool TryEndpoint(
        string addressToken,
        string portToken,
        string? passiveToken,
        out string? address,
        out int port,
        out string? error)
    {
        address = null;
        port = 0;
        error = null;
        if (!TryAddress(addressToken, out address))
        {
            error = "The DCC address is invalid.";
            return false;
        }

        if (!int.TryParse(portToken, NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
            port is < 0 or > 65535)
        {
            error = "The DCC port is invalid.";
            return false;
        }

        if (port == 0 && string.IsNullOrWhiteSpace(passiveToken))
        {
            error = "Passive DCC requires a token.";
            return false;
        }

        return true;
    }

    private static bool TryEndpointShape(string addressToken, string portToken) =>
        TryAddress(addressToken, out _) &&
        int.TryParse(portToken, NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
        port is >= 0 and <= 65535;

    private static bool TryPassiveToken(string? token, out string? error)
    {
        error = null;
        if (token is null) return true;
        if (!uint.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            error = "The passive DCC token is invalid.";
            return false;
        }
        return true;
    }

    private static bool TryAddress(string token, out string? address)
    {
        address = null;
        if (uint.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric))
        {
            address = string.Create(CultureInfo.InvariantCulture,
                $"{numeric >> 24}.{numeric >> 16 & 255}.{numeric >> 8 & 255}.{numeric & 255}");
            return true;
        }

        if (IPAddress.TryParse(token, out var parsed))
        {
            address = parsed.ToString();
            return true;
        }

        return false;
    }

    private static bool IsSafeFilename(string filename, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(filename) || filename is "." or ".." ||
            Path.IsPathRooted(filename) || filename.IndexOfAny(['/', '\\', ':']) >= 0 ||
            filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "The DCC filename is unsafe.";
            return false;
        }

        return true;
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
                error = "The DCC request contains control characters.";
                return null;
            }
            token.Append(character);
        }

        if (quoted)
        {
            error = "The DCC request contains an unterminated quote.";
            return null;
        }
        if (token.Length > 0) result.Add(token.ToString());
        return result;
    }

    private static bool Unsupported(string command, out string? error)
    {
        error = $"Unsupported DCC request type: {command}.";
        return false;
    }
}
