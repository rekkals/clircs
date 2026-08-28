namespace Clircs.Networking;

public interface IIrcTransport : IAsyncDisposable
{
    string RemoteDescription { get; }

    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    ValueTask CloseAsync(CancellationToken cancellationToken);
}

public interface IIrcTransportFactory
{
    ValueTask<IIrcTransport> ConnectAsync(IrcTransportOptions options, CancellationToken cancellationToken);
}

public sealed record IrcTransportOptions(IrcEndpoint Endpoint, TlsClientCertificate? ClientCertificate = null);
