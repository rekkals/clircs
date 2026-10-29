using System.Globalization;
using Clircs.ConsoleClient;
using Clircs.Identity;
using Clircs.Protocol;
using Clircs.State;
using Clircs.Sessions;

namespace Clircs.Core.Tests;

internal static class DateChangeTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("greetings suppress date changes while preserving playback boundaries", GreetingsPreserveDateTracking);
        suite.Add("date separators use displayed dates rather than UTC or receipt time", DisplayedDatesDetermineChanges);
        suite.Add("date separators preserve history and handle backwards date changes", HistoryPreservesEvents);
        suite.Add("date separators recognize boundaries before limited history snapshots", LimitedHistoryUsesPreviousDate);
        suite.Add("date separators render midnight using the local output palette", SeparatorsRenderAsLocalOutput);
        suite.Add("window storage isolates date changes and measures separator rows", WindowStorageMeasuresSeparators);
        suite.Add("limited viewport snapshots preserve the preceding event date", ViewportSnapshotsPreservePreviousDate);
        suite.Add("replacing progress entries updates surrounding date boundaries", ReplacementsUpdateDateBoundaries);
        suite.Add("viewport paging counts wrapped date separators within its row budget", PagingCountsSeparatorRows);
        suite.Add("wrapped date separators render only the selected rows", WrappedSeparatorsRenderSelectedRows);
        suite.Add("retained midnight markers survive toggling without duplication", RetainedMarkersSurviveToggling);
        suite.Add("midnight storage preserves scrolling without activity or numbering", MidnightStoragePreservesWindowState);
        suite.Add("new-date output prevents a duplicate clock marker", ExistingOutputPreventsClockDuplicate);
        suite.Add("hidden midnight markers do not displace visible history", HiddenMarkersPreserveViewportBudget);
        suite.Add("date clock detects local midnight once without message traffic", ClockDetectsLocalMidnight);
        suite.Add("date clock handles missed days and backward clock changes", ClockHandlesDateJumps);
        suite.Add("progress replacements keep one retained midnight boundary", ProgressReplacementKeepsOneMidnightMarker);
    }

    private static void GreetingsPreserveDateTracking()
    {
        var state = new NetworkSessionState(NetworkSessionId.New(), "test", IrcCaseMapping.Rfc1459);
        var processor = new IrcSessionProcessor(state, "me");
        var windows = new WindowStateRegistry();
        var midnight = At(23, 0);
        var current = new SessionEvent(
            state.Id, state.StatusBuffer.Id, SessionEventKind.Status, "connected", At(22, 23, 59));
        windows.AppendHistory(current);

        foreach (var line in new[]
        {
            ":server 001 me :Welcome",
            ":server 002 me :Your host is server",
            ":server 005 me NETWORK=Test :are supported by this server",
            ":server 422 me :MOTD File is missing"
        })
        {
            var greeting = processor.Process(IrcMessageParser.Parse(line), At(21, 12)).Single()
                with
            { ReceivedAt = midnight };
            Assert.True(windows.StoreEvent(
                greeting, 1, _ => 1, false, false, dateChange: true).DateChange is null);
            Assert.Equal(At(21, 12), greeting.Timestamp);
        }

        var marker = DateChangeSeparators.Create(state.Id, state.StatusBuffer.Id, midnight);
        Assert.True(windows.StoreDateChange(marker, 1, _ => 1).Stored);
        Assert.False(windows.StoreDateChange(marker, 1, _ => 1).Stored);
        var viewport = windows.ViewportSnapshot(state.StatusBuffer.Id, 1);
        Assert.True(ReferenceEquals(marker, DateChangeSeparators.ForHistory(
            viewport.History, viewport.PreviousTimestamp).Single()));

        var live = current with { Timestamp = midnight, ReceivedAt = midnight };
        Assert.True(windows.StoreEvent(
            live, 1, _ => 1, false, false, dateChange: true).DateChange is null);
        var history = windows.HistorySnapshot(state.StatusBuffer.Id);
        Assert.Equal(history.Length, DateChangeSeparators.ForHistory(history).Count);

        var replay = live with { Kind = SessionEventKind.Message, Timestamp = At(21, 13) };
        var stored = windows.StoreEvent(
            replay, 1, _ => 1, true, false, dateChange: true);
        Assert.True(stored.DateChange is not null);
        Assert.Equal("Date: 2026-10-21", stored.DateChange!.Text);
    }

    private static void DisplayedDatesDetermineChanges()
    {
        var replay = EventAt(22, 0, 1) with { ReceivedAt = At(25, 12) };

        // These displayed dates differ even though their UTC dates are the same.
        var separator = DateChangeSeparators.Before(replay, At(21, 23, 59));
        Assert.True(separator is not null);
        Assert.Equal("Date: 2026-10-22", separator!.Text);
        Assert.Equal(replay.NetworkSessionId, separator.NetworkSessionId);
        Assert.Equal(replay.BufferId, separator.BufferId);

        // UTC crosses midnight here, but the displayed date stays October 21.
        var sameDay = replay with { Timestamp = At(21, 19, 1) };
        Assert.True(DateChangeSeparators.Before(sameDay, At(21, 18, 59)) is null);
    }

    private static void HistoryPreservesEvents()
    {
        var first = EventAt(21, 23, 59) with { Text = "first" };
        var second = first with { Text = "second", Timestamp = At(22, 0, 1) };
        var third = second with { Text = "third", Timestamp = At(22, 12) };
        var backwards = first with { Text = "backwards", Timestamp = At(20, 12) };
        SessionEvent[] history = [first, second, third, backwards];

        var output = DateChangeSeparators.ForHistory(history);
        Assert.Equal(
            "first|Date: 2026-10-22|second|third|Date: 2026-10-20|backwards",
            string.Join('|', output.Select(item => item.Text)));
        Assert.True(ReferenceEquals(first, output[0]));
        Assert.True(ReferenceEquals(second, output[2]));
        Assert.True(ReferenceEquals(third, output[3]));
        Assert.True(ReferenceEquals(backwards, output[5]));

        Assert.Equal(4, history.Length);
        Assert.Equal("first|second|third|backwards", string.Join('|', history.Select(item => item.Text)));
        Assert.Equal(
            string.Join('|', output.Select(item => item.Text)),
            string.Join('|', DateChangeSeparators.ForHistory(history).Select(item => item.Text)));
    }

    private static void LimitedHistoryUsesPreviousDate()
    {
        var first = EventAt(22, 0, 7);
        Assert.Equal(0, DateChangeSeparators.ForHistory([], At(21, 23, 59)).Count);
        Assert.Equal(1, DateChangeSeparators.ForHistory([first]).Count);
        Assert.Equal(1, DateChangeSeparators.ForHistory([first], At(22, 0)).Count);

        var output = DateChangeSeparators.ForHistory([first], At(21, 23, 59));
        Assert.Equal(2, output.Count);
        Assert.Equal("Date: 2026-10-22", output[0].Text);
        Assert.True(ReferenceEquals(first, output[1]));
    }

    private static void SeparatorsRenderAsLocalOutput()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var originalOutput = Console.Out;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            var separator = DateChangeSeparators.Before(EventAt(22, 0, 7), At(21, 23, 59))!;
            Assert.Equal("Date: 2026-10-22", separator.Text);
            Assert.Equal(TimeSpan.Zero, separator.Timestamp.TimeOfDay);

            foreach (var theme in TerminalTheme.BuiltIns.Values)
            {
                Assert.Equal(theme.Dim, theme.EventColor(separator));
            }

            var presenter = new ConsolePresenter();
            presenter.SetTheme(TerminalTheme.BuiltIns["clircs"] with { ShowBufferName = false });
            Assert.Equal(1, presenter.MeasureEventRows(separator, "#clircs", 120));

            using var writer = new StringWriter();
            Console.SetOut(writer);
            presenter.Event(separator, "#clircs");
            Assert.Equal(
                "[00:00:00] Date: 2026-10-22",
                writer.ToString().Trim());
        }
        finally
        {
            Console.SetOut(originalOutput);
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static void WindowStorageMeasuresSeparators()
    {
        var windows = new WindowStateRegistry();
        var presenter = new ConsolePresenter();
        int MeasureRows(SessionEvent item) =>
            presenter.MeasureEventRows(item, "#clircs", 24);

        var first = EventAt(21, 23, 59);
        windows.Activate(first.NetworkSessionId, first.BufferId);
        Assert.True(windows.StoreEvent(
            first, 1, MeasureRows, false, true, dateChange: true).DateChange is null);
        windows.SetScrollOffset(first.BufferId, 4);

        var second = first with { Timestamp = At(22, 0, 1) };
        var changed = windows.StoreEvent(
            second, 1, MeasureRows, false, true, dateChange: true);
        Assert.True(changed.DateChange is not null);
        Assert.Equal(2, MeasureRows(changed.DateChange!));
        Assert.Equal(7, windows.ScrollOffset(first.BufferId));
        Assert.Equal(2, windows.HistorySnapshot(first.BufferId).Length);

        var other = first with { BufferId = BufferId.New(), Timestamp = At(24, 12) };
        Assert.True(windows.StoreEvent(
            other, 1, MeasureRows, false, true, dateChange: true).DateChange is null);
        Assert.Equal(7, windows.ScrollOffset(first.BufferId));

        var disabled = windows.StoreEvent(
            first with { Timestamp = At(23, 12) },
            1, MeasureRows, false, true, dateChange: false);
        Assert.True(disabled.DateChange is null);
        Assert.Equal(8, windows.ScrollOffset(first.BufferId));
        Assert.Equal(3, windows.HistorySnapshot(first.BufferId).Length);
    }

    private static void ViewportSnapshotsPreservePreviousDate()
    {
        var windows = new WindowStateRegistry();
        var first = EventAt(21, 23, 59);
        var second = first with { Timestamp = At(22, 0, 1) };
        windows.AppendHistory(first);
        windows.AppendHistory(second);

        var crossing = windows.ViewportSnapshot(first.BufferId, 1);
        Assert.Equal(1, crossing.History.Length);
        Assert.True(crossing.PreviousTimestamp == first.Timestamp);
        var output = DateChangeSeparators.ForHistory(
            crossing.History, crossing.PreviousTimestamp);
        Assert.Equal(2, output.Count);
        Assert.Equal("Date: 2026-10-22", output[0].Text);

        windows.AppendHistory(second with { Timestamp = At(22, 12) });
        var sameDay = windows.ViewportSnapshot(first.BufferId, 1);
        Assert.True(sameDay.PreviousTimestamp == second.Timestamp);
        Assert.Equal(1, DateChangeSeparators.ForHistory(
            sameDay.History, sameDay.PreviousTimestamp).Count);

        Assert.True(crossing.PreviousTimestamp == first.Timestamp);
        Assert.True(ReferenceEquals(second, crossing.History[0]));
        Assert.True(windows.ViewportSnapshot(first.BufferId).PreviousTimestamp is null);
    }

    private static void ReplacementsUpdateDateBoundaries()
    {
        foreach (var (index, expectedOffset, expectedRows) in new[]
                 {
                     (0, 6, 4),
                     (1, 7, 5),
                     (2, 6, 4)
                 })
        {
            var windows = new WindowStateRegistry();
            var first = EventAt(21, 23, 59);
            var progress = first with
            {
                Text = "progress",
                Fields = new Dictionary<string, string?>
                {
                    ["history.transientKey"] = "dcc.transfer.7",
                    ["history.replaceKey"] = "dcc.transfer.7"
                }
            };
            windows.Activate(first.NetworkSessionId, first.BufferId);
            for (var position = 0; position < 3; position++)
            {
                var item = position == index
                    ? progress
                    : first with { Text = $"row {position}" };
                windows.StoreEvent(item, 1, _ => 1, false, false, dateChange: true);
            }
            windows.SetScrollOffset(first.BufferId, 5);

            var replacement = progress with { Timestamp = At(22, 0, 1) };
            var changed = windows.StoreEvent(
                replacement, 1, _ => 1, false, false, dateChange: true);
            Assert.True(changed.Replaced);
            Assert.True(changed.DateChange is null);
            Assert.Equal(expectedOffset, windows.ScrollOffset(first.BufferId));
            var history = windows.HistorySnapshot(first.BufferId);
            Assert.Equal(3, history.Length);
            Assert.True(ReferenceEquals(replacement, history[index]));
            Assert.Equal(expectedRows, DateChangeSeparators.ForHistory(history).Count);

            windows.StoreEvent(progress, 1, _ => 1, false, false, dateChange: true);
            Assert.Equal(5, windows.ScrollOffset(first.BufferId));
            Assert.Equal(3, DateChangeSeparators.ForHistory(
                windows.HistorySnapshot(first.BufferId)).Count);
        }
    }

    private static void PagingCountsSeparatorRows()
    {
        var presenter = new ConsolePresenter();
        var first = EventAt(21, 23, 59) with { Text = "old" };
        var second = first with { Text = "new", Timestamp = At(22, 0, 1) };
        var history = DateChangeSeparators.ForHistory([first, second]);
        var separator = history[1];
        int MeasureRows(SessionEvent item) =>
            presenter.MeasureEventRows(item, "#clircs", 24);

        Assert.Equal(2, MeasureRows(separator));

        // The bottom page contains the separator's last row and the new event.
        var bottom = ViewportHistory.SelectRows(history, 0, 2, MeasureRows);
        Assert.Equal(2, bottom.Sum(slice => slice.TakeRows));
        Assert.Equal(2, bottom.Count);
        Assert.True(ReferenceEquals(separator, bottom[0].Item));
        Assert.Equal(1, bottom[0].SkipRows);
        Assert.Equal(1, bottom[0].TakeRows);
        Assert.True(ReferenceEquals(second, bottom[1].Item));

        // The preceding page contains the old event and the separator's first row.
        var previous = ViewportHistory.SelectRows(history, 2, 2, MeasureRows);
        Assert.Equal(2, previous.Sum(slice => slice.TakeRows));
        Assert.Equal(2, previous.Count);
        Assert.True(ReferenceEquals(first, previous[0].Item));
        Assert.True(ReferenceEquals(separator, previous[1].Item));
        Assert.Equal(0, previous[1].SkipRows);
        Assert.Equal(1, previous[1].TakeRows);

        // A wider viewport fits the complete separator beside the new event.
        var wide = ViewportHistory.SelectRows(
            history, 0, 2,
            item => presenter.MeasureEventRows(item, "#clircs", 120));
        Assert.Equal(2, wide.Count);
        Assert.True(ReferenceEquals(separator, wide[0].Item));
        Assert.Equal(0, wide[0].SkipRows);
        Assert.Equal(1, wide[0].TakeRows);
        Assert.True(ReferenceEquals(second, wide[1].Item));
    }

    private static void WrappedSeparatorsRenderSelectedRows()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var originalOutput = Console.Out;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var presenter = new ConsolePresenter();
            presenter.SetTheme(TerminalTheme.BuiltIns["clircs"] with { ShowBufferName = false });
            var separator = DateChangeSeparators.Before(EventAt(22, 0, 7), At(21, 23, 59))!;

            using var writer = new StringWriter();
            Console.SetOut(writer);

            presenter.DateChangeRows(separator, "#clircs", 0, 1, 24);
            Assert.Equal("[00:00:00] Date:" + Environment.NewLine, writer.ToString());

            writer.GetStringBuilder().Clear();
            presenter.DateChangeRows(separator, "#clircs", 1, 1, 24);
            Assert.Equal("2026-10-22" + Environment.NewLine, writer.ToString());

            writer.GetStringBuilder().Clear();
            presenter.DateChangeRows(separator, "#clircs", 0, 1, 120);
            Assert.Equal(
                "[00:00:00] Date: 2026-10-22" + Environment.NewLine,
                writer.ToString());

            // Exercise EventRows itself: skipping every row must produce no output.
            writer.GetStringBuilder().Clear();
            presenter.EventRows(
                separator,
                "#clircs",
                presenter.MeasureEventRows(separator, "#clircs"),
                1);
            Assert.Equal(string.Empty, writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static void RetainedMarkersSurviveToggling()
    {
        var first = EventAt(22, 23, 59);
        var observedAt = At(23, 0, 1);
        var marker = DateChangeSeparators.Create(
            first.NetworkSessionId, first.BufferId, observedAt);
        var following = first with { Timestamp = At(23, 1, 30) };
        SessionEvent[] history = [first, marker, following];

        Assert.Equal(observedAt, marker.ReceivedAt);
        Assert.Equal(TimeSpan.Zero, marker.Timestamp.TimeOfDay);

        var enabled = DateChangeSeparators.ForHistory(history);
        Assert.Equal(3, enabled.Count);
        Assert.True(ReferenceEquals(marker, enabled[1]));

        var disabled = DateChangeSeparators.ForHistory(history, enabled: false);
        Assert.Equal(2, disabled.Count);
        Assert.True(ReferenceEquals(first, disabled[0]));
        Assert.True(ReferenceEquals(following, disabled[1]));

        Assert.Equal(3, history.Length);
        Assert.Equal(3, DateChangeSeparators.ForHistory(history).Count);
    }

    private static void MidnightStoragePreservesWindowState()
    {
        var windows = new WindowStateRegistry();
        var presenter = new ConsolePresenter();
        int MeasureRows(SessionEvent item) =>
            presenter.MeasureEventRows(item, "#clircs", 24);

        var first = EventAt(22, 23, 59);
        windows.ActivateLocal(first.BufferId);
        windows.AppendHistory(first);
        windows.SetScrollOffset(first.BufferId, 3);

        var marker = DateChangeSeparators.Create(
            first.NetworkSessionId, first.BufferId, At(23, 0));
        var stored = windows.StoreDateChange(marker, MeasureRows(marker), MeasureRows);
        Assert.True(stored.Stored);
        Assert.True(stored.IsScrolled);
        Assert.Equal(5, windows.ScrollOffset(first.BufferId));
        Assert.Equal(2, windows.HistorySnapshot(first.BufferId).Length);
        Assert.Equal(0, windows.UnreadActivities(first.BufferId).Length);
        Assert.False(windows.HasNumber(first.BufferId));

        Assert.False(windows.StoreDateChange(marker, MeasureRows(marker), MeasureRows).Stored);
        Assert.Equal(5, windows.ScrollOffset(first.BufferId));

        var following = first with { Timestamp = At(23, 1, 30) };
        var next = windows.StoreEvent(
            following, MeasureRows(following), MeasureRows, false, false,
            assignNumber: false, dateChange: true);
        Assert.True(next.DateChange is null);
        Assert.Equal(6, windows.ScrollOffset(first.BufferId));

        var unopened = DateChangeSeparators.Create(
            first.NetworkSessionId, BufferId.New(), At(23, 0));
        Assert.False(windows.StoreDateChange(unopened, MeasureRows(unopened), MeasureRows).Stored);
        Assert.Equal(0, windows.HistorySnapshot(unopened.BufferId).Length);
    }

    private static void ExistingOutputPreventsClockDuplicate()
    {
        var windows = new WindowStateRegistry();
        var first = EventAt(22, 23, 59);
        windows.AppendHistory(first);

        var following = first with { Timestamp = At(23, 0) };
        var stored = windows.StoreEvent(
            following, 1, _ => 1, false, false,
            assignNumber: false, dateChange: true);
        Assert.True(stored.DateChange is not null);

        var marker = DateChangeSeparators.Create(
            first.NetworkSessionId, first.BufferId, At(23, 0));
        Assert.False(windows.StoreDateChange(marker, 1, _ => 1).Stored);

        var history = windows.HistorySnapshot(first.BufferId);
        Assert.Equal(2, history.Length);
        Assert.Equal(1, DateChangeSeparators.ForHistory(history)
            .Count(DateChangeSeparators.IsSeparator));
    }

    private static void HiddenMarkersPreserveViewportBudget()
    {
        var windows = new WindowStateRegistry();
        var first = EventAt(22, 12) with { Text = "first" };
        var second = first with { Text = "second", Timestamp = At(22, 13) };
        windows.AppendHistory(first);
        windows.AppendHistory(second);
        for (var day = 23; day <= 25; day++)
        {
            windows.AppendHistory(DateChangeSeparators.Create(
                first.NetworkSessionId, first.BufferId, At(day, 0)));
        }

        var snapshot = windows.ViewportSnapshot(first.BufferId, 2, dateChange: false);
        var visible = DateChangeSeparators.ForHistory(
            snapshot.History, snapshot.PreviousTimestamp, enabled: false);
        Assert.Equal(2, visible.Count);
        Assert.True(ReferenceEquals(first, visible[0]));
        Assert.True(ReferenceEquals(second, visible[1]));

        var history = windows.HistorySnapshot(first.BufferId);
        Assert.Equal(5, history.Length);
        Assert.Equal(5, DateChangeSeparators.ForHistory(history).Count);
    }

    private static void ClockDetectsLocalMidnight()
    {
        var clock = new DateChangeClock(At(22, 18, 59));

        // UTC crosses midnight, but the local date stays October 22.
        Assert.False(clock.Observe(At(22, 19, 1)));
        Assert.False(clock.Observe(At(22, 23, 59).AddSeconds(59)));

        // Local midnight crosses the date boundary without any message arriving.
        Assert.True(clock.Observe(At(23, 0)));
        Assert.False(clock.Observe(At(23, 0).AddMilliseconds(500)));
        Assert.False(clock.Observe(At(23, 1, 30)));

        Assert.True(clock.Observe(At(24, 0)));
        Assert.False(clock.Observe(At(24, 12)));
    }

    private static void ClockHandlesDateJumps()
    {
        var clock = new DateChangeClock(At(22, 23, 59));

        // After a long pause, report the currently observed date once.
        Assert.True(clock.Observe(At(25, 12)));
        Assert.False(clock.Observe(At(25, 12, 1)));

        // Follow a backward calendar adjustment and resume normal detection.
        Assert.True(clock.Observe(At(24, 12)));
        Assert.False(clock.Observe(At(24, 23, 59)));
        Assert.True(clock.Observe(At(25, 0)));
        Assert.False(clock.Observe(At(25, 1, 30)));
    }

    private static void ProgressReplacementKeepsOneMidnightMarker()
    {
        var windows = new WindowStateRegistry();
        var first = EventAt(22, 23, 58);
        var progress = first with
        {
            Text = "progress",
            Timestamp = At(22, 23, 59),
            Fields = new Dictionary<string, string?>
            {
                ["history.transientKey"] = "dcc.transfer.7",
                ["history.replaceKey"] = "dcc.transfer.7"
            }
        };
        windows.Activate(first.NetworkSessionId, first.BufferId);
        windows.AppendHistory(first);
        windows.StoreEvent(progress, 1, _ => 1, false, false, dateChange: true);

        var marker = DateChangeSeparators.Create(
            first.NetworkSessionId, first.BufferId, At(23, 0));
        windows.StoreDateChange(marker, 1, _ => 1);
        windows.SetScrollOffset(first.BufferId, 4);

        var replacement = progress with { Timestamp = At(23, 0, 1) };
        Assert.True(windows.StoreEvent(
            replacement, 1, _ => 1, false, false, dateChange: true).Replaced);

        var output = DateChangeSeparators.ForHistory(
            windows.HistorySnapshot(first.BufferId));
        Assert.Equal(3, output.Count);
        Assert.Equal(1, output.Count(DateChangeSeparators.IsSeparator));
        Assert.True(ReferenceEquals(replacement, output[^1]));
        Assert.Equal(4, windows.ScrollOffset(first.BufferId));

        windows.SetScrollOffset(first.BufferId, 0);
        var snapshot = windows.ViewportSnapshot(first.BufferId, 1);
        var display = DateChangeSeparators.ForHistory(
            snapshot.History, snapshot.PreviousTimestamp);
        var bottom = ViewportHistory.SelectRows(display, 0, 1, _ => 1);
        Assert.Equal(1, bottom.Count);
        Assert.True(ReferenceEquals(replacement, bottom[0].Item));

        windows.SetScrollOffset(first.BufferId, 4);
        windows.StoreEvent(progress, 1, _ => 1, false, false, dateChange: true);
        Assert.Equal(4, windows.ScrollOffset(first.BufferId));
        var restored = DateChangeSeparators.ForHistory(
            windows.HistorySnapshot(first.BufferId));
        Assert.Equal(3, restored.Count);
        Assert.True(ReferenceEquals(marker, restored[^1]));
    }

    private static SessionEvent EventAt(int day, int hour, int minute = 0) => new(
        NetworkSessionId.New(),
        BufferId.New(),
        SessionEventKind.Server,
        "message",
        At(day, hour, minute));

    private static DateTimeOffset At(int day, int hour, int minute = 0) =>
        new(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(-5));
}
