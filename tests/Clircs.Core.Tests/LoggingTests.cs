using Clircs.ConsoleClient;
using Clircs.Identity;
using Clircs.Sessions;
using Clircs.State;

namespace Clircs.Core.Tests;

internal static class LoggingTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("logging rules inherit and persist target overrides", RulesPersist);
        suite.Add("event logger writes daily UTF-8 files by window kind", WritesDailyFilesAsync);
        suite.Add("event logger retains a burst larger than its former queue", RetainsLargeBurstAsync);
        suite.Add("log formatter records semantic events but skips startup UI", FormatsSemanticEvents);
        suite.Add("logging status shows network and effective window rules", StatusPresentationIsReadable);
    }

    private static void RulesPersist()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "logging.json");
        var id = NetworkProfileId.New();
        var store = new LoggingSettingsStore(path);

        Assert.False(store.IsEnabled(id, "#clircs"));
        store.SetNetwork(id, "EFnet", true);
        Assert.True(store.IsEnabled(id, "#clircs"));
        store.SetTarget(id, "EFnet", "#clircs", false);
        Assert.False(store.IsEnabled(id, "#clircs"));
        Assert.True(store.IsEnabled(id, "#other"));

        var reloaded = new LoggingSettingsStore(path);
        Assert.False(reloaded.IsEnabled(id, "#clircs"));
        Assert.True(reloaded.IsEnabled(id, "#other"));
        reloaded.SetNetwork(id, "EFnet", false);
        Assert.True(reloaded.TargetOverride(id, "#clircs") is null);
    }

    private static async ValueTask WritesDailyFilesAsync()
    {
        using var temporary = new TemporaryDirectory();
        var timestamp = new DateTimeOffset(2026, 7, 28, 12, 34, 56, TimeSpan.Zero);
        var writer = new EventLogWriter(temporary.Path);
        writer.Enqueue("EFnet", BufferKind.Channel, "#clircs", timestamp, ["<slakker> hello"]);
        writer.Enqueue("EFnet", BufferKind.Query, "rekkals", timestamp, ["<rekkals> hi"]);
        writer.Enqueue("EFnet", BufferKind.Status, "status", timestamp, ["Connected."]);
        writer.Enqueue("EFnet", BufferKind.Diagnostics, "=debug", timestamp, [">> CAP LS 302"]);
        await writer.DisposeAsync();

        var channel = Path.Combine(temporary.Path, "EFnet", "#clircs", "2026-07-28.log");
        var query = Path.Combine(temporary.Path, "EFnet", "queries", "rekkals", "2026-07-28.log");
        var status = Path.Combine(temporary.Path, "EFnet", "status", "2026-07-28.log");
        var debug = Path.Combine(temporary.Path, "EFnet", "debug", "2026-07-28.log");
        Assert.True(File.Exists(channel));
        Assert.True(File.Exists(query));
        Assert.True(File.Exists(status));
        Assert.True(File.Exists(debug));
        Assert.Equal("[12:34:56] <slakker> hello", File.ReadAllText(channel).Trim());
        Assert.Equal("[12:34:56] >> CAP LS 302", File.ReadAllText(debug).Trim());
    }

    private static async ValueTask RetainsLargeBurstAsync()
    {
        const int eventCount = 5_000;
        using var temporary = new TemporaryDirectory();
        var timestamp = new DateTimeOffset(2026, 8, 25, 12, 34, 56, TimeSpan.Zero);
        var writer = new EventLogWriter(temporary.Path);
        for (var index = 0; index < eventCount; index++)
        {
            Assert.Equal(ResourceQueueWriteResult.Accepted, writer.Enqueue(
                "EFnet", BufferKind.Channel, "#clircs", timestamp, [$"<flooder> line {index}"]));
        }
        await writer.DisposeAsync();

        var path = Path.Combine(temporary.Path, "EFnet", "#clircs", "2026-08-25.log");
        var lines = File.ReadAllLines(path);
        Assert.Equal(eventCount, lines.Length);
        Assert.True(lines[0].EndsWith("line 0", StringComparison.Ordinal));
        Assert.True(lines[^1].EndsWith($"line {eventCount - 1}", StringComparison.Ordinal));
    }

    private static void FormatsSemanticEvents()
    {
        var session = NetworkSessionId.New();
        var buffer = BufferId.New();
        var message = new SessionEvent(
            session,
            buffer,
            SessionEventKind.Message,
            "ignored",
            DateTimeOffset.Now,
            new Dictionary<string, string?>
            {
                ["nick"] = "slakker",
                ["message"] = "hello",
                ["nickPrefix"] = "@"
            });
        Assert.Equal("<@slakker> hello", TranscriptFormatter.FormatLines(message).Single());

        var presentation = message with
        {
            Kind = SessionEventKind.Server,
            Presentation = new PresentationBlock(
                "WHOIS: slakker",
                [new PresentationField("Address", "~slakker@example.net")])
        };
        Assert.Equal("WHOIS: slakker,Address  ~slakker@example.net",
            string.Join(',', TranscriptFormatter.FormatLines(presentation)));

        var startup = message with
        {
            Fields = new Dictionary<string, string?> { ["event"] = "startup" }
        };
        Assert.Equal(0, TranscriptFormatter.FormatLines(startup).Count);

        var debug = message with
        {
            Kind = SessionEventKind.Diagnostic,
            Text = ">> AUTHENTICATE payload"
        };
        Assert.Equal(">> AUTHENTICATE payload", TranscriptFormatter.FormatLines(debug).Single());

        var transientProgress = message with
        {
            Kind = SessionEventKind.Status,
            Text = "DCC SEND #4: 50%",
            Fields = new Dictionary<string, string?>
            {
                ["history.transientKey"] = "dcc.transfer.4"
            }
        };
        Assert.Equal(0, TranscriptFormatter.FormatLines(transientProgress).Count);

        var themedJoin = message with
        {
            Kind = SessionEventKind.Join,
            Text = "themed output is irrelevant",
            Fields = new Dictionary<string, string?>
            {
                ["nick"] = "slakker",
                ["username"] = "~slakker",
                ["host"] = "example.net",
                ["channel"] = "#clircs"
            }
        };
        Assert.Equal("--> slakker [~slakker@example.net] joined #clircs",
            TranscriptFormatter.FormatLines(themedJoin).Single());
    }

    private static void StatusPresentationIsReadable()
    {
        var channel = new BufferState(
            BufferId.New(), NetworkSessionId.New(), BufferKind.Channel, "#ereet");
        var overridden = ClientApplication.LoggingStatusPresentation(
            "EFnet", channel, networkEnabled: false, windowOverride: true);
        Assert.Equal("Logging: EFnet/#ereet", overridden.Title);
        Assert.Equal("Network,off;Channel,on (channel override)", string.Join(';',
            overridden.Fields!.Select(field => $"{field.Label},{field.Value}")));

        var inherited = ClientApplication.LoggingStatusPresentation(
            "EFnet", channel, networkEnabled: true, windowOverride: null);
        Assert.Equal("Network,on;Channel,on", string.Join(';',
            inherited.Fields!.Select(field => $"{field.Label},{field.Value}")));

        var query = channel with { Kind = BufferKind.Query, Name = "rekkals" };
        var disabled = ClientApplication.LoggingStatusPresentation(
            "EFnet", query, networkEnabled: true, windowOverride: false);
        Assert.Equal("Query,off (query override)", $"{disabled.Fields![1].Label},{disabled.Fields[1].Value}");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"clircs-logging-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
