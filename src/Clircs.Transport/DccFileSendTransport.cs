using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Clircs.Transport;

public readonly record struct DccSendProgress(
    long BytesSent,
    long BytesAcknowledged,
    long TotalBytes,
    TimeSpan Elapsed,
    long InitialOffset = 0)
{
    public double BytesPerSecond => Elapsed.TotalSeconds <= 0
        ? 0
        : Math.Max(0, BytesAcknowledged - InitialOffset) / Elapsed.TotalSeconds;
}

public sealed class DccFileSendTransport : IAsyncDisposable
{
    public static readonly TimeSpan DefaultAcknowledgementTimeout = TimeSpan.FromSeconds(60);
    private const int BufferSize = 64 * 1024;
    private readonly IDccTransportConnection _connection;
    private readonly Stream _stream;
    private int _disposed;

    internal DccFileSendTransport(IDccTransportConnection connection)
    {
        _connection = connection;
        _stream = connection.Stream;
    }

    public string RemoteAddress => _connection.RemoteAddress;

    public bool IsSecure => _connection.IsSecure;

    public string? SecurityProtocol => _connection.SecurityProtocol;

    public string? PeerCertificateFingerprint => _connection.PeerCertificateFingerprint;

    public static async ValueTask<DccFileSendTransport> ConnectAsync(
        string address,
        int port,
        CancellationToken cancellationToken = default,
        bool secure = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        return new DccFileSendTransport(
            secure
                ? await TlsDccTransportConnection.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false)
                : await TcpDccTransportConnection.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask SendAsync(
        Stream source,
        long expectedBytes,
        Action<DccSendProgress>? progress = null,
        TimeSpan? acknowledgementTimeout = null,
        long initialOffset = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("The source stream is not readable.", nameof(source));
        if (expectedBytes < 0) throw new ArgumentOutOfRangeException(nameof(expectedBytes));
        if (initialOffset < 0 || initialOffset > expectedBytes)
            throw new ArgumentOutOfRangeException(nameof(initialOffset));
        if (initialOffset > 0 && !source.CanSeek)
            throw new ArgumentException("A resumed source stream must be seekable.", nameof(source));
        var timeout = acknowledgementTimeout ?? DefaultAcknowledgementTimeout;
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(acknowledgementTimeout));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var buffer = new byte[BufferSize];
        var acknowledgement = new byte[sizeof(uint)];
        if (source.CanSeek) source.Seek(initialOffset, SeekOrigin.Begin);
        var sent = initialOffset;
        var acknowledged = initialOffset;
        var previousLow = unchecked((uint)initialOffset);
        var acknowledgementBase = initialOffset & ~((long)uint.MaxValue);
        var clock = Stopwatch.StartNew();

        while (sent < expectedBytes)
        {
            var requested = (int)Math.Min(buffer.Length, expectedBytes - sent);
            var count = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException(
                    $"The source file ended after {sent} of {expectedBytes} bytes.");
            }

            await _stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            sent += count;

            while (acknowledged < sent)
            {
                using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                inactivity.CancelAfter(timeout);
                try
                {
                    await _stream.ReadExactlyAsync(acknowledgement, inactivity.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"The DCC receiver stopped acknowledging data for {timeout.TotalSeconds:0} seconds.");
                }
                catch (EndOfStreamException)
                {
                    throw new EndOfStreamException(
                        $"The DCC receiver closed the connection after acknowledging {acknowledged} of {expectedBytes} bytes.");
                }

                var low = BinaryPrimitives.ReadUInt32BigEndian(acknowledgement);
                if (low < previousLow && previousLow - low > uint.MaxValue / 2)
                {
                    acknowledgementBase += 1L << 32;
                }
                previousLow = low;
                // A malformed or premature acknowledgement cannot acknowledge bytes that have not
                // actually been written yet. Clamp to this batch rather than trusting the peer.
                acknowledged = Math.Min(sent, acknowledgementBase + low);
                progress?.Invoke(new DccSendProgress(sent, acknowledged, expectedBytes, clock.Elapsed, initialOffset));
            }
        }

        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (expectedBytes == initialOffset)
        {
            progress?.Invoke(new DccSendProgress(initialOffset, initialOffset, expectedBytes, clock.Elapsed, initialOffset));
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

public sealed class DccFileSendListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly bool _secure;
    private int _disposed;

    private DccFileSendListener(TcpListener listener, bool secure)
    {
        _listener = listener;
        _secure = secure;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static DccFileSendListener Start(
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
                return new DccFileSendListener(listener, secure);
            }
            catch (SocketException exception)
            {
                listener.Stop();
                lastError = exception;
            }
        }

        throw new InvalidOperationException(
            $"No available DCC listening port was found in {ports}.", lastError);
    }

    public async ValueTask<DccFileSendTransport> AcceptAsync(CancellationToken cancellationToken = default)
    {
        var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new DccFileSendTransport(
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
