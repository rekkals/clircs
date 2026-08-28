namespace Clircs.Protocol;

public enum IrcCaseMapping
{
    Ascii,
    Rfc1459,
    StrictRfc1459
}

public static class IrcCaseFold
{
    public static string Fold(string value, IrcCaseMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Create(value.Length, (value, mapping), static (destination, state) =>
        {
            for (var index = 0; index < state.value.Length; index++)
            {
                destination[index] = Fold(state.value[index], state.mapping);
            }
        });
    }

    public static char Fold(char value, IrcCaseMapping mapping)
    {
        if (value is >= 'A' and <= 'Z')
        {
            return (char)(value + ('a' - 'A'));
        }

        return mapping switch
        {
            IrcCaseMapping.Rfc1459 => value switch
            {
                '[' => '{',
                ']' => '}',
                '\\' => '|',
                '^' => '~',
                _ => value
            },
            IrcCaseMapping.StrictRfc1459 => value switch
            {
                '[' => '{',
                ']' => '}',
                '\\' => '|',
                _ => value
            },
            _ => value
        };
    }
}

public sealed class IrcNameComparer : IEqualityComparer<string>
{
    public IrcNameComparer(IrcCaseMapping mapping)
    {
        Mapping = mapping;
    }

    public IrcCaseMapping Mapping { get; }

    public bool Equals(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (IrcCaseFold.Fold(left[index], Mapping) != IrcCaseFold.Fold(right[index], Mapping))
            {
                return false;
            }
        }

        return true;
    }

    public int GetHashCode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var hash = new HashCode();
        foreach (var character in value)
        {
            hash.Add(IrcCaseFold.Fold(character, Mapping));
        }

        return hash.ToHashCode();
    }
}
