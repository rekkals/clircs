using System.IO.Compression;
using System.Text;
using Clircs.ConsoleClient;
using Clircs.Infrastructure;
using Clircs.Networking;
using Clircs.State;

namespace Clircs.Core.Tests;

internal static class BackupTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("backup includes user data and logs without temporary files or backups", CreatesCompleteSnapshot);
        suite.Add("backup preserves captured data while logging continues", PreservesCapturedDataAsync);
        suite.Add("backup rejects truncated logs and cleans up failed archives", RejectsTruncatedLog);
    }

    private static void CreatesCompleteSnapshot()
    {
        using var temporary = new TemporaryDirectory();
        var expected = new Dictionary<string, byte[]>
        {
            ["appearance.json"] = Encoding.UTF8.GetBytes("settings"),
            ["networks.toml"] = Encoding.UTF8.GetBytes("saved profiles"),
            ["network-secrets.json"] = Encoding.UTF8.GetBytes("encrypted credential data"),
            ["users/efnet.json"] = Encoding.UTF8.GetBytes("users"),
            ["logs/chat.log"] = Encoding.UTF8.GetBytes(
                string.Concat(Enumerable.Repeat("café こんにちは 😀\n", 6_000))),
            ["logs/session/irc.example.test_6667/status/2026-10-21.log"] =
                Encoding.UTF8.GetBytes("session log"),
            ["logs/application-errors.log"] = Encoding.UTF8.GetBytes("diagnostic log"),
            ["logs/empty.log"] = []
        };

        foreach (var file in expected)
        {
            var path = Path.Combine(temporary.Path, file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Value);
        }

        File.WriteAllText(Path.Combine(temporary.Path, "unfinished.tmp"), "ignore");
        File.WriteAllText(Path.Combine(temporary.Path, "logs", "unfinished.tmp"), "ignore");
        Directory.CreateDirectory(Path.Combine(temporary.Path, "backups"));
        File.WriteAllText(Path.Combine(temporary.Path, "backups", "old.zip"), "ignore");

        var manager = new BackupManager(
            temporary.Path + Path.DirectorySeparatorChar,
            () => new DateTimeOffset(2026, 10, 21, 12, 34, 56, TimeSpan.Zero));
        var created = manager.Create();
        Assert.True(File.Exists(created));

        using var archive = ZipFile.OpenRead(created);
        Assert.Equal(
            string.Join(',', expected.Keys.Order(StringComparer.Ordinal)),
            string.Join(',', archive.Entries.Select(entry => entry.FullName)
                .Order(StringComparer.Ordinal)));

        foreach (var file in expected)
        {
            Assert.True(ReadEntry(archive, file.Key).SequenceEqual(file.Value),
                $"Backup content differs for {file.Key}.");
        }

        Assert.Equal(1, manager.List().Count);
        Assert.Equal(2, Directory.EnumerateFileSystemEntries(manager.BackupDirectory).Count());
    }

    private static async ValueTask PreservesCapturedDataAsync()
    {
        using var temporary = new TemporaryDirectory();
        var settingsPath = Path.Combine(temporary.Path, "appearance.json");
        var logsDirectory = Path.Combine(temporary.Path, "logs");
        DurableFileWriter.Shared.WriteText(settingsPath, "before snapshot");

        await using var writer = new EventLogWriter(logsDirectory);
        var timestamp = new DateTimeOffset(2026, 10, 21, 12, 34, 56, TimeSpan.Zero);
        Assert.Equal(ResourceQueueWriteResult.Accepted, writer.Enqueue(
            "EFnet", BufferKind.Channel, "#clircs", timestamp,
            ["before: café こんにちは 😀"]));
        await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var manager = new BackupManager(
            temporary.Path,
            afterSnapshot: () =>
            {
                DurableFileWriter.Shared.WriteText(
                    settingsPath, "after snapshot", retainBackup: false);

                Assert.Equal(ResourceQueueWriteResult.Accepted, writer.Enqueue(
                    "EFnet", BufferKind.Channel, "#clircs", timestamp,
                    ["after snapshot"]));
                writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5))
                    .GetAwaiter().GetResult();

                DurableFileWriter.Shared.AppendText(
                    Path.Combine(logsDirectory, "later.log"), "new log");
                DurableFileWriter.Shared.WriteText(
                    Path.Combine(temporary.Path, "later.json"), "new settings");
            });

        var created = await Task.Run(manager.Create).WaitAsync(TimeSpan.FromSeconds(10));
        using var archive = ZipFile.OpenRead(created);
        const string logEntry = "logs/EFnet/#clircs/2026-10-21.log";

        Assert.Equal(
            $"appearance.json,{logEntry}",
            string.Join(',', archive.Entries.Select(entry => entry.FullName)
                .Order(StringComparer.Ordinal)));
        Assert.Equal("before snapshot",
            Encoding.UTF8.GetString(ReadEntry(archive, "appearance.json")));
        Assert.Equal(
            $"[12:34:56] before: café こんにちは 😀{Environment.NewLine}",
            Encoding.UTF8.GetString(ReadEntry(archive, logEntry)));

        Assert.Equal("after snapshot", File.ReadAllText(settingsPath));
        var liveLines = File.ReadAllLines(Path.Combine(
            logsDirectory, "EFnet", "#clircs", "2026-10-21.log"));
        Assert.Equal(2, liveLines.Length);
        Assert.Equal("[12:34:56] after snapshot", liveLines[1]);
        Assert.Equal(1, Directory.EnumerateFileSystemEntries(manager.BackupDirectory).Count());
    }

    private static void RejectsTruncatedLog()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, "appearance.json"), "settings");
        Directory.CreateDirectory(Path.Combine(temporary.Path, "logs"));
        var logPath = Path.Combine(temporary.Path, "logs", "chat.log");
        File.WriteAllText(logPath, "The complete log before capture.");

        var previous = new BackupManager(temporary.Path).Create();
        var previousBytes = File.ReadAllBytes(previous);
        var manager = new BackupManager(
            temporary.Path,
            afterSnapshot: () => File.WriteAllText(logPath, string.Empty));

        Assert.Throws<EndOfStreamException>(() => manager.Create());

        Assert.Equal(1, manager.List().Count);
        Assert.Equal(previous, manager.List()[0].FullName);
        Assert.True(File.ReadAllBytes(previous).SequenceEqual(previousBytes),
            "The failed backup changed an existing archive.");
        Assert.Equal(1, Directory.EnumerateFileSystemEntries(manager.BackupDirectory).Count());
    }

    private static byte[] ReadEntry(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path)
            ?? throw new InvalidOperationException($"Missing backup entry: {path}");
        using var source = entry.Open();
        using var contents = new MemoryStream();
        source.CopyTo(contents);
        return contents.ToArray();
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
