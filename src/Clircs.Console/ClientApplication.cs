using Clircs.Commands;
using Clircs.Dcc;
using Clircs.Identity;
using Clircs.Infrastructure;
using Clircs.Networking;
using Clircs.Scripting;
using Clircs.Sessions;
using Clircs.Transport;

namespace Clircs.ConsoleClient;

/// <summary>
/// Composes and coordinates the console client's application-level services and runtime dependencies.
/// </summary>
internal sealed partial class ClientApplication : IAsyncDisposable
{
    internal static readonly TimeSpan ConnectionAttemptTimeout = TimeSpan.FromSeconds(60);
    private static readonly IReadOnlyDictionary<string, string> ServiceTargets =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["nickserv"] = "NickServ",
            ["chanserv"] = "ChanServ",
            ["memoserv"] = "MemoServ",
            ["operserv"] = "OperServ",
            ["hostserv"] = "HostServ",
            ["botserv"] = "BotServ",
            ["limitserv"] = "LimitServ"
        };
    private static readonly IReadOnlyDictionary<string, string> HelpUsageOverrides =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["help"] = "/help [command]",
            ["about"] = "/about",
            ["config"] = "/config [path]",
            ["backup"] = "/backup [create|list|path]",
            ["logging"] = "/logging <on|off|status|list|path> [network] [target]",
            ["network"] = "/network [list|profiles|add|remove|use|status|sasl] [arguments]",
            ["disconnect"] = "/disconnect [reason]",
            ["reconnect"] = "/reconnect [cancel]",
            ["quit"] = "/quit [reason]",
            ["nick"] = "/nick <nickname>",
            ["away"] = "/away [message]",
            ["back"] = "/back",
            ["awaylog"] = "/awaylog [on|off|status]",
            ["messages"] = "/messages [list|read <nick>|delete <nick>|clear]",
            ["window"] = "/window [number|name]",
            ["close"] = "/close [--force]",
            ["autojoin"] = "/autojoin [channel|list|add|remove|run|clear]",
            ["rj"] = "/rj [channel]",
            ["say"] = "/say <message>",
            ["me"] = "/me <action>",
            ["ame"] = "/ame <action>",
            ["amsg"] = "/amsg <message>",
            ["ping"] = "/ping <nickname>",
            ["sv"] = "/sv",
            ["time"] = "/time [nickname]",
            ["part"] = "/part [channel] [reason]",
            ["cycle"] = "/cycle [channel] [reason]",
            ["topic"] = "/topic [channel] [topic]",
            ["rt"] = "/rt [channel]",
            ["mode"] = "/mode [target] [modes] [arguments]",
            ["op"] = "/op <nick> [nick ...]",
            ["deop"] = "/deop <nick> [nick ...]",
            ["voice"] = "/voice <nick> [nick ...]",
            ["devoice"] = "/devoice <nick> [nick ...]",
            ["kick"] = "/kick <nick> [reason]",
            ["ban"] = "/ban <nick|mask>",
            ["kickban"] = "/kickban <nick> [reason]",
            ["tban"] = "/tban <nick-or-mask> <duration> [--reason <text>]",
            ["mmode"] = "/mmode <+|-mode> <nick> [nick ...]",
            ["banlist"] = "/banlist [channel]",
            ["exceptlist"] = "/exceptlist [channel]",
            ["invitelist"] = "/invitelist [channel]",
            ["quietlist"] = "/quietlist [channel]",
            ["xdcc"] = "/xdcc <get|sget> <bot> <pack>",
            ["unban"] = "/unban <mask>",
            ["appendtopic"] = "/appendtopic <text>",
            ["adduser"] = "/adduser <handle> [hostmask|nickname]",
            ["addbot"] = "/addbot <handle> [hostmask|nickname]",
            ["remuser"] = "/remuser <handle>",
            ["addhost"] = "/addhost <handle> <hostmask|nick>",
            ["remhost"] = "/remhost <handle> <hostmask>",
            ["chattr"] = "/chattr <handle> <roles> [channel]",
            ["addchan"] = "/addchan <handle> <channel> [roles]",
            ["remchan"] = "/remchan <handle> <channel>",
            ["chinfo"] = "/chinfo <handle> [channel] <text|off>",
            ["uwhois"] = "/uwhois <handle|nick>",
            ["notify"] = "/notify [list|add|remove] [nick]",
            ["accept"] = "/accept [[-]nick,...]",
            ["cprot"] = "/cprot <on|off|status|detector> [arguments]",
            ["pprot"] = "/pprot <on|off|status|detector> [arguments]",
            ["server"] = "/server <host|profile> [port] [--tls] [--new] [--password]",
            ["protect"] = "/protect <operation> [arguments] [scope]",
            ["clones"] = "/clones [channel]",
            ["who"] = "/who [channel|nickname|mask]",
            ["whois"] = "/whois <nickname>",
            ["iwhois"] = "/iwhois <nickname>",
            ["whowas"] = "/whowas <nickname>",
            ["motd"] = "/motd",
            ["links"] = "/links [server-mask]",
            ["list"] = "/list [filters]",
            ["dns"] = "/dns <hostname|IP>",
            ["nickserv"] = "/nickserv <command> [arguments]",
            ["chanserv"] = "/chanserv <command> [arguments]",
            ["memoserv"] = "/memoserv <command> [arguments]",
            ["operserv"] = "/operserv <command> [arguments]",
            ["hostserv"] = "/hostserv <command> [arguments]",
            ["botserv"] = "/botserv <command> [arguments]",
            ["limitserv"] = "/limitserv <command> [arguments]",
            ["dcc"] = "/dcc [chat|schat|send|ssend|list|show|accept|resume|reject|cancel]",
            ["set"] = "/set [setting value]",
            ["theme"] = "/theme [list|reload|use <name>]",
            ["tls"] = "/tls [pins|forget <host> <port>]",
            ["script"] = "/script <list|load|unload|reload|errors|permissions> [arguments]"
        };
    private readonly CommandRegistry _commands = new();
    private readonly CommandExecutionCoordinator _commandExecution;
    private readonly ConsolePresenter _presenter = new();
    private readonly string _dataDirectory;
    private readonly BackupManager _backupManager;
    private readonly LoggingSettingsStore _loggingStore;
    private readonly EventLogWriter _logWriter;
    private readonly TlsCertificatePromptPolicy _tlsCertificatePolicy;
    private readonly NetworkProfileStore _profileStore;
    private readonly NetworkCredentialStore _networkCredentials;
    private readonly ScriptManager _scriptManager;
    private readonly UserDirectoryStore _userDirectoryStore;
    private readonly ThemeManager _themeManager;
    private readonly AppearanceSettingsStore _appearanceStore;
    private readonly AwayMessageStore _awayMessageStore;
    private readonly ProtectionSettingsStore _protectionStore;
    private readonly UserAndChannelPolicyCoordinator _userAndChannelPolicy = new();
    private readonly QuoteProvider _quotes;
    // Coordinates the few operations that must update a core IRC buffer and its
    // terminal window as one transaction. WindowStateRegistry protects its own data.
    private readonly object _windowTransactionGate = new();
    private readonly object _errorLogGate = new();
    private readonly SerializedEventDispatcher<Action> _applicationEvents;
    private readonly InboundSessionEventPump<SessionEvent> _inboundSessionEvents;
    private readonly InboundResourceCircuitBreaker _inboundResourceCircuitBreaker = new();
    private readonly LiveNetworkSessionRegistry _liveSessions = new();
    private readonly WindowStateRegistry _windowStates = new();
    private readonly OutputRoutingCoordinator _outputRouting = new();
    private readonly ChannelSynchronizationCoordinator _channelSynchronization = new();
    private readonly SessionTransientState _sessionTransientState = new();
    private readonly DccCoordinator _dcc = new();
    private readonly ClientPreferences _preferences;
    private readonly object _scriptHeaderGate = new();
    private readonly Dictionary<(string ScriptId, string ItemId), ScriptHeaderContribution> _scriptHeaders = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SessionWorkTracker _applicationWork;
    private RecentConnection? _recentConnection;
    private bool _exitRequested;
    public ClientApplication()
    {
        _applicationWork = new SessionWorkTracker(_lifetime.Token);
        _applicationEvents = new SerializedEventDispatcher<Action>(action => action());
        _inboundSessionEvents = new InboundSessionEventPump<SessionEvent>(
            OnSessionEvents,
            exception => LogUnexpectedApplicationWorkFailure("inbound IRC event delivery", exception));
        _commandExecution = new CommandExecutionCoordinator(_commands);
        var dataDirectory = ClientDataDirectory.Resolve();
        _dataDirectory = dataDirectory;
        _preferences = new ClientPreferences(
            CreateDefaultNickname(),
            CreateDefaultUsername(),
            DefaultDccDownloadDirectory(dataDirectory));
        _backupManager = new BackupManager(dataDirectory);
        _loggingStore = new LoggingSettingsStore(System.IO.Path.Combine(dataDirectory, "logging.json"));
        _logWriter = new EventLogWriter(System.IO.Path.Combine(dataDirectory, "logs"));
        _logWriter.ErrorRaised += OnLogWriterError;
        _tlsCertificatePolicy = new TlsCertificatePromptPolicy(
            _presenter,
            System.IO.Path.Combine(dataDirectory, "trusted-certificates.json"));
        _tlsCertificatePolicy.NoticeRaised += OnTlsCertificateNotice;
        _profileStore = new NetworkProfileStore(System.IO.Path.Combine(dataDirectory, "networks.toml"));
        _networkCredentials = new NetworkCredentialStore(System.IO.Path.Combine(dataDirectory, "network-secrets.json"));
        _userDirectoryStore = new UserDirectoryStore(System.IO.Path.Combine(dataDirectory, "users"));
        _themeManager = new ThemeManager(System.IO.Path.Combine(dataDirectory, "themes"));
        _appearanceStore = new AppearanceSettingsStore(System.IO.Path.Combine(dataDirectory, "appearance.json"));
        _awayMessageStore = new AwayMessageStore(System.IO.Path.Combine(dataDirectory, "away-messages.json"));
        _protectionStore = new ProtectionSettingsStore(System.IO.Path.Combine(dataDirectory, "protection.json"));
        _quotes = new QuoteProvider(dataDirectory, System.IO.Path.Combine(AppContext.BaseDirectory, "quotes.txt"));
        var appearance = _appearanceStore.Load();
        if (!string.IsNullOrWhiteSpace(appearance.Nickname)) _preferences.Nickname = appearance.Nickname;
        if (!string.IsNullOrWhiteSpace(appearance.AlternateNickname)) _preferences.AlternateNickname = appearance.AlternateNickname;
        else _preferences.AlternateNickname = $"{_preferences.Nickname}_";
        if (!string.IsNullOrWhiteSpace(appearance.Username)) _preferences.Username = appearance.Username;
        if (!string.IsNullOrWhiteSpace(appearance.RealName)) _preferences.RealName = appearance.RealName;
        _preferences.AwayMessage = appearance.AwayMessage;
        if (_themeManager.TryGet(appearance.Theme, out var selectedTheme))
        {
            _presenter.SetTheme(selectedTheme!);
        }
        else
        {
            _presenter.SetTheme(TerminalTheme.BuiltIns["clircs"]);
        }
        if (TryParseHostmaskVisibility(appearance.JoinHostmasks, out var joinVisibility)) _preferences.JoinHostmasks = joinVisibility;
        if (TryParseHostmaskVisibility(appearance.PartHostmasks, out var partVisibility)) _preferences.PartHostmasks = partVisibility;
        if (TryParseHostmaskVisibility(appearance.QuitHostmasks, out var quitVisibility)) _preferences.QuitHostmasks = quitVisibility;
        foreach (var route in appearance.OutputRoutes)
        {
            if (_outputRouting.Supports(route.Key) && TryParseOutputDestination(route.Value, out var destination))
            {
                _outputRouting.TrySetDestination(route.Key, destination);
            }
        }
        _preferences.AutoRejoinOnKick = appearance.AutoRejoinOnKick;
        _preferences.AnnounceUserInfoOnJoin = appearance.AnnounceUserInfoOnJoin;
        _preferences.DefaultKickMessage = appearance.DefaultKickMessage;
        _preferences.DefaultQuitMessage = appearance.DefaultQuitMessage;
        _preferences.DefaultTopicMessage = appearance.DefaultTopicMessage;
        _preferences.HighlightNickname = appearance.HighlightNickname;
        _preferences.CloneDetection = appearance.CloneDetection;
        _preferences.NetworkReconnect = appearance.NetworkReconnect;
        _preferences.KillReconnect = appearance.KillReconnect;
        _preferences.AwayLogging = appearance.AwayLogging;
        _preferences.DccAddress = appearance.DccAddress;
        if (DccPortRange.TryParse(appearance.DccPorts, out var dccPorts)) _preferences.DccPorts = dccPorts;
        _preferences.DccDownloads = ResolveDccDownloadDirectory(appearance.DccDownloads, dataDirectory);
        if (BanmaskFormatter.TryParse(appearance.DefaultBanmask, out var banmaskStyle)) _preferences.BanmaskStyle = banmaskStyle;
        _presenter.SetHostmaskVisibility(_preferences.JoinHostmasks, _preferences.PartHostmasks, _preferences.QuitHostmasks);
        _scriptManager = new ScriptManager(
            dataDirectory,
            new ScriptHostServices(
                PrintScriptOutput,
                RegisterScriptCommand,
                QueueScriptCommand,
                SetScriptHeader,
                ClearScriptHeader,
                ClearScriptHeaders,
                ReadScriptSecret),
            System.IO.Path.Combine(AppContext.BaseDirectory, "scripts"));
        RegisterCommands();
    }

    public async Task RunAsync()
    {
        _presenter.EnterFullScreen();
        _presenter.Banner();
        if (_profileStore.LoadError is not null)
        {
            _presenter.Result(_profileStore.LoadError, success: false);
        }
        foreach (var error in _themeManager.Errors)
        {
            _presenter.Result($"Theme not loaded: {error}", success: false);
        }
        if (_appearanceStore.LoadError is not null)
        {
            _presenter.Result(_appearanceStore.LoadError, success: false);
        }
        if (_awayMessageStore.LoadError is not null)
        {
            _presenter.Result(_awayMessageStore.LoadError, success: false);
        }
        if (_protectionStore.LoadError is not null)
        {
            _presenter.Result(_protectionStore.LoadError, success: false);
        }
        if (_loggingStore.LoadError is not null)
        {
            _presenter.Result(_loggingStore.LoadError, success: false);
        }
        foreach (var error in await _scriptManager.RestoreLoadedAsync(_lifetime.Token))
        {
            _presenter.Result(error, success: false);
        }
        Console.CancelKeyPress += OnCancelKeyPress;

        try
        {
            while (!_exitRequested && !_lifetime.IsCancellationRequested)
            {
                RefreshWindowChrome();
                var line = _presenter.ReadLine(
                    Prompt(), NicknameMatches, ScrollActiveViewport, ResizeActiveViewport,
                    historyKey: _windowStates.ActiveBufferId);
                if (line is null)
                {
                    break;
                }

                try
                {
                    var parsed = CommandLineParser.Parse(line);
                    var context = CaptureCommandContext();
                    CommandResult result;
                    if (parsed is ChatInput chat)
                    {
                        result = await _commandExecution.ExecuteAsync(
                            context,
                            cancellationToken => SayAsync(chat.Text, cancellationToken),
                            _lifetime.Token);
                    }
                    else
                    {
                        result = await _commandExecution.ExecuteAsync(context, (CommandInput)parsed, _lifetime.Token);
                    }

                    DisplayCommandResult(result, context);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (IsExpectedCommandFailure(exception))
                {
                    DisplayCommandResult(CommandResult.Failure(exception.Message), CaptureCommandContext());
                }
                catch (Exception exception)
                {
                    var context = CaptureCommandContext();
                    LogUnexpectedCommandFailure(line, context, exception);
                    DisplayCommandResult(
                        CommandResult.Failure("Command failed unexpectedly; details were written to the clircs error log"),
                        context);
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            try
            {
                await CloseAllSessionsAsync(ResolveQuitMessage(null));
                await _applicationWork.StopAndWaitAsync();
            }
            finally
            {
                _presenter.ExitFullScreen();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try
        {
            await CloseAllSessionsAsync(ResolveQuitMessage(null));
            await _applicationWork.StopAndWaitAsync();
            await _inboundSessionEvents.CompleteAsync();
            _applicationEvents.Complete();
            await _scriptManager.DisposeAsync();
            await _logWriter.DisposeAsync();
        }
        finally
        {
            _presenter.ExitFullScreen();
        }
        _applicationWork.Dispose();
        _lifetime.Dispose();
    }

    private void DisplayCommandResult(CommandResult result, CommandContext? context = null) =>
        _applicationEvents.Dispatch(() => DisplayCommandResultCore(result, context));

    private void DisplayCommandResultCore(CommandResult result, CommandContext? context)
    {
        if (result.Presentation is null && string.IsNullOrEmpty(result.Message)) return;
        var message = result.Presentation is null
            ? FormatLocalCommandResult(result.Message ?? string.Empty)
            : result.Message ?? string.Empty;
        var session = SessionFor(context?.NetworkSessionId);
        var buffer = BufferFor(session, context?.BufferId);
        if (session is not null && buffer is null) buffer = session.State.StatusBuffer;
        if (session is null || buffer is null)
        {
            if (result.Presentation is not null) _presenter.Presentation(result.Presentation);
            else if (result.Succeeded) _presenter.LocalResult(message);
            else _presenter.Result(message, success: false);
            return;
        }

        var sessionEvent = new SessionEvent(
            session.State.Id,
            buffer.Id,
            result.Succeeded ? SessionEventKind.Server : SessionEventKind.Error,
            message,
            DateTimeOffset.Now,
            result.Succeeded && result.Presentation is null
                ? new Dictionary<string, string?> { ["clientResult"] = "true" }
                : null,
            Presentation: result.Presentation);
        StoredWindowEvent stored;
        lock (_windowTransactionGate)
        {
            stored = session.State.TryGetBuffer(buffer.Id, out _)
                ? StoreWindowEventUnsafe(sessionEvent, buffer.Name, isReplay: false, trackUnread: false)
                : new StoredWindowEvent(false, false, false, false);
        }
        if (!stored.Stored) return;
        if (stored.IsActive && !stored.IsScrolled)
        {
            if (stored.Replaced) RedrawActiveBuffer();
            else _presenter.Event(sessionEvent, buffer.Name);
        }
        if (stored.IsActive) RefreshWindowChrome();
    }

    internal static string FormatLocalCommandResult(string message)
    {
        var trimmed = message.TrimEnd();
        return trimmed.EndsWith(".", StringComparison.Ordinal) &&
               !trimmed.EndsWith("..", StringComparison.Ordinal)
            ? trimmed[..^1]
            : trimmed;
    }

    private StoredWindowEvent StoreWindowEventUnsafe(
        SessionEvent sessionEvent,
        string bufferName,
        bool isReplay,
        bool trackUnread)
    {
        var incomingRows = _presenter.MeasureEventRows(sessionEvent, bufferName);
        var result = _windowStates.StoreEvent(
            sessionEvent,
            incomingRows,
            previous => _presenter.MeasureEventRows(previous, bufferName),
            isReplay,
            trackUnread,
            DateTimeOffset.UtcNow);
        return new StoredWindowEvent(
            result.Stored,
            result.Replaced,
            result.IsActive,
            result.IsScrolled,
            result.EmergencyLimitReached,
            result.TotalEmergencyLimitReached);
    }

    private readonly record struct StoredWindowEvent(
        bool Stored,
        bool Replaced,
        bool IsActive,
        bool IsScrolled,
        bool EmergencyLimitReached = false,
        bool TotalEmergencyLimitReached = false);

    private sealed record RecentConnection(IrcConnectionOptions Options, NetworkProfileId? ProfileId);

}
