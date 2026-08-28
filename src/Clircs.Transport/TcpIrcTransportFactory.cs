using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Clircs.Networking;

namespace Clircs.Transport;

public sealed class TcpIrcTransportFactory : IIrcTransportFactory
{
    private readonly ITlsCertificatePolicy? _certificatePolicy;

    public TcpIrcTransportFactory(ITlsCertificatePolicy? certificatePolicy = null)
    {
        _certificatePolicy = certificatePolicy;
    }

    public async ValueTask<IIrcTransport> ConnectAsync(IrcEndpoint endpoint, CancellationToken cancellationToken)
        => await ConnectAsync(new IrcTransportOptions(endpoint), cancellationToken).ConfigureAwait(false);

    public async ValueTask<IIrcTransport> ConnectAsync(IrcTransportOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var endpoint = options.Endpoint;
        ArgumentNullException.ThrowIfNull(endpoint);
        options.ClientCertificate?.Validate();
        if (options.ClientCertificate is not null && !endpoint.UseTls)
        {
            throw new ArgumentException("A TLS client certificate cannot be used with a plaintext endpoint.", nameof(options));
        }
        var client = new TcpClient();
        X509Certificate2? clientCertificate = null;

        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken).ConfigureAwait(false);
            client.NoDelay = true;
            Stream stream = client.GetStream();

            if (endpoint.UseTls)
            {
                var tlsStream = new SslStream(stream, leaveInnerStreamOpen: false);
                var authenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = endpoint.Host,
                    EnabledSslProtocols = SslProtocols.None,
                    CertificateRevocationCheckMode = X509RevocationMode.Online,
                    RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                        ValidateCertificate(endpoint, certificate, chain, errors)
                };

                if (options.ClientCertificate is not null)
                {
                    clientCertificate = X509CertificateLoader.LoadPkcs12FromFile(
                        options.ClientCertificate.Path,
                        options.ClientCertificate.Password,
                        X509KeyStorageFlags.EphemeralKeySet);
                    if (!clientCertificate.HasPrivateKey)
                    {
                        throw new CryptographicException("The TLS client certificate does not contain a private key.");
                    }
                    authenticationOptions.ClientCertificates = [clientCertificate];
                }

                await tlsStream.AuthenticateAsClientAsync(authenticationOptions, cancellationToken).ConfigureAwait(false);
                stream = tlsStream;
            }

            return new TcpIrcTransport(client, stream, endpoint.ToString(), clientCertificate);
        }
        catch
        {
            clientCertificate?.Dispose();
            client.Dispose();
            throw;
        }
    }

    private bool ValidateCertificate(
        IrcEndpoint endpoint,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (certificate is null || _certificatePolicy is null)
        {
            return false;
        }

        var ownsCertificate = certificate is not X509Certificate2;
        var certificate2 = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
        try
        {
            var problems = TlsCertificateProblems.None;
            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            {
                problems |= TlsCertificateProblems.CertificateNotAvailable;
            }

            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            {
                problems |= TlsCertificateProblems.NameMismatch;
            }

            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
            {
                problems |= TlsCertificateProblems.ChainErrors;
            }

            var chainErrors = chain?.ChainStatus
                .Where(status => status.Status != X509ChainStatusFlags.NoError)
                .Select(status => $"{status.Status}: {status.StatusInformation.Trim()}")
                .ToArray() ?? [];
            var info = new TlsCertificateInfo(
                endpoint,
                certificate2.Subject,
                certificate2.Issuer,
                new DateTimeOffset(certificate2.NotBefore).ToUniversalTime(),
                new DateTimeOffset(certificate2.NotAfter).ToUniversalTime(),
                Convert.ToHexString(SHA256.HashData(certificate2.RawData)),
                problems,
                chainErrors);
            return _certificatePolicy.Decide(info) == TlsCertificateDecision.Accept;
        }
        finally
        {
            if (ownsCertificate)
            {
                certificate2.Dispose();
            }
        }
    }
}

internal sealed class TcpIrcTransport : IIrcTransport
{
    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly X509Certificate2? _clientCertificate;
    private int _closed;

    public TcpIrcTransport(TcpClient client, Stream stream, string remoteDescription, X509Certificate2? clientCertificate = null)
    {
        _client = client;
        _stream = stream;
        _clientCertificate = clientCertificate;
        RemoteDescription = remoteDescription;
    }

    public string RemoteDescription { get; }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            throw new InvalidOperationException("The IRC transport is closed.");
        }

        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Close();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _stream.Dispose();
        _clientCertificate?.Dispose();
        _client.Dispose();
    }
}
