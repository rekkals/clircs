namespace Clircs.Identity;

public readonly record struct NetworkSessionId
{
    public NetworkSessionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A network session ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static NetworkSessionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

public readonly record struct BufferId
{
    public BufferId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A buffer ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static BufferId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

public readonly record struct NetworkProfileId
{
    public NetworkProfileId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A network profile ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static NetworkProfileId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

public readonly record struct DccSessionId
{
    public DccSessionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A DCC session ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static DccSessionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

public readonly record struct UserRecordId
{
    public UserRecordId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A user record ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static UserRecordId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}
