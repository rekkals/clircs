using Clircs.ConsoleClient;

namespace Clircs.Core.Tests;

internal static class AwayMessageStoreTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("away messages persist and remain network scoped", PersistsNetworkScopedMessages);
        suite.Add("away messages can be read and deleted by sender", ReadsAndDeletesBySender);
    }

    private static void PersistsNetworkScopedMessages()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "away-messages.json");
        var store = new AwayMessageStore(path);
        store.Add(Message("network:efnet", "EFnet", "Alice", "one"));
        store.Add(Message("network:dalnet", "DALnet", "Bob", "two"));

        var reloaded = new AwayMessageStore(path);
        var efnet = reloaded.ForNetwork("network:efnet");
        Assert.Equal(1, efnet.Count);
        Assert.Equal("Alice", efnet[0].Nickname);
        Assert.Equal("one", efnet[0].Text);
        Assert.Equal(1, reloaded.ForNetwork("network:dalnet").Count);
    }

    private static void ReadsAndDeletesBySender()
    {
        using var temporary = new TemporaryDirectory();
        var store = new AwayMessageStore(Path.Combine(temporary.Path, "away-messages.json"));
        store.Add(Message("network:efnet", "EFnet", "Alice", "one"));
        store.Add(Message("network:efnet", "EFnet", "Alice", "two"));
        store.Add(Message("network:efnet", "EFnet", "Bob", "three"));

        Assert.Equal(2, store.MarkRead("network:efnet", "alice"));
        Assert.True(store.ForNetwork("network:efnet").Where(entry => entry.Nickname == "Alice").All(entry => entry.Read));
        Assert.Equal(2, store.Delete("network:efnet", "ALICE"));
        Assert.Equal(1, store.ForNetwork("network:efnet").Count);
        Assert.Equal(1, store.Delete("network:efnet"));
        Assert.Equal(0, store.ForNetwork("network:efnet").Count);
    }

    private static AwayMessageEntry Message(string key, string network, string nick, string text) =>
        new(Guid.NewGuid(), key, network, nick, "user", "example.test", "message", text, DateTimeOffset.UtcNow, false);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clircs-away-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
