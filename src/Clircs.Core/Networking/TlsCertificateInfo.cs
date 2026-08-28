using System.Collections.ObjectModel;

namespace Clircs.Networking;

[Flags]
public enum TlsCertificateProblems
{
    None = 0,
    CertificateNotAvailable = 1,
    NameMismatch = 2,
    ChainErrors = 4
}

public enum TlsCertificateDecision
{
    Reject,
    Accept
}

public sealed class TlsCertificateInfo
{
    public TlsCertificateInfo(
        IrcEndpoint endpoint,
        string subject,
        string issuer,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        string sha256Fingerprint,
        TlsCertificateProblems problems,
        IEnumerable<string> chainErrors)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256Fingerprint);
        ArgumentNullException.ThrowIfNull(chainErrors);
        Subject = subject;
        Issuer = issuer;
        ValidFrom = validFrom;
        ValidUntil = validUntil;
        Sha256Fingerprint = NormalizeFingerprint(sha256Fingerprint);
        Problems = problems;
        ChainErrors = new ReadOnlyCollection<string>(chainErrors.ToArray());
    }

    public IrcEndpoint Endpoint { get; }

    public string Subject { get; }

    public string Issuer { get; }

    public DateTimeOffset ValidFrom { get; }

    public DateTimeOffset ValidUntil { get; }

    public string Sha256Fingerprint { get; }

    public TlsCertificateProblems Problems { get; }

    public IReadOnlyList<string> ChainErrors { get; }

    public bool IsCurrentlyValid
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            return now >= ValidFrom.ToUniversalTime() && now <= ValidUntil.ToUniversalTime();
        }
    }

    public string DisplayFingerprint => string.Join(':', Enumerable.Range(0, Sha256Fingerprint.Length / 2)
        .Select(index => Sha256Fingerprint.Substring(index * 2, 2)));

    public static string NormalizeFingerprint(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        var normalized = new string(fingerprint.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (normalized.Length != 64)
        {
            throw new ArgumentException("A SHA-256 certificate fingerprint must contain 64 hexadecimal characters.", nameof(fingerprint));
        }

        return normalized;
    }
}

public interface ITlsCertificatePolicy
{
    TlsCertificateDecision Decide(TlsCertificateInfo certificate);
}
