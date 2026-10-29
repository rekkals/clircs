using Clircs.Commands;
using Clircs.ConsoleClient;
using Clircs.Identity;
using Clircs.Protocol;
using Clircs.Scripting;
using Clircs.Sessions;
using Clircs.State;

namespace Clircs.Core.Tests;

internal static class ScriptWindowTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("script windows are scoped and reused across reconnects", WindowsAreScopedAndReused);
        suite.Add("script window lookup matches complete names in one scope", LookupUsesCompleteNames);
        suite.Add("script window limits apply across local and connection scopes", LimitsApplyAcrossScopes);
        suite.Add("script window removal preserves unrelated windows", RemovalPreservesOtherWindows);
        suite.Add("invalid script window names and native collisions are rejected", InvalidNamesAndCollisionsAreRejected);
        suite.Add("session removal invalidates its input handlers and preserves other scopes", RemovingWindowsInvalidatesInputAsync);
        suite.Add("script output inherits base styles without leaking inline styles", OutputStylesRemainScoped);
        suite.Add("script output retains IRC formatting and restores base colors", OutputRetainsIrcFormatting);
        suite.Add("script output preserves text while filtering terminal controls", OutputPreservesText);
        suite.Add("script output rejects invalid styles and missing text", OutputRejectsInvalidStyles);
        suite.Add("script output limits count all input before filtering", OutputLimitsCountAllInput);
        suite.Add("window host opens background windows without changing focus", HostOpensBackgroundWindowsAsync);
        suite.Add("application input preserves text and context while keeping commands native", HostInputPreservesTextAsync);
        suite.Add("window host stores output in its explicit destination", HostStoresTargetedOutputAsync);
        suite.Add("window host validates output before changing history", HostValidatesBeforeWritingAsync);
        suite.Add("window host rejects output after application shutdown", HostRejectsWritesAfterShutdownAsync);
        suite.Add("script output paging preserves exact wrapped rows across widths", ScriptOutputPagingPreservesRows);
        suite.Add("script output respects viewport row budgets", ScriptOutputRespectsViewportBudgets);
    }

    private static void WindowsAreScopedAndReused()
    {
        var windows = new ScriptWindowRegistry();
        var firstSession = NewSession();
        var secondSession = NewSession();

        var first = windows.Open("alpha", "alerts", firstSession);
        var second = windows.Open("alpha", "alerts", secondSession);
        var local = windows.Open("alpha", "alerts", null);
        var otherOwner = windows.Open("beta", "alerts", firstSession);

        Assert.Equal(
            4,
            new[] { first.Id, second.Id, local.Id, otherOwner.Id }.Distinct().Count());
        Assert.Equal("=alpha.alerts", first.Name);
        Assert.Equal(BufferKind.Script, first.Kind);
        Assert.Equal(firstSession.Id, first.NetworkSessionId);
        Assert.Equal(secondSession.Id, second.NetworkSessionId);
        Assert.True(firstSession.TryGetBuffer(first.Id, out var registered));
        Assert.Equal(first, registered!);
        Assert.Equal(local.Id, windows.LocalBuffersSnapshot().Single().Id);

        Assert.Equal(
            first.Id,
            windows.Open("ALPHA", "ALERTS", firstSession).Id);
        Assert.Equal(local.Id, windows.Open("alpha", "alerts", null).Id);

        firstSession.ResetForReconnect(clientTransportTls: true);

        Assert.Equal(
            first.Id,
            windows.Open("alpha", "alerts", firstSession).Id);
        Assert.True(firstSession.TryGetBuffer(first.Id, out _));
    }

    private static void LookupUsesCompleteNames()
    {
        var windows = new ScriptWindowRegistry();
        var session = NewSession();
        var connected = windows.Open("alpha", "alerts", session);
        var local = windows.Open("alpha", "alerts", null);

        Assert.Equal(
            connected.Id,
            windows.Find("=ALPHA.ALERTS", session.Id)!.Id);
        Assert.Equal(local.Id, windows.Find("=alpha.alerts", null)!.Id);
        Assert.Equal(connected.Id, windows.Find(connected.Id)!.Id);

        Assert.True(windows.Find("alerts", session.Id) is null);
        Assert.True(windows.Find("=alpha.alert", session.Id) is null);
        Assert.True(windows.Find("*alerts*", session.Id) is null);
        Assert.True(
            windows.Find("=alpha.alerts", NetworkSessionId.New()) is null);
        Assert.True(windows.Find(BufferId.New()) is null);
    }

    private static void LimitsApplyAcrossScopes()
    {
        var windows = new ScriptWindowRegistry();
        var firstSession = NewSession();
        var secondSession = NewSession();
        NetworkSessionState?[] scopes = [null, firstSession, secondSession];
        var opened = new List<BufferState>();

        for (var index = 0; index < ScriptWindowRegistry.MaximumWindowsPerScript; index++)
        {
            opened.Add(windows.Open(
                "alpha", $"w{index}", scopes[index % scopes.Length]));
        }

        var firstCount = firstSession.Buffers.Count;
        var secondCount = secondSession.Buffers.Count;

        foreach (var scope in scopes)
        {
            Assert.Throws<InvalidOperationException>(() =>
                windows.Open("alpha", "overflow", scope));
        }

        Assert.Equal(firstCount, firstSession.Buffers.Count);
        Assert.Equal(secondCount, secondSession.Buffers.Count);
        Assert.Equal(opened[0].Id, windows.Open("ALPHA", "W0", null).Id);
        Assert.Equal(opened[1].Id, windows.Open("alpha", "w1", firstSession).Id);

        var otherOwner = windows.Open("beta", "w0", null);
        Assert.Equal(otherOwner.Id, windows.Find("=beta.w0", null)!.Id);

        Assert.True(windows.Remove(opened[0].Id));
        Assert.True(windows.Find(opened[0].Id) is null);

        var replacement = windows.Open("alpha", "w0", null);
        Assert.False(replacement.Id == opened[0].Id);
        Assert.Equal(replacement.Id, windows.Find("=alpha.w0", null)!.Id);

        Assert.Throws<InvalidOperationException>(() =>
            windows.Open("alpha", "overflow", null));
    }

    private static void RemovalPreservesOtherWindows()
    {
        var windows = new ScriptWindowRegistry();
        var firstSession = NewSession();
        var secondSession = NewSession();
        var first = windows.Open("alpha", "alerts", firstSession);
        var otherOwner = windows.Open("beta", "alerts", firstSession);
        var second = windows.Open("alpha", "alerts", secondSession);
        var local = windows.Open("alpha", "alerts", null);

        windows.RemoveSession(firstSession.Id);

        Assert.True(windows.Find(first.Id) is null);
        Assert.True(windows.Find(otherOwner.Id) is null);
        Assert.Equal(second.Id, windows.Find(second.Id)!.Id);
        Assert.Equal(local.Id, windows.Find(local.Id)!.Id);
        Assert.Equal(local.Id, windows.LocalBuffersSnapshot().Single().Id);

        windows.RemoveSession(firstSession.Id);
        Assert.Equal(second.Id, windows.Find(second.Id)!.Id);

        Assert.True(secondSession.RemoveBuffer(second.Id));
        Assert.True(windows.Remove(second.Id));
        Assert.False(windows.Remove(second.Id));

        var reopened = windows.Open("alpha", "alerts", secondSession);
        Assert.False(reopened.Id == second.Id);
        Assert.True(windows.Find(second.Id) is null);
        Assert.Equal(reopened.Id, windows.Find("=alpha.alerts", secondSession.Id)!.Id);
        Assert.Equal(local.Id, windows.Find(local.Id)!.Id);
    }

    private static void InvalidNamesAndCollisionsAreRejected()
    {
        var windows = new ScriptWindowRegistry();
        var session = NewSession();
        string[] invalid =
        [
            null!,
            "",
            " ",
            "with space",
            "with.dot",
            "with/slash",
            "café",
            "\u001b[31m",
            new string('a', 65)
        ];

        foreach (var name in invalid)
        {
            Assert.Throws<ArgumentException>(() =>
                windows.Open("alpha", name, session));
            Assert.Throws<ArgumentException>(() =>
                windows.Open("alpha", name, null));
        }

        Assert.Equal(1, session.Buffers.Count);
        Assert.Equal(0, windows.LocalBuffersSnapshot().Length);

        var existing = session.GetOrCreateBuffer(
            BufferKind.Results, "=alpha.alerts");

        Assert.Throws<InvalidOperationException>(() =>
            windows.Open("alpha", "alerts", session));

        Assert.True(session.TryGetBuffer(existing.Id, out var retained));
        Assert.Equal(existing, retained!);
        Assert.True(windows.Find(existing.Id) is null);

        var valid = windows.Open("alpha", "now-playing_1", session);
        Assert.Equal("=alpha.now-playing_1", valid.Name);

        var maximumName = new string('a', 64);
        var maximum = windows.Open("alpha", maximumName, null);
        Assert.Equal($"=alpha.{maximumName}", maximum.Name);
    }

    private static async ValueTask RemovingWindowsInvalidatesInputAsync()
    {
        var windows = new ScriptWindowRegistry();
        var firstSession = NewSession();
        var secondSession = NewSession();
        var first = windows.Open("alpha", "chat", firstSession);
        var sibling = windows.Open("beta", "chat", firstSession);
        var other = windows.Open("alpha", "chat", secondSession);
        var local = windows.Open("alpha", "editor", null);
        ScriptWindowInputHandler callback = (_, text, _) =>
            ValueTask.FromResult(CommandResult.Success(text));
        using var firstRegistration = RequireInputRegistration(windows, "alpha", first.Id, callback);
        using var siblingRegistration = RequireInputRegistration(windows, "beta", sibling.Id, callback);
        using var otherRegistration = RequireInputRegistration(windows, "alpha", other.Id, callback);
        using var localRegistration = RequireInputRegistration(windows, "alpha", local.Id, callback);
        var context = new CommandContext(firstSession.Id, first.Id);
        var captured = RequireInputHandler(windows, context);

        windows.RemoveSession(firstSession.Id);

        Assert.False(firstRegistration.IsActive);
        Assert.False(siblingRegistration.IsActive);
        Assert.True(windows.Find(first.Id) is null);
        Assert.True(windows.Find(sibling.Id) is null);
        Assert.False((await captured(context, "stale", CancellationToken.None)).Succeeded);
        Assert.True(otherRegistration.IsActive);
        Assert.True(localRegistration.IsActive);
        var otherContext = new CommandContext(secondSession.Id, other.Id);
        var localContext = new CommandContext(null, local.Id);
        Assert.True((await RequireInputHandler(windows, otherContext)(
            otherContext, "still connected", CancellationToken.None)).Succeeded);
        Assert.True((await RequireInputHandler(windows, localContext)(
            localContext, "independent", CancellationToken.None)).Succeeded);
    }

    private static IScriptWindowInputRegistration RequireInputRegistration(
        ScriptWindowRegistry windows,
        string scriptId,
        BufferId bufferId,
        ScriptWindowInputHandler handler) =>
        windows.SetInputHandler(scriptId, bufferId, handler)
            ?? throw new InvalidOperationException("Expected an input registration.");

    private static ScriptWindowInputHandler RequireInputHandler(
        ScriptWindowRegistry windows,
        CommandContext context) =>
        windows.FindInputHandler(context)
            ?? throw new InvalidOperationException("Expected an active input handler.");

    private static void OutputStylesRemainScoped()
    {
        var formatted = ScriptWindowOutputFormatter.Format(new ScriptWindowOutput(
            [
                new ScriptOutputPart("\u0002before "),
                new ScriptOutputPart(
                    "warning", new ScriptOutputStyle(Role: TextColorRole.Warning)),
                new ScriptOutputPart(
                    " fixed", new ScriptOutputStyle(Color: ConsoleColor.Green)),
                new ScriptOutputPart(" after")
            ],
            new ScriptOutputStyle(Role: TextColorRole.Notice)));

        Assert.Equal("before warning fixed after", formatted.PlainText);
        Assert.True(formatted.HasFormatting);

        var before = StyleFor(formatted, "before");
        var warning = StyleFor(formatted, "warning");
        var fixedColor = StyleFor(formatted, "fixed");
        var after = StyleFor(formatted, "after");

        Assert.True(before.ForegroundRole == TextColorRole.Notice);
        Assert.True(before.Bold);
        Assert.True(warning.ForegroundRole == TextColorRole.Warning);
        Assert.False(warning.Bold);
        Assert.True(fixedColor.BaseForeground == ConsoleColor.Green);
        Assert.True(fixedColor.ForegroundRole is null);
        Assert.True(after.ForegroundRole == TextColorRole.Notice);
        Assert.True(after.BaseForeground is null);
        Assert.False(after.Bold);
    }

    private static void OutputRetainsIrcFormatting()
    {
        var theme = TerminalTheme.BuiltIns["clircs"] with
        {
            Notice = ConsoleColor.DarkCyan
        };
        var text =
            "base \u000304red\u000f reset \u000399default\u0003 end " +
            "\u0002bold\u000f \u001ditalic\u000f " +
            "\u001funderline\u000f \u0016reverse\u000f";

        (ScriptOutputStyle Style, ConsoleColor Expected)[] cases =
        [
            (new ScriptOutputStyle(Role: TextColorRole.Notice), ConsoleColor.DarkCyan),
            (new ScriptOutputStyle(Color: ConsoleColor.DarkMagenta), ConsoleColor.DarkMagenta)
        ];

        foreach (var item in cases)
        {
            var formatted = ScriptWindowOutputFormatter.Format(new ScriptWindowOutput(
                [new ScriptOutputPart(text)],
                item.Style));

            Assert.Equal(
                "base red reset default end bold italic underline reverse",
                formatted.PlainText);

            Assert.Equal(
                ConsoleColor.Red,
                ConsolePresenter.FormattedForeground(
                    theme, StyleFor(formatted, "red"), ConsoleColor.Gray));

            foreach (var word in new[] { "base", "reset", "default", "end" })
            {
                Assert.Equal(
                    item.Expected,
                    ConsolePresenter.FormattedForeground(
                        theme, StyleFor(formatted, word), ConsoleColor.Gray));
            }

            Assert.True(StyleFor(formatted, "bold").Bold);
            Assert.True(StyleFor(formatted, "italic").Italic);
            Assert.True(StyleFor(formatted, "underline").Underline);
            Assert.True(StyleFor(formatted, "reverse").Reverse);
            Assert.False(StyleFor(formatted, "italic").Bold);
        }
    }

    private static void OutputPreservesText()
    {
        var formatted = ScriptWindowOutputFormatter.Format(new ScriptWindowOutput(
            [new ScriptOutputPart("  café\t世界.  \r\n\u001b\u0007\u202e")]));

        Assert.Equal("  café\t世界.  ", formatted.PlainText);
        Assert.True(StyleFor(formatted, "café").ForegroundRole == TextColorRole.Normal);
        Assert.Equal(
            formatted.PlainText,
            string.Concat(formatted.Runs.Select(run => run.Text)));
    }

    private static void OutputRejectsInvalidStyles()
    {
        ScriptOutputStyle[] invalid =
        [
            new(Role: TextColorRole.Warning, Color: ConsoleColor.Cyan),
            new(Role: (TextColorRole)(-1)),
            new(Color: (ConsoleColor)(-1))
        ];

        foreach (var style in invalid)
        {
            Assert.Throws<ArgumentException>(() =>
                ScriptWindowOutputFormatter.Format(new ScriptWindowOutput(
                    [new ScriptOutputPart("text")],
                    style)));

            Assert.Throws<ArgumentException>(() =>
                ScriptWindowOutputFormatter.Format(new ScriptWindowOutput(
                    [
                        new ScriptOutputPart("before "),
                        new ScriptOutputPart("invalid", style)
                    ])));
        }

        Assert.Throws<ArgumentNullException>(() =>
            ScriptWindowOutputFormatter.Format(null!));
        Assert.Throws<ArgumentNullException>(() =>
            ScriptWindowOutputFormatter.Format(new ScriptWindowOutput(null!)));
        Assert.Throws<ArgumentNullException>(() =>
            ScriptWindowOutputFormatter.Format(new ScriptWindowOutput([null!])));
        Assert.Throws<ArgumentNullException>(() =>
            ScriptWindowOutputFormatter.Format(new ScriptWindowOutput(
                [new ScriptOutputPart(null!)])));
    }

    private static void OutputLimitsCountAllInput()
    {
        var limit = ScriptWindowOutputFormatter.MaximumTextLength;
        var maximum = new string('x', limit);
        var single = ScriptWindowOutputFormatter.Format(new ScriptWindowOutput(
            [new ScriptOutputPart(maximum)]));

        Assert.Equal(maximum, single.PlainText);

        var fragmented = ScriptWindowOutputFormatter.Format(new ScriptWindowOutput(
            Enumerable.Repeat(new ScriptOutputPart("x"), limit).ToArray()));

        Assert.Equal(maximum, fragmented.PlainText);

        Assert.Throws<InvalidOperationException>(() =>
            ScriptWindowOutputFormatter.Format(new ScriptWindowOutput(
                [new ScriptOutputPart(new string('x', limit + 1))])));

        Assert.Throws<InvalidOperationException>(() =>
            ScriptWindowOutputFormatter.Format(new ScriptWindowOutput(
                [
                    new ScriptOutputPart(new string('x', limit / 2)),
                    new ScriptOutputPart(new string('y', limit - limit / 2 + 1))
                ])));

        Assert.Throws<InvalidOperationException>(() =>
            ScriptWindowOutputFormatter.Format(new ScriptWindowOutput(
                [new ScriptOutputPart(new string('\u0002', limit + 1))])));

        Assert.Throws<InvalidOperationException>(() =>
            ScriptWindowOutputFormatter.Format(new ScriptWindowOutput(
                Enumerable.Repeat(
                    new ScriptOutputPart(""),
                    ScriptWindowOutputFormatter.MaximumParts + 1).ToArray())));
    }

    private static ValueTask HostOpensBackgroundWindowsAsync() =>
        WithWindowHostAsync((application, host, writer) =>
    {
        var status = host.Status(null)!.Value;
        Assert.Equal(status, host.Active()!.Value);
        Assert.Equal(status, host.Find("*", null)!.Value);

        var window = host.Open("alpha", "report", null);
        Assert.False(window == status);
        Assert.Equal(status, host.Active()!.Value);
        Assert.Equal(window, host.Find("=alpha.report", null)!.Value);
        Assert.Equal(window, host.Open("alpha", "REPORT", null));

        var missingNetwork = NetworkSessionId.New();
        Assert.True(host.Status(missingNetwork) is null);
        Assert.True(host.Find("=alpha.report", missingNetwork) is null);
        Assert.Throws<InvalidOperationException>(() =>
            host.Open("alpha", "missing", missingNetwork));

        Assert.True(host.Find("=alpha.missing", null) is null);
        Assert.Equal(status, host.Active()!.Value);
    });

    private static ValueTask HostInputPreservesTextAsync() =>
        WithWindowInputHostAsync(async (application, host, writer) =>
        {
            var first = host.Open("alpha", "editor", null);
            var visible = host.Open("beta", "editor", null);
            var context = new CommandContext(null, first);
            var seen = new List<string>();
            using var registration = host.OnInput("alpha", first, (captured, text, _) =>
            {
                Assert.Equal(context, captured);
                Task.Run(() => host.List("alpha")).WaitAsync(TimeSpan.FromSeconds(5))
                    .GetAwaiter().GetResult();
                seen.Add(text);
                return ValueTask.FromResult(CommandResult.Success());
            }) ?? throw new InvalidOperationException("Expected an input registration.");
            using var visibleRegistration = host.OnInput("beta", visible, (_, _, _) =>
                throw new InvalidOperationException("Input was redirected to the visible window."))
                ?? throw new InvalidOperationException("Expected an input registration.");

            Assert.True((await application.ExecuteInputAsync(
                context, "/window =beta.editor")).Succeeded);
            writer.GetStringBuilder().Clear();
            Assert.True((await application.ExecuteInputAsync(context, "")).Succeeded);
            Assert.Equal(0, seen.Count);
            (string Line, string Text)[] cases =
            [
                ("  unmatched \"quote  C:\\notes\\  ", "  unmatched \"quote  C:\\notes\\  "),
                ("   ", "   "),
                ("//literal /slash", "/literal /slash")
            ];
            foreach (var item in cases)
            {
                Assert.True((await application.ExecuteInputAsync(context, item.Line)).Succeeded);
                Assert.Equal(item.Text, seen[^1]);
            }

            Assert.Equal(cases.Length, seen.Count);
            Assert.True(host.Active() == visible);
            Assert.Equal(0, application.WindowHistorySnapshot(first).Length);
            Assert.Equal(string.Empty, writer.ToString());
            var uptime = await application.ExecuteInputAsync(context, "/uptime");
            Assert.True(uptime.Succeeded);
            Assert.True(uptime.Message!.Contains(" has been up for ", StringComparison.Ordinal));
            Assert.False((await application.ExecuteInputAsync(context, "/say test")).Succeeded);
            Assert.Equal(cases.Length, seen.Count);

            Assert.False((await application.ExecuteInputAsync(
                new CommandContext(NetworkSessionId.New(), first), "wrong scope")).Succeeded);
            Assert.True((await application.ExecuteInputAsync(context, "/close")).Succeeded);
            Assert.False(registration.IsActive);
            Assert.False((await application.ExecuteInputAsync(context, "closed target")).Succeeded);
            Assert.True(host.Active() == visible);
            Assert.Equal(cases.Length, seen.Count);
        });

    private static ValueTask HostStoresTargetedOutputAsync() =>
        WithWindowHostAsync((application, host, writer) =>
        {
            var status = host.Status(null)!.Value;
            var destination = host.Open("alpha", "report", null);
            var other = host.Open("beta", "report", null);
            var timestamp = new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero);
            var output = new ScriptWindowOutput(
                [
                    new ScriptOutputPart("background-target "),
                    new ScriptOutputPart(
                        "attention", new ScriptOutputStyle(Role: TextColorRole.Warning)),
                    new ScriptOutputPart(" done.  ")
                ],
                new ScriptOutputStyle(Role: TextColorRole.Notice),
                timestamp);

            writer.GetStringBuilder().Clear();
            var before = DateTimeOffset.UtcNow;
            Assert.True(host.Write("alpha", destination, output));
            var after = DateTimeOffset.UtcNow;

            var entry = application.WindowHistorySnapshot(destination).Single();
            Assert.Equal(destination, entry.BufferId);
            Assert.Equal("background-target attention done.  ", entry.Text);
            Assert.Equal(timestamp, entry.Timestamp);
            Assert.True(entry.ReceivedAt >= before && entry.ReceivedAt <= after);
            Assert.Equal(SessionEventKind.Status, entry.Kind);
            Assert.Equal("script.output", entry.Fields!["event"]!);
            Assert.Equal("alpha", entry.Fields!["scriptId"]!);

            var formatted = entry.FormattedContent!;
            Assert.Equal(entry.Text, formatted.PlainText);
            Assert.True(
                StyleFor(formatted, "background-target").ForegroundRole ==
                TextColorRole.Notice);
            Assert.True(
                StyleFor(formatted, "attention").ForegroundRole ==
                TextColorRole.Warning);
            Assert.True(
                StyleFor(formatted, "done").ForegroundRole ==
                TextColorRole.Notice);

            Assert.Equal(0, application.WindowHistorySnapshot(status).Length);
            Assert.Equal(0, application.WindowHistorySnapshot(other).Length);
            Assert.Equal(status, host.Active()!.Value);
            Assert.False(writer.ToString().Contains(
                "background-target", StringComparison.Ordinal));
        });

    private static ValueTask HostValidatesBeforeWritingAsync() =>
        WithWindowHostAsync((application, host, writer) =>
        {
            var status = host.Status(null)!.Value;
            writer.GetStringBuilder().Clear();

            Assert.True(host.Write("alpha", status, new ScriptWindowOutput(
                [new ScriptOutputPart("visible-script-output.")])));

            Assert.True(writer.ToString().Contains(
                "visible-script-output.", StringComparison.Ordinal));
            Assert.Equal(1, application.WindowHistorySnapshot(status).Length);

            writer.GetStringBuilder().Clear();
            var missing = BufferId.New();
            Assert.False(host.Write("alpha", missing, new ScriptWindowOutput(
                [new ScriptOutputPart("missing-target-output")])));

            Assert.Throws<ArgumentException>(() =>
                host.Write("alpha", status, new ScriptWindowOutput(
                    [
                        new ScriptOutputPart("should-not-appear"),
                        new ScriptOutputPart(
                            "invalid",
                            new ScriptOutputStyle(
                                Role: TextColorRole.Warning,
                                Color: ConsoleColor.Cyan))
                    ])));

            Assert.Equal(1, application.WindowHistorySnapshot(status).Length);
            Assert.Equal(0, application.WindowHistorySnapshot(missing).Length);
            Assert.Equal(string.Empty, writer.ToString());
        });

    private static async ValueTask HostRejectsWritesAfterShutdownAsync()
    {
        using var temporary = new TemporaryDirectory();
        using var writer = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(writer);
            ScriptWindowHostServices host;
            BufferId window;

            var application = new ClientApplication(temporary.Path);
            await using (application)
            {
                host = application.CreateScriptWindowHostServices();
                window = host.Open("alpha", "report", null);
            }

            writer.GetStringBuilder().Clear();

            var input = await application.ExecuteInputAsync(
                new CommandContext(null, window), "after-shutdown");
            Assert.False(input.Succeeded);
            Assert.True(input.Message!.Contains(
                "shutting down", StringComparison.OrdinalIgnoreCase));

            Assert.False(host.Write("alpha", window, new ScriptWindowOutput(
                [new ScriptOutputPart("after-shutdown")])));

            Assert.Throws<InvalidOperationException>(() =>
                host.Open("alpha", "another", null));

            Assert.Equal(0, host.List("alpha").Count);

            Assert.Throws<InvalidOperationException>(() =>
                host.OnInput(
                    "alpha",
                    window,
                    (_, text, _) => ValueTask.FromResult(CommandResult.Success(text))));
            Assert.Throws<InvalidOperationException>(() =>
                host.OnInput("alpha", window, null));

            Assert.Equal(string.Empty, writer.ToString());
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    private static void ScriptOutputPagingPreservesRows()
    {
        var sessionEvent = PagingScriptEvent();
        const string bufferName = "=alpha.report";

        foreach (var showBufferName in new[] { false, true })
        {
            var presenter = new ConsolePresenter();
            presenter.SetTheme(TerminalTheme.BuiltIns["clircs"] with
            {
                ShowBufferName = showBufferName
            });

            foreach (var width in new[] { 32, 67, 120, 200 })
            {
                var full = CaptureScriptRows(() => presenter.ScriptOutputRows(
                    sessionEvent, bufferName, 0, int.MaxValue, width));

                Assert.Equal(
                    presenter.MeasureEventRows(sessionEvent, bufferName, width),
                    full.Length);
                Assert.True(full.Length > 3);

                (int Skip, int Take)[] slices =
                [
                    (0, 1),
                    (1, 1),
                    (2, 3),
                    (full.Length / 2, 2),
                    (full.Length - 1, 5),
                    (full.Length, 1),
                    (int.MaxValue, 1),
                    (-3, 2),
                    (1, 0),
                    (1, -1),
                    (1, int.MaxValue)
                ];

                foreach (var slice in slices)
                {
                    var actual = CaptureScriptRows(() => presenter.ScriptOutputRows(
                        sessionEvent, bufferName, slice.Skip, slice.Take, width));
                    var expected = full
                        .Skip(Math.Max(0, slice.Skip))
                        .Take(Math.Max(0, slice.Take))
                        .ToArray();

                    Assert.True(
                        expected.SequenceEqual(actual),
                        $"Incorrect script rows: width={width}, names={showBufferName}, " +
                        $"skip={slice.Skip}, take={slice.Take}.");
                }
            }
        }
    }

    private static void ScriptOutputRespectsViewportBudgets()
    {
        var presenter = new ConsolePresenter();
        var sessionEvent = PagingScriptEvent();
        const string bufferName = "=alpha.report";
        SessionEvent[] history = [sessionEvent];

        var full = CaptureScriptRows(() => presenter.Event(sessionEvent, bufferName));
        Assert.Equal(
            presenter.MeasureEventRows(sessionEvent, bufferName),
            full.Length);

        var budget = Math.Min(3, full.Length);
        var maximumOffset = Math.Max(0, full.Length - budget);

        foreach (var offset in new[] { 0, Math.Min(2, maximumOffset), maximumOffset })
        {
            var slices = ViewportHistory.SelectRows(
                history,
                offset,
                budget,
                item => presenter.MeasureEventRows(item, bufferName));

            var actual = CaptureScriptRows(() =>
            {
                foreach (var slice in slices)
                {
                    presenter.EventRows(
                        slice.Item, bufferName, slice.SkipRows, slice.TakeRows);
                }
            });
            var expected = full.SkipLast(offset).TakeLast(budget).ToArray();

            Assert.True(actual.Length <= budget);
            Assert.True(
                expected.SequenceEqual(actual),
                $"Script viewport repeated or omitted rows at offset {offset}.");
        }
    }

    private static SessionEvent PagingScriptEvent()
    {
        var before = string.Join(" ", Enumerable.Range(0, 100)
            .Select(index => $"before{index:000}"));
        var after = string.Join(" ", Enumerable.Range(0, 100)
            .Select(index => $"after{index:000}"));
        var formatted = ScriptWindowOutputFormatter.Format(new ScriptWindowOutput(
            [
                new ScriptOutputPart(before + " "),
                new ScriptOutputPart(
                    "\u0002" + new string('W', 180) + "\u000f",
                    new ScriptOutputStyle(Role: TextColorRole.Warning)),
                new ScriptOutputPart(" \u000304red\u000f " + after)
            ],
            new ScriptOutputStyle(Role: TextColorRole.Notice)));

        return new SessionEvent(
            NetworkSessionId.New(),
            BufferId.New(),
            SessionEventKind.Status,
            formatted.PlainText,
            new DateTimeOffset(2026, 10, 27, 12, 34, 56, TimeSpan.Zero),
            new Dictionary<string, string?>
            {
                ["event"] = "script.output",
                ["scriptId"] = "paging"
            },
            FormattedContent: formatted);
    }

    private static string[] CaptureScriptRows(Action render)
    {
        var previous = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            render();
        }
        finally
        {
            Console.SetOut(previous);
        }

        var text = writer.ToString();
        if (text.Length == 0) return [];

        Assert.True(text.EndsWith(Environment.NewLine, StringComparison.Ordinal));
        return text.Split(Environment.NewLine)[..^1];
    }

    private static IrcTextStyle StyleFor(IrcFormattedText formatted, string text)
    {
        var offset = formatted.PlainText.IndexOf(text, StringComparison.Ordinal);
        if (offset < 0)
        {
            throw new InvalidOperationException($"Output did not contain '{text}'.");
        }

        foreach (var run in formatted.Runs)
        {
            if (offset < run.Text.Length) return run.Style;
            offset -= run.Text.Length;
        }

        throw new InvalidOperationException("Output styling did not cover the requested text.");
    }

    private static NetworkSessionState NewSession() =>
        new(NetworkSessionId.New(), "Example", IrcCaseMapping.Rfc1459);

    private static async ValueTask WithWindowHostAsync(
        Action<ClientApplication, ScriptWindowHostServices, StringWriter> test)
    {
        using var temporary = new TemporaryDirectory();
        using var writer = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(writer);
            await using var application = new ClientApplication(temporary.Path);
            test(application, application.CreateScriptWindowHostServices(), writer);
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    private static async ValueTask WithWindowInputHostAsync(
        Func<ClientApplication, ScriptWindowHostServices, StringWriter, ValueTask> test)
    {
        using var temporary = new TemporaryDirectory();
        using var writer = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(writer);
            await using var application = new ClientApplication(temporary.Path);
            await test(application, application.CreateScriptWindowHostServices(), writer);
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"clircs-script-window-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
