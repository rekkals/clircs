namespace Clircs.Protocol;

public sealed class IrcProtocolException : Exception
{
    public IrcProtocolException(string message)
        : base(message)
    {
    }
}
