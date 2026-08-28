using System.Text;
using System.Runtime.InteropServices;
using Clircs.Identity;
using Clircs.Sessions;

namespace Clircs.ConsoleClient;

internal sealed class ConsolePresenter
{
    internal const string FullScreenEnterSequence = "\u001b[?1049h\u001b[?1007l\u001b[2J\u001b[H";
    internal const string FullScreenExitSequence = "\u001b[?1007h\u001b[?1049l";
    internal static IReadOnlyList<(ConsoleColor Foreground, ConsoleColor Background)> StartupLogoColors { get; } =
    [
        (ConsoleColor.White, ConsoleColor.DarkCyan),
        (ConsoleColor.White, ConsoleColor.DarkRed),
        (ConsoleColor.White, ConsoleColor.DarkGreen),
        (ConsoleColor.Yellow, ConsoleColor.DarkBlue),
        (ConsoleColor.Magenta, ConsoleColor.DarkCyan),
        (ConsoleColor.White, ConsoleColor.DarkRed)
    ];
    private readonly object _consoleLock = new();
    private readonly StringBuilder _input = new();
    private readonly InputHistory _defaultInputHistory = new();
    private readonly Dictionary<BufferId, InputHistory> _inputHistories = [];
    private InputHistory _activeInputHistory;
    private readonly NicknameCompletion _nicknameCompletion = new();
    private string _prompt = string.Empty;
    private string? _pendingInput;
    private int _inputCursor;
    private int _inputViewStart;
    private int _renderTop;
    private int _renderRows = 2;
    private bool _readingInput;
    private bool _chromeVisible;
    private bool _maskInput;
    private Func<string, IReadOnlyList<string>>? _nicknameMatchProvider;
    private StatusBarModel _statusBar = new(["offline"], []);
    private BufferHeaderModel _bufferHeader = new(null, []);
    private string? _topicBar;
    private bool _topicBarWasVisible;
    private TerminalTheme _theme = TerminalTheme.BuiltIns["clircs"];
    private HostmaskVisibility _joinHostmasks = HostmaskVisibility.UserHost;
    private HostmaskVisibility _partHostmasks = HostmaskVisibility.UserHost;
    private HostmaskVisibility _quitHostmasks = HostmaskVisibility.UserHost;
    private bool _fullScreen;
    private bool _redrawing;
    private int _eventBatchDepth;
    private bool _eventBatchOutputStarted;
    private bool _eventBatchChromeDirty;
    private uint? _originalOutputMode;
    private int _observedWidth;

    public ConsolePresenter()
    {
        _activeInputHistory = _defaultInputHistory;
    }
    private int _observedHeight;
    private int _renderedWidth;
    private int _renderedHeight;
    private DateTimeOffset _resizeObservedAt;

    public TerminalTheme Theme => _theme;

    private static bool HasInteractiveConsole => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public int ViewportContentRows => HasInteractiveConsole
        ? Math.Max(1, Console.WindowHeight - 2 - (_topicBar is null ? 0 : 1))
        : 50;

    public int ViewportContentRowsFor(BufferHeaderModel header)
    {
        if (!HasInteractiveConsole) return 50;
        var width = Math.Max(1, Console.BufferWidth - 1);
        var hasHeader = BufferHeaderComposer.Compose(header, width, _theme.HeaderSeparator) is not null;
        return ContentRowsForHeight(Console.WindowHeight, hasHeader);
    }

    internal static int ContentRowsForHeight(int windowHeight, bool hasHeader) =>
        Math.Max(1, windowHeight - 2 - (hasHeader ? 1 : 0));

    public void EnterFullScreen()
    {
        if (!HasInteractiveConsole || _fullScreen) return;
        var output = GetStdHandle(-11);
        if (output != IntPtr.Zero && output != new IntPtr(-1) && GetConsoleMode(output, out var mode))
        {
            _originalOutputMode = mode;
            SetConsoleMode(output, mode | 0x0004);
        }
        Console.Write(FullScreenEnterSequence);
        _fullScreen = true;
        _observedWidth = _renderedWidth = Console.BufferWidth;
        _observedHeight = _renderedHeight = Console.WindowHeight;
        _resizeObservedAt = DateTimeOffset.UtcNow;
    }

    public void ExitFullScreen()
    {
        if (!_fullScreen) return;
        Console.ResetColor();
        Console.Write(FullScreenExitSequence);
        if (_originalOutputMode is { } mode)
        {
            var output = GetStdHandle(-11);
            if (output != IntPtr.Zero && output != new IntPtr(-1)) SetConsoleMode(output, mode);
        }
        _fullScreen = false;
    }

    public void SetTheme(TerminalTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        lock (_consoleLock)
        {
            if (_chromeVisible && HasInteractiveConsole)
            {
                ClearInputUnsafe();
            }
            _theme = theme;
            if (_chromeVisible && HasInteractiveConsole)
            {
                RenderInputUnsafe();
            }
        }
    }

    public void SetHostmaskVisibility(
        HostmaskVisibility joins,
        HostmaskVisibility parts,
        HostmaskVisibility quits)
    {
        lock (_consoleLock)
        {
            _joinHostmasks = joins;
            _partHostmasks = parts;
            _quitHostmasks = quits;
        }
    }

    public bool SetChrome(WindowChromeModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (_consoleLock)
        {
            var previousHeaderVisible = _topicBar is not null;
            var changed = !HeaderEquals(_bufferHeader, model.Header) ||
                !StatusEquals(_statusBar, model.Status) ||
                (_readingInput && !string.Equals(_prompt, model.Prompt, StringComparison.Ordinal));
            if (!changed) return false;

            if (_eventBatchDepth == 0 && _chromeVisible && HasInteractiveConsole) ClearInputUnsafe();
            ApplyChromeUnsafe(model);
            var layoutChanged = previousHeaderVisible != (_topicBar is not null);
            if (_eventBatchDepth > 0)
            {
                _eventBatchChromeDirty = true;
            }
            else if (!layoutChanged && _chromeVisible && HasInteractiveConsole)
            {
                RenderInputUnsafe();
                SetCursorVisibleUnsafe(_readingInput);
            }
            return layoutChanged;
        }
    }

    private void ApplyChromeUnsafe(WindowChromeModel model)
    {
        _bufferHeader = model.Header;
        _topicBar = BufferHeaderComposer.Compose(
            model.Header,
            HasInteractiveConsole ? Math.Max(1, Console.BufferWidth - 1) : 119,
            _theme.HeaderSeparator);
        _statusBar = model.Status;
        if (_readingInput) _prompt = model.Prompt;
    }

    private static bool HeaderEquals(BufferHeaderModel left, BufferHeaderModel right) =>
        string.Equals(left.Primary, right.Primary, StringComparison.Ordinal) &&
        left.Auxiliary.SequenceEqual(right.Auxiliary);

    private static bool StatusEquals(StatusBarModel left, StatusBarModel right) =>
        left.Fields.SequenceEqual(right.Fields) && left.Activity.SequenceEqual(right.Activity);

    public void Banner()
    {
        lock (_consoleLock)
        {
            if (HasInteractiveConsole)
            {
                Console.Clear();
                var top = Math.Max(Console.WindowTop,
                    Console.WindowTop + Console.WindowHeight - 2 - StartupRows(Console.BufferWidth));
                Console.SetCursorPosition(0, Math.Min(top, Console.BufferHeight - 1));
            }
            WriteStartupBlockUnsafe();
        }
    }

    public void About()
    {
        lock (_consoleLock)
        {
            BeginOutputUnsafe();
            WriteStartupBlockUnsafe();
            EndOutputUnsafe();
        }
    }

    private void WriteStartupBlockUnsafe()
    {
        WriteStartupLineUnsafe(string.Empty, _theme.Normal);
        if (HasInteractiveConsole && Console.BufferWidth >= 45)
        {
            WriteStartupLogo();
        }
        else
        {
            WriteStartupLineUnsafe("clircs", _theme.Accent);
        }
        WriteStartupQuoteUnsafe(ProductInfo.StartupQuote);
        WriteStartupLineUnsafe(string.Empty, _theme.Normal);
        WriteStartupLineUnsafe(ProductInfo.DisplayName, _theme.Dim);
        WriteStartupLineUnsafe(WindowsVersionDisplay.Current, _theme.Dim);
        WriteStartupLineUnsafe(ProductInfo.StartupHelp, _theme.Dim);
    }

    internal static int StartupRows(int width) => width >= 45 ? 13 : 7;

    internal static bool IsStartupEvent(SessionEvent sessionEvent) =>
        SessionEventPresentation.From(sessionEvent).Subtype == SessionEventSubtype.Startup;

    private static void WriteStartupLineUnsafe(string text, ConsoleColor color)
    {
        WriteColoredUnsafe(text, color);
        Console.WriteLine();
    }

    private void WriteStartupLogo()
    {
        var colors = StartupLogoColors;
        var tiles = new[]
        {
            (Lines: new[] { " █████ ", "██     ", "██     ", "██     ", " █████ " }, colors[0].Foreground, colors[0].Background, Offset: 1),
            (Lines: new[] { "██    ", "██    ", "██    ", "██    ", "█████ " }, colors[1].Foreground, colors[1].Background, Offset: 0),
            (Lines: new[] { "  ██  ", "      ", "  ██  ", "  ██  ", "  ██  " }, colors[2].Foreground, colors[2].Background, Offset: 1),
            (Lines: new[] { "████  ", "██ ██ ", "███   ", "██ ██ ", "██  ██" }, colors[3].Foreground, colors[3].Background, Offset: 0),
            (Lines: new[] { "      ", " ████ ", "██    ", "██    ", " ████ " }, colors[4].Foreground, colors[4].Background, Offset: 1),
            (Lines: new[] { "      ", " ████ ", "███   ", "  ███ ", "████  " }, colors[5].Foreground, colors[5].Background, Offset: 1)
        };

        lock (_consoleLock)
        {
            for (var row = 0; row < 7; row++)
            {
                for (var index = 0; index < tiles.Length; index++)
                {
                    var tile = tiles[index];
                    var localRow = row - tile.Offset;
                    if (localRow >= 0 && localRow < tile.Lines.Length)
                    {
                        SetConsoleColorsUnsafe(tile.Foreground, tile.Background);
                        Console.Write(tile.Lines[localRow]);
                    }
                    else
                    {
                        ResetConsoleColorsUnsafe();
                        Console.Write(new string(' ', tile.Lines[0].Length));
                    }
                    ResetConsoleColorsUnsafe();
                    if (index < tiles.Length - 1) Console.Write(' ');
                }
                Console.WriteLine();
            }
            ResetConsoleColorsUnsafe();
        }
    }

    private void WriteStartupQuoteUnsafe(string? quote)
    {
        quote = TerminalTextSanitizer.Sanitize(quote ?? string.Empty).Trim();
        if (quote.Length == 0) return;
        var width = HasInteractiveConsole ? Math.Max(2, Console.BufferWidth) : 120;
        if (quote.Length >= width) quote = quote[..Math.Max(1, width - 1)];
        var indent = HasInteractiveConsole ? Math.Min(16, Math.Max(2, width - quote.Length - 1)) : 4;
        WriteStartupLineUnsafe(new string(' ', indent) + quote, ConsoleColor.Blue);
    }

    public void Event(SessionEvent sessionEvent, string bufferName)
    {
        var semantics = SessionEventPresentation.From(sessionEvent);
        if (IsStartupEvent(sessionEvent))
        {
            About();
            return;
        }
        if (sessionEvent.Presentation is { } presentation)
        {
            WritePresentation(sessionEvent, bufferName, presentation);
            return;
        }

        if (sessionEvent.Kind == SessionEventKind.Highlight &&
            semantics.Nick is { } highlightedNick && semantics.Message is { } highlightedMessage)
        {
            WriteHighlightEvent(
                sessionEvent,
                bufferName,
                highlightedNick,
                highlightedMessage,
                sessionEvent.FormattedContent ?? IrcTextFormatting.Parse(highlightedMessage));
            return;
        }

        var text = FormatSemanticEvent(sessionEvent);
        var prefix = EventPrefix(sessionEvent, bufferName);
        if (sessionEvent.Kind == SessionEventKind.Message &&
            semantics.Nick is { } nick && semantics.Message is { } message)
        {
            var nickPrefix = _theme.ShowNickPrefix ? semantics.NickPrefix : null;
            WriteWrappedFormattedEvent(
                $"{prefix}<{nickPrefix}{nick}> ",
                sessionEvent.FormattedContent ?? IrcTextFormatting.Parse(message),
                _theme.EventColor(sessionEvent));
        }
        else if (sessionEvent.Kind == SessionEventKind.Action &&
                 semantics.Nick is { } actionNick && semantics.Message is { } actionMessage)
        {
            WriteWrappedFormattedEvent(
                $"{prefix}* {actionNick} ",
                sessionEvent.FormattedContent ?? IrcTextFormatting.Parse(actionMessage),
                _theme.EventColor(sessionEvent));
        }
        else if (sessionEvent.Kind == SessionEventKind.Notice &&
                 semantics.Subtype != SessionEventSubtype.Ctcp &&
                 semantics.Nick is { } noticeNick && semantics.Message is { } noticeMessage)
        {
            WriteWrappedFormattedEvent(
                $"{prefix}-{noticeNick}- ",
                sessionEvent.FormattedContent ?? IrcTextFormatting.Parse(noticeMessage),
                _theme.EventColor(sessionEvent));
        }
        else if (sessionEvent.FormattedContent is { } formattedContent &&
                 text.EndsWith(formattedContent.PlainText, StringComparison.Ordinal))
        {
            var leadingText = text[..(text.Length - formattedContent.PlainText.Length)];
            WriteWrappedFormattedEvent(
                prefix + leadingText,
                formattedContent,
                _theme.EventColor(sessionEvent));
        }
        else
        {
            WriteWrappedEvent(prefix, text, _theme.EventColor(sessionEvent));
        }
    }

    // Socket delivery groups short bursts before they reach the terminal. Holding the
    // console lock for one bounded group lets every line remain visible while the input,
    // topic, and status rows are removed and restored only once.
    public void RunEventBatch(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_consoleLock)
        {
            _eventBatchDepth++;
            try
            {
                action();
            }
            finally
            {
                _eventBatchDepth--;
                if (_eventBatchDepth == 0)
                {
                    if (_eventBatchOutputStarted)
                    {
                        FinishOutputUnsafe();
                    }
                    else if (_eventBatchChromeDirty && _chromeVisible && HasInteractiveConsole)
                    {
                        RenderInputUnsafe();
                        SetCursorVisibleUnsafe(_readingInput);
                    }
                    _eventBatchOutputStarted = false;
                    _eventBatchChromeDirty = false;
                }
            }
        }
    }

    public void EventRows(SessionEvent sessionEvent, string bufferName, int skipRows, int takeRows)
    {
        var totalRows = MeasureEventRows(sessionEvent, bufferName);
        if (skipRows <= 0 && takeRows >= totalRows)
        {
            Event(sessionEvent, bufferName);
            return;
        }

        if (sessionEvent.Presentation is { Fields.Count: > 0, Table: not null } mixedBlock)
        {
            WriteMixedPresentationRows(sessionEvent, bufferName, mixedBlock, skipRows, takeRows);
            return;
        }

        if (sessionEvent.Presentation?.Table is { } table)
        {
            var block = sessionEvent.Presentation;
            const int titleAndHeaderRows = 2;
            var selectedStart = Math.Max(0, skipRows - titleAndHeaderRows);
            var selectedEnd = Math.Clamp(skipRows + takeRows - titleAndHeaderRows, 0, table.Rows.Count);
            var includeSummary = !string.IsNullOrWhiteSpace(block.Summary) &&
                skipRows + takeRows > titleAndHeaderRows + table.Rows.Count;
            var fixedRows = titleAndHeaderRows + (includeSummary ? 1 : 0) +
                (_theme.InfoBottom.Length > 0 ? 1 : 0);
            var capacity = Math.Max(1, takeRows - fixedRows);
            var availableRows = Math.Max(0, selectedEnd - selectedStart);
            var selectedCount = Math.Min(capacity, availableRows);
            if (includeSummary)
            {
                selectedStart = Math.Max(selectedStart, selectedEnd - selectedCount);
            }

            var slicedTable = table with
            {
                Rows = table.Rows.Skip(selectedStart).Take(selectedCount).ToArray(),
                FormattedRows = table.FormattedRows.Skip(selectedStart).Take(selectedCount).ToArray()
            };
            WritePresentation(
                sessionEvent,
                bufferName,
                block with
                {
                    Fields = null,
                    Grid = null,
                    Table = slicedTable,
                    Summary = includeSummary ? block.Summary : null
                });
            return;
        }

        if (sessionEvent.Presentation?.Fields is { Count: > 0 } fields)
        {
            var block = sessionEvent.Presentation;
            var consoleWidth = EffectiveWidth(null);
            var prefix = EventPrefix(sessionEvent, bufferName);
            var labelWidth = block.FieldLabelWidth ?? fields.Max(field => field.Label.Length);
            var leading = new string(' ', prefix.Length + _theme.InfoSide.Length + labelWidth + 2);
            var rows = new List<PresentationField>();
            foreach (var field in fields)
            {
                var wrapped = TerminalWordWrapper.Wrap(leading, leading, field.Value, consoleWidth);
                var formattedOffset = 0;
                for (var index = 0; index < wrapped.Count; index++)
                {
                    IrcFormattedText? formattedRow = null;
                    if (field.FormattedValue is { } formatted)
                    {
                        formattedOffset = FindWrappedTextOffset(formatted.PlainText, wrapped[index].Text, formattedOffset);
                        formattedRow = SliceFormattedText(formatted, formattedOffset, wrapped[index].Text.Length);
                        formattedOffset += wrapped[index].Text.Length;
                    }
                    rows.Add(new PresentationField(
                        index == 0 ? field.Label : string.Empty,
                        wrapped[index].Text,
                        formattedRow));
                }
            }

            var titleRows = RowsForLength(prefix.Length + _theme.InfoTop.Length + block.Title.Length, consoleWidth);
            var selectedStart = Math.Max(0, skipRows - titleRows);
            var selectedEnd = Math.Clamp(skipRows + takeRows - titleRows, 0, rows.Count);
            var includeSummary = !string.IsNullOrWhiteSpace(block.Summary) &&
                skipRows + takeRows > titleRows + rows.Count;
            var fixedRows = titleRows + (includeSummary ? 1 : 0) + (_theme.InfoBottom.Length > 0 ? 1 : 0);
            var capacity = Math.Max(1, takeRows - fixedRows);
            var selectedCount = Math.Min(capacity, Math.Max(0, selectedEnd - selectedStart));
            if (includeSummary) selectedStart = Math.Max(selectedStart, selectedEnd - selectedCount);

            WritePresentation(sessionEvent, bufferName, block with
            {
                Fields = rows.Skip(selectedStart).Take(selectedCount).ToArray(),
                Grid = null,
                Table = null,
                Summary = includeSummary ? block.Summary : null,
                FieldLabelWidth = labelWidth
            });
            return;
        }

        if (sessionEvent.Presentation?.Grid is { Count: > 0 } grid)
        {
            var block = sessionEvent.Presentation;
            var consoleWidth = EffectiveWidth(null);
            var prefix = EventPrefix(sessionEvent, bufferName);
            var (columnWidth, columns) = CalculateGridLayout(
                grid,
                prefix.Length,
                consoleWidth,
                block.BracketGridCells);
            var titleRows = RowsForLength(
                prefix.Length + _theme.InfoTop.Length + block.Title.Length,
                consoleWidth);
            var gridRows = (grid.Count + columns - 1) / columns;
            var selectedStart = Math.Max(0, skipRows - titleRows);
            var selectedEnd = Math.Clamp(skipRows + takeRows - titleRows, 0, gridRows);
            var includeSummary = !string.IsNullOrWhiteSpace(block.Summary) &&
                skipRows + takeRows > titleRows + gridRows;
            var fixedRows = titleRows + (includeSummary ? 1 : 0) +
                (_theme.InfoBottom.Length > 0 ? 1 : 0);
            var capacity = Math.Max(1, takeRows - fixedRows);
            var selectedCount = Math.Min(capacity, Math.Max(0, selectedEnd - selectedStart));
            if (includeSummary)
            {
                selectedStart = Math.Max(selectedStart, selectedEnd - selectedCount);
            }
            var slicedGrid = grid
                .Skip(selectedStart * columns)
                .Take(selectedCount * columns)
                .ToArray();
            WritePresentation(
                sessionEvent,
                bufferName,
                block with
                {
                    Fields = null,
                    Table = null,
                    Grid = slicedGrid,
                    Summary = includeSummary ? block.Summary : null,
                    GridColumns = columns,
                    GridColumnWidth = columnWidth
                });
            return;
        }

        // Ordinary events are small. This fallback preserves their semantic
        // formatting if a resize happens to place one across a viewport edge.
        Event(sessionEvent, bufferName);
    }

    private void WriteMixedPresentationRows(
        SessionEvent sessionEvent,
        string bufferName,
        PresentationBlock block,
        int skipRows,
        int takeRows)
    {
        var consoleWidth = EffectiveWidth(null);
        var prefix = EventPrefix(sessionEvent, bufferName);
        var labelWidth = block.FieldLabelWidth ?? block.Fields!.Max(field => field.Label.Length);
        var fields = ExpandPresentationFields(block.Fields!, prefix, labelWidth, consoleWidth);
        var table = block.Table!;
        var titleRows = RowsForLength(
            prefix.Length + _theme.InfoTop.Length + block.Title.Length +
            (string.IsNullOrWhiteSpace(block.TitleHighlight) || TitleContainsHighlight(block)
                ? 0
                : 1 + block.TitleHighlight.Length),
            consoleWidth);
        var tableRows = 1 + table.Rows.Count;
        var contentRows = fields.Count + tableRows;
        var includeSummary = !string.IsNullOrWhiteSpace(block.Summary) &&
            skipRows + takeRows > titleRows + contentRows;
        var bottomRows = _theme.InfoBottom.Length > 0 ? 1 : 0;
        var capacity = Math.Max(1, takeRows - titleRows - bottomRows - (includeSummary ? 1 : 0));
        var contentSkip = Math.Max(0, skipRows - titleRows);

        var selectedFields = contentSkip < fields.Count
            ? fields.Skip(contentSkip).Take(capacity).ToArray()
            : [];
        var remaining = Math.Max(0, capacity - selectedFields.Length);
        PresentationTable? selectedTable = null;
        if (remaining > 0 && contentSkip < contentRows)
        {
            var tableOffset = Math.Max(0, contentSkip - fields.Count);
            var rowOffset = Math.Max(0, tableOffset - 1);
            var rowCapacity = Math.Max(0, remaining - 1);
            selectedTable = table with
            {
                Rows = table.Rows.Skip(rowOffset).Take(rowCapacity).ToArray(),
                FormattedRows = table.FormattedRows.Skip(rowOffset).Take(rowCapacity).ToArray()
            };
        }

        WritePresentation(sessionEvent, bufferName, block with
        {
            Fields = selectedFields.Length == 0 ? null : selectedFields,
            Grid = null,
            Table = selectedTable,
            Summary = includeSummary ? block.Summary : null,
            FieldLabelWidth = labelWidth
        });
    }

    private static IReadOnlyList<PresentationField> ExpandPresentationFields(
        IReadOnlyList<PresentationField> fields,
        string prefix,
        int labelWidth,
        int consoleWidth)
    {
        var leading = new string(' ', prefix.Length + labelWidth + 4);
        var rows = new List<PresentationField>();
        foreach (var field in fields)
        {
            var wrapped = TerminalWordWrapper.Wrap(leading, leading, field.Value, consoleWidth);
            var formattedOffset = 0;
            for (var index = 0; index < wrapped.Count; index++)
            {
                IrcFormattedText? formattedRow = null;
                if (field.FormattedValue is { } formatted)
                {
                    formattedOffset = FindWrappedTextOffset(formatted.PlainText, wrapped[index].Text, formattedOffset);
                    formattedRow = SliceFormattedText(formatted, formattedOffset, wrapped[index].Text.Length);
                    formattedOffset += wrapped[index].Text.Length;
                }
                rows.Add(new PresentationField(
                    index == 0 ? field.Label : string.Empty,
                    wrapped[index].Text,
                    formattedRow));
            }
        }
        return rows;
    }

    public void Result(string message, bool success = true) =>
        WriteLine(message, success ? _theme.Label : _theme.Error);

    public void LocalResult(string message) => WriteLine(message, _theme.Dim);

    public void Presentation(PresentationBlock presentation) => WritePresentation(
        new SessionEvent(NetworkSessionId.New(), BufferId.New(), SessionEventKind.Server, string.Empty, DateTimeOffset.Now,
            Presentation: presentation),
        "help",
        presentation);

    public string? ReadLine(
        string prompt,
        Func<string, IReadOnlyList<string>>? nicknameMatchProvider = null,
        Action<int>? scrollViewport = null,
        Action? resizeViewport = null,
        BufferId? historyKey = null)
    {
        if (!HasInteractiveConsole)
        {
            Console.Write(prompt);
            return Console.ReadLine();
        }

        lock (_consoleLock)
        {
            _prompt = prompt;
            _input.Clear();
            if (_pendingInput is { } pendingInput)
            {
                _input.Append(pendingInput);
                _pendingInput = null;
            }
            _inputCursor = _input.Length;
            _inputViewStart = 0;
            _maskInput = false;
            _activeInputHistory = HistoryForUnsafe(historyKey);
            _activeInputHistory.Begin();
            _nicknameCompletion.Reset();
            _nicknameMatchProvider = nicknameMatchProvider;
            if (ShouldReserveChromeRows(_chromeVisible))
            {
                ReserveChromeRowsUnsafe();
            }
            else
            {
                ClearInputUnsafe();
            }
            _renderTop = HasInteractiveConsole ? Console.CursorTop : 0;
            _renderRows = 2;
            _readingInput = true;
            _chromeVisible = true;
            RenderInputUnsafe();
            SetCursorVisibleUnsafe(true);
        }

        while (true)
        {
            if (!Console.KeyAvailable)
            {
                if (ResizeReady()) resizeViewport?.Invoke();
                Thread.Sleep(25);
                continue;
            }
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.PageUp)
            {
                scrollViewport?.Invoke(1);
                continue;
            }
            if (key.Key == ConsoleKey.PageDown)
            {
                scrollViewport?.Invoke(-1);
                continue;
            }
            lock (_consoleLock)
            {
                if (key.Key == ConsoleKey.Enter)
                {
                    var result = _input.ToString();
                    _activeInputHistory.Commit(result);
                    _readingInput = false;
                    _prompt = string.Empty;
                    _input.Clear();
                    _inputCursor = 0;
                    _inputViewStart = 0;
                    _nicknameMatchProvider = null;
                    RenderInputUnsafe();
                    SetCursorVisibleUnsafe(false);
                    return result;
                }

                if (key.Key == ConsoleKey.Z && key.Modifiers.HasFlag(ConsoleModifiers.Control) && _input.Length == 0)
                {
                    ClearInputUnsafe();
                    _readingInput = false;
                    _chromeVisible = false;
                    _nicknameMatchProvider = null;
                    return null;
                }

                UpdateInputRowUnsafe(() => ApplyKeyUnsafe(key));
            }
        }
    }

    public void ForgetInputHistory(BufferId bufferId)
    {
        lock (_consoleLock)
        {
            if (_inputHistories.Remove(bufferId, out var removed) &&
                ReferenceEquals(_activeInputHistory, removed))
            {
                _activeInputHistory = _defaultInputHistory;
            }
        }
    }

    internal InputHistory HistoryFor(BufferId? bufferId)
    {
        lock (_consoleLock)
        {
            return HistoryForUnsafe(bufferId);
        }
    }

    private InputHistory HistoryForUnsafe(BufferId? bufferId)
    {
        if (bufferId is null) return _defaultInputHistory;
        if (!_inputHistories.TryGetValue(bufferId.Value, out var history))
        {
            history = new InputHistory();
            _inputHistories.Add(bufferId.Value, history);
        }
        return history;
    }

    public string? ReadSecret(string prompt)
    {
        if (!HasInteractiveConsole)
        {
            return Console.ReadLine();
        }
        var secret = new StringBuilder();
        lock (_consoleLock)
        {
            var chromeWasVisible = _chromeVisible;
            _prompt = prompt;
            _input.Clear();
            _inputCursor = 0;
            _inputViewStart = 0;
            _maskInput = true;
            _readingInput = true;
            _chromeVisible = true;
            if (ShouldReserveChromeRows(chromeWasVisible))
            {
                ReserveChromeRowsUnsafe();
            }
            else
            {
                ClearInputUnsafe();
            }
            RenderInputUnsafe();
        }
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            lock (_consoleLock)
            {
                if (key.Key == ConsoleKey.Enter)
                {
                    var result = secret.ToString();
                    ClearInputUnsafe();
                    _readingInput = false;
                    _chromeVisible = false;
                    _maskInput = false;
                    _prompt = string.Empty;
                    _input.Clear();
                    _inputCursor = 0;
                    _inputViewStart = 0;
                    return result;
                }
                if (key.Key == ConsoleKey.Escape)
                {
                    ClearInputUnsafe();
                    _readingInput = false;
                    _chromeVisible = false;
                    _maskInput = false;
                    _prompt = string.Empty;
                    _input.Clear();
                    _inputCursor = 0;
                    _inputViewStart = 0;
                    return null;
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (secret.Length > 0)
                    {
                        UpdateInputRowUnsafe(() =>
                        {
                            secret.Length--;
                            _input.Length--;
                            _inputCursor--;
                        });
                    }
                    continue;
                }
                if (!char.IsControl(key.KeyChar))
                {
                    UpdateInputRowUnsafe(() =>
                    {
                        secret.Append(key.KeyChar);
                        _input.Append('*');
                        _inputCursor++;
                    });
                }
            }
        }
    }

    public void PrefillInput(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_consoleLock)
        {
            if (!_readingInput)
            {
                _pendingInput = value;
                return;
            }

            UpdateInputRowUnsafe(() => ReplaceInputUnsafe(value));
        }
    }

    private bool ResizeReady()
    {
        var width = Console.BufferWidth;
        var height = Console.WindowHeight;
        if (width != _observedWidth || height != _observedHeight)
        {
            _observedWidth = width;
            _observedHeight = height;
            _resizeObservedAt = DateTimeOffset.UtcNow;
            return false;
        }
        if (width == _renderedWidth && height == _renderedHeight ||
            DateTimeOffset.UtcNow - _resizeObservedAt < TimeSpan.FromMilliseconds(125)) return false;
        _renderedWidth = width;
        _renderedHeight = height;
        return true;
    }

    public void Clear(int outputRows = 0)
    {
        lock (_consoleLock)
        {
            if (_chromeVisible)
            {
                ClearInputUnsafe();
            }
            if (HasInteractiveConsole) Console.Clear();
            _chromeVisible = false;
            if (HasInteractiveConsole && outputRows > 0)
            {
                var contentTop = Console.WindowTop + (_topicBar is null ? 0 : 1);
                var top = Math.Max(contentTop, Console.WindowTop + Console.WindowHeight - 2 - outputRows);
                Console.SetCursorPosition(0, Math.Min(top, Console.BufferHeight - 1));
            }
            _renderTop = HasInteractiveConsole ? Console.CursorTop : 0;
        }
    }

    public void Redraw(WindowChromeModel chrome, int outputRows, Action draw)
    {
        ArgumentNullException.ThrowIfNull(chrome);
        ArgumentNullException.ThrowIfNull(draw);
        lock (_consoleLock)
        {
            if (_chromeVisible) ClearInputUnsafe();
            ApplyChromeUnsafe(chrome);
            if (HasInteractiveConsole) Console.Clear();
            if (HasInteractiveConsole && outputRows > 0)
            {
                var contentTop = Console.WindowTop + (_topicBar is null ? 0 : 1);
                var top = Math.Max(contentTop, Console.WindowTop + Console.WindowHeight - 2 - outputRows);
                Console.SetCursorPosition(0, Math.Min(top, Console.BufferHeight - 1));
            }
            _renderTop = HasInteractiveConsole ? Console.CursorTop : 0;
            _redrawing = true;
            try
            {
                draw();
            }
            finally
            {
                _redrawing = false;
                if (HasInteractiveConsole) Console.ResetColor();
                if (_eventBatchDepth > 0)
                {
                    _eventBatchOutputStarted = true;
                    _eventBatchChromeDirty = true;
                }
                else if (_chromeVisible && HasInteractiveConsole)
                {
                    ReserveChromeRowsUnsafe();
                    _renderTop = Console.CursorTop;
                    RenderInputUnsafe();
                    SetCursorVisibleUnsafe(_readingInput);
                }
            }
        }
    }

    internal int MeasureTextRows(string text, int? width = null) =>
        RowsForLength(TerminalTextSanitizer.Sanitize(text).Length, EffectiveWidth(width));

    internal int MeasureEventRows(SessionEvent sessionEvent, string bufferName, int? width = null)
    {
        var semantics = SessionEventPresentation.From(sessionEvent);
        var consoleWidth = EffectiveWidth(width);
        if (IsStartupEvent(sessionEvent)) return StartupRows(consoleWidth);
        if (sessionEvent.Presentation is not { } block)
        {
            var text = FormatSemanticEvent(sessionEvent);
            var eventPrefix = EventPrefix(sessionEvent, bufferName);
            if (sessionEvent.Kind is SessionEventKind.Message or SessionEventKind.Highlight &&
                semantics.Nick is { } nick && semantics.Message is { } message)
            {
                var nickPrefix = _theme.ShowNickPrefix ? semantics.NickPrefix : null;
                return TerminalWordWrapper.Wrap($"{eventPrefix}<{nickPrefix}{nick}> ", message, consoleWidth).Count;
            }
            return TerminalWordWrapper.Wrap(eventPrefix, text, consoleWidth).Count;
        }

        var prefix = EventPrefix(sessionEvent, bufferName);
        var titleHighlightIsInline = TitleContainsHighlight(block);
        var rows = RowsForLength(
            prefix.Length + _theme.InfoTop.Length + block.Title.Length +
            (string.IsNullOrWhiteSpace(block.TitleHighlight) || titleHighlightIsInline ? 0 : 1 + block.TitleHighlight.Length),
            consoleWidth);
        if (block.Fields is { Count: > 0 })
        {
            var labelWidth = block.FieldLabelWidth ?? block.Fields.Max(field => field.Label.Length);
            var fieldLeading = new string(' ', prefix.Length + _theme.InfoSide.Length + labelWidth + 2);
            rows += block.Fields.Sum(field => TerminalWordWrapper.Wrap(
                fieldLeading,
                fieldLeading,
                field.Value,
                consoleWidth).Count);
        }
        if (block.Grid is { Count: > 0 } grid)
        {
            var columns = block.GridColumns ??
                CalculateGridLayout(grid, prefix.Length, consoleWidth, block.BracketGridCells).Columns;
            rows += (grid.Count + columns - 1) / columns;
        }
        if (block.Table is { } table)
        {
            var layout = CalculateTableLayout(table, Math.Max(20, consoleWidth - prefix.Length - _theme.InfoSide.Length - 1));
            rows += 1 + table.Rows.Sum(row => TableRowHeight(row, layout));
        }
        if (!string.IsNullOrWhiteSpace(block.Summary))
        {
            rows += RowsForLength(prefix.Length + _theme.InfoSide.Length + block.Summary.Length, consoleWidth);
        }
        if (_theme.InfoBottom.Length > 0)
        {
            rows += RowsForLength(prefix.Length + _theme.InfoBottom.Length, consoleWidth);
        }
        return rows;
    }

    private static int RowsForLength(int length, int width) => Math.Max(1, length / Math.Max(2, width) + 1);

    private static int EffectiveWidth(int? width) => width ?? (HasInteractiveConsole ? Math.Max(2, Console.BufferWidth) : 120);

    private (int ColumnWidth, int Columns) CalculateGridLayout(
        IReadOnlyList<string> grid,
        int prefixLength,
        int consoleWidth,
        bool bracketCells)
    {
        var available = Math.Max(8, consoleWidth - prefixLength - _theme.InfoSide.Length - 1);
        var bracketWidth = bracketCells ? _theme.GridOpen.Length + _theme.GridClose.Length : 0;
        var longest = Math.Min(Math.Max(1, available - bracketWidth), grid.Max(item => item.Length));
        var columnWidth = Math.Min(available, longest + bracketWidth + 2);
        var columns = Math.Max(1, available / Math.Max(1, columnWidth));
        columnWidth = Math.Max(1, available / columns);
        return (columnWidth, columns);
    }

    internal static TableLayout CalculateTableLayout(PresentationTable table, int available)
    {
        var preserved = table.PreserveColumns ?? new HashSet<int>();
        var visible = Enumerable.Range(0, table.Columns.Count).ToList();
        var widths = Enumerable.Range(0, table.Columns.Count)
            .Select(index => Math.Max(table.Columns[index].Length,
                table.Rows.Count == 0 ? 0 : table.Rows.Max(row => index < row.Count ? row[index].Length : 0)))
            .Select((width, index) =>
            {
                var maximum = table.MaximumWidths is not null && index < table.MaximumWidths.Count
                    ? Math.Max(1, table.MaximumWidths[index])
                    : preserved.Contains(index) ? int.MaxValue : 32;
                return Math.Min(maximum, width);
            })
            .ToArray();

        if (table.KeepAllColumns)
        {
            while (visible.Sum(index => widths[index] + 2) > available &&
                   visible.Any(index => widths[index] > 3))
            {
                var widest = visible
                    .Where(index => widths[index] > 3)
                    .OrderByDescending(index => widths[index])
                    .First();
                widths[widest]--;
            }
        }
        else while (visible.Count > 2 && visible.Sum(index => widths[index] + 2) > available)
        {
            var removableColumns = visible.Where(index => !preserved.Contains(index)).ToArray();
            if (removableColumns.Length == 0) break;
            var removable = removableColumns[^1];
            visible.Remove(removable);
        }
        while (visible.Sum(index => widths[index] + 2) > available &&
               visible.Any(index => !preserved.Contains(index) && widths[index] > Math.Max(4, table.Columns[index].Length)))
        {
            var widest = visible
                .Where(index => !preserved.Contains(index) && widths[index] > Math.Max(4, table.Columns[index].Length))
                .OrderByDescending(index => widths[index])
                .First();
            widths[widest]--;
        }
        foreach (var index in visible.Where(preserved.Contains))
        {
            var room = available - visible.Where(other => other != index).Sum(other => widths[other] + 2) - 2;
            widths[index] = Math.Max(table.Columns[index].Length, Math.Min(widths[index], Math.Max(4, room)));
        }
        return new TableLayout(visible, widths, preserved);
    }

    private static int TableRowHeight(IReadOnlyList<string> row, TableLayout layout) => 1;

    internal sealed record TableLayout(
        IReadOnlyList<int> VisibleColumns,
        int[] Widths,
        IReadOnlySet<int> PreservedColumns);

    private string FormatSemanticEvent(SessionEvent sessionEvent)
    {
        var semantics = SessionEventPresentation.From(sessionEvent);

        if (sessionEvent.Kind == SessionEventKind.Join && semantics.Nick is { } joinedNick)
        {
            var identity = FormatIdentity(semantics, _joinHostmasks);
            return $"{_theme.JoinMarker} {joinedNick}{identity} joined {semantics.Channel}";
        }

        if (sessionEvent.Kind == SessionEventKind.Part && semantics.Nick is { } partedNick)
        {
            var quit = semantics.Subtype == SessionEventSubtype.Quit;
            var identity = FormatIdentity(semantics, quit ? _quitHostmasks : _partHostmasks);
            var reason = string.IsNullOrWhiteSpace(semantics.Reason)
                ? string.Empty
                : $" ({semantics.Reason})";
            return quit
                ? $"{_theme.PartMarker} {partedNick}{identity} quit{reason}"
                : semantics.Subtype == SessionEventSubtype.Kick
                    ? sessionEvent.Text
                    : $"{_theme.PartMarker} {partedNick}{identity} left {semantics.Channel}{reason}";
        }

        if (sessionEvent.Kind is SessionEventKind.Message or SessionEventKind.Highlight &&
            semantics.Nick is { } messageNick && semantics.Message is { } messageText)
        {
            var nickPrefix = _theme.ShowNickPrefix ? semantics.NickPrefix : null;
            return $"<{nickPrefix}{messageNick}> {messageText}";
        }

        return sessionEvent.Text;
    }

    private string EventPrefix(SessionEvent sessionEvent, string bufferName) => _theme.ShowBufferName
        ? $"[{sessionEvent.Timestamp:HH:mm:ss}] [{bufferName}] "
        : $"[{sessionEvent.Timestamp:HH:mm:ss}] ";

    private static string FormatIdentity(SessionEventPresentation semantics, HostmaskVisibility visibility)
    {
        var user = semantics.Username;
        var host = semantics.Host;
        var value = visibility switch
        {
            HostmaskVisibility.Full when user is not null && host is not null => $"{user}@{host}",
            HostmaskVisibility.UserHost when user is not null && host is not null => $"{user}@{host}",
            HostmaskVisibility.Host when host is not null => host,
            _ => null
        };
        return value is null ? string.Empty : $" [{value}]";
    }

    private void WritePresentation(SessionEvent sessionEvent, string bufferName, PresentationBlock block)
    {
        lock (_consoleLock)
        {
            BeginOutputUnsafe();
            var prefix = EventPrefix(sessionEvent, bufferName);
            WriteColoredUnsafe(prefix, _theme.Dim);
            WriteColoredUnsafe(_theme.InfoTop, _theme.Label);
            if (TitleContainsHighlight(block))
            {
                var highlightStart = block.Title.IndexOf(block.TitleHighlight!, StringComparison.Ordinal);
                WriteColoredUnsafe(block.Title[..highlightStart], _theme.Accent);
                WriteColoredUnsafe(block.TitleHighlight!, ConsoleColor.White);
                WriteColoredUnsafe(block.Title[(highlightStart + block.TitleHighlight!.Length)..], _theme.Accent);
            }
            else
            {
                WriteColoredUnsafe(block.Title, _theme.Accent);
                if (!string.IsNullOrWhiteSpace(block.TitleHighlight))
                {
                    Console.Write(' ');
                    WriteColoredUnsafe(block.TitleHighlight, ConsoleColor.White);
                }
            }
            Console.WriteLine();

            if (block.Fields is { Count: > 0 })
            {
                var labelWidth = block.FieldLabelWidth ?? block.Fields.Max(field => field.Label.Length);
                var consoleWidth = EffectiveWidth(null);
                foreach (var field in block.Fields)
                {
                    var leading = new string(' ', prefix.Length + _theme.InfoSide.Length + labelWidth + 2);
                    var lines = TerminalWordWrapper.Wrap(leading, leading, field.Value, consoleWidth);
                    var formattedOffset = 0;
                    for (var index = 0; index < lines.Count; index++)
                    {
                        WriteColoredUnsafe(prefix, _theme.Dim);
                        WriteColoredUnsafe(_theme.InfoSide, _theme.Label);
                        WriteColoredUnsafe(
                            index == 0 ? field.Label.PadRight(labelWidth) : new string(' ', labelWidth),
                            _theme.Label);
                        Console.Write("  ");
                        if (field.FormattedValue is { } formattedValue)
                        {
                            formattedOffset = FindWrappedTextOffset(
                                formattedValue.PlainText,
                                lines[index].Text,
                                formattedOffset);
                            WriteIrcFormattedRangeUnsafe(
                                formattedValue,
                                formattedOffset,
                                lines[index].Text.Length,
                                _theme.Normal);
                            formattedOffset += lines[index].Text.Length;
                        }
                        else
                        {
                            WriteColoredUnsafe(lines[index].Text, _theme.Normal);
                        }
                        Console.WriteLine();
                    }
                }
            }

            if (block.Grid is { Count: > 0 } grid)
            {
                var consoleWidth = EffectiveWidth(null);
                var bracketWidth = block.BracketGridCells ? _theme.GridOpen.Length + _theme.GridClose.Length : 0;
                var calculated = CalculateGridLayout(
                    grid, prefix.Length, consoleWidth, block.BracketGridCells);
                var columnWidth = block.GridColumnWidth ?? calculated.ColumnWidth;
                var columns = block.GridColumns ?? calculated.Columns;
                for (var offset = 0; offset < grid.Count; offset += columns)
                {
                    WriteColoredUnsafe(prefix, _theme.Dim);
                    WriteColoredUnsafe(_theme.InfoSide, _theme.Label);
                    for (var column = 0; column < columns && offset + column < grid.Count; column++)
                    {
                        var value = grid[offset + column];
                        if (value.Length > columnWidth)
                        {
                            value = columnWidth > 1 ? value[..(columnWidth - 1)] + "…" : value[..1];
                        }
                        var isLast = column == columns - 1 || offset + column == grid.Count - 1;
                        if (block.BracketGridCells)
                        {
                            var innerWidth = Math.Max(1, columnWidth - bracketWidth);
                            if (value.Length > innerWidth)
                            {
                                value = innerWidth > 1 ? value[..(innerWidth - 1)] + "…" : value[..1];
                            }
                            WriteColoredUnsafe(_theme.GridOpen, _theme.Label);
                            WriteColoredUnsafe(value.PadRight(innerWidth), _theme.Normal);
                            WriteColoredUnsafe(_theme.GridClose, _theme.Label);
                        }
                        else
                        {
                            WriteColoredUnsafe(isLast ? value : value.PadRight(columnWidth), _theme.Normal);
                        }
                    }
                    Console.WriteLine();
                }
            }

            if (block.Table is { } table)
            {
                var available = Math.Max(
                    20,
                    EffectiveWidth(null) - prefix.Length - _theme.InfoSide.Length - 1);
                var layout = CalculateTableLayout(table, available);
                WriteColoredUnsafe(prefix, _theme.Dim);
                WriteColoredUnsafe(_theme.InfoSide, _theme.Label);
                foreach (var index in layout.VisibleColumns)
                {
                    var heading = TruncateCell(table.Columns[index], layout.Widths[index]);
                    WriteColoredUnsafe(heading.PadRight(layout.Widths[index] + 2), _theme.Label);
                }
                Console.WriteLine();
                for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
                {
                    var row = table.Rows[rowIndex];
                    WriteColoredUnsafe(prefix, _theme.Dim);
                    WriteColoredUnsafe(_theme.InfoSide, _theme.Label);
                    foreach (var index in layout.VisibleColumns)
                    {
                        var value = index < row.Count ? row[index] : string.Empty;
                        value = TruncateCell(value, layout.Widths[index]);
                        var formatted = table.FormattedRows is not null &&
                            rowIndex < table.FormattedRows.Count &&
                            index < table.FormattedRows[rowIndex].Count
                                ? table.FormattedRows[rowIndex][index]
                                : null;
                        if (formatted is not null)
                        {
                            var sourceLength = Math.Min(formatted.PlainText.Length, value.Length);
                            if (formatted.PlainText.Length > layout.Widths[index] && sourceLength > 0) sourceLength--;
                            WriteIrcFormattedRangeUnsafe(formatted, 0, sourceLength, _theme.Normal);
                            if (sourceLength < value.Length)
                            {
                                WriteColoredUnsafe(value[sourceLength..], _theme.Normal);
                            }
                            WriteColoredUnsafe(
                                new string(' ', layout.Widths[index] + 2 - value.Length),
                                _theme.Normal);
                        }
                        else
                        {
                            WriteColoredUnsafe(value.PadRight(layout.Widths[index] + 2), _theme.Normal);
                        }
                    }
                    Console.WriteLine();
                }
            }

            if (!string.IsNullOrWhiteSpace(block.Summary))
            {
                WriteColoredUnsafe(prefix, _theme.Dim);
                WriteColoredUnsafe(_theme.InfoSide, _theme.Label);
                WriteColoredUnsafe(block.Summary, _theme.Dim);
                Console.WriteLine();
            }
            if (_theme.InfoBottom.Length > 0)
            {
                WriteColoredUnsafe(prefix, _theme.Dim);
                WriteColoredUnsafe(_theme.InfoBottom, _theme.Label);
                Console.WriteLine();
            }
            EndOutputUnsafe();
        }
    }

    private static bool TitleContainsHighlight(PresentationBlock block) =>
        !string.IsNullOrWhiteSpace(block.TitleHighlight) &&
        block.Title.Contains(block.TitleHighlight, StringComparison.Ordinal);

    private static string TruncateCell(string value, int width)
    {
        if (value.Length <= width) return value;
        return width > 1 ? value[..(width - 1)] + "…" : value[..1];
    }

    private static IrcFormattedText SliceFormattedText(IrcFormattedText formatted, int start, int length)
    {
        if (length <= 0) return new IrcFormattedText(string.Empty, []);
        var end = start + length;
        var runStart = 0;
        var runs = new List<IrcTextRun>();
        foreach (var run in formatted.Runs)
        {
            var runEnd = runStart + run.Text.Length;
            var sliceStart = Math.Max(start, runStart);
            var sliceEnd = Math.Min(end, runEnd);
            if (sliceEnd > sliceStart)
            {
                runs.Add(new IrcTextRun(
                    run.Text.Substring(sliceStart - runStart, sliceEnd - sliceStart),
                    run.Style));
            }
            if (runEnd >= end) break;
            runStart = runEnd;
        }
        return new IrcFormattedText(formatted.PlainText.Substring(start, length), runs);
    }


    private void ApplyKeyUnsafe(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Tab && _nicknameMatchProvider is not null)
        {
            var completedCursor = _nicknameCompletion.Complete(_input, _inputCursor, _nicknameMatchProvider);
            if (completedCursor is not null) _inputCursor = completedCursor.Value;
            return;
        }

        _nicknameCompletion.Reset();
        if (InputFormattingControls.TryTranslate(key, out var formattingControl))
        {
            _input.Insert(_inputCursor, formattingControl);
            _inputCursor++;
            return;
        }
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            switch (key.Key)
            {
                case ConsoleKey.A: _inputCursor = 0; return;
                case ConsoleKey.E: _inputCursor = _input.Length; return;
                case ConsoleKey.W: DeletePreviousWordUnsafe(); return;
            }
        }

        switch (key.Key)
        {
            case ConsoleKey.UpArrow: ReplaceInputUnsafe(_activeInputHistory.Previous(_input.ToString())); return;
            case ConsoleKey.DownArrow: ReplaceInputUnsafe(_activeInputHistory.Next()); return;
            case ConsoleKey.LeftArrow: _inputCursor = Math.Max(0, _inputCursor - 1); return;
            case ConsoleKey.RightArrow: _inputCursor = Math.Min(_input.Length, _inputCursor + 1); return;
            case ConsoleKey.Home: _inputCursor = 0; return;
            case ConsoleKey.End: _inputCursor = _input.Length; return;
            case ConsoleKey.Backspace when _inputCursor > 0: _input.Remove(_inputCursor - 1, 1); _inputCursor--; return;
            case ConsoleKey.Delete when _inputCursor < _input.Length: _input.Remove(_inputCursor, 1); return;
            case ConsoleKey.Escape: _input.Clear(); _inputCursor = 0; return;
        }
        if (!char.IsControl(key.KeyChar))
        {
            _input.Insert(_inputCursor, key.KeyChar);
            _inputCursor++;
        }
    }

    private void ReplaceInputUnsafe(string? value)
    {
        if (value is null) return;
        _input.Clear();
        _input.Append(value);
        _inputCursor = _input.Length;
        _inputViewStart = 0;
    }

    private void DeletePreviousWordUnsafe()
    {
        while (_inputCursor > 0 && char.IsWhiteSpace(_input[_inputCursor - 1])) _input.Remove(--_inputCursor, 1);
        while (_inputCursor > 0 && !char.IsWhiteSpace(_input[_inputCursor - 1])) _input.Remove(--_inputCursor, 1);
    }

    private void ClearInputUnsafe()
    {
        var width = Math.Max(2, Console.BufferWidth);
        var top = Math.Clamp(_renderTop, 0, Console.BufferHeight - 1);
        Console.ResetColor();
        for (var row = 0; row < _renderRows && top + row < Console.BufferHeight; row++)
        {
            Console.SetCursorPosition(0, top + row);
            EraseCurrentRowUnsafe(width);
        }
        Console.SetCursorPosition(0, top);
    }

    private void ClearInputRowUnsafe()
    {
        var width = Math.Max(2, Console.BufferWidth);
        var inputTop = Math.Min(Console.BufferHeight - 1, Math.Clamp(_renderTop, 0, Console.BufferHeight - 1) + 1);
        Console.ResetColor();
        Console.SetCursorPosition(0, inputTop);
        EraseCurrentRowUnsafe(width);
        Console.SetCursorPosition(0, inputTop);
    }

    private void UpdateInputRowUnsafe(Action update)
    {
        SetCursorVisibleUnsafe(false);
        try
        {
            ClearInputRowUnsafe();
            update();
            RenderInputRowUnsafe();
        }
        finally
        {
            SetCursorVisibleUnsafe(true);
        }
    }

    private static void SetCursorVisibleUnsafe(bool visible)
    {
        try
        {
            Console.CursorVisible = visible;
        }
        catch (Exception exception) when (exception is IOException or PlatformNotSupportedException)
        {
            // Some redirected and pseudo-console hosts do not expose cursor visibility.
        }
    }

    private void RenderInputUnsafe()
    {
        var width = Math.Max(2, Console.BufferWidth);
        var statusTop = ChromeStatusRow(Console.WindowTop, Console.WindowHeight, Console.BufferHeight);
        Console.SetCursorPosition(0, statusTop);
        _renderTop = statusTop;
        _renderRows = 2;
        WriteStatusBarUnsafe(width);
        RenderInputRowUnsafe();
        WriteTopicBarUnsafe(width);
    }

    private void RenderInputRowUnsafe()
    {
        var width = Math.Max(2, Console.BufferWidth);
        var statusTop = Math.Clamp(_renderTop, 0, Console.BufferHeight - 1);
        var inputTop = Math.Min(Console.BufferHeight - 1, statusTop + 1);
        Console.SetCursorPosition(0, inputTop);
        EraseCurrentRowUnsafe(width);
        var renderedInput = _maskInput
            ? new string('*', _input.Length)
            : InputFormattingControls.ToDisplayText(_input.ToString());
        var layout = InputLineLayouter.Calculate(
            _prompt, renderedInput, _inputCursor, width, _inputViewStart);
        _inputViewStart = layout.ViewStart;
        WriteColoredUnsafe(layout.Prompt, _theme.Dim);
        Console.Write(layout.Text);
        Console.SetCursorPosition(layout.CursorColumn, inputTop);
    }

    internal static int ChromeStatusRow(int windowTop, int windowHeight, int bufferHeight) =>
        Math.Max(windowTop, Math.Min(Math.Max(0, bufferHeight - 2), windowTop + windowHeight - 2));

    internal static int ChromeReservationScrolls(int cursorTop, int statusTop) =>
        Math.Max(0, cursorTop - statusTop);

    internal static bool ShouldReserveChromeRows(bool chromeVisible) => !chromeVisible;

    private void ReserveChromeRowsUnsafe()
    {
        var statusTop = ChromeStatusRow(Console.WindowTop, Console.WindowHeight, Console.BufferHeight);
        var scrolls = ChromeReservationScrolls(Console.CursorTop, statusTop);
        for (var index = 0; index < scrolls; index++)
        {
            Console.SetCursorPosition(0, Math.Min(Console.CursorTop, Console.BufferHeight - 1));
            Console.WriteLine();
        }
    }

    private void WriteTopicBarUnsafe(int width)
    {
        _topicBar = BufferHeaderComposer.Compose(
            _bufferHeader,
            Math.Max(1, width - 1),
            _theme.HeaderSeparator);
        if (!HasInteractiveConsole || (_topicBar is null && !_topicBarWasVisible)) return;
        var left = Console.CursorLeft;
        var top = Console.CursorTop;
        var barTop = Math.Clamp(Console.WindowTop, 0, Console.BufferHeight - 1);
        Console.SetCursorPosition(0, barTop);
        EraseCurrentRowUnsafe(width);
        if (_topicBar is null)
        {
            _topicBarWasVisible = false;
        }
        else
        {
            SetConsoleColorsUnsafe(_theme.TopicForeground, _theme.TopicBackground);
            var text = _topicBar.Length < width ? _topicBar : width > 4 ? _topicBar[..(width - 4)] + "..." : string.Empty;
            var formatted = _bufferHeader.FormattedPrimary;
            var styledLength = 0;
            if (formatted is not null)
            {
                var comparable = Math.Min(text.Length, formatted.PlainText.Length);
                while (styledLength < comparable && text[styledLength] == formatted.PlainText[styledLength])
                {
                    styledLength++;
                }
                WriteIrcFormattedRangeUnsafe(
                    formatted,
                    0,
                    styledLength,
                    _theme.TopicForeground,
                    _theme.TopicBackground);
                SetConsoleColorsUnsafe(_theme.TopicForeground, _theme.TopicBackground);
            }
            Console.Write(text[styledLength..].PadRight(Math.Max(0, width - 1 - styledLength)));
            ResetConsoleColorsUnsafe();
            _topicBarWasVisible = true;
        }
        var restoreTop = _topicBar is not null && top == barTop && barTop + 1 < Console.BufferHeight ? barTop + 1 : top;
        Console.SetCursorPosition(Math.Min(left, width - 1), Math.Min(restoreTop, Console.BufferHeight - 1));
    }

    private void WriteStatusBarUnsafe(int width)
    {
        EraseCurrentRowUnsafe(width);
        SetConsoleColorsUnsafe(_theme.StatusForeground, _theme.StatusBackground);
        var activityText = _statusBar.Activity.Count == 0
            ? string.Empty
            : $" Act: {string.Join(',', _statusBar.Activity.Select(item => item.Number))}";
        var fullContext = string.Join(_theme.StatusSeparator, _statusBar.Fields);
        var maximumContext = Math.Max(0, width - 1 - activityText.Length);
        var context = fullContext.Length <= maximumContext
            ? fullContext
            : maximumContext > 1 ? fullContext[..(maximumContext - 1)] + "…" : string.Empty;
        Console.Write(context.PadRight(maximumContext));
        if (_statusBar.Activity.Count > 0)
        {
            Console.Write(" Act: ");
            for (var index = 0; index < _statusBar.Activity.Count; index++)
            {
                if (index > 0)
                {
                    SetConsoleColorsUnsafe(_theme.StatusForeground, _theme.StatusBackground);
                    Console.Write(',');
                }
                var activityColor = ReadableActivityColor(_theme, _statusBar.Activity[index].Kind);
                SetConsoleColorsUnsafe(activityColor, _theme.StatusBackground);
                Console.Write(_statusBar.Activity[index].Number);
            }
            SetConsoleColorsUnsafe(_theme.StatusForeground, _theme.StatusBackground);
        }
        var written = maximumContext + activityText.Length;
        if (written < width - 1) Console.Write(new string(' ', width - 1 - written));
        ResetConsoleColorsUnsafe();
    }

    internal static ConsoleColor ReadableActivityColor(TerminalTheme theme, SessionEventKind kind)
    {
        var color = theme.EventColor(kind);
        if (color != theme.StatusBackground) return color;
        return theme.StatusForeground == theme.StatusBackground
            ? ConsoleColor.White
            : theme.StatusForeground;
    }

    private void SetConsoleColorsUnsafe(ConsoleColor foreground, ConsoleColor background)
    {
        if (_fullScreen)
        {
            Console.Write($"\u001b[{AnsiForeground(foreground)};{AnsiBackground(background)}m");
            return;
        }
        Console.ForegroundColor = foreground;
        Console.BackgroundColor = background;
    }

    private void ResetConsoleColorsUnsafe()
    {
        if (_fullScreen) Console.Write("\u001b[0m");
        else Console.ResetColor();
    }

    internal static int AnsiForeground(ConsoleColor color) => color switch
    {
        ConsoleColor.Black => 30,
        ConsoleColor.DarkRed => 31,
        ConsoleColor.DarkGreen => 32,
        ConsoleColor.DarkYellow => 33,
        ConsoleColor.DarkBlue => 34,
        ConsoleColor.DarkMagenta => 35,
        ConsoleColor.DarkCyan => 36,
        ConsoleColor.Gray => 37,
        ConsoleColor.DarkGray => 90,
        ConsoleColor.Red => 91,
        ConsoleColor.Green => 92,
        ConsoleColor.Yellow => 93,
        ConsoleColor.Blue => 94,
        ConsoleColor.Magenta => 95,
        ConsoleColor.Cyan => 96,
        ConsoleColor.White => 97,
        _ => 37
    };

    internal static int AnsiBackground(ConsoleColor color) => AnsiForeground(color) + 10;

    private void WriteLine(string text, ConsoleColor color)
    {
        lock (_consoleLock)
        {
            BeginOutputUnsafe();
            WriteColoredUnsafe(text, color);
            Console.WriteLine();
            EndOutputUnsafe();
        }
    }

    private void WriteHighlightEvent(
        SessionEvent sessionEvent,
        string bufferName,
        string nick,
        string message,
        IrcFormattedText formatted)
    {
        var semantics = SessionEventPresentation.From(sessionEvent);
        lock (_consoleLock)
        {
            BeginOutputUnsafe();
            var prefix = EventPrefix(sessionEvent, bufferName);
            var sourceText = string.Empty;
            if (semantics.IsHighlightEcho && semantics.Source is { } source)
            {
                sourceText = $"[{source}] ";
            }
            var nickPrefix = _theme.ShowNickPrefix ? semantics.NickPrefix : null;
            var lead = $"{prefix}{sourceText}<{nickPrefix}{nick}> ";
            var lines = TerminalWordWrapper.Wrap(lead, message, EffectiveWidth(null));
            var plainOffset = 0;
            for (var index = 0; index < lines.Count; index++)
            {
                if (index == 0)
                {
                    var timestampColor = semantics.IsHighlightEcho
                        ? _theme.Dim
                        : _theme.Message;
                    WriteColoredUnsafe(prefix, timestampColor);
                    if (sourceText.Length > 0) WriteColoredUnsafe(sourceText, _theme.Accent);
                    WriteColoredUnsafe("<", _theme.Normal);
                    WriteColoredUnsafe($"{nickPrefix}{nick}", _theme.Highlight);
                    WriteColoredUnsafe("> ", _theme.Normal);
                }
                else
                {
                    WriteColoredUnsafe(lines[index].Leading, _theme.Normal);
                }
                plainOffset = FindWrappedTextOffset(formatted.PlainText, lines[index].Text, plainOffset);
                WriteIrcFormattedRangeUnsafe(formatted, plainOffset, lines[index].Text.Length, _theme.Normal);
                plainOffset += lines[index].Text.Length;
                Console.WriteLine();
            }
            EndOutputUnsafe();
        }
    }

    private void WriteWrappedFormattedEvent(string leading, IrcFormattedText formatted, ConsoleColor defaultColor)
    {
        lock (_consoleLock)
        {
            BeginOutputUnsafe();
            var plainOffset = 0;
            foreach (var line in TerminalWordWrapper.Wrap(leading, formatted.PlainText, EffectiveWidth(null)))
            {
                WriteColoredUnsafe(line.Leading, defaultColor);
                plainOffset = FindWrappedTextOffset(formatted.PlainText, line.Text, plainOffset);
                WriteIrcFormattedRangeUnsafe(formatted, plainOffset, line.Text.Length, defaultColor);
                plainOffset += line.Text.Length;
                Console.WriteLine();
            }
            EndOutputUnsafe();
        }
    }

    private void WriteIrcFormattedRangeUnsafe(
        IrcFormattedText formatted,
        int start,
        int length,
        ConsoleColor defaultColor,
        ConsoleColor defaultBackground = ConsoleColor.Black)
    {
        if (length <= 0) return;
        var end = start + length;
        var runStart = 0;
        foreach (var run in formatted.Runs)
        {
            var runEnd = runStart + run.Text.Length;
            var sliceStart = Math.Max(start, runStart);
            var sliceEnd = Math.Min(end, runEnd);
            if (sliceEnd > sliceStart)
            {
                var foreground = IrcColor(run.Style.Foreground, defaultColor);
                var background = IrcColor(run.Style.Background, defaultBackground);
                if (run.Style.Reverse) (foreground, background) = (background, foreground);
                if (run.Style.Bold) foreground = Brighten(foreground);
                SetConsoleColorsUnsafe(foreground, background);
                if (_fullScreen)
                {
                    if (run.Style.Italic) Console.Write("\u001b[3m");
                    if (run.Style.Underline) Console.Write("\u001b[4m");
                }
                WriteHyperlinkedRangeUnsafe(formatted.PlainText, sliceStart, sliceEnd - sliceStart);
                ResetConsoleColorsUnsafe();
            }
            if (runEnd >= end) break;
            runStart = runEnd;
        }
    }

    private static int FindWrappedTextOffset(string plainText, string lineText, int start)
    {
        if (lineText.Length == 0) return Math.Min(start, plainText.Length);
        var found = plainText.IndexOf(lineText, Math.Min(start, plainText.Length), StringComparison.Ordinal);
        return found >= 0 ? found : Math.Min(start, plainText.Length);
    }

    internal static ConsoleColor IrcColor(int? color, ConsoleColor fallback)
    {
        if (color is null) return fallback;
        if (color is >= 0 and <= 15) return BaseIrcColors[color.Value];
        if (color is < 16 or > 98) return fallback;
        var rgb = ExtendedIrcColors[color.Value - 16];
        return ConsolePalette.MinBy(entry => ColorDistance(rgb, entry.Rgb)).Color;
    }

    private static long ColorDistance(int left, int right)
    {
        var red = (left >> 16 & 0xff) - (right >> 16 & 0xff);
        var green = (left >> 8 & 0xff) - (right >> 8 & 0xff);
        var blue = (left & 0xff) - (right & 0xff);
        return red * red + green * green + blue * blue;
    }

    private static readonly ConsoleColor[] BaseIrcColors =
    [
        ConsoleColor.White, ConsoleColor.Black, ConsoleColor.DarkBlue, ConsoleColor.DarkGreen,
        ConsoleColor.Red, ConsoleColor.DarkRed, ConsoleColor.DarkMagenta, ConsoleColor.DarkYellow,
        ConsoleColor.Yellow, ConsoleColor.Green, ConsoleColor.DarkCyan, ConsoleColor.Cyan,
        ConsoleColor.Blue, ConsoleColor.Magenta, ConsoleColor.DarkGray, ConsoleColor.Gray
    ];

    private static readonly int[] ExtendedIrcColors =
    [
        0x470000, 0x472100, 0x474700, 0x324700, 0x004700, 0x00472c, 0x004747, 0x002747,
        0x000047, 0x2e0047, 0x470047, 0x47002a, 0x740000, 0x743a00, 0x747400, 0x517400,
        0x007400, 0x007449, 0x007474, 0x004074, 0x000074, 0x4b0074, 0x740074, 0x740045,
        0xb50000, 0xb56300, 0xb5b500, 0x7db500, 0x00b500, 0x00b571, 0x00b5b5, 0x0063b5,
        0x0000b5, 0x7500b5, 0xb500b5, 0xb5006b, 0xff0000, 0xff8c00, 0xffff00, 0xb2ff00,
        0x00ff00, 0x00ffa0, 0x00ffff, 0x008cff, 0x0000ff, 0xa500ff, 0xff00ff, 0xff0098,
        0xff5959, 0xffb459, 0xffff71, 0xcfff60, 0x6fff6f, 0x65ffc9, 0x6dffff, 0x59b4ff,
        0x5959ff, 0xc459ff, 0xff66ff, 0xff59bc, 0xff9c9c, 0xffd39c, 0xffff9c, 0xe2ff9c,
        0x9cff9c, 0x9cffdb, 0x9cffff, 0x9cd3ff, 0x9c9cff, 0xdc9cff, 0xff9cff, 0xff94d3,
        0x000000, 0x131313, 0x282828, 0x363636, 0x4d4d4d, 0x656565, 0x818181, 0x9f9f9f,
        0xbcbcbc, 0xe2e2e2, 0xffffff
    ];

    private static readonly (ConsoleColor Color, int Rgb)[] ConsolePalette =
    [
        (ConsoleColor.Black, 0x000000), (ConsoleColor.DarkBlue, 0x000080),
        (ConsoleColor.DarkGreen, 0x008000), (ConsoleColor.DarkCyan, 0x008080),
        (ConsoleColor.DarkRed, 0x800000), (ConsoleColor.DarkMagenta, 0x800080),
        (ConsoleColor.DarkYellow, 0x808000), (ConsoleColor.Gray, 0xc0c0c0),
        (ConsoleColor.DarkGray, 0x808080), (ConsoleColor.Blue, 0x0000ff),
        (ConsoleColor.Green, 0x00ff00), (ConsoleColor.Cyan, 0x00ffff),
        (ConsoleColor.Red, 0xff0000), (ConsoleColor.Magenta, 0xff00ff),
        (ConsoleColor.Yellow, 0xffff00), (ConsoleColor.White, 0xffffff)
    ];

    private static ConsoleColor Brighten(ConsoleColor color) => color switch
    {
        ConsoleColor.Black => ConsoleColor.DarkGray,
        ConsoleColor.DarkBlue => ConsoleColor.Blue,
        ConsoleColor.DarkGreen => ConsoleColor.Green,
        ConsoleColor.DarkCyan => ConsoleColor.Cyan,
        ConsoleColor.DarkRed => ConsoleColor.Red,
        ConsoleColor.DarkMagenta => ConsoleColor.Magenta,
        ConsoleColor.DarkYellow => ConsoleColor.Yellow,
        ConsoleColor.Gray => ConsoleColor.White,
        _ => color
    };

    private void WriteWrappedEvent(string leading, string text, ConsoleColor color)
    {
        lock (_consoleLock)
        {
            BeginOutputUnsafe();
            var plainOffset = 0;
            foreach (var line in TerminalWordWrapper.Wrap(leading, text, EffectiveWidth(null)))
            {
                WriteColoredUnsafe(line.Leading, color);
                plainOffset = FindWrappedTextOffset(text, line.Text, plainOffset);
                if (HasInteractiveConsole) Console.ForegroundColor = color;
                WriteHyperlinkedRangeUnsafe(text, plainOffset, line.Text.Length);
                if (HasInteractiveConsole) Console.ResetColor();
                plainOffset += line.Text.Length;
                Console.WriteLine();
            }
            EndOutputUnsafe();
        }
    }

    private void WriteHyperlinkedRangeUnsafe(string text, int start, int length)
    {
        if (length <= 0) return;
        if (!_fullScreen)
        {
            Console.Write(text.AsSpan(start, length));
            return;
        }

        var end = Math.Min(text.Length, start + length);
        var cursor = start;
        foreach (var link in TerminalHyperlinkDetector.Find(text))
        {
            var linkStart = Math.Max(start, link.Start);
            var linkEnd = Math.Min(end, link.Start + link.Length);
            if (linkEnd <= linkStart) continue;
            if (linkStart > cursor) Console.Write(text.AsSpan(cursor, linkStart - cursor));
            Console.Write($"\u001b]8;;{link.Target}\u001b\\");
            Console.Write(text.AsSpan(linkStart, linkEnd - linkStart));
            Console.Write("\u001b]8;;\u001b\\");
            cursor = linkEnd;
        }
        if (cursor < end) Console.Write(text.AsSpan(cursor, end - cursor));
    }

    private void BeginOutputUnsafe()
    {
        if (_eventBatchDepth > 0)
        {
            if (_eventBatchOutputStarted) return;
            _eventBatchOutputStarted = true;
        }
        if (!_redrawing && _chromeVisible && HasInteractiveConsole) ClearInputUnsafe();
        if (!_redrawing && HasInteractiveConsole && _topicBarWasVisible) ClearTopicBarRowUnsafe(Math.Max(2, Console.BufferWidth));
    }

    private void EndOutputUnsafe()
    {
        Console.ResetColor();
        if (_eventBatchDepth > 0) return;
        FinishOutputUnsafe();
    }

    private void FinishOutputUnsafe()
    {
        if (!_redrawing && _chromeVisible && HasInteractiveConsole)
        {
            ReserveChromeRowsUnsafe();
            _renderTop = Console.CursorTop;
            RenderInputUnsafe();
            SetCursorVisibleUnsafe(_readingInput);
        }
    }

    private static void WriteColoredUnsafe(string text, ConsoleColor color)
    {
        if (HasInteractiveConsole) Console.ForegroundColor = color;
        Console.Write(text);
        if (HasInteractiveConsole) Console.ResetColor();
    }

    private void ClearTopicBarRowUnsafe(int width)
    {
        var left = Console.CursorLeft;
        var top = Console.CursorTop;
        var barTop = Math.Clamp(Console.WindowTop, 0, Console.BufferHeight - 1);
        Console.SetCursorPosition(0, barTop);
        EraseCurrentRowUnsafe(width);
        Console.SetCursorPosition(Math.Min(left, width - 1), Math.Min(top, Console.BufferHeight - 1));
    }

    private void EraseCurrentRowUnsafe(int width)
    {
        Console.ResetColor();
        if (_fullScreen)
        {
            Console.Write("\u001b[2K\r");
        }
        else
        {
            Console.Write(new string(' ', Math.Max(1, width - 1)));
            Console.SetCursorPosition(0, Console.CursorTop);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr consoleHandle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr consoleHandle, uint mode);
}
