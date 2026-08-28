namespace Clircs.Networking;

public sealed record IrcEndpoint
{
    public IrcEndpoint(string host, int port, bool useTls)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "An IRC port must be between 1 and 65535.");
        }

        Host = host;
        Port = port;
        UseTls = useTls;
    }

    public string Host { get; }

    public int Port { get; }

    public bool UseTls { get; }

    public override string ToString() => $"{Host}:{Port}{(UseTls ? " (TLS)" : string.Empty)}";
}
