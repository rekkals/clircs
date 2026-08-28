namespace Clircs.Networking;

public sealed record IrcConnectionOptions(
    IrcEndpoint Endpoint,
    IrcIdentity Identity,
    string? Password = null,
    SaslAuthentication? Sasl = null)
{
    public IrcConnectionOptions Validate()
    {
        ArgumentNullException.ThrowIfNull(Endpoint);
        ArgumentNullException.ThrowIfNull(Identity);
        if (Password?.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("The server password cannot contain CR, LF, or NUL.", nameof(Password));
        }
        Sasl?.Validate();
        if (Sasl is not null && !Endpoint.UseTls)
        {
            throw new ArgumentException($"SASL {Sasl.Mechanism} requires a TLS server endpoint.", nameof(Sasl));
        }

        return this;
    }
}
