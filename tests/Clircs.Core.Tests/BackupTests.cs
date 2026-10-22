using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Clircs.Commands;
using Clircs.ConsoleClient;
using Clircs.Infrastructure;
using Clircs.Networking;
using Clircs.State;
using System.Text.Json;

namespace Clircs.Core.Tests;

internal static class BackupTests
{
    private const string RestoreWorkspaceName =
    ".clircs-restore-0123456789abcdef0123456789abcdef.tmp";

    public static void Register(TestSuite suite)
    {
        suite.Add("backup includes user data and logs without temporary files or backups", CreatesCompleteSnapshot);
        suite.Add("backup preserves captured data while logging continues", PreservesCapturedDataAsync);
        suite.Add("backup rejects truncated logs and cleans up failed archives", RejectsTruncatedLog);
        suite.Add("restore accepts safe paths and ordinary archive entries", AcceptsSafeArchiveEntries);
        suite.Add("restore rejects unsafe archive paths", RejectsUnsafeArchivePaths);
        suite.Add("restore rejects reserved Windows names", RejectsReservedArchiveNames);
        suite.Add("restore protects the backups directory", ProtectsBackupDirectory);
        suite.Add("restore rejects conflicting archive paths", RejectsConflictingArchivePaths);
        suite.Add("restore rejects links and invalid entry types", RejectsUnsupportedArchiveEntries);
        suite.Add("restore rejects damaged archive contents", RejectsDamagedArchiveContents);
        suite.Add("restore honors cancellation before and during copying", CancelsEntryCopy);
        suite.Add("restore extracts staged files with their bytes and timestamps", ExtractsStagedFiles);
        suite.Add("restore rejects missing or occupied staging destinations", RejectsUnsafeStagingDestinations);
        suite.Add("restore validates all archive paths before writing files", ValidatesBeforeExtraction);
        suite.Add("restore preparation preserves current data and backup archives", PreparesRestoreWithoutChangingCurrentData);
        suite.Add("failed restore preparation removes its workspace", CleansUpFailedPreparation);
        suite.Add("cancelled restore preparation creates no workspace", CancelsRestorePreparation);
        suite.Add("restore recovery ownership preserves staged and rollback data", PreservesWorkspaceForRecovery);
        suite.Add("shared data leases coexist and block restore until all close", SharedLeasesBlockRestore);
        suite.Add("exclusive data leases block access only to their own folder", ExclusiveLeaseBlocksOtherAccess);
        suite.Add("restore rejects shared disposed or unrelated data leases", ValidatesExclusiveLeaseOwnership);
        suite.Add("restore journal persists phases and requires exclusive access", PersistsRestoreJournalPhases);
        suite.Add("restore journal rejects invalid metadata without changing it", RejectsInvalidRestoreJournal);
        suite.Add("restore journal rejects unsafe workspace names", RejectsUnsafeRestoreWorkspaceNames);
        suite.Add("failed restore journal writes preserve the previous phase", FailedJournalWritePreservesPreviousPhase);
        suite.Add("restore recovery without a journal leaves data untouched", RecoveryWithoutJournalLeavesDataUntouched);
        suite.Add("restore recovery rolls back each unfinished phase", RecoversUnfinishedRestorePhases);
        suite.Add("restore recovery cleans up completed transactions", CleansUpCompletedRestoreTransactions);
        suite.Add("restore recovery resumes after an interrupted rollback", ResumesInterruptedRestoreRecovery);
        suite.Add("restore recovery preserves conflicting original files", PreservesConflictingRecoveryFiles);
        suite.Add("restore recovery rejects incomplete workspaces before deleting data", RejectsIncompleteRecoveryWorkspaces);
        suite.Add("restore recovery survives failed journal updates", RecoversAfterFailedRecoveryJournalUpdates);
        suite.Add("restore replaces current data with the complete backup", ReplacesCurrentDataWithBackup);
        suite.Add("restore rolls back failures while moving files", RollsBackFailedRestoreMoves);
        suite.Add("restore rolls back failed journal updates", RollsBackFailedRestoreJournalUpdates);
        suite.Add("restore recovers interrupted replacement without losing originals", RecoversInterruptedReplacement);
        suite.Add("restore preserves recovery data when rollback also fails", RecoversAfterFailedRollback);
        suite.Add("restore keeps committed data when cleanup fails", KeepsCommittedRestoreAfterCleanupFailure);
        suite.Add("restore requires matching exclusive access and no pending recovery", RejectsInvalidRestoreApplication);
        suite.Add("startup allows shared access when no recovery is pending", StartupAllowsSharedAccessWithoutRecovery);
        suite.Add("startup completes recovery before returning shared access", StartupRecoversBeforeReturningSharedLease);
        suite.Add("failed startup preserves recovery data and releases its leases", FailedStartupReleasesDataLeases);
        suite.Add("restore paths accept backup filenames or full paths", ResolvesRestoreArchivePaths);
        suite.Add("restore confirmation requires an explicit affirmative answer", RestoreConfirmationRequiresExplicitAcceptance);
    }

    private static void CreatesCompleteSnapshot()
    {
        using var temporary = new TemporaryDirectory();
        var expected = new Dictionary<string, byte[]>
        {
            ["appearance.json"] = Encoding.UTF8.GetBytes("settings"),
            ["networks.toml"] = Encoding.UTF8.GetBytes("saved profiles"),
            ["network-secrets.json"] = Encoding.UTF8.GetBytes("encrypted credential data"),
            ["scripts/data.bin"] = [0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF],
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
        Assert.Equal(expected.Count, BackupRestoreArchive.Validate(archive).Count);
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

    private static void AcceptsSafeArchiveEntries()
    {
        const string logPath = "logs/EFnet/#clircs/2026-10-21.log";
        var expected = Encoding.UTF8.GetBytes("café こんにちは 😀");

        WithArchive(
            archive =>
            {
                var log = archive.CreateEntry(logPath);
                using (var contents = log.Open())
                {
                    contents.Write(expected);
                }

                archive.CreateEntry("scripts/résumé/main.js");

                // Explicit directories can appear after their children.
                var directory = archive.CreateEntry("scripts/résumé/");
                directory.ExternalAttributes =
                    (0x4000 << 16) | (int)FileAttributes.Directory;

                var regular = archive.CreateEntry("scripts/COM10.txt");
                regular.ExternalAttributes = unchecked(0x8000 << 16);

                archive.CreateEntry("scripts/.config");
                archive.CreateEntry("scripts/archive/backups/old.zip");
            },
            archive =>
            {
                var entries = BackupRestoreArchive.Validate(archive);
                Assert.Equal(6, entries.Count);

                var directory = entries.Single(
                    entry => entry.RelativePath == "scripts/résumé");
                Assert.True(directory.IsDirectory);

                var log = entries.Single(
                    entry => entry.RelativePath == logPath);
                Assert.False(log.IsDirectory);

                using var source = log.Source.Open();
                using var contents = new MemoryStream();
                source.CopyTo(contents);
                Assert.True(contents.ToArray().SequenceEqual(expected));
            });
    }

    private static void RejectsUnsafeArchivePaths()
    {
        string[] paths =
        [
            "/outside.txt",
            "//server/share/outside.txt",
            @"C:\outside.txt",
            "C:outside.txt",
            @"scripts\main.js",
            @"scripts/..\outside.txt",
            "../outside.txt",
            "scripts/../outside.txt",
            "./appearance.json",
            "scripts/./main.js",
            "scripts//main.js",
            "scripts//",
            "/",
            "scripts/name.",
            "scripts/name ",
            "scripts/name./main.js",
            "scripts/name /main.js",
            "scripts/data.txt:stream",
            "scripts/bad<name.txt",
            "scripts/bad>name.txt",
            "scripts/bad\"name.txt",
            "scripts/bad|name.txt",
            "scripts/bad?name.txt",
            "scripts/bad*name.txt",
            "scripts/bad\u0001name.txt",
            "scripts/bad\u007Fname.txt",
            "scripts/bad\u0085name.txt"
        ];

        foreach (var path in paths)
        {
            AssertRejectedArchive(path);
        }
    }

    private static void RejectsReservedArchiveNames()
    {
        string[] names =
        [
            "con",
            "PRN.txt",
            "AUX",
            "NUL .txt",
            "CLOCK$.log",
            "CONIN$",
            "CONOUT$.txt",
            "com1.zip",
            "COM9",
            "Lpt1.txt",
            "LPT9",
            "COM¹.txt",
            "COM².txt",
            "COM³.txt",
            "LPT¹.txt",
            "LPT².txt",
            "LPT³.txt"
        ];

        foreach (var name in names)
        {
            AssertRejectedArchive($"scripts/{name}");
        }
    }

    private static void ProtectsBackupDirectory()
    {
        AssertRejectedArchive("backups");
        AssertRejectedArchive("backups/");
        AssertRejectedArchive("BACKUPS/old.zip");
    }

    private static void RejectsConflictingArchivePaths()
    {
        string[][] cases =
        [
            ["appearance.json", "appearance.json"],
            ["appearance.json", "APPEARANCE.JSON"],
            ["scripts/", "SCRIPTS/"],
            ["scripts", "scripts/"],
            ["logs", "LOGS/chat.log"],
            ["LOGS/chat.log", "logs"],
            ["scripts", "scripts/example/main.js"],
            ["scripts/example/main.js", "scripts"]
        ];

        foreach (var paths in cases)
        {
            AssertRejectedArchive(paths);
        }
    }

    private static void RejectsUnsupportedArchiveEntries()
    {
        (string Path, int Attributes, bool HasContents)[] cases =
        [
            // Unix symbolic link and named pipe.
            ("link", unchecked(0xA000 << 16), false),
            ("pipe", 0x1000 << 16, false),

            ("link", (int)FileAttributes.ReparsePoint, false),

            // Directory metadata on a file, and file metadata on a directory.
            ("item", 0x4000 << 16, false),
            ("item/", unchecked(0x8000 << 16), false),
            ("item", (int)FileAttributes.Directory, false),

            ("item/", 0, true)
        ];

        foreach (var item in cases)
        {
            WithArchive(
                archive =>
                {
                    var entry = archive.CreateEntry(item.Path);
                    entry.ExternalAttributes = item.Attributes;

                    if (item.HasContents)
                    {
                        using var contents = entry.Open();
                        contents.WriteByte(1);
                    }
                },
                archive => Assert.Throws<InvalidDataException>(
                    () => BackupRestoreArchive.Validate(archive)));
        }
    }

    private static void RejectsDamagedArchiveContents()
    {
        var expected = Encoding.UTF8.GetBytes("123456789");

        using var fixture = new MemoryStream();
        using (var archive = new ZipArchive(
            fixture, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(
                "logs/chat.log", CompressionLevel.NoCompression);
            using var contents = entry.Open();
            contents.Write(expected);
        }

        var original = fixture.ToArray();

        // This fixture has one stored entry and no archive comment.
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(
            original.AsSpan(26));
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(
            original.AsSpan(28));
        var dataOffset = 30 + nameLength + extraLength;

        var endOffset = original.Length - 22;
        var directoryOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            original.AsSpan(endOffset + 16)));

        Action<byte[]>[] damageCases =
        [
            // Change file bytes while retaining the original checksum.
            bytes => bytes[dataOffset] ^= 1,

            // Change the saved checksum while retaining the original bytes.
            bytes => bytes[directoryOffset + 16] ^= 1,

            // Make the saved uncompressed length too short or too long.
            bytes => BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(directoryOffset + 24),
                (uint)(expected.Length - 1)),
            bytes => BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(directoryOffset + 24),
                (uint)(expected.Length + 1))
        ];

        foreach (var damage in damageCases)
        {
            var damaged = original.ToArray();
            damage(damaged);

            using var stream = new MemoryStream(damaged, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = BackupRestoreArchive.Validate(archive).Single();
            using var contents = new MemoryStream();

            Assert.Throws<InvalidDataException>(
                () => BackupRestoreArchive.CopyEntry(entry.Source, contents));

            using var staging = new TemporaryDirectory();
            Assert.Throws<InvalidDataException>(
                () => BackupRestoreArchive.Extract(archive, staging.Path));

            // Failed extraction must release the staged file.
            using var probe = new FileStream(
                Path.Combine(staging.Path, "logs", "chat.log"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            using var data = new TemporaryDirectory();
            var archivePath = Path.Combine(data.Path, "damaged.zip");
            var settings = Path.Combine(data.Path, "appearance.json");
            File.WriteAllBytes(archivePath, damaged);
            File.WriteAllText(settings, "current settings");

            Assert.Throws<InvalidDataException>(
                () => PreparedBackupRestore.Create(data.Path, archivePath));

            Assert.False(Directory.EnumerateDirectories(
                Path.Combine(data.Path, "backups")).Any());
            Assert.Equal("current settings", File.ReadAllText(settings));
            Assert.True(File.ReadAllBytes(archivePath).SequenceEqual(damaged));
        }
    }

    private static void CancelsEntryCopy()
    {
        WithArchive(
            archive =>
            {
                var entry = archive.CreateEntry(
                    "logs/chat.log", CompressionLevel.NoCompression);
                using var contents = entry.Open();
                contents.Write(new byte[200_000]);
            },
            archive =>
            {
                var entry = BackupRestoreArchive.Validate(archive).Single();

                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                using var untouched = new MemoryStream();

                var beforeCopy = Assert.Throws<OperationCanceledException>(
                    () => BackupRestoreArchive.CopyEntry(
                        entry.Source, untouched, cancellation.Token));

                Assert.Equal(cancellation.Token, beforeCopy.CancellationToken);
                Assert.Equal(0L, untouched.Length);

                using var staging = new TemporaryDirectory();
                Assert.Throws<OperationCanceledException>(
                    () => BackupRestoreArchive.Extract(
                        archive, staging.Path, cancellation.Token));
                Assert.False(
                    Directory.EnumerateFileSystemEntries(staging.Path).Any());

                using var duringCopy = new CancellationTokenSource();
                using var partial = new CancelOnWriteStream(duringCopy);

                var afterWrite = Assert.Throws<OperationCanceledException>(
                    () => BackupRestoreArchive.CopyEntry(
                        entry.Source, partial, duringCopy.Token));

                Assert.Equal(duringCopy.Token, afterWrite.CancellationToken);
                Assert.True(partial.Length > 0,
                    "Cancellation should occur after copying has started.");
            });
    }

    private static void ExtractsStagedFiles()
    {
        using var staging = new TemporaryDirectory();
        var expected = new Dictionary<string, byte[]>
        {
            ["appearance.json"] = Encoding.UTF8.GetBytes("saved settings"),
            ["logs/EFnet/#clircs/2026-10-21.log"] = Encoding.UTF8.GetBytes(
                string.Concat(Enumerable.Repeat("café こんにちは 😀\n", 6_000))),
            ["scripts/résumé/data.bin"] = [0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF],
            ["logs/empty.log"] = []
        };
        var timestamp = new DateTimeOffset(
            2026, 10, 20, 12, 34, 56, TimeSpan.Zero);

        WithArchive(
            archive =>
            {
                foreach (var file in expected)
                {
                    var entry = archive.CreateEntry(file.Key);
                    entry.LastWriteTime = timestamp;
                    using var contents = entry.Open();
                    contents.Write(file.Value);
                }

                archive.CreateEntry("scripts/résumé/");
                archive.CreateEntry("scripts/empty/");
            },
            archive =>
            {
                BackupRestoreArchive.Extract(
                    archive, staging.Path + Path.DirectorySeparatorChar);

                Assert.Equal(expected.Count, Directory.EnumerateFiles(
                    staging.Path, "*", SearchOption.AllDirectories).Count());

                foreach (var file in expected)
                {
                    var path = Path.Combine(staging.Path, file.Key);
                    Assert.True(File.ReadAllBytes(path).SequenceEqual(file.Value),
                        $"Extracted content differs for {file.Key}.");
                    Assert.Equal(
                        archive.GetEntry(file.Key)!.LastWriteTime.UtcDateTime,
                        File.GetLastWriteTimeUtc(path));
                }

                var emptyDirectory = Path.Combine(staging.Path, "scripts", "empty");
                Assert.True(Directory.Exists(emptyDirectory));
                Assert.False(Directory.EnumerateFileSystemEntries(emptyDirectory).Any());
            });
    }

    private static void RejectsUnsafeStagingDestinations()
    {
        using var temporary = new TemporaryDirectory();
        var missing = Path.Combine(temporary.Path, "missing");
        var occupiedFile = Path.Combine(temporary.Path, "occupied-file");
        var occupiedDirectory = Path.Combine(temporary.Path, "occupied-directory");
        var settings = Path.Combine(occupiedDirectory, "appearance.json");

        File.WriteAllText(occupiedFile, "keep this file");
        Directory.CreateDirectory(occupiedDirectory);
        File.WriteAllText(settings, "existing settings");

        WithArchive(
            archive => archive.CreateEntry("appearance.json"),
            archive =>
            {
                foreach (var destination in new[]
                {
                    missing, occupiedFile, occupiedDirectory
                })
                {
                    Assert.Throws<IOException>(
                        () => BackupRestoreArchive.Extract(archive, destination));
                }

                Assert.False(Directory.Exists(missing));
                Assert.Equal("keep this file", File.ReadAllText(occupiedFile));
                Assert.Equal("existing settings", File.ReadAllText(settings));
                Assert.Equal(1,
                    Directory.EnumerateFileSystemEntries(occupiedDirectory).Count());
            });
    }

    private static void ValidatesBeforeExtraction()
    {
        using var temporary = new TemporaryDirectory();
        var staging = Path.Combine(temporary.Path, "staging");
        var outside = Path.Combine(temporary.Path, "outside.txt");
        Directory.CreateDirectory(staging);
        File.WriteAllText(outside, "keep this file");

        WithArchive(
            archive =>
            {
                archive.CreateEntry("appearance.json");
                archive.CreateEntry("../outside.txt");
            },
            archive =>
            {
                Assert.Throws<InvalidDataException>(
                    () => BackupRestoreArchive.Extract(archive, staging));

                Assert.False(Directory.EnumerateFileSystemEntries(staging).Any());
                Assert.Equal("keep this file", File.ReadAllText(outside));
            });
    }

    private static void PreparesRestoreWithoutChangingCurrentData()
    {
        using var temporary = new TemporaryDirectory();
        var settings = Path.Combine(temporary.Path, "appearance.json");
        var logs = Path.Combine(temporary.Path, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(settings, "saved settings");
        File.WriteAllText(Path.Combine(logs, "chat.log"), "café こんにちは 😀");

        var manager = new BackupManager(temporary.Path);
        var previousArchive = manager.Create();
        var archivePath = manager.Create();
        var previousBytes = File.ReadAllBytes(previousArchive);
        var archiveBytes = File.ReadAllBytes(archivePath);

        File.WriteAllText(settings, "current settings");
        var later = Path.Combine(temporary.Path, "later.txt");
        File.WriteAllText(later, "keep until confirmation");

        using var prepared = PreparedBackupRestore.Create(
            temporary.Path + Path.DirectorySeparatorChar, archivePath);
        var workspace = prepared.WorkspaceDirectory;

        Assert.Equal("saved settings", File.ReadAllText(
            Path.Combine(prepared.StagingDirectory, "appearance.json")));
        Assert.Equal("café こんにちは 😀", File.ReadAllText(
            Path.Combine(prepared.StagingDirectory, "logs", "chat.log")));
        Assert.False(File.Exists(
            Path.Combine(prepared.StagingDirectory, "later.txt")));
        Assert.Equal("current settings", File.ReadAllText(settings));

        // Preparation must release the source archive before confirmation.
        using (var probe = new FileStream(
            archivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(archiveBytes.LongLength, probe.Length);
        }

        prepared.Dispose();
        prepared.Dispose();

        Assert.False(Directory.Exists(workspace));
        Assert.Equal("current settings", File.ReadAllText(settings));
        Assert.Equal("keep until confirmation", File.ReadAllText(later));
        Assert.True(File.ReadAllBytes(previousArchive).SequenceEqual(previousBytes));
        Assert.True(File.ReadAllBytes(archivePath).SequenceEqual(archiveBytes));
        Assert.Equal(2,
            Directory.EnumerateFileSystemEntries(manager.BackupDirectory).Count());
        Assert.Throws<ObjectDisposedException>(
            () => prepared.PreserveForRecovery());
    }

    private static void CleansUpFailedPreparation()
    {
        using var temporary = new TemporaryDirectory();
        var settings = Path.Combine(temporary.Path, "appearance.json");
        var backups = Path.Combine(temporary.Path, "backups");
        Directory.CreateDirectory(backups);
        File.WriteAllText(settings, "current settings");

        var unsafeArchive = Path.Combine(backups, "unsafe.zip");
        using (var archive = ZipFile.Open(unsafeArchive, ZipArchiveMode.Create))
        {
            archive.CreateEntry("appearance.json");
            archive.CreateEntry("../outside.txt");
        }
        var unsafeBytes = File.ReadAllBytes(unsafeArchive);

        Assert.Throws<InvalidDataException>(
            () => PreparedBackupRestore.Create(temporary.Path, unsafeArchive));

        var malformedArchive = Path.Combine(backups, "malformed.zip");
        File.WriteAllText(malformedArchive, "This is not a ZIP archive.");

        Assert.Throws<InvalidDataException>(
            () => PreparedBackupRestore.Create(temporary.Path, malformedArchive));
        Assert.Throws<FileNotFoundException>(
            () => PreparedBackupRestore.Create(
                temporary.Path, Path.Combine(backups, "missing.zip")));

        Assert.False(Directory.EnumerateDirectories(backups).Any());
        Assert.Equal("current settings", File.ReadAllText(settings));
        Assert.True(File.ReadAllBytes(unsafeArchive).SequenceEqual(unsafeBytes));
        Assert.Equal("This is not a ZIP archive.",
            File.ReadAllText(malformedArchive));
    }

    private static void CancelsRestorePreparation()
    {
        using var temporary = new TemporaryDirectory();
        var settings = Path.Combine(temporary.Path, "appearance.json");
        File.WriteAllText(settings, "saved settings");

        var manager = new BackupManager(temporary.Path);
        var archivePath = manager.Create();
        var archiveBytes = File.ReadAllBytes(archivePath);
        File.WriteAllText(settings, "current settings");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failure = Assert.Throws<OperationCanceledException>(
            () => PreparedBackupRestore.Create(
                temporary.Path, archivePath, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal("current settings", File.ReadAllText(settings));
        Assert.True(File.ReadAllBytes(archivePath).SequenceEqual(archiveBytes));
        Assert.Equal(1,
            Directory.EnumerateFileSystemEntries(manager.BackupDirectory).Count());
    }

    private static void PreservesWorkspaceForRecovery()
    {
        using var temporary = new TemporaryDirectory();
        var settings = Path.Combine(temporary.Path, "appearance.json");
        File.WriteAllText(settings, "saved settings");
        var archivePath = new BackupManager(temporary.Path).Create();
        File.WriteAllText(settings, "current settings");

        using var prepared = PreparedBackupRestore.Create(
            temporary.Path, archivePath);
        prepared.PreserveForRecovery();

        var previous = Path.Combine(prepared.WorkspaceDirectory, "previous");
        Directory.CreateDirectory(previous);
        File.Copy(settings, Path.Combine(previous, "appearance.json"));

        prepared.Dispose();

        Assert.True(Directory.Exists(prepared.WorkspaceDirectory));
        Assert.Equal("saved settings", File.ReadAllText(
            Path.Combine(prepared.StagingDirectory, "appearance.json")));
        Assert.Equal("current settings", File.ReadAllText(
            Path.Combine(previous, "appearance.json")));
        Assert.Equal("current settings", File.ReadAllText(settings));
        Assert.True(File.Exists(archivePath));
    }

    private static void SharedLeasesBlockRestore()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "data");

        using var first = DataDirectoryLease.AcquireShared(root);
        using var second = DataDirectoryLease.AcquireShared(
            root + Path.DirectorySeparatorChar);

        Assert.Throws<IOException>(() =>
        {
            using var unexpected = DataDirectoryLease.AcquireExclusive(root);
        });

        first.Dispose();

        Assert.Throws<IOException>(() =>
        {
            using var unexpected = DataDirectoryLease.AcquireExclusive(root);
        });

        second.Dispose();

        using var exclusive = DataDirectoryLease.AcquireExclusive(root);
        exclusive.EnsureExclusiveFor(root);
    }

    private static void ExclusiveLeaseBlocksOtherAccess()
    {
        using var temporary = new TemporaryDirectory();
        var firstRoot = Path.Combine(temporary.Path, "first");
        var secondRoot = Path.Combine(temporary.Path, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);

        using var exclusive = DataDirectoryLease.AcquireExclusive(firstRoot);

        Assert.Throws<IOException>(() =>
        {
            using var unexpected = DataDirectoryLease.AcquireShared(firstRoot);
        });
        Assert.Throws<IOException>(() =>
        {
            using var unexpected = DataDirectoryLease.AcquireExclusive(firstRoot);
        });

        using var independent = DataDirectoryLease.AcquireExclusive(secondRoot);
        independent.EnsureExclusiveFor(secondRoot);

        exclusive.Dispose();

        using var shared = DataDirectoryLease.AcquireShared(firstRoot);
    }

    private static void ValidatesExclusiveLeaseOwnership()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "data");
        var unrelated = Path.Combine(temporary.Path, "data-other");

        using (var shared = DataDirectoryLease.AcquireShared(root))
        {
            Assert.Throws<InvalidOperationException>(
                () => shared.EnsureExclusiveFor(root));
        }

        using var exclusive = DataDirectoryLease.AcquireExclusive(root);
        exclusive.EnsureExclusiveFor(root + Path.DirectorySeparatorChar);

        Assert.Throws<InvalidOperationException>(
            () => exclusive.EnsureExclusiveFor(unrelated));

        exclusive.Dispose();
        exclusive.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => exclusive.EnsureExclusiveFor(root));

        using var replacement = DataDirectoryLease.AcquireShared(root);
    }

    private static void PersistsRestoreJournalPhases()
    {
        using var temporary = new TemporaryDirectory();
        var journal = new BackupRestoreJournal(
            temporary.Path + Path.DirectorySeparatorChar);
        Assert.True(journal.Load() is null);

        using var exclusive = DataDirectoryLease.AcquireExclusive(temporary.Path);

        foreach (var phase in Enum.GetValues<BackupRestorePhase>())
        {
            var state = new BackupRestoreState(RestoreWorkspaceName, phase);
            journal.Save(state, exclusive);

            Assert.Equal(state, journal.Load()!);
            Assert.Equal(
                Path.Combine(journal.BackupDirectory, RestoreWorkspaceName),
                journal.WorkspaceFor(state));
            Assert.False(File.Exists(journal.Path + ".bak"));
        }

        journal.Delete(exclusive);
        Assert.True(journal.Load() is null);
        exclusive.Dispose();

        using var shared = DataDirectoryLease.AcquireShared(temporary.Path);
        var pending = new BackupRestoreState(
            RestoreWorkspaceName, BackupRestorePhase.SavingOriginals);

        Assert.Throws<InvalidOperationException>(
            () => journal.Save(pending, shared));
        Assert.Throws<InvalidOperationException>(
            () => journal.Delete(shared));
        Assert.True(journal.Load() is null);
    }

    private static void RejectsInvalidRestoreJournal()
    {
        using var temporary = new TemporaryDirectory();
        var settings = Path.Combine(temporary.Path, "appearance.json");
        File.WriteAllText(settings, "current settings");

        var journal = new BackupRestoreJournal(temporary.Path);
        Directory.CreateDirectory(journal.BackupDirectory);

        string[] documents =
        [
            "",
            "null",
            "{",
            "{}",
            JsonSerializer.Serialize(new
            {
                SchemaVersion = 99,
                WorkspaceName = RestoreWorkspaceName,
                Phase = "Installing"
            }),
            JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                WorkspaceName = RestoreWorkspaceName
            }),
            JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                Phase = "Installing"
            }),
            JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                WorkspaceName = RestoreWorkspaceName,
                Phase = "Unknown"
            }),
            JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                WorkspaceName = RestoreWorkspaceName,
                Phase = 0
            }),
            JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                WorkspaceName = "../outside",
                Phase = "Installing"
            })
        ];

        foreach (var document in documents)
        {
            File.WriteAllText(journal.Path, document);

            Assert.Throws<InvalidDataException>(() => journal.Load());
            Assert.Equal(document, File.ReadAllText(journal.Path));
        }

        Assert.Equal("current settings", File.ReadAllText(settings));
    }

    private static void RejectsUnsafeRestoreWorkspaceNames()
    {
        using var temporary = new TemporaryDirectory();
        using var exclusive = DataDirectoryLease.AcquireExclusive(temporary.Path);
        var journal = new BackupRestoreJournal(temporary.Path);
        var original = new BackupRestoreState(
            RestoreWorkspaceName, BackupRestorePhase.SavingOriginals);
        journal.Save(original, exclusive);
        var originalText = File.ReadAllText(journal.Path);

        string[] names =
        [
            "",
            "../" + RestoreWorkspaceName,
            @"..\" + RestoreWorkspaceName,
            Path.Combine(temporary.Path, RestoreWorkspaceName),
            RestoreWorkspaceName + "/data",
            RestoreWorkspaceName + ".extra",
            ".clircs-restore-not-a-guid.tmp",
            ".clircs-restore-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx.tmp"
        ];

        foreach (var name in names)
        {
            var invalid = original with { WorkspaceName = name };

            Assert.Throws<InvalidDataException>(
                () => journal.Save(invalid, exclusive));
            Assert.Throws<InvalidDataException>(
                () => journal.WorkspaceFor(invalid));
            Assert.Equal(originalText, File.ReadAllText(journal.Path));
        }

        var unknownPhase = original with { Phase = (BackupRestorePhase)999 };
        Assert.Throws<InvalidDataException>(
            () => journal.Save(unknownPhase, exclusive));
        Assert.Equal(original, journal.Load()!);
    }

    private static void FailedJournalWritePreservesPreviousPhase()
    {
        using var temporary = new TemporaryDirectory();
        using var exclusive = DataDirectoryLease.AcquireExclusive(temporary.Path);
        var journal = new BackupRestoreJournal(temporary.Path);
        var original = new BackupRestoreState(
            RestoreWorkspaceName, BackupRestorePhase.SavingOriginals);
        journal.Save(original, exclusive);
        var originalText = File.ReadAllText(journal.Path);

        var failing = new BackupRestoreJournal(
            temporary.Path,
            new DurableFileWriter(
                _ => throw new IOException("simulated journal failure")));

        Assert.Throws<IOException>(() => failing.Save(
            original with { Phase = BackupRestorePhase.Installing },
            exclusive));

        Assert.Equal(original, journal.Load()!);
        Assert.Equal(originalText, File.ReadAllText(journal.Path));
        Assert.False(File.Exists(journal.Path + ".bak"));
        Assert.Equal(0,
            Directory.GetFiles(journal.BackupDirectory, "*.tmp").Length);
    }

    private static void RecoveryWithoutJournalLeavesDataUntouched()
    {
        using var fixture = new RestoreRecoveryFixture(
            BackupRestorePhase.SavingOriginals);
        fixture.Write(fixture.DataDirectory, "appearance.json", "current settings");
        fixture.Write(fixture.StagingDirectory, "appearance.json", "staged settings");

        var transaction = new BackupRestoreTransaction(fixture.DataDirectory);

        Assert.True(transaction.Recover(fixture.Lease) is null);
        fixture.AssertCurrentFiles(("appearance.json", "current settings"));
        Assert.Equal("staged settings", File.ReadAllText(
            Path.Combine(fixture.StagingDirectory, "appearance.json")));
        Assert.True(Directory.Exists(fixture.WorkspaceDirectory));
    }

    private static void RecoversUnfinishedRestorePhases()
    {
        BackupRestorePhase[] phases =
        [
            BackupRestorePhase.SavingOriginals,
            BackupRestorePhase.Installing,
            BackupRestorePhase.RestoringOriginals
        ];

        foreach (var phase in phases)
        {
            using var fixture = new RestoreRecoveryFixture(phase);
            fixture.Write(fixture.StagingDirectory, "appearance.json", "backup settings");
            fixture.Write(fixture.PreviousDirectory, "logs/chat.log", "original log");

            if (phase == BackupRestorePhase.Installing)
            {
                fixture.Write(fixture.PreviousDirectory, "appearance.json", "original settings");
                fixture.Write(fixture.DataDirectory, "appearance.json", "partial new settings");
                fixture.Write(fixture.DataDirectory, "logs/new.log", "partial new log");
                fixture.Write(fixture.DataDirectory, "new-only.txt", "partial new file");
            }
            else
            {
                fixture.Write(fixture.DataDirectory, "appearance.json", "original settings");
            }

            fixture.Journal.Save(fixture.State, fixture.Lease);

            var transaction = new BackupRestoreTransaction(fixture.DataDirectory);
            var result = transaction.Recover(fixture.Lease);

            Assert.Equal(BackupRestorePhase.RolledBack, result!.Value);
            fixture.AssertCurrentFiles(
                ("appearance.json", "original settings"),
                ("logs/chat.log", "original log"));
            fixture.AssertCompleted();
        }
    }

    private static void CleansUpCompletedRestoreTransactions()
    {
        BackupRestorePhase[] phases =
        [
            BackupRestorePhase.Committed,
            BackupRestorePhase.RolledBack
        ];

        foreach (var phase in phases)
        {
            foreach (var workspaceAlreadyRemoved in new[] { false, true })
            {
                using var fixture = new RestoreRecoveryFixture(phase);
                fixture.Write(fixture.DataDirectory, "appearance.json", "authoritative settings");
                fixture.Write(fixture.PreviousDirectory, "obsolete.txt", "discard this");
                fixture.Write(fixture.StagingDirectory, "unused.txt", "discard this too");
                fixture.Journal.Save(fixture.State, fixture.Lease);

                if (workspaceAlreadyRemoved)
                {
                    Directory.Delete(fixture.WorkspaceDirectory, recursive: true);
                }

                var transaction = new BackupRestoreTransaction(fixture.DataDirectory);
                var result = transaction.Recover(fixture.Lease);

                Assert.Equal(phase, result!.Value);
                fixture.AssertCurrentFiles(
                    ("appearance.json", "authoritative settings"));
                fixture.AssertCompleted();
                Assert.True(transaction.Recover(fixture.Lease) is null);
            }
        }
    }

    private static void ResumesInterruptedRestoreRecovery()
    {
        using var fixture = new RestoreRecoveryFixture(
            BackupRestorePhase.Installing);
        fixture.Write(fixture.PreviousDirectory, "appearance.json", "original settings");
        fixture.Write(fixture.PreviousDirectory, "logs/chat.log", "original log");
        fixture.Write(fixture.DataDirectory, "new-only.txt", "partial installation");
        fixture.Journal.Save(fixture.State, fixture.Lease);

        var interrupted = new BackupRestoreTransaction(
            fixture.DataDirectory,
            afterMove: (phase, _) =>
            {
                Assert.Equal(BackupRestorePhase.RestoringOriginals, phase);
                throw new IOException("simulated interruption during recovery");
            });

        Assert.Throws<IOException>(() => interrupted.Recover(fixture.Lease));

        Assert.Equal(
            BackupRestorePhase.RestoringOriginals,
            fixture.Journal.Load()!.Phase);
        Assert.Equal("original settings", File.ReadAllText(
            Path.Combine(fixture.DataDirectory, "appearance.json")));
        Assert.Equal("original log", File.ReadAllText(
            Path.Combine(fixture.PreviousDirectory, "logs", "chat.log")));

        var resumed = new BackupRestoreTransaction(fixture.DataDirectory);
        var result = resumed.Recover(fixture.Lease);

        Assert.Equal(BackupRestorePhase.RolledBack, result!.Value);
        fixture.AssertCurrentFiles(
            ("appearance.json", "original settings"),
            ("logs/chat.log", "original log"));
        fixture.AssertCompleted();
    }

    private static void PreservesConflictingRecoveryFiles()
    {
        BackupRestorePhase[] phases =
        [
            BackupRestorePhase.SavingOriginals,
            BackupRestorePhase.RestoringOriginals
        ];

        foreach (var phase in phases)
        {
            foreach (var directoryConflict in new[] { false, true })
            {
                using var fixture = new RestoreRecoveryFixture(phase);
                var currentPath = directoryConflict
                    ? "logs/current.log"
                    : "appearance.json";
                var originalPath = directoryConflict
                    ? "logs/original.log"
                    : "appearance.json";

                fixture.Write(fixture.DataDirectory, currentPath, "current contents");
                fixture.Write(fixture.PreviousDirectory, originalPath, "original contents");
                fixture.Journal.Save(fixture.State, fixture.Lease);

                var transaction = new BackupRestoreTransaction(fixture.DataDirectory);

                Assert.Throws<IOException>(() => transaction.Recover(fixture.Lease));

                fixture.AssertCurrentFiles((currentPath, "current contents"));
                Assert.Equal("original contents", File.ReadAllText(
                    Path.Combine(fixture.PreviousDirectory, originalPath)));
                Assert.Equal(
                    BackupRestorePhase.RestoringOriginals,
                    fixture.Journal.Load()!.Phase);
                Assert.True(Directory.Exists(fixture.WorkspaceDirectory));
            }
        }
    }

    private static void RejectsIncompleteRecoveryWorkspaces()
    {
        string[] missingDirectories =
        [
            "",
            PreparedBackupRestore.StagingDirectoryName,
            "previous"
        ];

        foreach (var missing in missingDirectories)
        {
            using var fixture = new RestoreRecoveryFixture(
                BackupRestorePhase.Installing);
            fixture.Write(fixture.DataDirectory, "appearance.json", "current settings");
            fixture.Journal.Save(fixture.State, fixture.Lease);

            var missingPath = Path.Combine(fixture.WorkspaceDirectory, missing);
            Directory.Delete(missingPath, recursive: true);

            var transaction = new BackupRestoreTransaction(fixture.DataDirectory);

            Assert.Throws<IOException>(() => transaction.Recover(fixture.Lease));

            fixture.AssertCurrentFiles(("appearance.json", "current settings"));
            Assert.Equal(
                BackupRestorePhase.Installing,
                fixture.Journal.Load()!.Phase);
        }
    }

    private static void RecoversAfterFailedRecoveryJournalUpdates()
    {
        foreach (var failOnWrite in new[] { 1, 2 })
        {
            using var fixture = new RestoreRecoveryFixture(
                BackupRestorePhase.Installing);
            fixture.Write(fixture.PreviousDirectory, "appearance.json", "original settings");
            fixture.Write(fixture.PreviousDirectory, "logs/chat.log", "original log");
            fixture.Write(fixture.DataDirectory, "new-only.txt", "partial installation");
            fixture.Journal.Save(fixture.State, fixture.Lease);

            var writes = 0;
            var failing = new BackupRestoreTransaction(
                fixture.DataDirectory,
                files: new DurableFileWriter(_ =>
                {
                    writes++;
                    if (writes == failOnWrite)
                    {
                        throw new IOException("simulated recovery journal failure");
                    }
                }));

            Assert.Throws<IOException>(() => failing.Recover(fixture.Lease));

            Assert.Equal(
                failOnWrite == 1
                    ? BackupRestorePhase.Installing
                    : BackupRestorePhase.RestoringOriginals,
                fixture.Journal.Load()!.Phase);
            Assert.True(Directory.Exists(fixture.WorkspaceDirectory));

            if (failOnWrite == 1)
            {
                Assert.Equal("original settings", File.ReadAllText(
                    Path.Combine(fixture.PreviousDirectory, "appearance.json")));
                Assert.Equal("original log", File.ReadAllText(
                    Path.Combine(fixture.PreviousDirectory, "logs", "chat.log")));
            }
            else
            {
                fixture.AssertCurrentFiles(
                    ("appearance.json", "original settings"),
                    ("logs/chat.log", "original log"));
            }

            var resumed = new BackupRestoreTransaction(fixture.DataDirectory);
            var result = resumed.Recover(fixture.Lease);

            Assert.Equal(BackupRestorePhase.RolledBack, result!.Value);
            fixture.AssertCurrentFiles(
                ("appearance.json", "original settings"),
                ("logs/chat.log", "original log"));
            fixture.AssertCompleted();
        }
    }

    private static void ReplacesCurrentDataWithBackup()
    {
        using var fixture = new RestoreApplyFixture();
        string[] readOnlyFiles =
        [
            "logs/current.log",
            "scripts/current.lua"
        ];

        foreach (var relativePath in readOnlyFiles)
        {
            var path = Path.Combine(fixture.DataDirectory, relativePath);
            File.SetAttributes(
                path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        }

        try
        {
            var transaction = new BackupRestoreTransaction(fixture.DataDirectory);
            transaction.Apply(fixture.Prepared, fixture.Lease);
            fixture.Prepared.Dispose();

            fixture.AssertCurrentFiles(fixture.SavedFiles);
            fixture.AssertCompleted();
        }
        finally
        {
            // Allow fixture cleanup even if the restore regression returns.
            foreach (var root in new[]
            {
                fixture.DataDirectory,
                Path.Combine(fixture.Prepared.WorkspaceDirectory, "previous")
            })
            {
                foreach (var relativePath in readOnlyFiles)
                {
                    var path = Path.Combine(root, relativePath);
                    if (File.Exists(path))
                    {
                        File.SetAttributes(
                            path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                    }
                }
            }
        }
    }

    private static void RollsBackFailedRestoreMoves()
    {
        BackupRestorePhase[] phases =
        [
            BackupRestorePhase.SavingOriginals,
            BackupRestorePhase.Installing
        ];

        foreach (var phase in phases)
        {
            foreach (var name in new[]
            {
                "appearance.json", "logs", "networks.toml", "scripts", "switched",
                phase == BackupRestorePhase.SavingOriginals
                    ? "later.txt"
                    : "old-only.txt"
            })
            {
                using var fixture = new RestoreApplyFixture();
                var transaction = new BackupRestoreTransaction(
                    fixture.DataDirectory,
                    afterMove: (actualPhase, actualName) =>
                    {
                        if (actualPhase == phase && actualName == name)
                        {
                            throw new IOException("simulated restore move failure");
                        }
                    });

                Assert.Throws<IOException>(() =>
                    transaction.Apply(fixture.Prepared, fixture.Lease));

                fixture.Prepared.Dispose();
                fixture.AssertCurrentFiles(fixture.OriginalFiles);
                fixture.AssertCompleted();
            }
        }
    }

    private static void RollsBackFailedRestoreJournalUpdates()
    {
        foreach (var failOnWrite in new[] { 1, 2, 3 })
        {
            using var fixture = new RestoreApplyFixture();
            var writes = 0;
            var transaction = new BackupRestoreTransaction(
                fixture.DataDirectory,
                files: new DurableFileWriter(_ =>
                {
                    writes++;
                    if (writes == failOnWrite)
                    {
                        throw new IOException("simulated restore journal failure");
                    }
                }));

            Assert.Throws<IOException>(() =>
                transaction.Apply(fixture.Prepared, fixture.Lease));

            fixture.Prepared.Dispose();
            fixture.AssertCurrentFiles(fixture.OriginalFiles);
            fixture.AssertCompleted();
        }
    }

    private static void RecoversInterruptedReplacement()
    {
        BackupRestorePhase[] phases =
        [
            BackupRestorePhase.SavingOriginals,
            BackupRestorePhase.Installing
        ];

        foreach (var phase in phases)
        {
            foreach (var name in new[]
            {
                "appearance.json", "logs", "networks.toml", "scripts", "switched",
                phase == BackupRestorePhase.SavingOriginals
                    ? "later.txt"
                    : "old-only.txt"
            })
            {
                using var fixture = new RestoreApplyFixture();
                var interrupted = new BackupRestoreTransaction(
                    fixture.DataDirectory,
                    afterMove: (actualPhase, actualName) =>
                    {
                        if (actualPhase == phase && actualName == name)
                        {
                            throw new SimulatedRestoreInterruptionException();
                        }
                    });

                Assert.Throws<SimulatedRestoreInterruptionException>(() =>
                    interrupted.Apply(fixture.Prepared, fixture.Lease));

                fixture.Prepared.Dispose();

                Assert.True(Directory.Exists(fixture.Prepared.WorkspaceDirectory));
                Assert.Equal(phase, fixture.Journal.Load()!.Phase);

                var resumed = new BackupRestoreTransaction(fixture.DataDirectory);
                var result = resumed.Recover(fixture.Lease);

                Assert.Equal(BackupRestorePhase.RolledBack, result!.Value);
                fixture.AssertCurrentFiles(fixture.OriginalFiles);
                fixture.AssertCompleted();
            }
        }
    }

    private static void RecoversAfterFailedRollback()
    {
        using var fixture = new RestoreApplyFixture();
        var transaction = new BackupRestoreTransaction(
            fixture.DataDirectory,
            afterMove: (phase, _) =>
            {
                if (phase == BackupRestorePhase.Installing)
                {
                    throw new IOException("simulated installation failure");
                }

                if (phase == BackupRestorePhase.RestoringOriginals)
                {
                    throw new IOException("simulated rollback failure");
                }
            });

        var failure = Assert.Throws<IOException>(() =>
            transaction.Apply(fixture.Prepared, fixture.Lease));

        Assert.True(failure.InnerException is AggregateException);
        Assert.Equal(2,
            ((AggregateException)failure.InnerException!).InnerExceptions.Count);

        fixture.Prepared.Dispose();

        Assert.True(Directory.Exists(fixture.Prepared.WorkspaceDirectory));
        Assert.Equal(
            BackupRestorePhase.RestoringOriginals,
            fixture.Journal.Load()!.Phase);

        var resumed = new BackupRestoreTransaction(fixture.DataDirectory);
        var result = resumed.Recover(fixture.Lease);

        Assert.Equal(BackupRestorePhase.RolledBack, result!.Value);
        fixture.AssertCurrentFiles(fixture.OriginalFiles);
        fixture.AssertCompleted();
    }

    private static void KeepsCommittedRestoreAfterCleanupFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new TestSkippedException(
                "This test requires Windows file deletion sharing rules.");
        }

        using var fixture = new RestoreApplyFixture();
        FileStream? heldOriginal = null;

        try
        {
            var transaction = new BackupRestoreTransaction(
                fixture.DataDirectory,
                afterMove: (phase, name) =>
                {
                    if (phase == BackupRestorePhase.SavingOriginals &&
                        name == "appearance.json")
                    {
                        heldOriginal = new FileStream(
                            Path.Combine(
                                fixture.Prepared.WorkspaceDirectory,
                                "previous",
                                "appearance.json"),
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read);
                    }
                });

            Assert.Throws<IOException>(() =>
                transaction.Apply(fixture.Prepared, fixture.Lease));

            Assert.True(heldOriginal is not null);
            Assert.Equal(
                BackupRestorePhase.Committed,
                fixture.Journal.Load()!.Phase);
            fixture.AssertCurrentFiles(fixture.SavedFiles);

            fixture.Prepared.Dispose();
            Assert.True(Directory.Exists(fixture.Prepared.WorkspaceDirectory));
        }
        finally
        {
            heldOriginal?.Dispose();
        }

        var resumed = new BackupRestoreTransaction(fixture.DataDirectory);
        var result = resumed.Recover(fixture.Lease);

        Assert.Equal(BackupRestorePhase.Committed, result!.Value);
        fixture.AssertCurrentFiles(fixture.SavedFiles);
        fixture.AssertCompleted();
    }

    private static void RejectsInvalidRestoreApplication()
    {
        using var fixture = new RestoreApplyFixture();
        var transaction = new BackupRestoreTransaction(fixture.DataDirectory);

        fixture.Lease.Dispose();

        using (var shared = DataDirectoryLease.AcquireShared(fixture.DataDirectory))
        {
            Assert.Throws<InvalidOperationException>(() =>
                transaction.Apply(fixture.Prepared, shared));
        }

        Assert.Throws<ObjectDisposedException>(() =>
            transaction.Apply(fixture.Prepared, fixture.Lease));

        using var other = new TemporaryDirectory();
        using var otherLease = DataDirectoryLease.AcquireExclusive(other.Path);

        Assert.Throws<InvalidOperationException>(() =>
            transaction.Apply(fixture.Prepared, otherLease));

        var otherTransaction = new BackupRestoreTransaction(other.Path);
        Assert.Throws<InvalidOperationException>(() =>
            otherTransaction.Apply(fixture.Prepared, otherLease));

        using var exclusive = DataDirectoryLease.AcquireExclusive(
            fixture.DataDirectory);
        var pending = new BackupRestoreState(
            RestoreWorkspaceName, BackupRestorePhase.Installing);
        fixture.Journal.Save(pending, exclusive);
        var journalBytes = File.ReadAllBytes(fixture.Journal.Path);

        Assert.Throws<IOException>(() =>
            transaction.Apply(fixture.Prepared, exclusive));

        fixture.AssertCurrentFiles(fixture.OriginalFiles);
        Assert.True(Directory.Exists(fixture.Prepared.StagingDirectory));
        Assert.True(File.ReadAllBytes(fixture.Journal.Path)
            .SequenceEqual(journalBytes));
    }

    private static void StartupAllowsSharedAccessWithoutRecovery()
    {
        using var temporary = new TemporaryDirectory();
        var settings = Path.Combine(temporary.Path, "appearance.json");
        File.WriteAllText(settings, "current settings");

        using (var first = ClientDataDirectory.AcquireForStartup(
            temporary.Path, out var firstRecovery))
        using (var second = ClientDataDirectory.AcquireForStartup(
            temporary.Path, out var secondRecovery))
        {
            Assert.True(firstRecovery is null);
            Assert.True(secondRecovery is null);
            Assert.False(first.IsExclusive);
            Assert.False(second.IsExclusive);
            Assert.Equal(temporary.Path, first.DataDirectory);
            Assert.Equal("current settings", File.ReadAllText(settings));

            Assert.Throws<IOException>(() =>
            {
                using var unexpected = DataDirectoryLease.AcquireExclusive(
                    temporary.Path);
            });
        }

        using var exclusive = DataDirectoryLease.AcquireExclusive(temporary.Path);
        Assert.True(exclusive.IsExclusive);
    }

    private static void StartupRecoversBeforeReturningSharedLease()
    {
        BackupRestorePhase[] phases =
        [
            BackupRestorePhase.Installing,
            BackupRestorePhase.Committed
        ];

        foreach (var phase in phases)
        {
            using var fixture = new RestoreRecoveryFixture(phase);
            fixture.Write(fixture.PreviousDirectory, "appearance.json", "original settings");
            fixture.Write(fixture.PreviousDirectory, "logs/chat.log", "original log");
            fixture.Write(fixture.StagingDirectory, "unused.txt", "staged data");

            fixture.Write(
                fixture.DataDirectory,
                "appearance.json",
                phase == BackupRestorePhase.Installing
                    ? "partial settings"
                    : "restored settings");
            fixture.Write(
                fixture.DataDirectory,
                "logs/chat.log",
                phase == BackupRestorePhase.Installing
                    ? "partial log"
                    : "restored log");

            if (phase == BackupRestorePhase.Installing)
            {
                fixture.Write(fixture.DataDirectory, "new-only.txt", "partial installation");
            }

            fixture.Journal.Save(fixture.State, fixture.Lease);
            fixture.Lease.Dispose();

            using var startup = ClientDataDirectory.AcquireForStartup(
                fixture.DataDirectory, out var recoveredPhase);

            Assert.False(startup.IsExclusive);
            Assert.Equal(
                phase == BackupRestorePhase.Installing
                    ? BackupRestorePhase.RolledBack
                    : BackupRestorePhase.Committed,
                recoveredPhase!.Value);

            if (phase == BackupRestorePhase.Installing)
            {
                fixture.AssertCurrentFiles(
                    ("appearance.json", "original settings"),
                    ("logs/chat.log", "original log"));
            }
            else
            {
                fixture.AssertCurrentFiles(
                    ("appearance.json", "restored settings"),
                    ("logs/chat.log", "restored log"));
            }

            fixture.AssertCompleted();

            using var companion = DataDirectoryLease.AcquireShared(
                fixture.DataDirectory);
            Assert.False(companion.IsExclusive);

            Assert.Throws<IOException>(() =>
            {
                using var unexpected = DataDirectoryLease.AcquireExclusive(
                    fixture.DataDirectory);
            });
        }
    }

    private static void FailedStartupReleasesDataLeases()
    {
        string[] failures =
        [
            "invalid journal",
            "missing originals",
            "shared access"
        ];

        foreach (var failure in failures)
        {
            using var fixture = new RestoreRecoveryFixture(
                BackupRestorePhase.Installing);
            fixture.Write(fixture.DataDirectory, "appearance.json", "current settings");
            fixture.Write(fixture.PreviousDirectory, "appearance.json", "original settings");
            fixture.Write(fixture.StagingDirectory, "appearance.json", "staged settings");
            fixture.Journal.Save(fixture.State, fixture.Lease);
            fixture.Lease.Dispose();

            if (failure == "invalid journal")
            {
                File.WriteAllText(fixture.Journal.Path, "{");
            }
            else if (failure == "missing originals")
            {
                Directory.Delete(fixture.PreviousDirectory, recursive: true);
            }

            var journalBytes = File.ReadAllBytes(fixture.Journal.Path);
            using var blocker = failure == "shared access"
                ? DataDirectoryLease.AcquireShared(fixture.DataDirectory)
                : null;

            if (failure == "invalid journal")
            {
                Assert.Throws<InvalidDataException>(() =>
                {
                    using var unexpected = ClientDataDirectory.AcquireForStartup(
                        fixture.DataDirectory, out _);
                });
            }
            else
            {
                Assert.Throws<IOException>(() =>
                {
                    using var unexpected = ClientDataDirectory.AcquireForStartup(
                        fixture.DataDirectory, out _);
                });
            }

            fixture.AssertCurrentFiles(("appearance.json", "current settings"));
            Assert.True(File.ReadAllBytes(fixture.Journal.Path)
                .SequenceEqual(journalBytes));
            Assert.Equal("staged settings", File.ReadAllText(
                Path.Combine(fixture.StagingDirectory, "appearance.json")));

            blocker?.Dispose();

            using var exclusive = DataDirectoryLease.AcquireExclusive(
                fixture.DataDirectory);
            Assert.True(exclusive.IsExclusive);
        }
    }

    private static void ResolvesRestoreArchivePaths()
    {
        using var temporary = new TemporaryDirectory();
        var manager = new BackupManager(temporary.Path);

        foreach (var filename in new[]
        {
            "clircs-backup-20261021-123456.zip",
            "snapshot with spaces.zip",
            "café こんにちは.zip"
        })
        {
            Assert.Equal(
                Path.Combine(manager.BackupDirectory, filename),
                manager.ResolveRestorePath(filename));
        }

        var fullPath = Path.Combine(
            temporary.Path, "archive folder", "snapshot with spaces.zip");
        Assert.Equal(fullPath, manager.ResolveRestorePath(fullPath));

        var parsed = (CommandInput)CommandLineParser.Parse(
            $"/backup restore \"{fullPath}\"");
        Assert.Equal(2, parsed.Arguments.Count);
        Assert.Equal(fullPath, manager.ResolveRestorePath(parsed.Arguments[1]));

        var variable = $"CLIRCS_RESTORE_TEST_{Guid.NewGuid():N}";
        var previousValue = Environment.GetEnvironmentVariable(variable);

        try
        {
            Environment.SetEnvironmentVariable(
                variable, Path.GetDirectoryName(fullPath));

            Assert.Equal(
                fullPath,
                manager.ResolveRestorePath(
                    $"%{variable}%{Path.DirectorySeparatorChar}{Path.GetFileName(fullPath)}"));

            Environment.SetEnvironmentVariable(variable, "snapshot with spaces.zip");

            Assert.Equal(
                Path.Combine(manager.BackupDirectory, "snapshot with spaces.zip"),
                manager.ResolveRestorePath($"%{variable}%"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previousValue);
        }

        string?[] rejected =
        [
            null,
            "",
            " ",
            ".",
            "..",
            "../snapshot.zip",
            @"..\snapshot.zip",
            "nested/snapshot.zip",
            @"nested\snapshot.zip",
            "/snapshot.zip",
            @"\snapshot.zip",
            "C:snapshot.zip"
        ];

        foreach (var path in rejected)
        {
            Assert.Throws<ArgumentException>(() =>
                manager.ResolveRestorePath(path!));
        }
    }

    private static void RestoreConfirmationRequiresExplicitAcceptance()
    {
        string[] accepted = ["y", "Y", "yes", "YES", " YeS "];

        foreach (var response in accepted)
        {
            Assert.True(ClientApplication.IsBackupRestoreConfirmed(response),
                $"Explicit confirmation was rejected: {response}");
        }

        string?[] rejected =
        [
            null,
            "",
            " ",
            "n",
            "N",
            "no",
            "NO",
            "true",
            "1",
            "yesterday",
            "yes please",
            "y n",
            "restore"
        ];

        foreach (var response in rejected)
        {
            Assert.False(ClientApplication.IsBackupRestoreConfirmed(response),
                $"Restore accepted an invalid confirmation: {response ?? "(null)"}");
        }
    }

    private static void AssertRejectedArchive(params string[] paths)
    {
        WithArchive(
            archive =>
            {
                foreach (var path in paths)
                {
                    archive.CreateEntry(path);
                }
            },
            archive => Assert.Throws<InvalidDataException>(
                () => BackupRestoreArchive.Validate(archive)));
    }

    private static void WithArchive(
        Action<ZipArchive> populate,
        Action<ZipArchive> inspect)
    {
        using var contents = new MemoryStream();
        using (var archive = new ZipArchive(
            contents, ZipArchiveMode.Create, leaveOpen: true))
        {
            populate(archive);
        }

        contents.Position = 0;
        using var reopened = new ZipArchive(contents, ZipArchiveMode.Read);
        inspect(reopened);
    }

    private static byte[] ReadEntry(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path)
            ?? throw new InvalidOperationException($"Missing backup entry: {path}");
        using var contents = new MemoryStream();
        BackupRestoreArchive.CopyEntry(entry, contents);
        return contents.ToArray();
    }

    private sealed class SimulatedRestoreInterruptionException : Exception
    {
    }

    private sealed class RestoreApplyFixture : IDisposable
    {
        private readonly TemporaryDirectory _temporary = new();
        private readonly Dictionary<string, byte[]> _archives;

        public RestoreApplyFixture()
        {
            Lease = DataDirectoryLease.AcquireExclusive(DataDirectory);
            Journal = new BackupRestoreJournal(DataDirectory);
            WriteFiles(SavedFiles);

            var manager = new BackupManager(DataDirectory);
            manager.Create();
            var archivePath = manager.Create();
            _archives = Directory.EnumerateFiles(
                    manager.BackupDirectory, "*.zip")
                .ToDictionary(
                    path => path,
                    File.ReadAllBytes,
                    StringComparer.OrdinalIgnoreCase);

            foreach (var entry in Directory.EnumerateFileSystemEntries(
                DataDirectory).ToArray())
            {
                if (Path.GetFileName(entry).Equals(
                    "backups", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }

            WriteFiles(OriginalFiles);
            Prepared = PreparedBackupRestore.Create(
                DataDirectory, archivePath);
        }

        public string DataDirectory => _temporary.Path;
        public DataDirectoryLease Lease { get; }
        public BackupRestoreJournal Journal { get; }
        public PreparedBackupRestore Prepared { get; }

        public IReadOnlyDictionary<string, byte[]> SavedFiles { get; } =
            new Dictionary<string, byte[]>
            {
                ["appearance.json"] = Encoding.UTF8.GetBytes("saved settings"),
                ["networks.toml"] = Encoding.UTF8.GetBytes("saved profiles"),
                ["logs/chat.log"] = Encoding.UTF8.GetBytes("café こんにちは 😀"),
                ["scripts/data.bin"] = [0x00, 0x7F, 0x80, 0xFF],
                ["switched/child.txt"] = Encoding.UTF8.GetBytes("saved directory"),
                ["old-only.txt"] = Encoding.UTF8.GetBytes("restore this file")
            };

        public IReadOnlyDictionary<string, byte[]> OriginalFiles { get; } =
            new Dictionary<string, byte[]>
            {
                ["appearance.json"] = Encoding.UTF8.GetBytes("current settings"),
                ["networks.toml"] = Encoding.UTF8.GetBytes("current profiles"),
                ["logs/current.log"] = Encoding.UTF8.GetBytes("current log"),
                ["scripts/current.lua"] = Encoding.UTF8.GetBytes("current script"),
                ["switched"] = Encoding.UTF8.GetBytes("currently a file"),
                ["later.txt"] = Encoding.UTF8.GetBytes("added after backup")
            };

        private void WriteFiles(IReadOnlyDictionary<string, byte[]> files)
        {
            foreach (var file in files)
            {
                var path = Path.Combine(DataDirectory, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, file.Value);
            }
        }

        public void AssertCurrentFiles(
            IReadOnlyDictionary<string, byte[]> expected)
        {
            var actual = Directory.EnumerateFiles(
                    DataDirectory, "*", SearchOption.AllDirectories)
                .Select(path => new
                {
                    FullPath = path,
                    RelativePath = Path.GetRelativePath(DataDirectory, path)
                        .Replace('\\', '/')
                })
                .Where(file => !file.RelativePath.StartsWith(
                    "backups/", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(
                    file => file.RelativePath,
                    file => File.ReadAllBytes(file.FullPath),
                    StringComparer.OrdinalIgnoreCase);

            Assert.Equal(expected.Count, actual.Count);

            foreach (var file in expected)
            {
                Assert.True(actual.TryGetValue(file.Key, out var contents),
                    $"Missing restored file: {file.Key}");
                Assert.True(contents!.SequenceEqual(file.Value),
                    $"Restored content differs for {file.Key}.");
            }
        }

        public void AssertCompleted()
        {
            Assert.False(Directory.Exists(Prepared.WorkspaceDirectory));
            Assert.True(Journal.Load() is null);
            Assert.False(Directory.EnumerateDirectories(
                Journal.BackupDirectory).Any());
            Assert.False(Directory.EnumerateFiles(
                Journal.BackupDirectory, "*.tmp").Any());

            foreach (var archive in _archives)
            {
                Assert.True(File.ReadAllBytes(archive.Key)
                    .SequenceEqual(archive.Value),
                    $"Backup archive changed: {archive.Key}");
            }
        }

        public void Dispose()
        {
            try
            {
                Prepared.Dispose();
            }
            finally
            {
                Lease.Dispose();
                _temporary.Dispose();
            }
        }
    }

    private sealed class RestoreRecoveryFixture : IDisposable
    {
        private readonly TemporaryDirectory _temporary = new();

        public RestoreRecoveryFixture(BackupRestorePhase phase)
        {
            Lease = DataDirectoryLease.AcquireExclusive(DataDirectory);
            Journal = new BackupRestoreJournal(DataDirectory);
            State = new BackupRestoreState(RestoreWorkspaceName, phase);
            WorkspaceDirectory = Journal.WorkspaceFor(State);
            StagingDirectory = Path.Combine(
                WorkspaceDirectory, PreparedBackupRestore.StagingDirectoryName);
            PreviousDirectory = Path.Combine(WorkspaceDirectory, "previous");

            Directory.CreateDirectory(StagingDirectory);
            Directory.CreateDirectory(PreviousDirectory);
            File.WriteAllText(
                Path.Combine(Journal.BackupDirectory, "keep.zip"),
                "preserved backup");
        }

        public string DataDirectory => _temporary.Path;
        public DataDirectoryLease Lease { get; }
        public BackupRestoreJournal Journal { get; }
        public BackupRestoreState State { get; }
        public string WorkspaceDirectory { get; }
        public string StagingDirectory { get; }
        public string PreviousDirectory { get; }

        public void Write(string directory, string relativePath, string contents)
        {
            var path = Path.Combine(directory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public void AssertCurrentFiles(
            params (string Path, string Contents)[] expected)
        {
            var actual = Directory.EnumerateFiles(
                    DataDirectory, "*", SearchOption.AllDirectories)
                .Select(path => new
                {
                    FullPath = path,
                    RelativePath = Path.GetRelativePath(DataDirectory, path)
                        .Replace('\\', '/')
                })
                .Where(file => !file.RelativePath.StartsWith(
                    "backups/", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(
                    file => file.RelativePath,
                    file => File.ReadAllText(file.FullPath),
                    StringComparer.OrdinalIgnoreCase);

            Assert.Equal(expected.Length, actual.Count);

            foreach (var file in expected)
            {
                Assert.True(actual.TryGetValue(file.Path, out var contents),
                    $"Missing restored file: {file.Path}");
                Assert.Equal(file.Contents, contents!);
            }
        }

        public void AssertCompleted()
        {
            Assert.False(Directory.Exists(WorkspaceDirectory));
            Assert.True(Journal.Load() is null);
            Assert.Equal("preserved backup", File.ReadAllText(
                Path.Combine(Journal.BackupDirectory, "keep.zip")));
        }

        public void Dispose()
        {
            Lease.Dispose();
            _temporary.Dispose();
        }
    }

    private sealed class CancelOnWriteStream : MemoryStream
    {
        private readonly CancellationTokenSource _cancellation;

        public CancelOnWriteStream(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, count);
            _cancellation.Cancel();
        }
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
