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

    public WindowViewportSnapshot ViewportSnapshot(BufferId bufferId, int? bottomEntryLimit = null)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(bufferId, out var state)) return new WindowViewportSnapshot([], 0);
            if (state.ScrollOffset == 0 && bottomEntryLimit is { } limit)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
                var count = Math.Min(state.History.Count, limit);
                var history = new SessionEvent[count];
                var start = state.History.Count - count;
                for (var index = 0; index < count; index++) history[index] = state.History[start + index];
                return new WindowViewportSnapshot(history, 0);
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

    public WindowEventStoreResult StoreEvent(
        SessionEvent sessionEvent,
        int incomingRows,
        Func<SessionEvent, int> measureRows,
        bool isReplay,
        bool trackUnread,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(incomingRows);
        ArgumentNullException.ThrowIfNull(measureRows);
        lock (_gate)
        {
            var state = EnsureUnsafe(sessionEvent.BufferId);
            var historyUpdate = ViewportHistory.StoreEvent(state.History, sessionEvent);
            if (!historyUpdate.Stored) return new WindowEventStoreResult(false, false, false, false);

            var replacedRows = historyUpdate.Previous is null ? 0 : measureRows(historyUpdate.Previous);
            var isActive = _activeBufferId == sessionEvent.BufferId;
            if (isActive && state.ScrollOffset > 0)
            {
                state.ScrollOffset = Math.Max(0, state.ScrollOffset +
                    (historyUpdate.Replaced ? incomingRows - replacedRows : incomingRows));
            }

            ScrollbackRetention.Trim(state.History, now);
            var emergencyLimitReached = ScrollbackRetention.EnforceEmergencyLimit(state.History);
            var totalEmergencyLimitReached = EnforceTotalHistoryLimitUnsafe(
                ScrollbackRetention.EmergencyMaximumTotalEntries,
                ScrollbackRetention.MinimumEntries);
            AssignNumberUnsafe(sessionEvent.BufferId);

            var suppressActivity = sessionEvent.Fields?.GetValueOrDefault("suppressActivity") == "true";
            if (trackUnread && !isActive && !isReplay && !suppressActivity)
                state.Unread.Add(sessionEvent.Kind);

            return new WindowEventStoreResult(
                true,
                historyUpdate.Replaced,
                isActive,
                isActive && state.ScrollOffset > 0,
                emergencyLimitReached,
                totalEmergencyLimitReached);
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
                .MinBy(state => state.History[0].Timestamp);
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

    public int AssignNumber(BufferId bufferId)
    {
        lock (_gate) return AssignNumberUnsafe(bufferId);
    }

    public bool TryAssignPreferredNumber(BufferId bufferId, int number)
    {
        lock (_gate)
        {
            if (number < 1 || _windows.Values.Any(state => state.Number == number)) return false;
            var state = EnsureUnsafe(bufferId);
            if (state.Number is not null) return false;
            state.Number = number;
            _nextNumber = Math.Max(_nextNumber, number + 1);
            return true;
        }
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
        lock (_gate) EnsureUnsafe(bufferId).Unread.Add(kind);
    }

    public SessionEventKind[] UnreadKinds(BufferId bufferId)
    {
        lock (_gate)
            return _windows.TryGetValue(bufferId, out var state) ? [.. state.Unread] : [];
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
                .Select(entry => new WindowActivity(entry.Key, entry.Value.Number!.Value, [.. entry.Value.Unread]))
                .ToArray();
            return new WindowChromeState(offset, activity);
        }
    }

    public void Activate(BufferId bufferId)
    {
        lock (_gate) ActivateUnsafe(bufferId);
    }

    public void Activate(NetworkSessionId sessionId, BufferId bufferId)
    {
        lock (_gate)
        {
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
        lock (_gate)
        {
            _windows.Remove(bufferId);
            if (_activeBufferId == bufferId) ClearActiveUnsafe();
        }
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
            _windows.Remove(bufferId);
            return true;
        }
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
        AssignNumberUnsafe(bufferId);
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

    internal sealed record WindowActivity(BufferId BufferId, int Number, SessionEventKind[] Kinds);
    internal sealed record WindowChromeState(int ScrollOffset, WindowActivity[] Activity);
    internal sealed record WindowViewportSnapshot(SessionEvent[] History, int ScrollOffset);
    internal readonly record struct WindowEventStoreResult(
        bool Stored,
        bool Replaced,
        bool IsActive,
        bool IsScrolled,
        bool EmergencyLimitReached = false,
        bool TotalEmergencyLimitReached = false);

    private sealed class WindowState
    {
        public WindowEventHistory History { get; } = new();
        public HashSet<SessionEventKind> Unread { get; } = [];
        public int? Number { get; set; }
        public int ScrollOffset { get; set; }
    }
}
