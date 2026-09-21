using System.Collections.ObjectModel;

namespace Clircs.Protocol;

public sealed class IrcMessage
{
    public const int TraditionalParameterLimit = 15;

    public IrcMessage(
        string? prefix,
        string command,
        IEnumerable<string> parameters,
        IReadOnlyDictionary<string, string?>? tags = null)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                "An IRC command must be a non-empty token.",
                nameof(command));
        }

        ArgumentNullException.ThrowIfNull(parameters);
        var parameterArray = parameters.ToArray();

        if (parameterArray.Any(parameter => parameter is null))
        {
            throw new ArgumentException(
                "IRC parameters cannot be null.",
                nameof(parameters));
        }

        var tagDictionary = tags is null
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : new Dictionary<string, string?>(tags, StringComparer.Ordinal);

        Prefix = prefix;
        Command = command.ToUpperInvariant();
        Parameters = new ReadOnlyCollection<string>(parameterArray);
        Tags = new ReadOnlyDictionary<string, string?>(tagDictionary);
    }

    public string? Prefix { get; }

    public string Command { get; }

    public IReadOnlyList<string> Parameters { get; }

    public IReadOnlyDictionary<string, string?> Tags { get; }

    public bool HasTags => Tags.Count > 0;

    public bool ExceedsTraditionalParameterLimit =>
        Parameters.Count > TraditionalParameterLimit;

    public string? Trailing => Parameters.Count == 0 ? null : Parameters[^1];
}
