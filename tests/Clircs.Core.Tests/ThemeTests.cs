using Clircs.ConsoleClient;
using Clircs.Identity;
using Clircs.Networking;
using Clircs.Protocol;
using Clircs.Sessions;
using Clircs.State;
using System.Net;

namespace Clircs.Core.Tests;

internal static class ThemeTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("default theme uses a subdued status bar", DefaultStatusBarIsSubdued);
        suite.Add("activity numbers remain visible against the status bar", ActivityNumbersRemainVisible);
        suite.Add("startup logo and default text colors do not drift", StartupAndTextColorsAreStable);
        suite.Add("contextual settings and protection help is available", ContextualHelpIsAvailable);
        suite.Add("fullscreen input disables mouse-wheel arrow translation", FullScreenDisablesAlternateScroll);
        suite.Add("startup presentations are measurable history entries", StartupPresentationIsHistoryReady);
        suite.Add("user themes inherit a constrained built-in palette", ThemeInheritance);
        suite.Add("damaged themes are isolated without losing built-ins", DamagedThemeIsIsolated);
        suite.Add("theme parsing accepts comments and rejects ambiguous duplicates", ThemeParsingIsDeterministic);
        suite.Add("appearance settings persist theme routes and hostmask visibility", AppearancePersistence);
        suite.Add("LINKS output defaults to the status window", LinksDefaultsToStatus);
        suite.Add("LIST output defaults to a dedicated result window", ListDefaultsToDedicated);
        suite.Add("DNS output defaults to active and formats addresses and nicknames", DnsDefaultsAndFormatting);
        suite.Add("CTCP output defaults to the active window", CtcpDefaultsToActive);
        suite.Add("invites default to active output and kicks default to red", InviteAndKickDefaults);
        suite.Add("quote provider copies and selects bundled quotes", QuoteProviderSelectsBundledQuotes);
        suite.Add("input history navigates without duplicates", InputHistoryNavigates);
        suite.Add("legacy data is copied into the clircs directory", LegacyDataIsCopied);
        suite.Add("nickname completion cycles matches and preserves its prefix", NicknameCompletionCycles);
        suite.Add("named banmask styles generate consistent masks", BanmaskStylesAreConsistent);
        suite.Add("viewport measurement counts rendered NAMES rows instead of nick cells", NamesGridMeasurementUsesColumns);
        suite.Add("viewport paging keeps histories isolated and honors row budgets", ViewportPagingHonorsRows);
        suite.Add("viewport paging can move through one very large result", ViewportPagingSlicesLargeResults);
        suite.Add("viewport paging slices large NAMES grids instead of repeating them", ViewportPagingSlicesNamesGrid);
        suite.Add("viewport paging slices large field boxes instead of repeating them", ViewportPagingSlicesFieldBoxes);
        suite.Add("viewport paging retains mixed fields and tables", ViewportPagingRetainsMixedPresentations);
        suite.Add("transient viewport events replace one history entry and become permanent", TransientViewportEventsReplaceInPlace);
        suite.Add("window activation resets unread and scroll state through one owner", WindowActivationResetsTransientState);
        suite.Add("window removal clears all terminal state without reusing numbers", WindowRemovalClearsTerminalState);
        suite.Add("viewport history snapshots are isolated from live event arrival", ViewportHistorySnapshotsAreIsolated);
        suite.Add("terminal wrapping keeps ordinary words intact", TerminalWrappingKeepsWordsIntact);
        suite.Add("wrapped URL fragments retain one complete hyperlink target", WrappedUrlsRetainCompleteTarget);
        suite.Add("transcript continuation lines begin at the left edge", TranscriptContinuationsBeginAtLeftEdge);
        suite.Add("info field continuation lines retain their indentation", InfoFieldContinuationsRetainIndentation);
        suite.Add("window close behavior follows the active buffer kind", WindowCloseBehaviorFollowsBufferKind);
        suite.Add("window close selects the nearest lower numbered buffer", WindowCloseSelectsPreviousNumber);
        suite.Add("status windows use a distinct not-applicable target marker", StatusWindowsUseDistinctTargetMarker);
        suite.Add("bare theme output is concise and excludes the current theme", ThemeOverviewIsConcise);
        suite.Add("long input stays on one row and scrolls horizontally", LongInputScrollsHorizontally);
        suite.Add("input formatting shortcuts insert IRC control codes", InputFormattingShortcutsInsertControlCodes);
        suite.Add("WHO address columns expand when the viewport grows", WhoAddressesExpandWithViewport);
        suite.Add("status and input rows cannot overwrite the latest output", ChromeRowsReserveLatestOutput);
        suite.Add("returning to input reuses existing status rows", ExistingChromeRowsAreReused);
        suite.Add("full redraws cannot interleave live terminal output", FullRedrawsAreAtomic);
        suite.Add("topic headers reserve a content row before redraw", TopicHeadersReserveContentRows);
        suite.Add("preserved table columns stay visible without wrapping the viewport", PreservedTableColumnsDoNotWrap);
        suite.Add("status labels distinguish IRC and bouncer TLS", StatusLabelsDistinguishTlsHops);
        suite.Add("channel status totals use compact labels", ChannelStatusTotalsUseCompactLabels);
        suite.Add("raw IRC debug lines expose direction and visible control delimiters", RawDebugLinesAreReadable);
        suite.Add("NickServ identification masks supplied passwords locally", NickServPasswordsAreMasked);
        suite.Add("buffer headers share space without losing auxiliary content", BufferHeadersCompose);
        suite.Add("plain local results use dim text without sentence punctuation", LocalResultsAreDim);
        suite.Add("traditional IRC colors map to the Windows console palette", IrcColorsUseConsolePalette);
        suite.Add("client-built information tables retain IRC styling", ClientTablesRetainIrcStyling);
    }

    private static void StatusLabelsDistinguishTlsHops()
    {
        Assert.Equal("(1) EFNet[TLS]", ClientApplication.FormatStatusNetworkField(1, "EFNet", upstreamTls: true));
        Assert.Equal("(1) EFNet", ClientApplication.FormatStatusNetworkField(1, "EFNet", upstreamTls: false));
        Assert.Equal("irc.deft.com", ClientApplication.FormatStatusServerField(
            "irc.deft.com", bouncerName: null, clientTransportTls: true));
        Assert.Equal("irc.deft.com [ZNC]", ClientApplication.FormatStatusServerField(
            "irc.deft.com", "ZNC", clientTransportTls: false));
        Assert.Equal("irc.deft.com [ZNC/TLS]", ClientApplication.FormatStatusServerField(
            "irc.deft.com", "ZNC", clientTransportTls: true));
        Assert.Equal("#clircs(+nt)", ClientApplication.FormatStatusChannelField("#clircs", "nt"));
        Assert.Equal("#clircs", ClientApplication.FormatStatusChannelField("#clircs", string.Empty));
    }

    private static void ChannelStatusTotalsUseCompactLabels()
    {
        Assert.Equal("own", ClientApplication.PrefixCountLabel('q', '~'));
        Assert.Equal("adm", ClientApplication.PrefixCountLabel('a', '&'));
        Assert.Equal("ops", ClientApplication.PrefixCountLabel('o', '@'));
        Assert.Equal("hlf", ClientApplication.PrefixCountLabel('h', '%'));
        Assert.Equal("voc", ClientApplication.PrefixCountLabel('v', '+'));
        Assert.Equal("sys", ClientApplication.PrefixCountLabel('Y', '!'));
    }

    private static void RawDebugLinesAreReadable()
    {
        var received = ClientApplication.FormatWireDebugLine(new IrcWireLine(
            IrcWireDirection.Received,
            ":nick!user@host PRIVMSG me :\u0001VERSION\u0001",
            DateTimeOffset.UtcNow));
        var sent = ClientApplication.FormatWireDebugLine(new IrcWireLine(
            IrcWireDirection.Sent,
            "WHO #clircs\u202E",
            DateTimeOffset.UtcNow));

        Assert.Equal("<< :nick!user@host PRIVMSG me :\\x01VERSION\\x01", received);
        Assert.Equal(">> WHO #clircs\\u202E", sent);
    }

    private static void NickServPasswordsAreMasked()
    {
        Assert.Equal(
            "identify slakker ********",
            ClientApplication.MaskServiceCommand(
                "nickserv",
                "identify slakker hunter2",
                ["identify", "slakker", "hunter2"]));
        Assert.Equal(
            "help register",
            ClientApplication.MaskServiceCommand(
                "nickserv",
                "help register",
                ["help", "register"]));
    }

    private static void DefaultStatusBarIsSubdued()
    {
        Assert.Equal(ConsoleColor.DarkCyan, TerminalTheme.BuiltIns["clircs"].StatusBackground);
        Assert.Equal(ConsoleColor.Black, TerminalTheme.BuiltIns["clircs"].StatusForeground);
        Assert.Equal(ConsoleColor.Cyan, TerminalTheme.BuiltIns["clircs"].Mode);
        Assert.Equal(ConsoleColor.Magenta, TerminalTheme.BuiltIns["clircs"].Action);
        Assert.Equal(ConsoleColor.Green, TerminalTheme.BuiltIns["clircs"].Nick);
        Assert.Equal(ConsoleColor.Red, TerminalTheme.BuiltIns["clircs"].Kick);
    }

    private static void IrcColorsUseConsolePalette()
    {
        Assert.Equal(ConsoleColor.Red, ConsolePresenter.IrcColor(4, ConsoleColor.Gray));
        Assert.Equal(ConsoleColor.DarkMagenta, ConsolePresenter.IrcColor(6, ConsoleColor.Gray));
        Assert.Equal(ConsoleColor.Yellow, ConsolePresenter.IrcColor(8, ConsoleColor.Gray));
        Assert.Equal(ConsoleColor.Green, ConsolePresenter.IrcColor(9, ConsoleColor.Gray));
        Assert.Equal(ConsoleColor.Cyan, ConsolePresenter.IrcColor(11, ConsoleColor.Gray));
        Assert.Equal(ConsoleColor.Red, ConsolePresenter.IrcColor(52, ConsoleColor.Gray));
        Assert.Equal(ConsoleColor.Blue, ConsolePresenter.IrcColor(60, ConsoleColor.Gray));
        Assert.Equal(ConsoleColor.Black, ConsolePresenter.IrcColor(88, ConsoleColor.Gray));
        Assert.Equal(ConsoleColor.White, ConsolePresenter.IrcColor(98, ConsoleColor.Gray));
    }

    private static void ClientTablesRetainIrcStyling()
    {
        var table = new PresentationTable(
            ["Nick", "Name"],
            [(IReadOnlyList<string>)["He-Man", "\u000307,12I Have The Power!\u000f"]]);

        Assert.Equal("I Have The Power!", table.Rows[0][1]);
        Assert.Equal(7, table.FormattedRows[0][1]!.Runs[0].Style.Foreground!.Value);
        Assert.Equal(12, table.FormattedRows[0][1]!.Runs[0].Style.Background!.Value);
    }

    private static void ActivityNumbersRemainVisible()
    {
        var theme = TerminalTheme.BuiltIns["clircs"];
        Assert.Equal(theme.StatusBackground, theme.EventColor(SessionEventKind.Part));
        Assert.False(ConsolePresenter.ReadableActivityColor(theme, SessionEventKind.Part) == theme.StatusBackground);
    }

    private static void LocalResultsAreDim()
    {
        Assert.Equal("altnick changed to slak", ClientApplication.FormatLocalCommandResult(
            "altnick changed to slak."));
        Assert.Equal("Please wait...", ClientApplication.FormatLocalCommandResult("Please wait..."));
        var localResult = new SessionEvent(
            NetworkSessionId.New(),
            BufferId.New(),
            SessionEventKind.Server,
            "changed",
            DateTimeOffset.Now,
            new Dictionary<string, string?> { ["clientResult"] = "true" });
        Assert.Equal(TerminalTheme.BuiltIns["clircs"].Dim,
            TerminalTheme.BuiltIns["clircs"].EventColor(localResult));
    }

    private static void BufferHeadersCompose()
    {
        var model = new BufferHeaderModel(
            "[#clircs] A reasonably long channel topic",
            [new BufferHeaderItem("Playing: TOOL - H. [6:07]", 100, 20)]);
        var wide = BufferHeaderComposer.Compose(model, 100)!;
        Assert.True(wide.Contains("A reasonably long channel topic", StringComparison.Ordinal));
        Assert.True(wide.Contains("Playing: TOOL - H. [6:07]", StringComparison.Ordinal));

        var narrow = BufferHeaderComposer.Compose(model, 48)!;
        Assert.True(narrow.Length <= 48);
        Assert.True(narrow.Contains("Playing:", StringComparison.Ordinal));

        var noTopic = BufferHeaderComposer.Compose(new BufferHeaderModel(
            null,
            [new BufferHeaderItem("CPU: 7% | Network: 2.1 MB/s", 10, 12)]), 80);
        Assert.Equal("CPU: 7% | Network: 2.1 MB/s", noTopic!);
    }

    private static void StartupAndTextColorsAreStable()
    {
        Assert.True(ConsolePresenter.StartupLogoColors.SequenceEqual(new[]
        {
            (ConsoleColor.White, ConsoleColor.DarkCyan),
            (ConsoleColor.White, ConsoleColor.DarkRed),
            (ConsoleColor.White, ConsoleColor.DarkGreen),
            (ConsoleColor.Yellow, ConsoleColor.DarkBlue),
            (ConsoleColor.Magenta, ConsoleColor.DarkCyan),
            (ConsoleColor.White, ConsoleColor.DarkRed)
        }));
        Assert.Equal(ConsoleColor.Gray, TerminalTheme.BuiltIns["clircs"].Normal);
        Assert.Equal(ConsoleColor.Gray, TerminalTheme.BuiltIns["clircs"].Message);
        Assert.Equal(30, ConsolePresenter.AnsiForeground(ConsoleColor.Black));
        Assert.Equal(46, ConsolePresenter.AnsiBackground(ConsoleColor.DarkCyan));
        Assert.Equal(95, ConsolePresenter.AnsiForeground(ConsoleColor.Magenta));
    }

    private static void ContextualHelpIsAvailable()
    {
        using var temporary = new TemporaryDirectory();
        var previous = Environment.GetEnvironmentVariable("CLIRCS_DATA_DIR");
        Environment.SetEnvironmentVariable("CLIRCS_DATA_DIR", temporary.Path);
        try
        {
            var application = new ClientApplication();
            var kickRejoin = application.SettingHelp("kickrejoin")!;
            Assert.Equal("HELP:", kickRejoin.Title);
            Assert.Equal("/set kickrejoin", kickRejoin.TitleHighlight!);
            Assert.True(kickRejoin.Fields!.Any(field => field.Label == "Description" && field.Value.Contains("rejoins", StringComparison.Ordinal)));
            Assert.True(kickRejoin.Fields!.Any(field => field.Label == "Currently" && field.Value == "off"));
            Assert.Equal("/set nickname", application.SettingHelp("identity.nick")!.TitleHighlight!);
            Assert.Equal("/set whois.output", application.SettingHelp("output.whois")!.TitleHighlight!);
            Assert.Equal("/set list.output", application.SettingHelp("list.output")!.TitleHighlight!);
            Assert.True(application.SettingHelp("list.output")!.Fields!
                .Any(field => field.Label == "Currently" && field.Value == "dedicated"));
            Assert.Equal("/set hostmasks.join", application.SettingHelp("hostmasks.join")!.TitleHighlight!);
            Assert.True(application.SettingHelp("network.reconnect")!.Fields!
                .Any(field => field.Label == "Currently" && field.Value == "on"));
            Assert.True(application.SettingHelp("kill.reconnect")!.Fields!
                .Any(field => field.Label == "Currently" && field.Value == "on"));
            application.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLIRCS_DATA_DIR", previous);
        }

        var detector = ClientApplication.ProtectionHelp("text.count")!;
        Assert.Equal("HELP:", detector.Title);
        Assert.Equal("/protect text.count", detector.TitleHighlight!);
        Assert.True(detector.Fields!.Any(field => field.Label == "Usage" && field.Value.Contains("text.count", StringComparison.Ordinal)));
        Assert.True(ClientApplication.ProtectionHelp("audit") is not null);
    }

    private static void FullScreenDisablesAlternateScroll()
    {
        Assert.True(ConsolePresenter.FullScreenEnterSequence.Contains("\u001b[?1007l", StringComparison.Ordinal));
        Assert.True(ConsolePresenter.FullScreenExitSequence.Contains("\u001b[?1007h", StringComparison.Ordinal));
    }

    private static void StartupPresentationIsHistoryReady()
    {
        var startup = new SessionEvent(
            NetworkSessionId.New(),
            BufferId.New(),
            SessionEventKind.Status,
            ProductInfo.DisplayName,
            DateTimeOffset.Now,
            new Dictionary<string, string?> { ["event"] = "startup" });
        var presenter = new ConsolePresenter();
        Assert.True(ConsolePresenter.IsStartupEvent(startup));
        Assert.Equal(13, presenter.MeasureEventRows(startup, "status", 120));
        Assert.Equal(ProductInfo.Description, ProductInfo.StartupQuote);
    }

    private static void ThemeInheritance()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, "custom.toml"), """
            name = "custom"
            base = "phosphor"

            [palette]
            accent = "Cyan"
            kick = "DarkRed"
            nick = "DarkGreen"
            status_background = "DarkGreen"
            topic_foreground = "White"
            topic_background = "DarkBlue"

            [markers]
            join = "+++"

            [layout]
            show_buffer_name = true
            show_nick_prefix = false
            grid_open = "< "
            grid_close = " >"
            """);

        var manager = new ThemeManager(temporary.Path);
        Assert.True(manager.TryGet("custom", out var theme));
        Assert.Equal(ConsoleColor.Cyan, theme!.Accent);
        Assert.Equal(ConsoleColor.DarkGreen, theme.Nick);
        Assert.Equal(ConsoleColor.DarkRed, theme.Kick);
        Assert.Equal(ConsoleColor.Gray, theme.Normal);
        Assert.Equal(ConsoleColor.White, theme.TopicForeground);
        Assert.Equal(ConsoleColor.DarkBlue, theme.TopicBackground);
        Assert.Equal("+++", theme.JoinMarker);
        Assert.True(theme.ShowBufferName);
        Assert.False(theme.ShowNickPrefix);
        Assert.Equal("< ", theme.GridOpen);
        Assert.Equal(" >", theme.GridClose);
    }

    private static void DamagedThemeIsIsolated()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, "broken.toml"), "this is not valid");
        var manager = new ThemeManager(temporary.Path);

        Assert.True(manager.TryGet("clircs", out _));
        Assert.True(manager.TryGet("clirc", out _));
        Assert.False(manager.TryGet("midnight", out _));
        Assert.Equal(1, manager.Errors.Count);
    }

    private static void ThemeParsingIsDeterministic()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, "a.toml"), """
            name = "duplicate" # a valid inline comment
            base = "clircs"
            [markers]
            join = "#>" # the hash inside the quotes is data
            """);
        File.WriteAllText(Path.Combine(temporary.Path, "b.toml"), """
            name = "duplicate"
            base = "plain"
            """);
        File.WriteAllText(Path.Combine(temporary.Path, "repeated.toml"), """
            name = "repeated"
            name = "again"
            """);

        var manager = new ThemeManager(temporary.Path);
        Assert.True(manager.TryGet("duplicate", out var duplicate));
        Assert.Equal("#>", duplicate!.JoinMarker);
        Assert.True(manager.Errors.Any(error => error.StartsWith("b.toml:", StringComparison.Ordinal)));
        Assert.True(manager.Errors.Any(error => error.StartsWith("repeated.toml:", StringComparison.Ordinal)));
    }

    private static void AppearancePersistence()
    {
        using var temporary = new TemporaryDirectory();
        var store = new AppearanceSettingsStore(Path.Combine(temporary.Path, "appearance.json"));
        store.Save(new AppearanceSettings(
            "phosphor", "full", "userhost", "off",
            new Dictionary<string, string> { ["whois"] = "dedicated" },
            AutoRejoinOnKick: true,
            DefaultKickMessage: "lewser",
            AnnounceUserInfoOnJoin: true,
            HighlightNickname: false,
            DefaultBanmask: "userhost",
            Nickname: "rekkals",
            AlternateNickname: "rekkals_",
            Username: "example",
            RealName: "Example User",
            CloneDetection: false,
            NetworkReconnect: false,
            KillReconnect: false,
            DccAddress: "203.0.113.42",
            DccPorts: "50000-50009",
            DccDownloads: Path.Combine(temporary.Path, "downloads"),
            AwayMessage: "out getting lunch"));

        var loaded = store.Load();
        Assert.Equal("phosphor", loaded.Theme);
        Assert.Equal("userhost", loaded.PartHostmasks);
        Assert.Equal("dedicated", loaded.OutputRoutes["whois"]);
        Assert.True(loaded.AutoRejoinOnKick);
        Assert.Equal("lewser", loaded.DefaultKickMessage!);
        Assert.True(loaded.AnnounceUserInfoOnJoin);
        Assert.False(loaded.HighlightNickname);
        Assert.Equal("userhost", loaded.DefaultBanmask);
        Assert.Equal("rekkals", loaded.Nickname!);
        Assert.Equal("rekkals_", loaded.AlternateNickname!);
        Assert.Equal("example", loaded.Username!);
        Assert.Equal("Example User", loaded.RealName!);
        Assert.False(loaded.CloneDetection);
        Assert.False(loaded.NetworkReconnect);
        Assert.False(loaded.KillReconnect);
        Assert.Equal("203.0.113.42", loaded.DccAddress);
        Assert.Equal("50000-50009", loaded.DccPorts);
        Assert.Equal(Path.Combine(temporary.Path, "downloads"), loaded.DccDownloads!);
        Assert.Equal("out getting lunch", loaded.AwayMessage);
    }

    private static void LinksDefaultsToStatus()
    {
        using var temporary = new TemporaryDirectory();
        var store = new AppearanceSettingsStore(Path.Combine(temporary.Path, "missing.json"));
        Assert.Equal("status", store.Load().OutputRoutes["links"]);
    }

    private static void ListDefaultsToDedicated()
    {
        using var temporary = new TemporaryDirectory();
        var store = new AppearanceSettingsStore(Path.Combine(temporary.Path, "missing.json"));
        Assert.Equal("dedicated", store.Load().OutputRoutes["list"]);
    }

    private static void DnsDefaultsAndFormatting()
    {
        using var temporary = new TemporaryDirectory();
        var store = new AppearanceSettingsStore(Path.Combine(temporary.Path, "missing.json"));
        Assert.Equal("active", store.Load().OutputRoutes["dns"]);

        var forward = ClientApplication.DnsResultText(
            "irc.example.test",
            new IPHostEntry
            {
                HostName = "irc.example.test",
                AddressList = [IPAddress.Parse("192.0.2.10"), IPAddress.Parse("2001:db8::10")],
                Aliases = []
            });
        Assert.Equal("DNS: Resolved irc.example.test to 192.0.2.10, 2001:db8::10", forward);

        var reverse = ClientApplication.DnsResultText(
            "192.0.2.10",
            new IPHostEntry
            {
                HostName = "irc.example.test",
                AddressList = [IPAddress.Parse("192.0.2.10")],
                Aliases = []
            });
        Assert.Equal("DNS: Resolved 192.0.2.10 to irc.example.test", reverse);

        var state = new NetworkSessionState(NetworkSessionId.New(), "Example", IrcCaseMapping.Rfc1459);
        state.GetOrCreateChannel("#clircs").GetOrAddMember("rekkals", "user", "chat.example.test");
        Assert.Equal("chat.example.test", ClientApplication.ResolveDnsLookupTarget(state, "rekkals"));
        Assert.Equal("localhost", ClientApplication.ResolveDnsLookupTarget(state, "localhost"));
    }

    private static void CtcpDefaultsToActive()
    {
        using var temporary = new TemporaryDirectory();
        var store = new AppearanceSettingsStore(Path.Combine(temporary.Path, "missing.json"));
        Assert.Equal("active", store.Load().OutputRoutes["ctcp"]);
        Assert.Equal("active", store.Load().OutputRoutes["notice"]);
        Assert.Equal("active", store.Load().OutputRoutes["messageguard"]);
        Assert.True(store.Load().HighlightNickname);
        Assert.Equal("host", store.Load().DefaultBanmask);
        Assert.True(store.Load().CloneDetection);
        Assert.True(store.Load().NetworkReconnect);
        Assert.True(store.Load().KillReconnect);
    }

    private static void InviteAndKickDefaults()
    {
        using var temporary = new TemporaryDirectory();
        var store = new AppearanceSettingsStore(Path.Combine(temporary.Path, "missing.json"));
        Assert.Equal("active", store.Load().OutputRoutes["invite"]);
        var kick = new SessionEvent(
            NetworkSessionId.New(),
            BufferId.New(),
            SessionEventKind.Part,
            "Alice was kicked",
            DateTimeOffset.Now,
            new Dictionary<string, string?> { ["event"] = "kick" });
        Assert.Equal(ConsoleColor.Red, TerminalTheme.BuiltIns["clircs"].EventColor(kick));
        Assert.Equal(ConsoleColor.DarkCyan, TerminalTheme.BuiltIns["clircs"].EventColor(SessionEventKind.Part));
    }

    private static void QuoteProviderSelectsBundledQuotes()
    {
        using var temporary = new TemporaryDirectory();
        var bundled = Path.Combine(temporary.Path, "bundled.txt");
        var data = Path.Combine(temporary.Path, "data");
        File.WriteAllText(bundled, "first\nsecond\n");
        var provider = new QuoteProvider(data, bundled);
        var selected = provider.Next();
        Assert.True(selected is "first" or "second");
        Assert.True(File.Exists(provider.Path));

        File.WriteAllText(provider.Path, "kept\nWhat? Something like 36?\n");
        _ = new QuoteProvider(data, bundled);
        var migrated = File.ReadAllText(provider.Path);
        Assert.True(migrated.Contains("kept", StringComparison.Ordinal));
        Assert.True(migrated.Contains("Look at you two whipping out your Preciouses.", StringComparison.Ordinal));
        Assert.False(migrated.Contains("What? Something like 36?", StringComparison.Ordinal));
    }

    private static void InputHistoryNavigates()
    {
        var history = new InputHistory();
        history.Commit("/server example.test");
        history.Commit("/server example.test");
        history.Commit("hello");
        history.Commit("   ");
        history.Begin();

        Assert.Equal("hello", history.Previous("unfinished")!);
        Assert.Equal("/server example.test", history.Previous("ignored")!);
        Assert.Equal("/server example.test", history.Previous("ignored")!);
        Assert.Equal("hello", history.Next()!);
        Assert.Equal("unfinished", history.Next()!);
        Assert.True(history.Next() is null);

        var presenter = new ConsolePresenter();
        var first = BufferId.New();
        var second = BufferId.New();
        presenter.HistoryFor(first).Commit("first-window");
        presenter.HistoryFor(second).Commit("second-window");
        Assert.Equal("first-window", presenter.HistoryFor(first).Previous(string.Empty)!);
        Assert.Equal("second-window", presenter.HistoryFor(second).Previous(string.Empty)!);
        presenter.ForgetInputHistory(first);
        Assert.True(presenter.HistoryFor(first).Previous(string.Empty) is null);
    }

    private static void LegacyDataIsCopied()
    {
        using var temporary = new TemporaryDirectory();
        var legacy = Path.Combine(temporary.Path, "clirc");
        Directory.CreateDirectory(Path.Combine(legacy, "scripts"));
        File.WriteAllText(Path.Combine(legacy, "networks.toml"), "legacy");
        File.WriteAllText(Path.Combine(legacy, "scripts", "kept.txt"), "kept");

        var resolved = ClientDataDirectory.ResolveDefault(temporary.Path);
        Assert.Equal(Path.Combine(temporary.Path, "clircs"), resolved);
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(resolved, "networks.toml")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(resolved, "scripts", "kept.txt")));
        Assert.True(File.Exists(Path.Combine(legacy, "networks.toml")));
    }

    private static void NicknameCompletionCycles()
    {
        var completion = new NicknameCompletion();
        var input = new System.Text.StringBuilder("sla");
        static IReadOnlyList<string> Matches(string prefix) => prefix == "sla" ? ["slakker", "slappy"] : [];

        var cursor = completion.Complete(input, input.Length, Matches);
        Assert.Equal("slakker: ", input.ToString());
        Assert.Equal(input.Length, cursor!.Value);
        cursor = completion.Complete(input, cursor.Value, Matches);
        Assert.Equal("slappy: ", input.ToString());
        cursor = completion.Complete(input, cursor!.Value, Matches);
        Assert.Equal("slakker: ", input.ToString());

        completion.Reset();
        input = new System.Text.StringBuilder("hello sla");
        cursor = completion.Complete(input, input.Length, Matches);
        Assert.Equal("hello slakker", input.ToString());
        Assert.Equal(input.Length, cursor!.Value);
    }

    private static void NamesGridMeasurementUsesColumns()
    {
        var presenter = new ConsolePresenter();
        var presentation = new PresentationBlock(
            "Users (#channel): 12, Ops: 9, Voice: 0, Normal: 3",
            Grid:
            [
                "@AndroSyn", "@bartman", "@BeAsTaH", "blk", "@Kan", "@Mongoose",
                "@scrim", "@shitsack", "slakker", "@viper", "@wicked", "rekkals"
            ],
            BracketGridCells: true,
            TitleHighlight: "#channel");
        var sessionEvent = new SessionEvent(
            NetworkSessionId.New(), BufferId.New(), SessionEventKind.ChannelInfo, string.Empty,
            DateTimeOffset.Now, Presentation: presentation);

        Assert.Equal(4, presenter.MeasureEventRows(sessionEvent, "#channel", width: 110));
    }

    private static void ViewportPagingHonorsRows()
    {
        var history = new[] { "status-a", "status-b", "box", "latest" };
        var rows = new Dictionary<string, int>
        {
            ["status-a"] = 1,
            ["status-b"] = 1,
            ["box"] = 3,
            ["latest"] = 1
        };

        var latest = ViewportHistory.SelectRows(history, offsetRows: 0, rowBudget: 4, item => rows[item]);
        Assert.Equal("box,latest", string.Join(',', latest.Select(slice => slice.Item)));
        var previous = ViewportHistory.SelectRows(history, offsetRows: 4, rowBudget: 4, item => rows[item]);
        Assert.Equal("status-a,status-b,box", string.Join(',', previous.Select(slice => slice.Item)));
        Assert.Equal(2, previous[^1].TakeRows);

        var crossing = ViewportHistory.SelectRows(
            new[] { "large", "latest" }, offsetRows: 0, rowBudget: 5,
            item => item == "large" ? 10 : 2);
        Assert.Equal("large,latest", string.Join(',', crossing.Select(slice => slice.Item)));
    }

    private static void WindowActivationResetsTransientState()
    {
        var states = new WindowStateRegistry();
        var session = NetworkSessionId.New();
        var buffer = BufferId.New();
        var number = states.AssignNumber(buffer);
        states.MarkUnread(buffer, SessionEventKind.Highlight);
        states.SetScrollOffset(buffer, 48);

        states.Activate(session, buffer);

        Assert.Equal(number, states.AssignNumber(buffer));
        Assert.False(states.IsUnread(buffer));
        Assert.Equal(0, states.ScrollOffset(buffer));
        Assert.True(states.IsActive(session, buffer));
    }

    private static void WindowRemovalClearsTerminalState()
    {
        var states = new WindowStateRegistry();
        var first = BufferId.New();
        var second = BufferId.New();
        var session = NetworkSessionId.New();
        var firstNumber = states.AssignNumber(first);
        states.AppendHistory(new SessionEvent(
            session, first, SessionEventKind.Message, "hello", DateTimeOffset.Now));
        states.MarkUnread(first, SessionEventKind.Message);
        states.SetScrollOffset(first, 24);
        states.Activate(session, first);

        states.Remove(first);

        Assert.True(states.HistoryIsEmpty(first));
        Assert.False(states.IsUnread(first));
        Assert.Equal(0, states.ScrollOffset(first));
        Assert.False(states.HasNumber(first));
        Assert.True(states.ActiveLocation() == (null, null));
        Assert.True(states.AssignNumber(second) > firstNumber);
    }

    private static void ViewportHistorySnapshotsAreIsolated()
    {
        var states = new WindowStateRegistry();
        var session = NetworkSessionId.New();
        var buffer = BufferId.New();
        states.AppendHistory(new SessionEvent(
            session, buffer, SessionEventKind.Message, "first", DateTimeOffset.Now));

        var snapshot = states.HistorySnapshot(buffer);
        states.AppendHistory(new SessionEvent(
            session, buffer, SessionEventKind.Message, "second", DateTimeOffset.Now));

        Assert.Equal(1, snapshot.Length);
        Assert.Equal("first", snapshot[0].Text);
        Assert.Equal(2, states.HistorySnapshot(buffer).Length);
    }

    private static void TransientViewportEventsReplaceInPlace()
    {
        var sessionId = NetworkSessionId.New();
        var bufferId = BufferId.New();
        var history = new List<SessionEvent>
        {
            new(sessionId, bufferId, SessionEventKind.Status, "Sending file", DateTimeOffset.Now)
        };
        var firstProgress = new SessionEvent(
            sessionId, bufferId, SessionEventKind.Status, "10%", DateTimeOffset.Now,
            new Dictionary<string, string?>
            {
                ["history.transientKey"] = "dcc.transfer.7",
                ["history.replaceKey"] = "dcc.transfer.7"
            });
        var secondProgress = firstProgress with { Text = "50%" };
        var completed = new SessionEvent(
            sessionId, bufferId, SessionEventKind.Status, "Sent file", DateTimeOffset.Now,
            new Dictionary<string, string?>
            {
                ["history.replaceKey"] = "dcc.transfer.7",
                ["history.finalKey"] = "dcc.transfer.7"
            });

        var first = ViewportHistory.StoreEvent(history, firstProgress);
        Assert.True(first.Stored);
        Assert.False(first.Replaced);
        Assert.Equal(2, history.Count);
        var second = ViewportHistory.StoreEvent(history, secondProgress);
        Assert.True(second.Stored);
        Assert.True(second.Replaced);
        Assert.Equal("10%", second.Previous!.Text);
        Assert.Equal(2, history.Count);
        Assert.Equal("50%", history[^1].Text);
        var final = ViewportHistory.StoreEvent(history, completed);
        Assert.True(final.Stored);
        Assert.True(final.Replaced);
        Assert.Equal("50%", final.Previous!.Text);
        Assert.Equal(2, history.Count);
        Assert.Equal("Sent file", history[^1].Text);
        Assert.True(history[^1].Fields?.ContainsKey("history.transientKey") != true);
        var lateProgress = ViewportHistory.StoreEvent(history, secondProgress with { Text = "75%" });
        Assert.False(lateProgress.Stored);
        Assert.Equal(2, history.Count);
        Assert.Equal("Sent file", history[^1].Text);
    }

    private static void ViewportPagingSlicesLargeResults()
    {
        var history = new[] { "before", "who", "after" };
        var rows = new Dictionary<string, int>
        {
            ["before"] = 2,
            ["who"] = 794,
            ["after"] = 3
        };

        var bottom = ViewportHistory.SelectRows(history, offsetRows: 0, rowBudget: 25, item => rows[item]);
        Assert.Equal("who,after", string.Join(',', bottom.Select(slice => slice.Item)));
        Assert.True(bottom[0].SkipRows > 700);

        var previous = ViewportHistory.SelectRows(history, offsetRows: 25, rowBudget: 25, item => rows[item]);
        Assert.Equal("who", string.Join(',', previous.Select(slice => slice.Item)));
        Assert.True(previous[0].SkipRows < bottom[0].SkipRows);
        Assert.Equal(25, previous[0].TakeRows);
    }

    private static void ViewportPagingSlicesNamesGrid()
    {
        var presenter = new ConsolePresenter();
        var grid = Enumerable.Range(0, 800).Select(index => $"nick{index:0000}").ToArray();
        var sessionEvent = new SessionEvent(
            NetworkSessionId.New(),
            BufferId.New(),
            SessionEventKind.Server,
            "NAMES #large",
            DateTimeOffset.Now,
            Presentation: new PresentationBlock(
                "Users (#large): 800",
                Grid: grid,
                BracketGridCells: true,
                TitleHighlight: "#large"));
        var original = Console.Out;
        try
        {
            var firstWriter = new StringWriter();
            Console.SetOut(firstWriter);
            presenter.EventRows(sessionEvent, "#large", skipRows: 20, takeRows: 25);
            var first = firstWriter.ToString();

            var secondWriter = new StringWriter();
            Console.SetOut(secondWriter);
            presenter.EventRows(sessionEvent, "#large", skipRows: 40, takeRows: 25);
            var second = secondWriter.ToString();

            Assert.False(first == second);
            Assert.True(first.Contains("nick0152", StringComparison.Ordinal));
            Assert.False(second.Contains("nick0152", StringComparison.Ordinal));
            Assert.True(second.Contains("nick0312", StringComparison.Ordinal));
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private static void ViewportPagingSlicesFieldBoxes()
    {
        var presenter = new ConsolePresenter();
        var fields = Enumerable.Range(0, 80)
            .Select(index => new PresentationField($"setting{index:00}", $"value {index:00} with enough words to wrap cleanly"))
            .ToArray();
        var sessionEvent = new SessionEvent(
            NetworkSessionId.New(), BufferId.New(), SessionEventKind.Server, "settings", DateTimeOffset.Now,
            Presentation: new PresentationBlock("Client Settings", Fields: fields));
        var original = Console.Out;
        try
        {
            var firstWriter = new StringWriter();
            Console.SetOut(firstWriter);
            presenter.EventRows(sessionEvent, "#large", skipRows: 20, takeRows: 20);
            var first = firstWriter.ToString();

            var secondWriter = new StringWriter();
            Console.SetOut(secondWriter);
            presenter.EventRows(sessionEvent, "#large", skipRows: 40, takeRows: 20);
            var second = secondWriter.ToString();

            Assert.False(first == second);
            Assert.True(first.Contains("setting", StringComparison.Ordinal));
            Assert.True(second.Contains("setting", StringComparison.Ordinal));
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private static void ViewportPagingRetainsMixedPresentations()
    {
        var presenter = new ConsolePresenter();
        var sessionEvent = new SessionEvent(
            NetworkSessionId.New(), BufferId.New(), SessionEventKind.Server, "protection", DateTimeOffset.Now,
            Presentation: new PresentationBlock(
                "Personal protection: EFNet",
                [
                    new PresentationField("Protection", "off"),
                    new PresentationField("Ignore time", "45s")
                ],
                new PresentationTable(
                    ["Detector", "State", "Events", "Within"],
                    [
                        ["private message", "on", "6", "5s"],
                        ["private notice", "on", "6", "5s"],
                        ["ctcp.user", "on", "4", "10s"],
                        ["invite", "on", "4", "30s"]
                    ])));
        var original = Console.Out;
        try
        {
            var fieldWriter = new StringWriter();
            Console.SetOut(fieldWriter);
            presenter.EventRows(sessionEvent, "#clircs", skipRows: 0, takeRows: 5);
            var fields = fieldWriter.ToString();
            Assert.True(fields.Contains("Protection", StringComparison.Ordinal));
            Assert.True(fields.Contains("Ignore time", StringComparison.Ordinal));

            var tableWriter = new StringWriter();
            Console.SetOut(tableWriter);
            presenter.EventRows(sessionEvent, "#clircs", skipRows: 3, takeRows: 7);
            var table = tableWriter.ToString();
            Assert.True(table.Contains("Detector", StringComparison.Ordinal));
            Assert.True(table.Contains("private message", StringComparison.Ordinal));
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private static void TerminalWrappingKeepsWordsIntact()
    {
        var wrapped = TerminalWordWrapper.Wrap(string.Empty, "the word already moves together", 18);
        Assert.Equal(2, wrapped.Count);
        Assert.Equal("the word already", wrapped[0].Text);
        Assert.Equal("moves together", wrapped[1].Text);
        Assert.Equal(wrapped[0].Leading.Length, wrapped[1].Leading.Length);

        var longWord = TerminalWordWrapper.Wrap(string.Empty, "abcdefghijkl", 7);
        Assert.True(longWord.Count > 1);
        Assert.Equal("abcdefghijkl", string.Concat(longWord.Select(line => line.Text)));
    }

    private static void WrappedUrlsRetainCompleteTarget()
    {
        const string text = "See https://example.test/a/very/long/address?one=two&three=four now";
        var links = TerminalHyperlinkDetector.Find(text);
        Assert.Equal(1, links.Count);
        Assert.Equal("https://example.test/a/very/long/address?one=two&three=four", links[0].Target);

        var wrapped = TerminalWordWrapper.Wrap("[12:34] ", text, 30);
        Assert.True(wrapped.Count > 2);
        Assert.True(links[0].Length > wrapped[1].Text.Length);
    }

    private static void TranscriptContinuationsBeginAtLeftEdge()
    {
        var wrapped = TerminalWordWrapper.Wrap("[12:34] <nickname> ", "one two three four five six", 30);
        Assert.True(wrapped.Count > 1);
        Assert.Equal("[12:34] <nickname> ", wrapped[0].Leading);
        Assert.Equal(string.Empty, wrapped[1].Leading);
    }

    private static void InfoFieldContinuationsRetainIndentation()
    {
        var wrapped = TerminalWordWrapper.Wrap("| Host  ", "|       ", "abcdefghijklmnopqrstuvwxyz", 19);
        Assert.Equal(3, wrapped.Count);
        Assert.Equal("| Host  ", wrapped[0].Leading);
        Assert.Equal("abcdefghij", wrapped[0].Text);
        Assert.Equal("|       ", wrapped[1].Leading);
        Assert.Equal("klmnopqrst", wrapped[1].Text);
        Assert.Equal("|       ", wrapped[2].Leading);
        Assert.Equal("uvwxyz", wrapped[2].Text);
    }

    private static void WindowCloseBehaviorFollowsBufferKind()
    {
        Assert.Equal(ClientApplication.BufferCloseAction.Refuse,
            ClientApplication.CloseActionFor(BufferKind.Status, joinedChannel: false));
        Assert.Equal(ClientApplication.BufferCloseAction.PartThenClose,
            ClientApplication.CloseActionFor(BufferKind.Channel, joinedChannel: true));
        Assert.Equal(ClientApplication.BufferCloseAction.CloseImmediately,
            ClientApplication.CloseActionFor(BufferKind.Channel, joinedChannel: false));
        Assert.Equal(ClientApplication.BufferCloseAction.CloseImmediately,
            ClientApplication.CloseActionFor(BufferKind.Query, joinedChannel: false));
        Assert.Equal(ClientApplication.BufferCloseAction.CloseImmediately,
            ClientApplication.CloseActionFor(BufferKind.Results, joinedChannel: false));
        Assert.Equal(ClientApplication.BufferCloseAction.CloseImmediately,
            ClientApplication.CloseActionFor(BufferKind.Diagnostics, joinedChannel: false));
    }

    private static void WindowCloseSelectsPreviousNumber()
    {
        Assert.Equal(4, ClientApplication.PreviousBufferNumber(6, [1, 2, 3, 4, 7, 9])!.Value);
        Assert.Equal(1, ClientApplication.PreviousBufferNumber(2, [1, 3, 4])!.Value);
        Assert.True(ClientApplication.PreviousBufferNumber(1, [2, 3]) is null);
    }

    private static void StatusWindowsUseDistinctTargetMarker()
    {
        var network = NetworkSessionId.New();
        Assert.Equal("-", ClientApplication.WindowTarget(
            new BufferState(BufferId.New(), network, BufferKind.Status, "irc.example.test")));
        Assert.Equal("#clircs", ClientApplication.WindowTarget(
            new BufferState(BufferId.New(), network, BufferKind.Channel, "#clircs")));
    }

    private static void ThemeOverviewIsConcise()
    {
        var overview = ClientApplication.ThemeOverview(
            "clircs",
            ["plain", "clircs", "phosphor"]);

        Assert.Equal("Theme", overview.Title);
        var fields = overview.Fields!;
        Assert.Equal("clircs", fields.Single(field => field.Label == "Current").Value);
        Assert.Equal(
            "phosphor, plain",
            fields.Single(field => field.Label == "Available").Value);
        Assert.Equal("Use: /theme list|reload|use <name>", overview.Summary!);
        Assert.False(fields.Any(field => field.Label.Contains("color", StringComparison.OrdinalIgnoreCase)));
    }

    private static void LongInputScrollsHorizontally()
    {
        var input = "abcdefghijklmnopqrstuvwxyz";
        var layout = InputLineLayouter.Calculate("[#clircs] ", input, input.Length, 20, 0);
        Assert.Equal("[#clircs] ", layout.Prompt);
        Assert.True(layout.ViewStart > 0);
        Assert.True(layout.Text.Length <= 9);
        Assert.Equal(19, layout.CursorColumn);

        var movedLeft = InputLineLayouter.Calculate("[#clircs] ", input, 3, 20, layout.ViewStart);
        Assert.Equal(3, movedLeft.ViewStart);
        Assert.Equal(10, movedLeft.CursorColumn);
        Assert.False(movedLeft.Text.Contains('\n'));
    }

    private static void InputFormattingShortcutsInsertControlCodes()
    {
        var expected = new[]
        {
            (ConsoleKey.B, InputFormattingControls.Bold),
            (ConsoleKey.K, InputFormattingControls.Color),
            (ConsoleKey.O, InputFormattingControls.Reset),
            (ConsoleKey.R, InputFormattingControls.Reverse),
            (ConsoleKey.I, InputFormattingControls.Italic),
            (ConsoleKey.U, InputFormattingControls.Underline)
        };
        foreach (var (key, control) in expected)
        {
            Assert.True(InputFormattingControls.TryTranslate(
                new ConsoleKeyInfo(control, key, shift: false, alt: false, control: true),
                out var actual));
            Assert.Equal(control, actual);
        }

        var wireText = $"{InputFormattingControls.Bold}bold{InputFormattingControls.Reset} " +
            $"{InputFormattingControls.Color}04red{InputFormattingControls.Color}";
        var displayText = InputFormattingControls.ToDisplayText(wireText);
        Assert.Equal(wireText.Length, displayText.Length);
        Assert.False(displayText.Any(char.IsControl));
        Assert.Equal("bold red", IrcTextFormatting.ToPlainText(wireText));
    }

    private static void WhoAddressesExpandWithViewport()
    {
        var address = "user@" + new string('a', 58) + ".example.test";
        var name = "https://irc.example.test/members/" + new string('n', 48);
        var table = new PresentationTable(
            ["Nick", "Status", "Address", "Server", "Name"],
            [["slakker", "here", address, "irc.example.test", name]],
            KeepAllColumns: true,
            MaximumWidths: [24, 12, PresentationTable.UnboundedWidth, 28, PresentationTable.UnboundedWidth]);

        var narrow = ConsolePresenter.CalculateTableLayout(table, 80);
        var wide = ConsolePresenter.CalculateTableLayout(table, 260);
        Assert.True(narrow.Widths[2] < address.Length);
        Assert.True(narrow.Widths[4] < name.Length);
        Assert.Equal(address.Length, wide.Widths[2]);
        Assert.Equal(name.Length, wide.Widths[4]);
        Assert.True(wide.Widths[2] > narrow.Widths[2]);
        Assert.True(wide.Widths[4] > narrow.Widths[4]);
    }

    private static void ChromeRowsReserveLatestOutput()
    {
        var statusTop = ConsolePresenter.ChromeStatusRow(windowTop: 0, windowHeight: 30, bufferHeight: 30);
        Assert.Equal(28, statusTop);
        Assert.Equal(0, ConsolePresenter.ChromeReservationScrolls(cursorTop: 28, statusTop));
        Assert.Equal(1, ConsolePresenter.ChromeReservationScrolls(cursorTop: 29, statusTop));

        var offsetStatusTop = ConsolePresenter.ChromeStatusRow(windowTop: 100, windowHeight: 30, bufferHeight: 130);
        Assert.Equal(128, offsetStatusTop);
        Assert.Equal(1, ConsolePresenter.ChromeReservationScrolls(cursorTop: 129, offsetStatusTop));
    }

    private static void ExistingChromeRowsAreReused()
    {
        Assert.True(ConsolePresenter.ShouldReserveChromeRows(chromeVisible: false));
        Assert.False(ConsolePresenter.ShouldReserveChromeRows(chromeVisible: true));
    }

    private static void FullRedrawsAreAtomic()
    {
        var presenter = new ConsolePresenter();
        var chrome = new WindowChromeModel(
            new BufferHeaderModel(null, []),
            new StatusBarModel(["(1) EFNet", "status"], []),
            "[status] ");
        using var redrawStarted = new ManualResetEventSlim();
        using var releaseRedraw = new ManualResetEventSlim();
        var original = Console.Out;
        try
        {
            var writer = new StringWriter();
            Console.SetOut(writer);
            Exception? redrawFailure = null;
            var redraw = new Thread(() =>
            {
                try
                {
                    presenter.Redraw(chrome, 2, () =>
                    {
                        presenter.LocalResult("history one");
                        redrawStarted.Set();
                        releaseRedraw.Wait(TimeSpan.FromSeconds(5));
                        presenter.LocalResult("history two");
                    });
                }
                catch (Exception exception)
                {
                    redrawFailure = exception;
                    redrawStarted.Set();
                }
            });
            redraw.Start();

            Assert.True(redrawStarted.Wait(TimeSpan.FromSeconds(5)));
            if (redrawFailure is not null) throw redrawFailure;
            var liveOutput = new Thread(() => presenter.LocalResult("live event"));
            liveOutput.Start();
            Assert.False(liveOutput.Join(TimeSpan.FromMilliseconds(100)));
            releaseRedraw.Set();
            Assert.True(redraw.Join(TimeSpan.FromSeconds(5)));
            Assert.True(liveOutput.Join(TimeSpan.FromSeconds(5)));

            var output = writer.ToString();
            Assert.True(output.IndexOf("history one", StringComparison.Ordinal) <
                output.IndexOf("history two", StringComparison.Ordinal));
            Assert.True(output.IndexOf("history two", StringComparison.Ordinal) <
                output.IndexOf("live event", StringComparison.Ordinal));
        }
        finally
        {
            releaseRedraw.Set();
            Console.SetOut(original);
        }
    }

    private static void TopicHeadersReserveContentRows()
    {
        Assert.Equal(28, ConsolePresenter.ContentRowsForHeight(windowHeight: 30, hasHeader: false));
        Assert.Equal(27, ConsolePresenter.ContentRowsForHeight(windowHeight: 30, hasHeader: true));
        Assert.Equal(1, ConsolePresenter.ContentRowsForHeight(windowHeight: 2, hasHeader: true));
    }

    private static void PreservedTableColumnsDoNotWrap()
    {
        var presenter = new ConsolePresenter();
        var longAddress = new string('a', 180) + "@example.test";
        var sessionEvent = new SessionEvent(
            NetworkSessionId.New(), BufferId.New(), SessionEventKind.Server, string.Empty,
            DateTimeOffset.Now,
            Presentation: new PresentationBlock(
                "CLONES:",
                Table: new PresentationTable(
                    ["Nick", "Address", "Name"],
                    [["slakker", longAddress, "Slakker"]],
                    new HashSet<int> { 1 })));
        var original = Console.Out;
        try
        {
            var writer = new StringWriter();
            Console.SetOut(writer);
            presenter.Event(sessionEvent, "#clircs");
            var rendered = writer.ToString();
            Assert.False(rendered.Contains(longAddress, StringComparison.Ordinal));
            Assert.Equal(presenter.MeasureEventRows(sessionEvent, "#clircs"),
                rendered.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private static void BanmaskStylesAreConsistent()
    {
        var member = new ChannelMemberState("Alice", "~alice", "dialup.example.test");
        Assert.Equal("*!*@dialup.example.test", BanmaskFormatter.Create(member, BanmaskStyle.Host));
        Assert.Equal("*!~alice@dialup.example.test", BanmaskFormatter.Create(member, BanmaskStyle.UserHost));
        Assert.Equal("Alice!~alice@dialup.example.test", BanmaskFormatter.Create(member, BanmaskStyle.NickUserHost));
        Assert.Equal("*!*@*.example.test", BanmaskFormatter.Create(member, BanmaskStyle.WildcardHost));

        var address = new ChannelMemberState("Alice", "alice", "192.0.2.7");
        Assert.Equal("*!*@192.0.2.7", BanmaskFormatter.Create(address, BanmaskStyle.WildcardHost));
        Assert.True(BanmaskFormatter.TryParse("nick-userhost", out var parsed));
        Assert.Equal(BanmaskStyle.NickUserHost, parsed);
        Assert.False(BanmaskFormatter.TryParse("3", out _));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"clirc-theme-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
