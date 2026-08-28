using Clircs.Infrastructure;
using Clircs.Networking;

namespace Clircs.Core.Tests;

internal static class TrustedCertificateStoreTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("certificate pins persist and remain endpoint-scoped", PinsPersistAndRemainScoped);
        suite.Add("expired certificates cannot be pinned or silently trusted", ExpiredPinsAreRejected);
        suite.Add("a damaged trust file is preserved instead of overwritten", DamagedStoreIsPreserved);
    }

    private static void PinsPersistAndRemainScoped()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = System.IO.Path.Combine(directory, "trusted-certificates.json");
            var certificate = Certificate("irc.example.test", 6697, 'A');
            var store = new TrustedCertificateStore(path);
            store.AddOrReplace(certificate);

            var reloaded = new TrustedCertificateStore(path);
            Assert.True(reloaded.IsTrusted(certificate));
            Assert.Equal("CN=test", reloaded.Entries[0].Issuer!);
            Assert.True(reloaded.Entries[0].ValidFromUtc is not null);
            Assert.True(reloaded.Entries[0].ValidUntilUtc is not null);
            Assert.False(reloaded.IsTrusted(Certificate("other.example.test", 6697, 'A')));
            Assert.False(reloaded.IsTrusted(Certificate("irc.example.test", 7000, 'A')));
            Assert.False(reloaded.IsTrusted(Certificate("irc.example.test", 6697, 'B')));
            Assert.True(reloaded.Remove("irc.example.test", 6697));
            Assert.False(new TrustedCertificateStore(path).IsTrusted(certificate));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void ExpiredPinsAreRejected()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new TrustedCertificateStore(System.IO.Path.Combine(directory, "pins.json"));
            var expired = new TlsCertificateInfo(
                new IrcEndpoint("irc.example.test", 6697, true),
                "CN=test",
                "CN=test",
                DateTimeOffset.UtcNow.AddDays(-30),
                DateTimeOffset.UtcNow.AddDays(-1),
                new string('C', 64),
                TlsCertificateProblems.ChainErrors,
                ["UntrustedRoot"]);
            Assert.False(store.IsTrusted(expired));
            Assert.Throws<InvalidOperationException>(() => store.AddOrReplace(expired));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void DamagedStoreIsPreserved()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = System.IO.Path.Combine(directory, "pins.json");
            File.WriteAllText(path, "{not valid json");
            var store = new TrustedCertificateStore(path);
            Assert.True(store.LoadError is not null);
            Assert.Throws<InvalidOperationException>(() => store.AddOrReplace(Certificate("irc.example.test", 6697, 'D')));
            Assert.Equal("{not valid json", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static TlsCertificateInfo Certificate(string host, int port, char fingerprintCharacter) =>
        new(
            new IrcEndpoint(host, port, true),
            "CN=test",
            "CN=test",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30),
            new string(fingerprintCharacter, 64),
            TlsCertificateProblems.ChainErrors,
            ["UntrustedRoot"]);

    private static string CreateTemporaryDirectory()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"clirc-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
