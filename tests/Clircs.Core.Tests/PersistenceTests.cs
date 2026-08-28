using Clircs.Identity;
using Clircs.Infrastructure;
using Clircs.Networking;

namespace Clircs.Core.Tests;

internal static class PersistenceTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("failed profile commits do not change live state", FailedProfileCommitRollsBack);
        suite.Add("failed certificate commits do not change trust state", FailedCertificateCommitRollsBack);
        suite.Add("durable replacement retains the previous complete file", DurableReplacementRetainsBackup);
    }

    private static void FailedProfileCommitRollsBack()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "networks.toml");
        var writer = new DurableFileWriter(_ => throw new IOException("simulated interruption"));
        var store = new NetworkProfileStore(path, writer);
        var profile = new NetworkProfile(
            NetworkProfileId.New(), "TestNet", [new IrcEndpoint("irc.example.test", 6697, true)],
            new IrcIdentity(["tester"], "tester", "Test User"));

        Assert.Throws<IOException>(() => store.Add(profile));
        Assert.Equal(0, store.Entries.Count);
        Assert.False(File.Exists(path));
        Assert.Equal(0, Directory.GetFiles(directory.Path, "*.tmp").Length);
    }

    private static void FailedCertificateCommitRollsBack()
    {
        using var directory = new TemporaryDirectory();
        var writer = new DurableFileWriter(_ => throw new IOException("simulated interruption"));
        var store = new TrustedCertificateStore(Path.Combine(directory.Path, "pins.json"), writer);
        var certificate = new TlsCertificateInfo(
            new IrcEndpoint("irc.example.test", 6697, true), "CN=test", "CN=test",
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1),
            new string('A', 64), TlsCertificateProblems.ChainErrors, ["UntrustedRoot"]);

        Assert.Throws<IOException>(() => store.AddOrReplace(certificate));
        Assert.False(store.IsTrusted(certificate));
        Assert.Equal(0, store.Entries.Count);
    }

    private static void DurableReplacementRetainsBackup()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var writer = new DurableFileWriter();
        writer.WriteText(path, "first");
        writer.WriteText(path, "second");

        Assert.Equal("second", File.ReadAllText(path));
        Assert.Equal("first", File.ReadAllText(path + ".bak"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"clircs-persistence-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
