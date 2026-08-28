using System.IO.Compression;
using Clircs.ConsoleClient;

namespace Clircs.Core.Tests;

internal static class BackupTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("backup snapshots user data without recursively backing up backups", CreatesCompleteSnapshot);
    }

    private static void CreatesCompleteSnapshot()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, "appearance.json"), "settings");
        Directory.CreateDirectory(Path.Combine(temporary.Path, "users"));
        File.WriteAllText(Path.Combine(temporary.Path, "users", "efnet.json"), "users");
        File.WriteAllText(Path.Combine(temporary.Path, "unfinished.tmp"), "ignore");
        Directory.CreateDirectory(Path.Combine(temporary.Path, "backups"));
        File.WriteAllText(Path.Combine(temporary.Path, "backups", "old.zip"), "ignore");
        Directory.CreateDirectory(Path.Combine(temporary.Path, "logs"));
        File.WriteAllText(Path.Combine(temporary.Path, "logs", "chat.log"), "ignore");

        var manager = new BackupManager(
            temporary.Path,
            () => new DateTimeOffset(2026, 7, 28, 12, 34, 56, TimeSpan.Zero));
        var created = manager.Create();
        Assert.True(File.Exists(created));

        using var archive = ZipFile.OpenRead(created);
        var entries = archive.Entries.Select(entry => entry.FullName).Order().ToArray();
        Assert.Equal("appearance.json,users/efnet.json", string.Join(',', entries));
        Assert.Equal(1, manager.List().Count);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"clircs-backup-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
