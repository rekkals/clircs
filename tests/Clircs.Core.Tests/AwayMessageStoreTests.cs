using Clircs.ConsoleClient;

namespace Clircs.Core.Tests;

internal static class AwayMessageStoreTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("away messages persist with inbox-scoped numbering", PersistsInboxScopedNumbers);
        suite.Add("away messages can be read and deleted by sender", ReadsAndDeletesBySender);
        suite.Add("temporary away inboxes stay in memory", TemporaryInboxesStayInMemory);
    }

    private static void PersistsInboxScopedNumbers()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "away-messages.json");
        var store = new AwayMessageStore(path);
        store.Add(Message("profile:one", "EFnet", "Alice", "one"));
        store.Add(Message("profile:one", "EFnet", "Alice", "two"));
        store.Add(Message("profile:two", "EFnetAlt", "Alice", "other one"));
        store.Add(Message("profile:two", "EFnetAlt", "Alice", "other two"));
        Assert.Equal(1, store.Delete("profile:two", "ALICE", 2));

        var reloaded = new AwayMessageStore(path);
        var first = reloaded.ForInbox("profile:one");
        var second = reloaded.ForInbox("profile:two");
        Assert.Equal(2, first.Count);
        Assert.Equal("Alice", first[0].Nickname);
        Assert.Equal("one", first[0].Text);
        Assert.Equal(1L, first[0].Number);
        Assert.Equal(2L, first[1].Number);
        Assert.Equal(1, second.Count);
        Assert.Equal("other one", second[0].Text);
        Assert.Equal(1L, second[0].Number);

        reloaded.Add(Message("profile:two", "EFnetAlt", "alice", "other three"));
        Assert.Equal(3L, reloaded.ForInbox("profile:two").Last().Number);
        Assert.Equal("other one", reloaded.Read("profile:two", "ALICE", 1)!.Text);
        Assert.False(reloaded.ForInbox("profile:one")[0].Read);
        Assert.True(new AwayMessageStore(path).ForInbox("profile:two")[0].Read);
    }

    private static void ReadsAndDeletesBySender()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "away-messages.json");
        var store = new AwayMessageStore(path);
        const string inboxKey = "profile:one";
        store.Add(Message(inboxKey, "EFnet", "Alice", "one"));
        store.Add(Message(inboxKey, "EFnet", "Alice", "two"));
        store.Add(Message(inboxKey, "EFnet", "Bob", "three"));

        var read = store.Read(inboxKey, "ALICE", 2)!;
        Assert.Equal("two", read.Text);
        Assert.True(read.Read);
        var entries = store.ForInbox(inboxKey);
        Assert.False(entries[0].Read);
        Assert.False(entries[2].Read);
        Assert.Equal(1L, entries[2].Number);
        Assert.True(store.Read(inboxKey, "Bob", 2) is null);
        Assert.Equal(0, store.Delete(inboxKey, "Bob", 2));

        Assert.Equal(1, store.Delete(inboxKey, "alice", 2));
        Assert.True(store.Read(inboxKey, "Alice", 2) is null);
        store.Add(Message(inboxKey, "EFnet", "alice", "four"));
        Assert.Equal(3L, store.ForInbox(inboxKey).Last().Number);
        Assert.True(store.Read(inboxKey, "alice", 1)!.Read);
        Assert.True(store.Read(inboxKey, "alice", 3)!.Read);
        Assert.True(store.ForInbox(inboxKey)
            .Where(entry => entry.Nickname.Equals("Alice", StringComparison.OrdinalIgnoreCase))
            .All(entry => entry.Read));
        Assert.Equal(2, store.Delete(inboxKey, "ALICE"));
        Assert.Equal(1, store.ForInbox(inboxKey).Count);

        store.Add(Message(inboxKey, "EFnet", "Alice", "reset"));
        Assert.Equal(1L, store.ForInbox(inboxKey).Last().Number);
        Assert.Equal(1, store.Delete(inboxKey, "Alice", 1));
        Assert.Equal(1, store.Delete(inboxKey));
        Assert.Equal(0, store.ForInbox(inboxKey).Count);

        var reloaded = new AwayMessageStore(path);
        reloaded.Add(Message(inboxKey, "EFnet", "Bob", "after clear"));
        Assert.Equal(1L, reloaded.ForInbox(inboxKey).Single().Number);
    }

    private static void TemporaryInboxesStayInMemory()
    {
        var store = new AwayMessageStore();
        store.Add(Message("session:one", "EFnet", "Alice", "temporary"));

        Assert.Equal(1L, store.ForInbox("session:one").Single().Number);
        Assert.Equal(0, store.ForInbox("session:two").Count);
        Assert.Equal(0, new AwayMessageStore().ForInbox("session:one").Count);
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
