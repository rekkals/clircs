using System.Net.Sockets;
using Clircs.Commands;
using Clircs.Identity;
using Clircs.Infrastructure;
using Clircs.Networking;
using Clircs.Protection;
using Clircs.Protocol;
using Clircs.Scripting;
using Clircs.Sessions;
using Clircs.State;

namespace Clircs.ConsoleClient;

// Owns terminal windows, buffer selection, chrome, scrollback, and input history.
internal sealed partial class ClientApplication
{
    // Application-owned status surface for when we don't have an active network session
    private readonly BufferState _localStatusBuffer = new(
        BufferId.New(),
        NetworkSessionId.New(),
        BufferKind.Status,
        "*");
    private readonly DateChangeClock _dateChangeClock = new(DateTimeOffset.Now);
    private CommandResult SwitchTo(IrcNetworkSession? session, BufferState buffer)
    {
        lock (_windowTransactionGate)
        {
            ActivateWindowUnsafe(session, buffer);
        }

        RedrawActiveBuffer(session, buffer, pendingEvent: null);

        return CommandResult.Success();
    }

    private void ActivateWindowUnsafe(IrcNetworkSession? session, BufferState buffer)
    {
        if (session is null)
        {
            _windowStates.ActivateLocal(buffer.Id);
        }
        else
        {
            _windowStates.Activate(session.State.Id, buffer.Id);
        }
    }

    private (NetworkSessionId? SessionId, BufferId? BufferId) ActiveLocation()
        => _windowStates.ActiveLocation();

    private void PresentWindowEvent(
        SessionEvent sessionEvent,
        string bufferName,
        SessionEvent? separator)
    {
        if (separator is null)
        {
            _presenter.Event(sessionEvent, bufferName);
            return;
        }

        _presenter.RunEventBatch(() =>
        {
            _presenter.Event(separator, bufferName);
            _presenter.Event(sessionEvent, bufferName);
        });
    }

    private void RedrawActiveBuffer(SessionEvent? pendingEvent = null)
    {
        var (session, buffer) = ActiveDisplayWindowSnapshot();
        if (buffer is not null)
        {
            RedrawActiveBuffer(session, buffer, pendingEvent);
        }
    }

    private void RedrawActiveBuffer(
        IrcNetworkSession? session,
        BufferState buffer,
        SessionEvent? pendingEvent)
    {
        var chrome = BuildWindowChrome(session, buffer);
        var viewportRows = _presenter.ViewportContentRowsFor(chrome.Header);
        var viewport = _windowStates.ViewportSnapshot(
            buffer.Id, viewportRows, dateChange: _preferences.DateChange);
        var displayHistory = DateChangeSeparators.ForHistory(
            viewport.History, viewport.PreviousTimestamp, _preferences.DateChange);

        var pendingRows = 0;
        if (pendingEvent is not null)
        {
            pendingRows = _presenter.MeasureEventRows(pendingEvent, buffer.Name);
            var previousTimestamp = DateChangeSeparators.PreviousTimestamp(
                viewport.History, viewport.History.Length);
            if (_preferences.DateChange &&
                DateChangeSeparators.Before(pendingEvent, previousTimestamp) is { } separator)
            {
                pendingRows += _presenter.MeasureEventRows(separator, buffer.Name);
            }
        }

        var offset = viewport.ScrollOffset;
        if (offset > 0)
        {
            var totalRows = displayHistory.Sum(item => _presenter.MeasureEventRows(item, buffer.Name));
            offset = Math.Clamp(offset, 0, Math.Max(0, totalRows - viewportRows));
        }
        _windowStates.SetScrollOffsetIfActive(buffer.Id, offset);
        var history = VisibleHistory(displayHistory, offset, viewportRows, buffer.Name, pendingRows);

        var outputRows = history.Sum(slice => slice.TakeRows) + pendingRows;
        _presenter.Redraw(chrome, outputRows, () =>
        {
            foreach (var slice in history)
            {
                _presenter.EventRows(slice.Item, buffer.Name, slice.SkipRows, slice.TakeRows);
            }
        });
    }

    private ViewportHistory.Slice<SessionEvent>[] VisibleHistory(
        IReadOnlyList<SessionEvent> history,
        int offset,
        int viewportRows,
        string bufferName,
        int pendingRows)
    {
        var budget = Math.Max(0, viewportRows - pendingRows);
        if (budget == 0) return [];

        return ViewportHistory.SelectRows(
            history,
            offset,
            budget,
            sessionEvent => _presenter.MeasureEventRows(sessionEvent, bufferName)).ToArray();
    }

    private void ScrollActiveViewport(int direction)
    {
        var (_, buffer) = ActiveDisplayWindowSnapshot();
        if (buffer is null || direction == 0) return;
        var viewport = _windowStates.ViewportSnapshot(buffer.Id);
        if (viewport.History.Length == 0) return;

        var displayHistory = DateChangeSeparators.ForHistory(
            viewport.History, viewport.PreviousTimestamp, _preferences.DateChange);
        var rowBudget = Math.Max(1, _presenter.ViewportContentRows);
        var pageSize = Math.Max(1, rowBudget - 4);
        var totalRows = displayHistory.Sum(sessionEvent => _presenter.MeasureEventRows(sessionEvent, buffer.Name));
        var maximumOffset = Math.Max(0, totalRows - rowBudget);
        if (!_windowStates.SetScrollOffsetIfActive(buffer.Id, direction > 0
                ? Math.Min(maximumOffset, viewport.ScrollOffset + pageSize)
                : Math.Max(0, viewport.ScrollOffset - pageSize))) return;
        RedrawActiveBuffer();
    }

    private void ResizeActiveViewport()
    {
        RedrawActiveBuffer();
    }

    internal static SessionEvent StartupEvent(IrcNetworkSession session, BufferState buffer) => new(
        session.State.Id,
        buffer.Id,
        SessionEventKind.Status,
        ProductInfo.DisplayName,
        DateTimeOffset.Now,
        new Dictionary<string, string?> { ["event"] = "startup" });

    private SessionEvent LocalStartupEvent() => new(
        _localStatusBuffer.NetworkSessionId,
        _localStatusBuffer.Id,
        SessionEventKind.Status,
        ProductInfo.DisplayName,
        DateTimeOffset.Now,
        new Dictionary<string, string?> { ["event"] = "startup" });

    private CommandResult MoveBuffer(int offset)
    {
        var entries = OrderedBuffers().ToArray();
        if (entries.Length == 0)
        {
            return CommandResult.Failure("No windows are open.");
        }

        var current = Array.FindIndex(entries, entry => _windowStates.IsActiveBuffer(entry.Buffer.Id));
        var next = ((current < 0 ? 0 : current) + offset + entries.Length) % entries.Length;
        return SwitchTo(entries[next].Session, entries[next].Buffer);
    }

    private IrcNetworkSession? RequireSession(out CommandResult failure)
    {
        var session = ActiveSession();
        if (session is null)
        {
            failure = CommandResult.Failure("Not connected. Use /server <host|profile> [port] [--tls] [--new].");
            return null;
        }

        failure = CommandResult.Success();
        return session;
    }

    private IrcNetworkSession? ActiveSession() =>
        ResolveWindowContext(_commandExecution.CurrentContext).Session;

    private BufferState? ActiveBuffer() =>
        ResolveWindowContext(_commandExecution.CurrentContext).Buffer;

    private CommandContext CaptureCommandContext()
    {
        var active = _windowStates.ActiveLocation();
        return new CommandContext(active.SessionId, active.BufferId);
    }

    private IrcNetworkSession? SessionFor(NetworkSessionId? sessionId)
        => _windowStates.ResolveSession(_liveSessions, sessionId);

    private static BufferState? BufferFor(IrcNetworkSession? session, BufferId? bufferId) =>
        session is not null && bufferId is not null && session.State.TryGetBuffer(bufferId.Value, out var buffer)
            ? buffer
            : null;

    private static bool IsExpectedCommandFailure(Exception exception) =>
        exception is
            CommandLineException or
            IrcProtocolException or
            IOException or
            UnauthorizedAccessException or
            SocketException;

    private void LogUnexpectedCommandFailure(string commandLine, CommandContext context, Exception exception)
    {
        try
        {
            var command = commandLine.TrimStart().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "<empty>";
            var path = System.IO.Path.Combine(_dataDirectory, "logs", "command-errors.log");
            DurableFileWriter.Shared.AppendText(
                path,
                $"{DateTimeOffset.Now:O} Command={command} Session={context.NetworkSessionId} Buffer={context.BufferId}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // The diagnostic path must never replace the original failure with a second failure.
        }
        catch (UnauthorizedAccessException)
        {
            // The diagnostic path must never replace the original failure with a second failure.
        }
    }

    private string? ActiveTarget()
    {
        var buffer = ActiveBuffer();
        if (buffer?.Kind == BufferKind.Query)
        {
            return buffer.Name;
        }

        var session = ActiveSession();
        return buffer?.Kind == BufferKind.Channel && session?.State.TryGetChannel(buffer.Name, out _) == true
            ? buffer.Name
            : null;
    }

    private string? ActiveChannel()
    {
        var buffer = ActiveBuffer();
        return buffer?.Kind == BufferKind.Channel ? buffer.Name : null;
    }

    private string DccChatStatus(BufferId bufferId)
    {
        if (_dcc.RequestIdForChatBuffer(bufferId) is not { } requestId ||
            !_dcc.Requests.TryGet(requestId, out var request))
        {
            return "closed";
        }
        return DccStateText(request!.State);
    }

    private IEnumerable<(IrcNetworkSession? Session, BufferState Buffer)> OrderedBuffers()
    {
        var entries = SessionsSnapshot()
            .SelectMany(session => session.State.Buffers.Select(buffer => (
                Session: (IrcNetworkSession?)session,
                Buffer: buffer)))
            .Concat(_scriptWindows.LocalBuffersSnapshot().Select(buffer => (
                Session: (IrcNetworkSession?)null,
                Buffer: buffer)))
            .ToArray();

        foreach (var entry in entries)
        {
            BufferNumber(entry.Buffer.Id);
        }

        return entries.OrderBy(entry => BufferNumber(entry.Buffer.Id));
    }

    private string Prompt()
    {
        var buffer = ActiveBuffer();
        return Prompt(buffer);
    }

    private static string Prompt(BufferState? buffer)
    {
        var label = buffer?.Kind == BufferKind.Status ? "status" : buffer?.Name ?? "status";
        return $"[{label}] ";
    }

    private IReadOnlyList<string> NicknameMatches(string prefix)
    {
        var session = ActiveSession();
        var buffer = ActiveBuffer();
        if (session is null || buffer?.Kind != BufferKind.Channel ||
            !session.State.TryGetChannel(buffer.Name, out var channel))
        {
            return [];
        }

        var foldedPrefix = IrcCaseFold.Fold(prefix, session.State.CaseMapping);
        return channel!.Members
            .Select(member => member.Nickname)
            .Where(nickname => IrcCaseFold.Fold(nickname, session.State.CaseMapping)
                .StartsWith(foldedPrefix, StringComparison.Ordinal))
            .OrderBy(nickname => nickname, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task RefreshWindowClockAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            _applicationEvents.Dispatch(() =>
            {
                if (cancellationToken.IsCancellationRequested) return;

                var now = DateTimeOffset.Now;
                if (_dateChangeClock.Observe(now) && _preferences.DateChange)
                {
                    RecordDateChanges(now);
                }

                if (_preferences.LagMeter &&
                    ActiveSession()?.ConnectionState == IrcConnectionState.Online)
                {
                    RefreshWindowChrome();
                }
            });
        }
    }

    private void RecordDateChanges(DateTimeOffset now)
    {
        var buffers = SessionsSnapshot()
            .SelectMany(session => session.State.Buffers)
            .Where(buffer => _windowStates.HasNumber(buffer.Id))
            .Concat(_scriptWindows.LocalBuffersSnapshot())
            .Append(_localStatusBuffer)
            .ToArray();

        _presenter.RunEventBatch(() =>
        {
            foreach (var buffer in buffers)
            {
                var separator = DateChangeSeparators.Create(
                    buffer.NetworkSessionId, buffer.Id, now);
                var rows = _presenter.MeasureEventRows(separator, buffer.Name);
                WindowStateRegistry.WindowEventStoreResult stored;
                lock (_windowTransactionGate)
                {
                    stored = _windowStates.StoreDateChange(
                        separator,
                        rows,
                        item => _presenter.MeasureEventRows(item, buffer.Name));
                }

                if (stored.Stored && stored.IsActive && !stored.IsScrolled)
                {
                    _presenter.Event(separator, buffer.Name);
                }
            }
            RefreshWindowChrome();
        });
    }

    private void RefreshWindowChrome()
    {
        var (session, buffer) = ActiveDisplayWindowSnapshot();
        if (!_presenter.SetChrome(BuildWindowChrome(session, buffer))) return;

        if (buffer is not null)
        {
            RedrawActiveBuffer(session, buffer, pendingEvent: null);
        }
        else
        {
            _presenter.RenderChrome();
        }
    }

    private (IrcNetworkSession? Session, BufferState? Buffer) ActiveDisplayWindowSnapshot() =>
        ResolveWindowContext(null);

    private (IrcNetworkSession? Session, BufferState? Buffer) ResolveWindowContext(
        CommandContext? context)
    {
        var location = context is null ||
            (context.NetworkSessionId is null && context.BufferId is null)
                ? _windowStates.ActiveLocation()
                : (SessionId: context.NetworkSessionId, BufferId: context.BufferId);

        if (location.SessionId is { } sessionId)
        {
            var session = _liveSessions.Find(sessionId);
            return (session, BufferFor(session, location.BufferId));
        }

        if (location.BufferId == _localStatusBuffer.Id)
        {
            return (null, _localStatusBuffer);
        }

        return (null, location.BufferId is { } bufferId
            ? _scriptWindows.FindLocal(bufferId)
            : null);
    }

    private WindowChromeModel BuildWindowChrome(IrcNetworkSession? session, BufferState? buffer) =>
        new(BuildBufferHeader(session, buffer), BuildStatusBar(session, buffer), Prompt(buffer));

    private BufferHeaderModel BuildBufferHeader(IrcNetworkSession? session, BufferState? buffer)
    {
        string? primary = null;
        if (session is not null && buffer?.Kind == BufferKind.Channel &&
            session.State.TryGetChannel(buffer.Name, out var channel) &&
            !string.IsNullOrWhiteSpace(channel!.Topic))
        {
            primary = $"[{buffer.Name}] {channel.Topic}";
        }

        var activeBufferId = buffer?.Id.Value.ToString();
        ScriptHeaderContribution[] contributions;
        lock (_scriptHeaderGate)
        {
            contributions = _scriptHeaders.Values
                .Where(item => item.BufferId is null ||
                    string.Equals(item.BufferId, activeBufferId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        return new BufferHeaderModel(
            primary,
            contributions
                .Select(item => new BufferHeaderItem(item.Text, item.Priority, item.MinimumWidth))
                .ToArray(),
            primary is null ? null : IrcTextFormatting.Parse(primary));
    }

    internal StatusBarModel BuildStatusBar(IrcNetworkSession? session, BufferState? buffer)
    {
        var fields = new List<string>();
        if (session is null || buffer is null)
        {
            if (buffer is { Kind: BufferKind.Script })
            {
                fields.Add($"({BufferNumber(buffer.Id)}) {ProductInfo.DisplayName}");
                fields.Add(buffer.Name);
            }
            else
            {
                fields.Add(ProductInfo.DisplayName);
                fields.Add("offline");
            }
        }
        else
        {
            var number = BufferNumber(buffer.Id);
            var networkName = ProfileFor(session)?.DisplayName ?? session.Features.NetworkName ?? session.State.DisplayName;
            var upstreamTls = session.State.BouncerName is null
                ? session.State.ClientTransportTls
                : session.State.UpstreamTls == true;
            fields.Add(FormatStatusNetworkField(
                number, networkName, _preferences.StatusTls && upstreamTls));
            if (buffer.Kind == BufferKind.Status)
            {
                fields.Add(FormatStatusServerField(
                    session.State.ServerName ?? session.Options.Endpoint.Host,
                    _preferences.StatusBouncer ? session.State.BouncerName : null,
                    _preferences.StatusTls && session.State.ClientTransportTls));
            }
            fields.Add(buffer.Kind == BufferKind.Status ? "status" : buffer.Name);
            var displayedNickname = _preferences.StatusUserModes && session.State.UserModes.Length > 0
                ? $"{session.CurrentNickname}({session.State.UserModes})"
                : session.CurrentNickname;
            if (session.State.IsAway) fields.Add("away");
            if (buffer.Kind == BufferKind.DccChat)
            {
                fields.Add(DccChatStatus(buffer.Id));
            }
            else if (session.ConnectionState != IrcConnectionState.Online)
            {
                fields.Add(ConnectionStatusLabel(session));
            }
            if (buffer.Kind == BufferKind.Channel && session.State.TryGetChannel(buffer.Name, out var channel))
            {
                var currentChannel = channel!;
                if (currentChannel.TryGetMember(session.CurrentNickname, out var self))
                {
                    var prefix = session.Features.HighestPrefix(self!.PrefixModes);
                    displayedNickname = _preferences.StatusUserModes && session.State.UserModes.Length > 0
                        ? $"{prefix}{session.CurrentNickname}({session.State.UserModes})"
                        : $"{prefix}{session.CurrentNickname}";
                }
                var modes = _preferences.StatusChannelModes
                    ? new string(currentChannel.Modes.Keys.Order().ToArray())
                    : string.Empty;
                var channelField = FormatStatusChannelField(buffer.Name, modes);
                var channelFieldIndex = fields.FindIndex(field => field == buffer.Name);
                if (channelFieldIndex >= 0) fields[channelFieldIndex] = channelField;
                fields.Insert(2, displayedNickname);

                if (_preferences.StatusTotals)
                {
                    var members = currentChannel.Members;
                    var ranked = new List<string> { $"tot:{members.Count}" };
                    var rankedUsers = 0;
                    foreach (var (mode, symbol) in session.Features.PrefixModes)
                    {
                        var count = members.Count(member => session.Features.HighestPrefix(member.PrefixModes) == symbol);
                        rankedUsers += count;
                        if (mode != 'Y')
                        {
                            ranked.Add($"{PrefixCountLabel(mode, symbol)}:{count}");
                        }
                    }
                    ranked.Add($"non:{Math.Max(0, members.Count - rankedUsers)}");
                    fields.Add(string.Join(' ', ranked));
                }
            }
            else
            {
                fields.Insert(2, displayedNickname);
            }
        }

        StatusWarning? warning = null;
        if (_preferences.LagMeter)
        {
            var lagDisplay = LagMeterFormatter.Format(session?.Lag ?? default);
            fields[0] += $"[Lag:{lagDisplay.Text}]";
            if (lagDisplay.Severity != LagMeterSeverity.Normal)
            {
                warning = new StatusWarning(
                    fields[0].Length - 4,
                    3,
                    lagDisplay.Severity);
            }
        }

        var chromeState = _windowStates.ChromeState(buffer?.Id);
        if (chromeState.ScrollOffset > 0)
        {
            fields.Add($"scroll:{chromeState.ScrollOffset}");
        }
        var activity = chromeState.Activity
            .Select(entry => new StatusActivity(
                entry.Number,
                entry.Kinds.OrderByDescending(ActivityPriority).First()))
            .OrderBy(entry => entry.Number)
            .ToArray();
        return new StatusBarModel(fields, activity, warning);
    }

    internal static string FormatStatusNetworkField(int number, string networkName, bool upstreamTls) =>
        $"({number}) {networkName}{(upstreamTls ? "[TLS]" : string.Empty)}";

    internal static string FormatStatusChannelField(string channel, string modes) =>
        modes.Length == 0 ? channel : $"{channel}(+{modes})";

    // Removing support for ZNC for now since its CAP ACK enabling server-time carries
    // a time tag (violating spec). I'll add it back after an upstream fix happens, or,
    // alternatively, the spec is changed to allow the tag in the ACK. Keeping internal
    // detection for TLS-hop reporting and profile handling.
    internal static string? BouncerDisplayName(string? bouncerName) =>
        bouncerName?.Equals("ZNC", StringComparison.OrdinalIgnoreCase) == true
            ? null
            : bouncerName;

    internal static string FormatStatusServerField(string serverName, string? bouncerName, bool clientTransportTls)
    {
        bouncerName = BouncerDisplayName(bouncerName);
        if (string.IsNullOrWhiteSpace(bouncerName))
        {
            return serverName;
        }
        return $"{serverName} [{bouncerName}{(clientTransportTls ? "/TLS" : string.Empty)}]";
    }

    private string ConnectionStatusLabel(IrcNetworkSession session)
    {
        if (_liveSessions.IsReconnecting(session.State.Id)) return "reconnecting";
        return session.ConnectionState switch
        {
            IrcConnectionState.Connecting => "connecting",
            IrcConnectionState.Registering => "registering",
            IrcConnectionState.Disconnecting => "disconnecting",
            IrcConnectionState.Online => "online",
            _ => "offline"
        };
    }

    private IrcNetworkSession[] SessionsSnapshot()
    {
        return [.. _liveSessions.SessionSnapshot()
            .OrderBy(session => session.State.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    private IrcNetworkSession? FindSession(NetworkSessionId id)
    {
        return _liveSessions.Find(id);
    }

    private LiveNetworkSession? RuntimeFor(IrcNetworkSession session) => RuntimeFor(session.State.Id);

    private CancellationToken SessionWorkToken(IrcNetworkSession session) =>
        RuntimeFor(session)?.Work.Token ?? _lifetime.Token;

    private LiveNetworkSession? RuntimeFor(NetworkSessionId id)
    {
        return _liveSessions.Runtime(id);
    }

    private NetworkProfileId? ProfileIdFor(IrcNetworkSession session)
    {
        return _liveSessions.ProfileId(session.State.Id);
    }

    private NetworkProfile? ProfileFor(IrcNetworkSession session) =>
        ProfileIdFor(session) is { } id ? _profileStore.Find(id) : null;

    private IrcIdentity CurrentIdentity() =>
        new([_preferences.Nickname, _preferences.AlternateNickname], _preferences.Username, _preferences.RealName);

    private NetworkProfile? FindInferredProfileFor(IrcNetworkSession session) =>
        FindInferredProfile(
            _profileStore.Entries,
            session.Options.Endpoint,
            session.Features.NetworkName,
            session.State.BouncerName is not null);

    internal static NetworkProfile? FindInferredProfile(
        IReadOnlyList<NetworkProfile> profiles,
        IrcEndpoint endpoint,
        string? advertisedNetwork,
        bool bouncerDetected)
    {
        // One bouncer endpoint may attach to several upstream networks, so its
        // endpoint and advertised network cannot identify a saved profile safely.
        if (bouncerDetected)
        {
            return null;
        }

        var endpointMatches = profiles.Where(profile => profile.Endpoints.Any(candidate =>
            candidate.Port == endpoint.Port &&
            candidate.UseTls == endpoint.UseTls &&
            candidate.Host.Equals(
                endpoint.Host,
                StringComparison.OrdinalIgnoreCase))).ToArray();
        if (endpointMatches.Length == 1)
        {
            return endpointMatches[0];
        }

        if (string.IsNullOrWhiteSpace(advertisedNetwork))
        {
            return null;
        }

        var networkMatches = profiles.Where(profile =>
            profile.NetworkName?.Equals(
                advertisedNetwork,
                StringComparison.OrdinalIgnoreCase) == true).ToArray();
        return networkMatches.Length == 1
            ? networkMatches[0]
            : null;
    }

    private NetworkProfile EnsureProfileFor(IrcNetworkSession session, out bool created)
    {
        var associated = ProfileFor(session);
        if (associated is not null)
        {
            created = false;
            return associated;
        }

        var inferred = FindInferredProfileFor(session);
        if (inferred is not null)
        {
            var matched = UpdateProfileFromSession(inferred, session);
            AssociateProfile(session, matched);
            created = false;
            return matched;
        }

        var advertisedNetwork = session.Features.NetworkName;
        var profileNames = _profileStore.Entries.Select(profile => profile.DisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var profileName = string.IsNullOrWhiteSpace(advertisedNetwork)
            ? session.State.DisplayName
            : advertisedNetwork;
        if (profileNames.Contains(profileName))
        {
            for (var suffix = 2; ; suffix++)
            {
                var candidate = $"{profileName}#{suffix}";
                if (!profileNames.Contains(candidate))
                {
                    profileName = candidate;
                    break;
                }
            }
        }

        var nicknames = new[] { session.CurrentNickname }
            .Concat(session.Options.Identity.Nicknames)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var profile = new NetworkProfile(
            NetworkProfileId.New(),
            profileName,
            session.State.BouncerName is null ? [session.Options.Endpoint] : [],
            new IrcIdentity(nicknames, session.Options.Identity.Username, session.Options.Identity.RealName),
            networkName: advertisedNetwork);
        _profileStore.Add(profile);
        AssociateProfile(session, profile);
        created = true;
        return profile;
    }

    private NetworkProfile UpdateProfileFromSession(NetworkProfile profile, IrcNetworkSession session)
    {
        // Temporary direct connections can teach a logical network profile a server endpoint.
        // Bouncer endpoints are retained only when explicitly configured on their own profile.
        var updated = session.State.BouncerName is null
            ? profile.WithEndpoint(session.Options.Endpoint)
            : profile;
        var advertisedNetwork = session.Features.NetworkName;
        if (profile.NetworkName is null && !string.IsNullOrWhiteSpace(advertisedNetwork))
        {
            updated = updated.WithNetworkName(advertisedNetwork);
        }

        if (!ReferenceEquals(updated, profile))
        {
            _profileStore.Replace(updated);
        }

        return updated;
    }

    private void AssociateProfile(IrcNetworkSession session, NetworkProfile profile)
    {
        _liveSessions.AssociateProfile(session.State.Id, profile.Id);
        RememberRecentConnection(ConnectionRouteFor(session), profile.Id);
    }

    private void RememberRecentConnection(IrcConnectionOptions options, NetworkProfileId? profileId) =>
        Volatile.Write(ref _recentConnection, new RecentConnection(options, profileId));

    private RecentConnection? RecentConnectionSnapshot() => Volatile.Read(ref _recentConnection);

    private static IrcNetworkSession? FindSession(string selector, IReadOnlyList<IrcNetworkSession> sessions)
    {
        if (int.TryParse(selector, out var number) && number >= 1 && number <= sessions.Count)
        {
            return sessions[number - 1];
        }

        return sessions.FirstOrDefault(session =>
            session.State.DisplayName.Equals(selector, StringComparison.OrdinalIgnoreCase) ||
            session.State.Id.ToString().StartsWith(selector, StringComparison.OrdinalIgnoreCase));
    }

    private (IrcNetworkSession? Session, BufferState Buffer)? FindBuffer(string selector)
    {
        var separator = selector.IndexOf('/');
        if (separator > 0)
        {
            var sessions = SessionsSnapshot();
            var session = FindSession(selector[..separator], sessions);
            if (session is not null &&
                session.State.TryGetBuffer(selector[(separator + 1)..], out var qualified))
            {
                return (session, qualified!);
            }

            return null;
        }

        var active = ActiveSession();
        if (active is not null &&
            active.State.TryGetBuffer(selector, out var buffer))
        {
            return (active, buffer!);
        }

        var local = _scriptWindows.Find(selector, null);
        return local is null ? null : ((IrcNetworkSession?)null, local);
    }

    private string UniqueDisplayName(string requested)
    {
        var existing = SessionsSnapshot().Select(session => session.State.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(requested))
        {
            return requested;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{requested}#{suffix}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private void SelectFallbackSessionUnsafe()
    {
        var fallback = _liveSessions.SessionSnapshot()
            .OrderBy(session => session.State.DisplayName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (fallback is null)
        {
            _windowStates.ActivateLocal(_localStatusBuffer.Id);
            return;
        }
        _windowStates.Activate(fallback.State.Id, fallback.State.StatusBuffer.Id);
    }

    private void SelectAfterSessionCloseUnsafe(IrcNetworkSession closingSession)
    {
        var closingNumber = _windowStates.ActiveBufferId is { } active
            ? _windowStates.NumberOr(active, int.MaxValue)
            : int.MaxValue;
        var candidates = OrderedBuffers()
            .Where(entry => entry.Session?.State.Id != closingSession.State.Id)
            .Select(entry => (
                entry.Session,
                entry.Buffer,
                Number: BufferNumber(entry.Buffer.Id)))
            .ToArray();
        var selected = candidates
            .Where(candidate => candidate.Number < closingNumber)
            .OrderByDescending(candidate => candidate.Number)
            .FirstOrDefault();

        if (selected.Buffer is null)
        {
            selected = candidates.OrderBy(candidate => candidate.Number).FirstOrDefault();
        }

        if (selected.Buffer is not null)
        {
            ActivateWindowUnsafe(selected.Session, selected.Buffer);
        }
        else
        {
            _windowStates.ActivateLocal(_localStatusBuffer.Id);
        }
    }

    private static readonly ProtectionDetector[] ChannelProtectionDetectors =
    [
        ProtectionDetector.Text, ProtectionDetector.Repeat, ProtectionDetector.Join, ProtectionDetector.Nick,
        ProtectionDetector.MassKick, ProtectionDetector.MassDeop, ProtectionDetector.Caps,
        ProtectionDetector.Controls, ProtectionDetector.ChannelCtcp
    ];

    private static readonly ProtectionDetector[] PersonalProtectionDetectors =
    [
        ProtectionDetector.PrivateMessage, ProtectionDetector.PrivateNotice, ProtectionDetector.Ctcp,
        ProtectionDetector.Invite
    ];

}
