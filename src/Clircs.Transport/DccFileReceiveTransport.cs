using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Clircs.Transport;

public readonly record struct DccReceiveProgress(
    long BytesReceived,
    long TotalBytes,
    TimeSpan Elapsed,
    long InitialOffset = 0)
{
    public double BytesPerSecond => Elapsed.TotalSeconds <= 0
        ? 0
        : Math.Max(0, BytesReceived - InitialOffset) / Elapsed.TotalSeconds;
}

public sealed class DccFileReceiveTransport : IAsyncDisposable
{
    public static readonly TimeSpan DefaultInactivityTimeout = TimeSpan.FromSeconds(60);
    private const int BufferSize = 64 * 1024;
    private readonly IDccTransportConnection _connection;
    private readonly Stream _stream;
    private int _disposed;

    internal DccFileReceiveTransport(IDccTransportConnection connection)
    {
        _connection = connection;
        _stream = connection.Stream;
    }

    public string RemoteAddress => _connection.RemoteAddress;

    public bool IsSecure => _connection.IsSecure;

    public string? SecurityProtocol => _connection.SecurityProtocol;

    public string? PeerCertificateFingerprint => _connection.PeerCertificateFingerprint;

    public static async ValueTask<DccFileReceiveTransport> ConnectAsync(
        string address,
        int port,
        CancellationToken cancellationToken = default,
        bool secure = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));

        return new DccFileReceiveTransport(
            secure
                ? await TlsDccTransportConnection.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false)
                : await TcpDccTransportConnection.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask ReceiveAsync(
        Stream destination,
        long expectedBytes,
        Action<DccReceiveProgress>? progress = null,
        TimeSpan? inactivityTimeout = null,
        long initialOffset = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("The destination stream is not writable.", nameof(destination));
        if (expectedBytes < 0) throw new ArgumentOutOfRangeException(nameof(expectedBytes));
        if (initialOffset < 0 || initialOffset > expectedBytes)
            throw new ArgumentOutOfRangeException(nameof(initialOffset));
        if (initialOffset > 0 && !destination.CanSeek)
            throw new ArgumentException("A resumed destination stream must be seekable.", nameof(destination));
        var timeout = inactivityTimeout ?? DefaultInactivityTimeout;
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(inactivityTimeout));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var buffer = new byte[BufferSize];
        var acknowledgement = new byte[sizeof(uint)];
        if (destination.CanSeek) destination.Seek(initialOffset, SeekOrigin.Begin);
        var received = initialOffset;
        var clock = Stopwatch.StartNew();
        while (received < expectedBytes)
        {
            var requested = (int)Math.Min(buffer.Length, expectedBytes - received);
            int count;
            using (var inactivity = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                inactivity.CancelAfter(timeout);
                try
                {
                    count = await _stream.ReadAsync(buffer.AsMemory(0, requested), inactivity.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"The DCC sender stopped sending data for {timeout.TotalSeconds:0} seconds.");
                }
            }

            if (count == 0)
            {
                throw new EndOfStreamException(
                    $"The DCC sender closed the connection after {received} of {expectedBytes} bytes.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            received += count;
            BinaryPrimitives.WriteUInt32BigEndian(acknowledgement, unchecked((uint)received));
            await _stream.WriteAsync(acknowledgement, cancellationToken).ConfigureAwait(false);
            progress?.Invoke(new DccReceiveProgress(received, expectedBytes, clock.Elapsed, initialOffset));
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (expectedBytes == initialOffset)
        {
            progress?.Invoke(new DccReceiveProgress(initialOffset, expectedBytes, clock.Elapsed, initialOffset));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public sealed class DccFileReceiveListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly bool _secure;
    private int _disposed;

    private DccFileReceiveListener(TcpListener listener, bool secure)
    {
        _listener = listener;
        _secure = secure;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static DccFileReceiveListener Start(
        IPAddress advertisedAddress,
        DccPortRange ports,
        bool secure = false)
    {
        ArgumentNullException.ThrowIfNull(advertisedAddress);
        var listenAddress = advertisedAddress.AddressFamily switch
        {
            AddressFamily.InterNetwork => IPAddress.Any,
            AddressFamily.InterNetworkV6 => IPAddress.IPv6Any,
            _ => throw new ArgumentException("The DCC address must be IPv4 or IPv6.", nameof(advertisedAddress))
        };
        Exception? lastError = null;
        foreach (var port in ports.Ports())
        {
            var listener = new TcpListener(listenAddress, port);
            try
            {
                listener.Start(1);
                return new DccFileReceiveListener(listener, secure);
            }
            catch (SocketException exception)
            {
                listener.Stop();
                lastError = exception;
            }
        }
        throw new InvalidOperationException($"No available DCC listening port was found in {ports}.", lastError);
    }

    public async ValueTask<DccFileReceiveTransport> AcceptAsync(CancellationToken cancellationToken = default)
    {
        var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new DccFileReceiveTransport(
                _secure
                    ? await TlsDccTransportConnection.FromAcceptedClientAsync(client, cancellationToken)
                        .ConfigureAwait(false)
                    : TcpDccTransportConnection.FromAcceptedClient(client));
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
