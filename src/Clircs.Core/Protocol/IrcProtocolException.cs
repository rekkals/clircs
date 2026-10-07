namespace Clircs.Protocol;

public class IrcProtocolException : Exception
{
    public IrcProtocolException(string message)
        : base(message)
    {
    }
}

public sealed class IrcCapabilityException : IrcProtocolException
{
    public IrcCapabilityException(bool duringRegistration, string command)
        : base(duringRegistration
            ? "Received message tags before tag support was negotiated"
            : $"Received unnegotiated message tags on {command}")
    {
        DuringRegistration = duringRegistration;
        Command = command;
    }

    public bool DuringRegistration { get; }

    public string Command { get; }
}
