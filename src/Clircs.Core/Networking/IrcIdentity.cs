using System.Collections.ObjectModel;

namespace Clircs.Networking;

public sealed class IrcIdentity
{
    public IrcIdentity(IEnumerable<string> nicknames, string username, string realName)
    {
        ArgumentNullException.ThrowIfNull(nicknames);
        var nickArray = nicknames.Where(nick => !string.IsNullOrWhiteSpace(nick)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (nickArray.Length == 0)
        {
            throw new ArgumentException("At least one nickname is required.", nameof(nicknames));
        }

        if (nickArray.Any(ContainsInvalidTokenCharacter))
        {
            throw new ArgumentException("Nicknames cannot contain spaces, commas, colons, CR, LF, or NUL.", nameof(nicknames));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        if (ContainsInvalidTokenCharacter(username))
        {
            throw new ArgumentException("The username must be one IRC token.", nameof(username));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(realName);
        if (realName.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("The real name cannot contain CR, LF, or NUL.", nameof(realName));
        }

        Nicknames = new ReadOnlyCollection<string>(nickArray);
        Username = username;
        RealName = realName;
    }

    public IReadOnlyList<string> Nicknames { get; }

    public string Username { get; }

    public string RealName { get; }

    private static bool ContainsInvalidTokenCharacter(string value) =>
        value.Any(character => char.IsWhiteSpace(character) || character is ',' or ':' or '\0');
}
