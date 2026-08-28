using Clircs.ConsoleClient;
using Clircs.Identity;
using Clircs.Infrastructure;
using Clircs.Networking;
using Clircs.Users;

namespace Clircs.Core.Tests;

internal static class NetworkProfileStoreTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("network profiles round-trip stable identity and scoped settings", ProfilesRoundTrip);
        suite.Add("adding a server endpoint preserves logical-network settings", AddingEndpointPreservesNetworkSettings);
        suite.Add("removing a bouncer endpoint preserves logical-network settings", RemovingEndpointPreservesNetworkSettings);
        suite.Add("legacy reconnect defaults migrate to 99 attempts", LegacyReconnectDefaultMigrates);
        suite.Add("changing profile identity preserves network settings", ChangingIdentityPreservesNetworkSettings);
        suite.Add("unconfigured network profiles round-trip and accept a later endpoint", UnconfiguredProfilesRoundTrip);
        suite.Add("network profile names are unique without regard to case", ProfileNamesAreUnique);
        suite.Add("a damaged network profile file is preserved", DamagedProfileFileIsPreserved);
        suite.Add("SASL profile policy round-trips without storing its password", SaslPolicyRoundTripsWithoutPassword);
        suite.Add("SASL EXTERNAL profile settings round-trip without storing its certificate password", SaslExternalSettingsRoundTrip);
        suite.Add("SASL passwords are protected by the current Windows user", SaslPasswordsUseWindowsProtection);
        suite.Add("network profile presentation gives each server its own row", NetworkProfilePresentationUsesServerRows);
        suite.Add("adduser usage describes hostmask and nickname shorthand", AddUserUsageIsConcise);
    }

    private static void NetworkProfilePresentationUsesServerRows()
    {
        var configured = new NetworkProfile(
            NetworkProfileId.New(),
            "EFnet",
            [
                new IrcEndpoint("irc1.example.test", 6697, true),
                new IrcEndpoint("irc2.example.test", 6667, false)
            ],
            new IrcIdentity(["TestNick"], "test", "Test User"));
        var unconfigured = new NetworkProfile(
            NetworkProfileId.New(),
            "FutureNet",
            [],
            new IrcIdentity(["OtherNick"], "other", "Other User"));

        var rows = ClientApplication.NetworkProfileRows([configured, unconfigured]);

        Assert.Equal(3, rows.Count);
        Assert.Equal("EFnet", rows[0][0]);
        Assert.Equal("irc1.example.test:6697 (TLS)", rows[0][1]);
        Assert.Equal("TestNick", rows[0][2]);
        Assert.Equal(string.Empty, rows[1][0]);
        Assert.Equal("irc2.example.test:6667", rows[1][1]);
        Assert.Equal(string.Empty, rows[1][2]);
        Assert.Equal("FutureNet", rows[2][0]);
        Assert.Equal("[no server configured]", rows[2][1]);
    }

    private static void AddUserUsageIsConcise()
    {
        Assert.Equal("Usage: /adduser <handle> [hostmask|nickname]", ClientApplication.AddUserUsage(UserRole.None));
        Assert.Equal("Usage: /addbot <handle> [hostmask|nickname]", ClientApplication.AddUserUsage(UserRole.Bot));
    }

    private static void ChangingIdentityPreservesNetworkSettings()
    {
        var profile = new NetworkProfile(
            NetworkProfileId.New(),
            "EFnet",
            [new IrcEndpoint("irc.example.test", 6697, true)],
            new IrcIdentity(["OldNick"], "olduser", "Old Name"),
            ["#clircs"],
            networkName: "EFnet",
            notifyNicknames: ["Friend"],
            userModes: "+iw");
        var updated = profile.WithIdentity(new IrcIdentity(["NewNick", "NewNick_"], "newuser", "New Name"));

        Assert.Equal(profile.Id, updated.Id);
        Assert.Equal("NewNick", updated.Identity.Nicknames[0]);
        Assert.Equal("NewNick_", updated.Identity.Nicknames[1]);
        Assert.Equal("newuser", updated.Identity.Username);
        Assert.Equal("#clircs", updated.AutojoinChannels[0]);
        Assert.Equal("Friend", updated.NotifyNicknames[0]);
        Assert.Equal("+iw", updated.UserModes);
    }

    private static void UnconfiguredProfilesRoundTrip()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = System.IO.Path.Combine(directory, "networks.toml");
            var profile = new NetworkProfile(
                NetworkProfileId.New(),
                "FutureNet",
                [],
                new IrcIdentity(["TestNick"], "test", "Test User"));
            var store = new NetworkProfileStore(path);
            store.Add(profile);

            var reloaded = new NetworkProfileStore(path);
            var dormant = reloaded.Find("futurenet")!;
            Assert.False(dormant.IsConfigured);
            Assert.Throws<InvalidOperationException>(() => dormant.CreateConnectionOptions());

            var configured = dormant.WithEndpoint(new IrcEndpoint("irc.example.test", 6697, true));
            reloaded.Replace(configured);
            Assert.True(new NetworkProfileStore(path).Find("FutureNet")!.IsConfigured);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AddingEndpointPreservesNetworkSettings()
    {
        var profile = new NetworkProfile(
            NetworkProfileId.New(),
            "EFnet",
            [new IrcEndpoint("irc1.example.test", 6697, true)],
            new IrcIdentity(["TestNick"], "test", "Test User"),
            ["#shared"],
            networkName: "EFnet");
        var updated = profile.WithEndpoint(new IrcEndpoint("irc2.example.test", 6667, false));

        Assert.Equal(profile.Id, updated.Id);
        Assert.Equal("EFnet", updated.NetworkName!);
        Assert.Equal("#shared", updated.AutojoinChannels[0]);
        Assert.Equal(2, updated.Endpoints.Count);
        Assert.Equal("irc2.example.test", updated.Endpoints[1].Host);
    }

    private static void RemovingEndpointPreservesNetworkSettings()
    {
        var server = new IrcEndpoint("irc.example.test", 6697, true);
        var bouncer = new IrcEndpoint("znc.example.test", 6697, true);
        var profile = new NetworkProfile(
            NetworkProfileId.New(),
            "EFnet",
            [server, bouncer],
            new IrcIdentity(["TestNick"], "test", "Test User"),
            ["#shared"],
            networkName: "EFnet");

        var updated = profile.WithoutEndpoint(bouncer);

        Assert.Equal(1, updated.Endpoints.Count);
        Assert.Equal("irc.example.test", updated.Endpoints[0].Host);
        Assert.Equal("#shared", updated.AutojoinChannels[0]);
        Assert.Equal("EFnet", updated.NetworkName!);
    }

    private static void LegacyReconnectDefaultMigrates()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = System.IO.Path.Combine(directory, "networks.toml");
            var profile = new NetworkProfile(
                NetworkProfileId.New(),
                "EFnet",
                [new IrcEndpoint("irc.example.test", 6697, true)],
                new IrcIdentity(["TestNick"], "test", "Test User"),
                reconnect: new ReconnectPolicy(8, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(120)));
            var store = new NetworkProfileStore(path);
            store.Add(profile);

            var migrated = new NetworkProfileStore(path).Find("EFnet")!;
            Assert.Equal(99, migrated.Reconnect.MaximumAttempts);
            Assert.Equal(99, ReconnectPolicy.Default.MaximumAttempts);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void ProfilesRoundTrip()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = System.IO.Path.Combine(directory, "networks.toml");
            var id = NetworkProfileId.New();
            var profile = new NetworkProfile(
                id,
                "TestNet",
                [new IrcEndpoint("irc1.example.test", 6697, true), new IrcEndpoint("irc2.example.test", 6667, false)],
                new IrcIdentity(["TestNick", "TestNick_"], "test", "Test User"),
                ["#one", "#two"],
                new ReconnectPolicy(5, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(90)),
                "CanonicalNet",
                ["Alice", "Bob"],
                "+iw");
            var store = new NetworkProfileStore(path);
            store.Add(profile);

            var reloaded = new NetworkProfileStore(path);
            var saved = reloaded.Find("testnet");
            Assert.True(saved is not null);
            Assert.Equal(id, saved!.Id);
            Assert.Equal(2, saved.Endpoints.Count);
            Assert.Equal("irc2.example.test", saved.Endpoints[1].Host);
            Assert.Equal("TestNick_", saved.Identity.Nicknames[1]);
            Assert.Equal("#two", saved.AutojoinChannels[1]);
            Assert.Equal(5, saved.Reconnect.MaximumAttempts);
            Assert.Equal("CanonicalNet", saved.NetworkName!);
            Assert.Equal("Bob", saved.NotifyNicknames[1]);
            Assert.Equal("+iw", saved.UserModes);
            Assert.True(File.ReadAllText(path).Contains("user_modes = ", StringComparison.Ordinal));
            Assert.True(File.ReadAllText(path).Contains("[[network]]", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void ProfileNamesAreUnique()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new NetworkProfileStore(System.IO.Path.Combine(directory, "networks.toml"));
            store.Add(Profile("EFnet"));
            Assert.Throws<InvalidOperationException>(() => store.Add(Profile("efNET")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void DamagedProfileFileIsPreserved()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = System.IO.Path.Combine(directory, "networks.toml");
            File.WriteAllText(path, "version = nope\n[[network]]\n");
            var store = new NetworkProfileStore(path);
            Assert.True(store.LoadError is not null);
            Assert.Throws<InvalidOperationException>(() => store.Add(Profile("TestNet")));
            Assert.Equal("version = nope\n[[network]]\n", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void SaslPolicyRoundTripsWithoutPassword()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = System.IO.Path.Combine(directory, "networks.toml");
            var profile = new NetworkProfile(
                NetworkProfileId.New(),
                "Libera.Chat",
                [new IrcEndpoint("irc.libera.chat", 6697, true)],
                new IrcIdentity(["TestNick"], "test", "Test User"),
                sasl: new SaslProfileSettings("services-account", Required: true));
            var store = new NetworkProfileStore(path);
            store.Add(profile);

            var text = File.ReadAllText(path);
            var reloaded = new NetworkProfileStore(path).Find("Libera.Chat")!;
            Assert.Equal("services-account", reloaded.Sasl!.Username);
            Assert.True(reloaded.Sasl.Required);
            Assert.True(text.Contains("sasl_mechanism = \"PLAIN\"", StringComparison.Ordinal));
            Assert.True(text.Contains("sasl_required = true", StringComparison.Ordinal));
            Assert.False(text.Contains("password", StringComparison.OrdinalIgnoreCase));
            Assert.Throws<ArgumentException>(() => profile.WithEndpoint(
                new IrcEndpoint("insecure.example", 6667, false)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void SaslPasswordsUseWindowsProtection()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new TestSkippedException("DPAPI credential protection is Windows-specific");
        }
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = System.IO.Path.Combine(directory, "network-secrets.json");
            var profileId = NetworkProfileId.New();
            var store = new NetworkCredentialStore(path);
            store.SetSaslSecret(profileId, "correct horse battery staple");

            Assert.True(store.HasSaslSecret(profileId));
            Assert.False(File.ReadAllText(path).Contains("correct horse", StringComparison.Ordinal));
            Assert.Equal("correct horse battery staple", new NetworkCredentialStore(path).GetSaslSecret(profileId)!);
            Assert.True(store.Remove(profileId));
            Assert.False(store.HasSaslSecret(profileId));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void SaslExternalSettingsRoundTrip()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = System.IO.Path.Combine(directory, "networks.toml");
            var certificatePath = System.IO.Path.Combine(directory, "irc-client.pfx");
            var profile = new NetworkProfile(
                NetworkProfileId.New(),
                "Libera.Chat",
                [new IrcEndpoint("irc.libera.chat", 6697, true)],
                new IrcIdentity(["TestNick"], "test", "Test User"),
                sasl: SaslProfileSettings.External(certificatePath, required: true));
            new NetworkProfileStore(path).Add(profile);

            var text = File.ReadAllText(path);
            var reloaded = new NetworkProfileStore(path).Find("Libera.Chat")!;
            Assert.Equal(SaslMechanisms.External, reloaded.Sasl!.Mechanism);
            Assert.Equal(certificatePath, reloaded.Sasl.ClientCertificatePath!);
            Assert.True(reloaded.Sasl.Required);
            Assert.False(text.Contains("certificate_password", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static NetworkProfile Profile(string name) => new(
        NetworkProfileId.New(),
        name,
        [new IrcEndpoint("irc.example.test", 6697, true)],
        new IrcIdentity(["TestNick"], "test", "Test User"));

    private static string CreateTemporaryDirectory()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"clirc-profile-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
