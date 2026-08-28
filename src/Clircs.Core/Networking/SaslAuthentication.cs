namespace Clircs.Networking;

public static class SaslMechanisms
{
    public const string Plain = "PLAIN";
    public const string External = "EXTERNAL";
}

public sealed record TlsClientCertificate(string Path, string Password)
{
    public TlsClientCertificate Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Path);
        if (Path.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("The client certificate path cannot contain CR, LF, or NUL.", nameof(Path));
        if (Password.Length > 16_384 || Password.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("The client certificate password must contain no more than 16,384 characters without CR, LF, or NUL.", nameof(Password));
        return this;
    }

    public override string ToString() =>
        $"TlsClientCertificate {{ Path = {Path}, Password = ******** }}";
}

public sealed record SaslAuthentication
{
    public SaslAuthentication(string username, string password, bool required = true)
        : this(SaslMechanisms.Plain, username, password, null, required) { }

    private SaslAuthentication(string mechanism, string? authorizationIdentity, string? password,
        TlsClientCertificate? clientCertificate, bool required)
    {
        Mechanism = mechanism;
        AuthorizationIdentity = authorizationIdentity;
        Password = password;
        ClientCertificate = clientCertificate;
        Required = required;
    }

    public string Mechanism { get; }
    public string? AuthorizationIdentity { get; }
    public string? Username => Mechanism == SaslMechanisms.Plain ? AuthorizationIdentity : null;
    public string? Password { get; }
    public TlsClientCertificate? ClientCertificate { get; }
    public bool Required { get; }

    public static SaslAuthentication External(TlsClientCertificate clientCertificate,
        string? authorizationIdentity = null, bool required = true) =>
        new(SaslMechanisms.External, authorizationIdentity, null, clientCertificate, required);

    public SaslAuthentication Validate()
    {
        if (Mechanism == SaslMechanisms.Plain)
        {
            ValidateIdentity(AuthorizationIdentity, required: true, "SASL account");
            if (Password is null || Password.Length is < 1 or > 16_384 || Password.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new ArgumentException("The SASL password must contain 1-16,384 characters without CR, LF, or NUL.", nameof(Password));
            if (ClientCertificate is not null)
                throw new ArgumentException("SASL PLAIN does not use a TLS client certificate.", nameof(ClientCertificate));
        }
        else if (Mechanism == SaslMechanisms.External)
        {
            ValidateIdentity(AuthorizationIdentity, required: false, "SASL authorization identity");
            ArgumentNullException.ThrowIfNull(ClientCertificate);
            ClientCertificate.Validate();
            if (Password is not null)
                throw new ArgumentException("SASL EXTERNAL does not use an account password.", nameof(Password));
        }
        else
        {
            throw new ArgumentException($"Unsupported SASL mechanism '{Mechanism}'.", nameof(Mechanism));
        }
        return this;
    }

    public override string ToString() =>
        $"SaslAuthentication {{ Mechanism = {Mechanism}, AuthorizationIdentity = {AuthorizationIdentity ?? "(certificate)"}, Secret = ********, Required = {Required} }}";

    private static void ValidateIdentity(string? value, bool required, string description)
    {
        if (required && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"The {description} is required.");
        if (value is not null && (value.Length > 256 || value.IndexOfAny(['\r', '\n', '\0']) >= 0))
            throw new ArgumentException($"The {description} must contain no more than 256 characters without CR, LF, or NUL.");
    }
}

public sealed record SaslAuthenticationEvent(bool Succeeded, bool Required, string Mechanism, string? Identity, string Detail);

public sealed class IrcSaslException(string message) : IOException(message);

internal static class SaslPayload
{
    public static IReadOnlyList<string> Plain(string username, string password) =>
        Fragment(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{username}\0{username}\0{password}")));

    public static IReadOnlyList<string> External(string? authorizationIdentity) =>
        string.IsNullOrEmpty(authorizationIdentity)
            ? ["+"]
            : Fragment(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(authorizationIdentity)));

    private static IReadOnlyList<string> Fragment(string payload)
    {
        var chunks = new List<string>((payload.Length / 400) + 1);
        for (var offset = 0; offset < payload.Length; offset += 400)
            chunks.Add(payload.Substring(offset, Math.Min(400, payload.Length - offset)));
        if (payload.Length % 400 == 0) chunks.Add("+");
        return chunks;
    }
}

internal static class SaslPlainPayload
{
    public static IReadOnlyList<string> Encode(string username, string password) => SaslPayload.Plain(username, password);
}
