using Clircs.Identity;
using Clircs.Sessions;
using Clircs.State;

namespace Clircs.ConsoleClient;

/// <summary>
/// Owns synchronized terminal-window state, including active selection, numbering,
/// unread activity, scroll position, and retained presentation history.
/// </summary>
internal sealed class WindowStateRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<BufferId, WindowState> _windows = [];
    private readonly List<NetworkSessionId> _connectionOrder = [];
    private int _nextNumber = 1;
    private NetworkSessionId? _activeSessionId;
    private BufferId? _activeBufferId;

    public NetworkSessionId? ActiveSessionId { get { lock (_gate) return _activeSessionId; } }
    public BufferId? ActiveBufferId { get { lock (_gate) return _activeBufferId; } }

    public (NetworkSessionId? SessionId, BufferId? BufferId) ActiveLocation()
    {
        lock (_gate) return (_activeSessionId, _activeBufferId);
    }

    public bool IsActiveSession(NetworkSessionId sessionId)
    {
        lock (_gate) return _activeSessionId == sessionId;
    }

    public bool IsActiveBuffer(BufferId bufferId)
    {
        lock (_gate) return _activeBufferId == bufferId;
    }

    public bool IsActive(NetworkSessionId sessionId, BufferId bufferId)
    {
        lock (_gate) return _activeSessionId == sessionId && _activeBufferId == bufferId;
    }

    public BufferId? ActiveBufferFor(NetworkSessionId sessionId)
    {
        lock (_gate) return _activeSessionId == sessionId ? _activeBufferId : null;
    }

    public IrcNetworkSession? ResolveSession(
        LiveNetworkSessionRegistry sessions,
        NetworkSessionId? requestedSessionId = null)
    {
        var sessionId = requestedSessionId ?? ActiveSessionId;
        return sessionId is { } id ? sessions.Find(id) : null;
    }

    public BufferState? ResolveBuffer(
        LiveNetworkSessionRegistry sessions,
        NetworkSessionId? requestedSessionId = null,
        BufferId? requestedBufferId = null)
    {
        var location = ActiveLocation();
        var sessionId = requestedSessionId ?? location.SessionId;
        var bufferId = requestedBufferId ?? location.BufferId;
        var session = sessionId is { } id ? sessions.Find(id) : null;
        return session is not null && bufferId is { } selected && session.State.TryGetBuffer(selected, out var buffer)
            ? buffer
            : null;
    }

    public (IrcNetworkSession? Session, BufferState? Buffer) ResolveActive(
        LiveNetworkSessionRegistry sessions)
    {
        var location = ActiveLocation();
        var session = location.SessionId is { } sessionId ? sessions.Find(sessionId) : null;
        var buffer = session is not null && location.BufferId is { } bufferId &&
            session.State.TryGetBuffer(bufferId, out var foundBuffer)
                ? foundBuffer
                : null;
        return (session, buffer);
    }

    public WindowViewportSnapshot ViewportSnapshot(
        BufferId bufferId,
        int? bottomEntryLimit = null,
        bool dateChange = true)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(bufferId, out var state)) return new WindowViewportSnapshot([], 0);
            if (state.ScrollOffset == 0 && bottomEntryLimit is { } limit)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
                var start = state.History.Count;
                var visibleCount = 0;
                while (start > 0 && visibleCount < limit)
                {
                    start--;
                    if (!DateChangeSeparators.IsSeparator(state.History[start]) ||
                        DateChangeSeparators.IsVisible(
                            state.History[start],
                            DateChangeSeparators.PreviousTimestamp(state.History, start),
                            dateChange))
                    {
                        visibleCount++;
                    }
                }

                var count = state.History.Count - start;
                var history = new SessionEvent[count];
                for (var index = 0; index < count; index++) history[index] = state.History[start + index];
                var previousTimestamp =
                    DateChangeSeparators.PreviousTimestamp(state.History, start);
                return new WindowViewportSnapshot(history, 0, previousTimestamp);
            }
            return new WindowViewportSnapshot([.. state.History], state.ScrollOffset);
        }
    }

    public SessionEvent[] HistorySnapshot(BufferId bufferId)
    {
        lock (_gate)
            return _windows.TryGetValue(bufferId, out var state) ? [.. state.History] : [];
    }

    public bool HistoryIsEmpty(BufferId bufferId)
    {
        lock (_gate)
            return !_windows.TryGetValue(bufferId, out var state) || state.History.Count == 0;
    }

    public void ReplaceHistory(BufferId bufferId, IEnumerable<SessionEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        lock (_gate)
        {
            var state = EnsureUnsafe(bufferId);
            state.History.Clear();
            state.History.AddRange(history);
        }
    }

    public void AppendHistory(SessionEvent sessionEvent)
    {
        lock (_gate) EnsureUnsafe(sessionEvent.BufferId).History.Add(sessionEvent);
    }

    public WindowEventStoreResult StoreDateChange(
        SessionEvent separator,
        int incomingRows,
        Func<SessionEvent, int> measureRows)
    {
        ArgumentNullException.ThrowIfNull(separator);
        ArgumentOutOfRangeException.ThrowIfNegative(incomingRows);
        ArgumentNullException.ThrowIfNull(measureRows);
        if (!DateChangeSeparators.IsSeparator(separator))
        {
            throw new ArgumentException("A date separator is required.", nameof(separator));
        }

        lock (_gate)
        {
            if (!_windows.TryGetValue(separator.BufferId, out var state) ||
                DateChangeSeparators.PreviousTimestamp(
                    state.History, state.History.Count)?.Date == separator.Timestamp.Date)
            {
                return new WindowEventStoreResult(false, false, false, false);
            }

            return StoreEvent(
                separator,
                incomingRows,
                measureRows,
                isReplay: false,
                trackUnread: false,
                assignNumber: false,
                dateChange: true);
        }
    }

    public WindowEventStoreResult StoreEvent(
        SessionEvent sessionEvent,
        int incomingRows,
        Func<SessionEvent, int> measureRows,
        bool isReplay,
        bool trackUnread,
        bool assignNumber = true,
        bool dateChange = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(incomingRows);
        ArgumentNullException.ThrowIfNull(measureRows);
        lock (_gate)
        {
            var state = EnsureUnsafe(sessionEvent.BufferId);
            var historyUpdate = ViewportHistory.StoreEvent(state.History, sessionEvent);
            if (!historyUpdate.Stored) return new WindowEventStoreResult(false, false, false, false);

            var previousTimestamp =
                DateChangeSeparators.PreviousTimestamp(state.History, historyUpdate.Index);
            var separator = dateChange
                ? DateChangeSeparators.Before(sessionEvent, previousTimestamp)
                : null;
            if (separator is not null) incomingRows += measureRows(separator);

            var replacedRows = historyUpdate.Previous is null ? 0 : measureRows(historyUpdate.Previous);
            if (dateChange && historyUpdate.Previous is { } previous)
            {
                if (DateChangeSeparators.Before(previous, previousTimestamp) is { } oldSeparator)
                {
                    replacedRows += measureRows(oldSeparator);
                }

                var oldTimestamp = DateChangeSeparators.TrackingTimestamp(previous) ?? previousTimestamp;
                var newTimestamp = DateChangeSeparators.TrackingTimestamp(sessionEvent) ?? previousTimestamp;

                // Replacing an entry can change the date boundary on either side.
                var followingIndex = historyUpdate.Index + 1;
                while (followingIndex < state.History.Count &&
                       DateChangeSeparators.TrackingTimestamp(state.History[followingIndex]) is null)
                {
                    followingIndex++;
                }

                if (followingIndex < state.History.Count)
                {
                    var following = state.History[followingIndex];
                    if (DateChangeSeparators.IsSeparator(following))
                    {
                        if (DateChangeSeparators.IsVisible(following, oldTimestamp))
                        {
                            replacedRows += measureRows(following);
                        }
                        if (DateChangeSeparators.IsVisible(following, newTimestamp))
                        {
                            incomingRows += measureRows(following);
                        }
                    }
                    if (DateChangeSeparators.Before(following, oldTimestamp) is { } oldFollowingSeparator)
                    {
                        replacedRows += measureRows(oldFollowingSeparator);
                    }
                    if (DateChangeSeparators.Before(following, newTimestamp) is { } newFollowingSeparator)
                    {
                        incomingRows += measureRows(newFollowingSeparator);
                    }
                }
            }

            var isActive = _activeBufferId == sessionEvent.BufferId;
            if (isActive && state.ScrollOffset > 0)
            {
                state.ScrollOffset = Math.Max(0, state.ScrollOffset +
                    (historyUpdate.Replaced ? incomingRows - replacedRows : incomingRows));
            }

            ScrollbackRetention.EnforceRetentionLimit(state.History);
            EnforceTotalHistoryLimitUnsafe(
                ScrollbackRetention.MaximumTotalEntries,
                ScrollbackRetention.MinimumEntries);

            var emergencyLimitReached = ScrollbackRetention.EnforceEmergencyLimit(state.History);
            var totalEmergencyLimitReached = EnforceTotalHistoryLimitUnsafe(
                ScrollbackRetention.EmergencyMaximumTotalEntries,
                ScrollbackRetention.MinimumEntries);
            if (assignNumber && state.Number is null)
            {
                RegisterWindow(sessionEvent.BufferId, sessionEvent.NetworkSessionId);
            }

            var suppressActivity = sessionEvent.Fields?.GetValueOrDefault("suppressActivity") == "true";
            if (trackUnread && !isActive && !isReplay && !suppressActivity)
                state.Unread.Add(new WindowActivityKind(
                    sessionEvent.Kind,
                    SessionEventPresentation.From(sessionEvent).Subtype));

            return new WindowEventStoreResult(
                true,
                historyUpdate.Replaced,
                isActive,
                isActive && state.ScrollOffset > 0,
                emergencyLimitReached,
                totalEmergencyLimitReached,
                historyUpdate.Replaced ? null : separator);
        }
    }

    public bool EnforceTotalHistoryLimit(
        int maximumEntries = ScrollbackRetention.EmergencyMaximumTotalEntries,
        int minimumEntriesPerWindow = ScrollbackRetention.MinimumEntries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumEntriesPerWindow);
        lock (_gate) return EnforceTotalHistoryLimitUnsafe(maximumEntries, minimumEntriesPerWindow);
    }

    private bool EnforceTotalHistoryLimitUnsafe(int maximumEntries, int minimumEntriesPerWindow)
    {
        var excess = _windows.Values.Sum(state => (long)state.History.Count) - maximumEntries;
        if (excess <= 0) return false;

        while (excess > 0)
        {
            var candidate = _windows.Values
                .Where(state => state.History.Count > minimumEntriesPerWindow)
                .MinBy(state => state.History[0].ReceivedAt);
            if (candidate is null) break;
            var remove = (int)Math.Min(excess, candidate.History.Count - minimumEntriesPerWindow);
            candidate.History.RemoveFirst(remove);
            excess -= remove;
        }
        return true;
    }

    public int ScrollOffset(BufferId bufferId)
    {
        lock (_gate)
            return _windows.TryGetValue(bufferId, out var state) ? state.ScrollOffset : 0;
    }

    public void SetScrollOffset(BufferId bufferId, int offset)
    {
        lock (_gate) EnsureUnsafe(bufferId).ScrollOffset = Math.Max(0, offset);
    }

    public bool SetScrollOffsetIfActive(BufferId bufferId, int offset)
    {
        lock (_gate)
        {
            if (_activeBufferId != bufferId) return false;
            EnsureUnsafe(bufferId).ScrollOffset = Math.Max(0, offset);
            return true;
        }
    }

    public void RegisterConnection(
        NetworkSessionId sessionId,
        BufferId statusBufferId,
        int? position = null)
    {
        lock (_gate)
        {
            if (!_connectionOrder.Contains(sessionId))
            {
                var index = Math.Clamp(
                    position ?? _connectionOrder.Count, 0, _connectionOrder.Count);
                _connectionOrder.Insert(index, sessionId);
            }

            var state = EnsureUnsafe(statusBufferId);
            state.SessionId = sessionId;
            state.IsStatusWindow = true;
            AssignNumberUnsafe(statusBufferId);
            RenumberUnsafe();
        }
    }

    public int? ConnectionPosition(NetworkSessionId sessionId)
    {
        lock (_gate)
        {
            var index = _connectionOrder.IndexOf(sessionId);
            return index >= 0 ? index : null;
        }
    }

    public int RegisterWindow(BufferId bufferId, NetworkSessionId? sessionId)
    {
        lock (_gate)
        {
            var state = EnsureUnsafe(bufferId);
            if (state.Number is { } existing && state.SessionId == sessionId)
            {
                return existing;
            }

            if (sessionId is { } connection && !_connectionOrder.Contains(connection))
            {
                _connectionOrder.Add(connection);
            }

            state.SessionId = sessionId;
            AssignNumberUnsafe(bufferId);
            RenumberUnsafe();
            return state.Number!.Value;
        }
    }

    public int AssignNumber(BufferId bufferId)
    {
        lock (_gate) return AssignNumberUnsafe(bufferId);
    }

    public int NumberOr(BufferId bufferId, int fallback)
    {
        lock (_gate)
            return _windows.TryGetValue(bufferId, out var state) && state.Number is { } number ? number : fallback;
    }

    public bool HasNumber(BufferId bufferId)
    {
        lock (_gate)
            return _windows.TryGetValue(bufferId, out var state) && state.Number is not null;
    }

    public bool IsUnread(BufferId bufferId)
    {
        lock (_gate)
            return _windows.TryGetValue(bufferId, out var state) && state.Unread.Count > 0;
    }

    public void MarkUnread(BufferId bufferId, SessionEventKind kind)
    {
        lock (_gate)
        {
            EnsureUnsafe(bufferId).Unread.Add(
                new WindowActivityKind(kind, SessionEventSubtype.None));
        }
    }

    public void ClearActivity()
    {
        lock (_gate)
        {
            foreach (var state in _windows.Values)
            {
                state.Unread.Clear();
            }
        }
    }

    public SessionEventKind[] UnreadKinds(BufferId bufferId)
    {
        lock (_gate)
        {
            return _windows.TryGetValue(bufferId, out var state)
                ? [.. state.Unread.Select(activity => activity.Kind).Distinct()]
                : [];
        }
    }

    public WindowActivityKind[] UnreadActivities(BufferId bufferId)
    {
        lock (_gate)
        {
            return _windows.TryGetValue(bufferId, out var state)
                ? [.. state.Unread]
                : [];
        }
    }

    public WindowChromeState ChromeState(BufferId? bufferId)
    {
        lock (_gate)
        {
            var offset = bufferId is { } id && _windows.TryGetValue(id, out var state)
                ? state.ScrollOffset
                : 0;
            var activity = _windows
                .Where(entry => entry.Value.Number is not null && entry.Value.Unread.Count > 0)
                .Select(entry => new WindowActivity(
                    entry.Key,
                    entry.Value.Number!.Value,
                    [.. entry.Value.Unread.Select(activity => activity.Kind).Distinct()]))
                .ToArray();
            return new WindowChromeState(offset, activity);
        }
    }

    public void ActivateLocal(BufferId bufferId)
    {
        lock (_gate)
        {
            _activeSessionId = null;
            _activeBufferId = bufferId;
            ActivateUnsafe(bufferId);
        }
    }

    public void Activate(NetworkSessionId sessionId, BufferId bufferId)
    {
        lock (_gate)
        {
            RegisterWindow(bufferId, sessionId);
            _activeSessionId = sessionId;
            _activeBufferId = bufferId;
            ActivateUnsafe(bufferId);
        }
    }

    public void ClearActive()
    {
        lock (_gate) ClearActiveUnsafe();
    }

    public void Remove(BufferId bufferId)
    {
        lock (_gate) RemoveWindowUnsafe(bufferId);
    }

    public bool TryRemoveInactiveEmpty(BufferId bufferId)
    {
        lock (_gate)
        {
            if (_activeBufferId == bufferId ||
                !_windows.TryGetValue(bufferId, out var state) ||
                state.History.Count != 0)
            {
                return false;
            }

            RemoveWindowUnsafe(bufferId);
            return true;
        }
    }

    private void RemoveWindowUnsafe(BufferId bufferId)
    {
        if (!_windows.Remove(bufferId, out var removed)) return;

        if (removed.SessionId is { } sessionId &&
            !_windows.Values.Any(state => state.SessionId == sessionId))
        {
            _connectionOrder.Remove(sessionId);
        }

        if (removed.Number is not null) RenumberUnsafe();
        if (_activeBufferId == bufferId) ClearActiveUnsafe();
    }

    private void RenumberUnsafe()
    {
        var numbered = _windows.Values
            .Where(state => state.Number is not null)
            .OrderBy(state => state.SessionId is { } sessionId
                ? _connectionOrder.IndexOf(sessionId)
                : int.MaxValue)
            .ThenBy(state => state.IsStatusWindow ? 0 : 1)
            .ThenBy(state => state.Number)
            .ToArray();

        for (var index = 0; index < numbered.Length; index++)
        {
            numbered[index].Number = index + 1;
        }

        _nextNumber = numbered.Length + 1;
    }

    private int AssignNumberUnsafe(BufferId bufferId)
    {
        var state = EnsureUnsafe(bufferId);
        if (state.Number is { } existing) return existing;
        state.Number = _nextNumber++;
        return state.Number.Value;
    }

    private void ActivateUnsafe(BufferId bufferId)
    {
        var state = EnsureUnsafe(bufferId);
        state.Unread.Clear();
        state.ScrollOffset = 0;
    }

    private void ClearActiveUnsafe()
    {
        _activeSessionId = null;
        _activeBufferId = null;
    }

    private WindowState EnsureUnsafe(BufferId bufferId)
    {
        if (_windows.TryGetValue(bufferId, out var state)) return state;
        state = new WindowState();
        _windows.Add(bufferId, state);
        return state;
    }

    internal readonly record struct WindowActivityKind(
        SessionEventKind Kind,
        SessionEventSubtype Subtype);

    internal sealed record WindowActivity(BufferId BufferId, int Number, SessionEventKind[] Kinds);
    internal sealed record WindowChromeState(int ScrollOffset, WindowActivity[] Activity);
    internal sealed record WindowViewportSnapshot(
        SessionEvent[] History,
        int ScrollOffset,
        DateTimeOffset? PreviousTimestamp = null);

    internal readonly record struct WindowEventStoreResult(
        bool Stored,
        bool Replaced,
        bool IsActive,
        bool IsScrolled,
        bool EmergencyLimitReached = false,
        bool TotalEmergencyLimitReached = false,
        SessionEvent? DateChange = null);

    private sealed class WindowState
    {
        public WindowEventHistory History { get; } = new();
        public HashSet<WindowActivityKind> Unread { get; } = [];
        public NetworkSessionId? SessionId { get; set; }
        public bool IsStatusWindow { get; set; }
        public int? Number { get; set; }
        public int ScrollOffset { get; set; }
    }
}
