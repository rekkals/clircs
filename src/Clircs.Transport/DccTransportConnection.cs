using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Clircs.Transport;

// DCC protocol engines operate on this boundary rather than owning a raw NetworkStream.
// Secure DCC can supply an SslStream-backed implementation without duplicating CHAT or SEND.
internal interface IDccTransportConnection : IAsyncDisposable
{
    Stream Stream { get; }
    string RemoteAddress { get; }
    bool IsSecure { get; }
    string? SecurityProtocol { get; }
    string? PeerCertificateFingerprint { get; }
}

internal sealed class TcpDccTransportConnection : IDccTransportConnection
{
    private readonly TcpClient _client;
    private int _disposed;

    private TcpDccTransportConnection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        Stream = client.GetStream();
    }

    public Stream Stream { get; }

    public bool IsSecure => false;

    public string? SecurityProtocol => null;

    public string? PeerCertificateFingerprint => null;

    public string RemoteAddress =>
        (_client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";

    public static async ValueTask<TcpDccTransportConnection> ConnectAsync(
        string address,
        int port,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
            return new TcpDccTransportConnection(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static TcpDccTransportConnection FromAcceptedClient(TcpClient client) => new(client);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Stream.Dispose();
            _client.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}

internal sealed class TlsDccTransportConnection : IDccTransportConnection
{
    private static readonly Lazy<X509Certificate2> ServerCertificate = new(CreateServerCertificate);
    private readonly IDccTransportConnection _inner;
    private readonly SslStream _stream;
    private int _disposed;

    private TlsDccTransportConnection(
        IDccTransportConnection inner,
        SslStream stream,
        string? peerCertificateFingerprint)
    {
        _inner = inner;
        _stream = stream;
        PeerCertificateFingerprint = peerCertificateFingerprint;
    }

    public Stream Stream => _stream;

    public string RemoteAddress => _inner.RemoteAddress;

    public bool IsSecure => true;

    public string? SecurityProtocol => _stream.SslProtocol.ToString();

    public string? PeerCertificateFingerprint { get; }

    public static async ValueTask<TlsDccTransportConnection> ConnectAsync(
        string address,
        int port,
        CancellationToken cancellationToken)
    {
        var inner = await TcpDccTransportConnection.ConnectAsync(address, port, cancellationToken)
            .ConfigureAwait(false);
        SslStream? stream = null;
        byte[]? peerCertificate = null;
        try
        {
            stream = new SslStream(
                inner.Stream,
                leaveInnerStreamOpen: true,
                (_, certificate, _, _) =>
                {
                    if (certificate is not null) peerCertificate = certificate.GetRawCertData();
                    return certificate is not null;
                });
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = address,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, cancellationToken).ConfigureAwait(false);
            return new TlsDccTransportConnection(inner, stream, Fingerprint(peerCertificate));
        }
        catch
        {
            stream?.Dispose();
            await inner.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async ValueTask<TlsDccTransportConnection> FromAcceptedClientAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        var inner = TcpDccTransportConnection.FromAcceptedClient(client);
        SslStream? stream = null;
        byte[]? peerCertificate = null;
        try
        {
            stream = new SslStream(
                inner.Stream,
                leaveInnerStreamOpen: true,
                (_, certificate, _, _) =>
                {
                    if (certificate is not null) peerCertificate = certificate.GetRawCertData();
                    return true;
                });
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = ServerCertificate.Value,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, cancellationToken).ConfigureAwait(false);
            return new TlsDccTransportConnection(inner, stream, Fingerprint(peerCertificate));
        }
        catch
        {
            stream?.Dispose();
            await inner.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stream.DisposeAsync().ConfigureAwait(false);
        await _inner.DisposeAsync().ConfigureAwait(false);
    }

    private static string? Fingerprint(byte[]? certificate) => certificate is null
        ? null
        : Convert.ToHexString(SHA256.HashData(certificate));

    private static X509Certificate2 CreateServerCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=clircs secure DCC",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var created = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        return X509CertificateLoader.LoadPkcs12(
            created.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
    }
}
