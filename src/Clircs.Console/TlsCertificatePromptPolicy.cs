using Clircs.Infrastructure;
using Clircs.Networking;
using Clircs.Sessions;

namespace Clircs.ConsoleClient;

internal sealed class TlsCertificatePromptPolicy : ITlsCertificatePolicy
{
    private readonly ConsolePresenter _presenter;
    private readonly TrustedCertificateStore _store;

    public TlsCertificatePromptPolicy(ConsolePresenter presenter, string? storePath = null)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        var path = storePath ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "clircs",
            "trusted-certificates.json");
        _store = new TrustedCertificateStore(path);
    }

    public IReadOnlyList<TrustedCertificatePin> Pins => _store.Entries;

    public string StorePath => _store.Path;

    public event Action<TlsCertificateNotice>? NoticeRaised;

    public TlsCertificateDecision Decide(TlsCertificateInfo certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (_store.IsTrusted(certificate))
        {
            Report(certificate.Endpoint, $"TLS: accepted pinned certificate for {certificate.Endpoint.Host}:{certificate.Endpoint.Port}.");
            return TlsCertificateDecision.Accept;
        }

        Report(certificate.Endpoint, "TLS CERTIFICATE WARNING", success: false);
        Report(certificate.Endpoint, $"Server: {certificate.Endpoint.Host}:{certificate.Endpoint.Port}", success: false);
        Report(certificate.Endpoint, $"Validation problem: {certificate.Problems}", success: false);
        foreach (var chainError in certificate.ChainErrors)
        {
            Report(certificate.Endpoint, $"Chain: {TerminalTextSanitizer.Sanitize(chainError)}", success: false);
        }

        Report(certificate.Endpoint, $"Subject: {TerminalTextSanitizer.Sanitize(certificate.Subject)}", success: false);
        Report(certificate.Endpoint, $"Issuer: {TerminalTextSanitizer.Sanitize(certificate.Issuer)}", success: false);
        Report(certificate.Endpoint, $"Valid from: {certificate.ValidFrom:u}", success: false);
        Report(certificate.Endpoint, $"Valid until: {certificate.ValidUntil:u}", success: false);
        Report(certificate.Endpoint, $"SHA-256: {certificate.DisplayFingerprint}", success: false);

        var existing = _store.FindForEndpoint(certificate.Endpoint);
        if (existing is not null && !string.Equals(
                TlsCertificateInfo.NormalizeFingerprint(existing.Sha256Fingerprint),
                certificate.Sha256Fingerprint,
                StringComparison.Ordinal))
        {
            Report(
                certificate.Endpoint,
                $"WARNING: this endpoint was pinned to a different certificate: {FormatFingerprint(existing.Sha256Fingerprint)}",
                success: false);
        }

        if (_store.LoadError is not null)
        {
            Report(certificate.Endpoint, _store.LoadError, success: false);
        }

        if (!certificate.IsCurrentlyValid)
        {
            Report(certificate.Endpoint, "This certificate is outside its validity period and cannot be remembered.", success: false);
            var expiredChoice = _presenter.ReadLine("Accept [o]nce or [r]eject (default)? ");
            return expiredChoice?.Trim().Equals("o", StringComparison.OrdinalIgnoreCase) == true
                ? TlsCertificateDecision.Accept
                : TlsCertificateDecision.Reject;
        }

        var choice = _presenter.ReadLine("Accept [o]nce, [a]lways for this exact server/certificate, or [r]eject (default)? ");
        if (choice?.Trim().Equals("o", StringComparison.OrdinalIgnoreCase) == true)
        {
            return TlsCertificateDecision.Accept;
        }

        if (choice?.Trim().Equals("a", StringComparison.OrdinalIgnoreCase) != true)
        {
            return TlsCertificateDecision.Reject;
        }

        try
        {
            _store.AddOrReplace(certificate);
            Report(certificate.Endpoint, $"Pinned this certificate for {certificate.Endpoint.Host}:{certificate.Endpoint.Port}.");
            return TlsCertificateDecision.Accept;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Report(certificate.Endpoint, $"Could not save the certificate pin: {exception.Message}", success: false);
            return TlsCertificateDecision.Reject;
        }
    }

    public bool Forget(string host, int port) => _store.Remove(host, port);

    private void Report(IrcEndpoint endpoint, string text, bool success = true)
    {
        var handler = NoticeRaised;
        if (handler is null)
        {
            _presenter.Result(text, success);
            return;
        }
        handler(new TlsCertificateNotice(endpoint, text, success));
    }

    private static string FormatFingerprint(string fingerprint)
    {
        var normalized = TlsCertificateInfo.NormalizeFingerprint(fingerprint);
        return string.Join(':', Enumerable.Range(0, normalized.Length / 2)
            .Select(index => normalized.Substring(index * 2, 2)));
    }
}

internal sealed record TlsCertificateNotice(IrcEndpoint Endpoint, string Text, bool Success);
