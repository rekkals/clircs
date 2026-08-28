using System.Text;
using System.Text.Json;
using Clircs.Networking;

namespace Clircs.Infrastructure;

public sealed record TrustedCertificatePin(
    string Host,
    int Port,
    string Sha256Fingerprint,
    string Subject,
    DateTimeOffset TrustedAtUtc,
    string? Issuer = null,
    DateTimeOffset? ValidFromUtc = null,
    DateTimeOffset? ValidUntilUtc = null);

public sealed class TrustedCertificateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly DurableFileWriter _files;
    private List<TrustedCertificatePin> _pins = [];

    public TrustedCertificateStore(string path, DurableFileWriter? files = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        _files = files ?? DurableFileWriter.Shared;
        Load();
    }

    public string Path => _path;

    public string? LoadError { get; private set; }

    public IReadOnlyList<TrustedCertificatePin> Entries
    {
        get
        {
            lock (_gate)
            {
                return _pins.OrderBy(pin => pin.Host, StringComparer.OrdinalIgnoreCase).ThenBy(pin => pin.Port).ToArray();
            }
        }
    }

    public bool IsTrusted(TlsCertificateInfo certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.IsCurrentlyValid)
        {
            return false;
        }

        lock (_gate)
        {
            return _pins.Any(pin =>
                EndpointEquals(pin, certificate.Endpoint) &&
                string.Equals(
                    TlsCertificateInfo.NormalizeFingerprint(pin.Sha256Fingerprint),
                    certificate.Sha256Fingerprint,
                    StringComparison.Ordinal));
        }
    }

    public TrustedCertificatePin? FindForEndpoint(IrcEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        lock (_gate)
        {
            return _pins.FirstOrDefault(pin => EndpointEquals(pin, endpoint));
        }
    }

    public void AddOrReplace(TlsCertificateInfo certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.IsCurrentlyValid)
        {
            throw new InvalidOperationException("A certificate outside its validity period cannot be remembered.");
        }

        lock (_gate)
        {
            EnsureWritable();
            var candidate = new List<TrustedCertificatePin>(_pins);
            candidate.RemoveAll(pin => EndpointEquals(pin, certificate.Endpoint));
            candidate.Add(new TrustedCertificatePin(
                certificate.Endpoint.Host,
                certificate.Endpoint.Port,
                certificate.Sha256Fingerprint,
                certificate.Subject,
                DateTimeOffset.UtcNow,
                certificate.Issuer,
                certificate.ValidFrom,
                certificate.ValidUntil));
            Save(candidate);
            _pins = candidate;
        }
    }

    public bool Remove(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        lock (_gate)
        {
            EnsureWritable();
            var candidate = new List<TrustedCertificatePin>(_pins);
            var removed = candidate.RemoveAll(pin =>
                pin.Port == port && string.Equals(pin.Host, host, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                Save(candidate);
                _pins = candidate;
            }

            return removed;
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_path, Encoding.UTF8);
            var pins = JsonSerializer.Deserialize<List<TrustedCertificatePin>>(json, JsonOptions) ?? [];
            foreach (var pin in pins)
            {
                if (string.IsNullOrWhiteSpace(pin.Host) || pin.Port is < 1 or > 65535)
                {
                    throw new InvalidDataException("The trusted-certificate file contains an invalid endpoint.");
                }

                TlsCertificateInfo.NormalizeFingerprint(pin.Sha256Fingerprint);
            }

            _pins = pins;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            LoadError = $"Could not read trusted certificate pins from '{_path}': {exception.Message}";
            _pins = [];
        }
    }

    private void Save(IReadOnlyList<TrustedCertificatePin> pins)
    {
        _files.WriteText(_path, JsonSerializer.Serialize(pins, JsonOptions), retainBackup: true, new UTF8Encoding(false));
    }

    private void EnsureWritable()
    {
        if (LoadError is not null)
        {
            throw new InvalidOperationException(
                $"{LoadError} The file was preserved and must be repaired or removed before pins can be changed.");
        }
    }

    private static bool EndpointEquals(TrustedCertificatePin pin, IrcEndpoint endpoint) =>
        pin.Port == endpoint.Port && string.Equals(pin.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase);
}
