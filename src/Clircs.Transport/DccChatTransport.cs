using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Clircs.Protocol;

namespace Clircs.Transport;

public sealed class DccChatTransport : IAsyncDisposable
{
    public const int MaximumLineBytes = 4096;
    private readonly IDccTransportConnection _connection;
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposed;

    private DccChatTransport(IDccTransportConnection connection)
    {
        _connection = connection;
        _stream = connection.Stream;
    }

    public string RemoteAddress => _connection.RemoteAddress;

    public bool IsSecure => _connection.IsSecure;

    public string? SecurityProtocol => _connection.SecurityProtocol;

    public string? PeerCertificateFingerprint => _connection.PeerCertificateFingerprint;

    public static async ValueTask<DccChatTransport> ConnectAsync(
        string address,
        int port,
        CancellationToken cancellationToken,
        bool secure = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        IDccTransportConnection connection = secure
            ? await TlsDccTransportConnection.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false)
            : await TcpDccTransportConnection.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
        return new DccChatTransport(connection);
    }

    internal static DccChatTransport FromAcceptedClient(TcpClient client) =>
        new(TcpDccTransportConnection.FromAcceptedClient(client));

    internal static async ValueTask<DccChatTransport> FromAcceptedClientAsync(
        TcpClient client,
        bool secure,
        CancellationToken cancellationToken)
    {
        if (!secure) return FromAcceptedClient(client);
        return new DccChatTransport(
            await TlsDccTransportConnection.FromAcceptedClientAsync(client, cancellationToken).ConfigureAwait(false));
    }

    public async IAsyncEnumerable<string> ReadLinesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var readBuffer = new byte[1024];
        var pending = new List<byte>();
        while (true)
        {
            var read = await _stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (pending.Count > 0) yield return DecodeLine(pending);
                yield break;
            }

            for (var index = 0; index < read; index++)
            {
                var value = readBuffer[index];
                if (value == (byte)'\n')
                {
                    yield return DecodeLine(pending);
                    pending.Clear();
                    continue;
                }
                if (pending.Count >= MaximumLineBytes)
                {
                    throw new InvalidDataException($"DCC CHAT line exceeds {MaximumLineBytes} bytes.");
                }
                pending.Add(value);
            }
        }
    }

    public async ValueTask SendLineAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("DCC CHAT text must be one line.", nameof(text));
        }
        var encoded = IrcTextEncoding.Encode(text + "\r\n");
        if (encoded.Length > MaximumLineBytes)
        {
            throw new ArgumentException($"DCC CHAT text cannot exceed {MaximumLineBytes - 2} encoded bytes.", nameof(text));
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string DecodeLine(List<byte> bytes)
    {
        var count = bytes.Count > 0 && bytes[^1] == (byte)'\r' ? bytes.Count - 1 : bytes.Count;
        return IrcTextEncoding.Decode(bytes.ToArray().AsSpan(0, count));
    }
}

public sealed class DccChatListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly bool _secure;
    private int _disposed;

    private DccChatListener(TcpListener listener, bool secure)
    {
        _listener = listener;
        _secure = secure;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static DccChatListener Start() => Start(IPAddress.Any, DccPortRange.Random, false);

    public static DccChatListener Start(DccPortRange ports) => Start(IPAddress.Any, ports, false);

    public static DccChatListener Start(
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
                return new DccChatListener(listener, secure);
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

    public async ValueTask<DccChatTransport> AcceptAsync(CancellationToken cancellationToken = default)
    {
        var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DccChatTransport.FromAcceptedClientAsync(client, _secure, cancellationToken)
                .ConfigureAwait(false);
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

public readonly record struct DccPortRange(int First, int Last)
{
    public static DccPortRange Random { get; } = new(0, 0);

    public IEnumerable<int> Ports()
    {
        if (First == 0 && Last == 0)
        {
            yield return 0;
            yield break;
        }
        if (First is < 1 or > 65535 || Last < First || Last > 65535)
        {
            throw new InvalidOperationException("The DCC port range is invalid.");
        }
        for (var port = First; port <= Last; port++) yield return port;
    }

    public static bool TryParse(string value, out DccPortRange range)
    {
        range = Random;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Equals("random", StringComparison.OrdinalIgnoreCase)) return true;
        var separator = value.IndexOf('-');
        if (separator < 0)
        {
            if (!int.TryParse(value, out var port) || port is < 1 or > 65535) return false;
            range = new DccPortRange(port, port);
            return true;
        }
        if (!int.TryParse(value[..separator], out var first) ||
            !int.TryParse(value[(separator + 1)..], out var last) ||
            first is < 1 or > 65535 || last < first || last > 65535)
        {
            return false;
        }
        range = new DccPortRange(first, last);
        return true;
    }

    public override string ToString() => First == 0
        ? "random"
        : First == Last ? First.ToString() : $"{First}-{Last}";
}

public static class DccAddressSelector
{
    public static async ValueTask<IPAddress> SelectAdvertisedAddressAsync(
        string configuredAddress,
        string? visibleHost,
        string remoteHost,
        int remotePort,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredAddress);
        if (!configuredAddress.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return await ResolveAddressAsync(configuredAddress, requirePublic: false, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"dcc.address '{configuredAddress}' does not resolve to an IPv4 or IPv6 address.");
        }

        IPAddress? visibleIPv4 = null;
        if (!string.IsNullOrWhiteSpace(visibleHost))
        {
            var visibleAddresses = await ResolveAddressesAsync(visibleHost, requirePublic: true, cancellationToken)
                .ConfigureAwait(false);
            var visibleIPv6 = visibleAddresses.FirstOrDefault(IsGlobalIPv6);
            if (visibleIPv6 is not null) return visibleIPv6;
            visibleIPv4 = visibleAddresses.FirstOrDefault(IsPublicIPv4);
        }

        var localIPv6 = SelectLocalGlobalIPv6();
        if (localIPv6 is not null) return localIPv6;
        if (visibleIPv4 is not null) return visibleIPv4;

        var localAddress = await SelectLocalIPv4Async(remoteHost, remotePort, cancellationToken).ConfigureAwait(false);
        if (IsPublicIPv4(localAddress)) return localAddress;
        throw new InvalidOperationException(
            $"clircs could only determine the private IPv4 address {localAddress} and no public IPv6 address. " +
            "Set dcc.address to a reachable public address and configure dcc.ports to a forwarded port or range, or use passive DCC.");
    }

    public static async ValueTask<IPAddress> SelectLocalIPv4Async(
        string remoteHost,
        int remotePort,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(remoteHost, cancellationToken).ConfigureAwait(false);
            foreach (var remote in addresses.Where(address => address.AddressFamily == AddressFamily.InterNetwork))
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(remote, remotePort);
                if (socket.LocalEndPoint is IPEndPoint local && !IPAddress.Any.Equals(local.Address))
                {
                    return local.Address;
                }
            }
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
        }

        var fallback = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up &&
                network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address));
        return fallback ?? IPAddress.Loopback;
    }

    public static uint ToDccInteger(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var bytes = address.MapToIPv4().GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    public static string ToDccAddressToken(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => ToDccInteger(address).ToString(System.Globalization.CultureInfo.InvariantCulture),
            AddressFamily.InterNetworkV6 => address.ToString(),
            _ => throw new ArgumentException("The DCC address must be IPv4 or IPv6.", nameof(address))
        };
    }

    public static bool IsPublicIPv4(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] != 0 &&
            bytes[0] != 10 &&
            bytes[0] != 127 &&
            !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
            !(bytes[0] == 169 && bytes[1] == 254) &&
            !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
            !(bytes[0] == 192 && bytes[1] == 168) &&
            !(bytes[0] == 198 && bytes[1] is 18 or 19) &&
            bytes[0] < 224;
    }

    public static bool IsGlobalIPv6(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetworkV6 ||
            IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6Multicast ||
            address.IsIPv6SiteLocal || address.IsIPv4MappedToIPv6 || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.IPv6None))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return (bytes[0] & 0xfe) != 0xfc;
    }

    private static IPAddress? SelectLocalGlobalIPv6() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(network => network.OperationalStatus == OperationalStatus.Up &&
            network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(network => network.GetIPProperties().UnicastAddresses)
        .Select(address => address.Address)
        .FirstOrDefault(IsGlobalIPv6);

    private static async ValueTask<IPAddress?> ResolveAddressAsync(
        string host,
        bool requirePublic,
        CancellationToken cancellationToken)
    {
        var addresses = await ResolveAddressesAsync(host, requirePublic, cancellationToken).ConfigureAwait(false);
        return addresses
            .OrderByDescending(address => address.AddressFamily == AddressFamily.InterNetworkV6)
            .FirstOrDefault();
    }

    private static async ValueTask<IReadOnlyList<IPAddress>> ResolveAddressesAsync(
        string host,
        bool requirePublic,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return IsUsable(literal, requirePublic) ? [literal] : [];
        }
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            return addresses
                .Where(address => IsUsable(address, requirePublic))
                .ToArray();
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            return [];
        }
    }

    private static bool IsUsable(IPAddress address, bool requirePublic) => address.AddressFamily switch
    {
        AddressFamily.InterNetwork => !IPAddress.Any.Equals(address) && !IPAddress.Broadcast.Equals(address) &&
            (!requirePublic || IsPublicIPv4(address)),
        AddressFamily.InterNetworkV6 => !address.Equals(IPAddress.IPv6Any) && !address.Equals(IPAddress.IPv6None) &&
            (!requirePublic || IsGlobalIPv6(address)),
        _ => false
    };
}
