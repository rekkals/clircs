using Clircs.Identity;
using Clircs.Protocol;

namespace Clircs.State;

public sealed class NetworkSessionState
{
    private readonly object _gate = new();
    private readonly HashSet<char> _userModes = [];
    private Dictionary<string, BufferState> _buffers;
    private Dictionary<string, ChannelState> _channels;

    public NetworkSessionState(NetworkSessionId id, string displayName, IrcCaseMapping caseMapping)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        Id = id;
        DisplayName = displayName;
        CaseMapping = caseMapping;
        _buffers = new Dictionary<string, BufferState>(new IrcNameComparer(caseMapping));
        _channels = new Dictionary<string, ChannelState>(new IrcNameComparer(caseMapping));
        StatusBuffer = CreateBuffer(BufferKind.Status, "*");
    }

    public NetworkSessionId Id { get; }

    public string DisplayName { get; }

    public IrcCaseMapping CaseMapping { get; private set; }

    public BufferState StatusBuffer { get; }

    public bool IsAway { get; private set; }

    public string? ServerName { get; private set; }

    public string? AccountName { get; private set; }

    public string? VisibleHost { get; private set; }

    public string? BouncerName { get; private set; }

    public bool ClientTransportTls { get; private set; }

    public bool? UpstreamTls { get; private set; }

    public string UserModes
    {
        get
        {
            lock (_gate)
            {
                return _userModes.Count == 0
                    ? string.Empty
                    : "+" + new string(_userModes.Order().ToArray());
            }
        }
    }

    public IReadOnlyCollection<BufferState> Buffers
    {
        get
        {
            lock (_gate)
            {
                return _buffers.Values.ToArray();
            }
        }
    }

    public BufferState GetOrCreateBuffer(BufferKind kind, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            if (_buffers.TryGetValue(name, out var existing))
            {
                if (existing.Kind != kind)
                {
                    throw new InvalidOperationException($"Buffer '{name}' already exists as {existing.Kind}.");
                }

                return existing;
            }

            return CreateBuffer(kind, name);
        }
    }

    public IReadOnlyCollection<ChannelState> Channels
    {
        get
        {
            lock (_gate)
            {
                return _channels.Values.ToArray();
            }
        }
    }

    public ChannelState GetOrCreateChannel(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            if (_channels.TryGetValue(name, out var existing))
            {
                return existing;
            }

            var channel = new ChannelState(name, CaseMapping);
            _channels.Add(name, channel);
            return channel;
        }
    }

    public bool TryGetChannel(string name, out ChannelState? channel)
    {
        lock (_gate)
        {
            return _channels.TryGetValue(name, out channel);
        }
    }

    public bool RemoveChannel(string name)
    {
        lock (_gate)
        {
            return _channels.Remove(name);
        }
    }

    public bool TryGetBuffer(string name, out BufferState? buffer)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            return _buffers.TryGetValue(name, out buffer);
        }
    }

    public bool TryGetBuffer(BufferId id, out BufferState? buffer)
    {
        lock (_gate)
        {
            buffer = _buffers.Values.FirstOrDefault(candidate => candidate.Id == id);
            return buffer is not null;
        }
    }

    public bool RemoveBuffer(BufferId id)
    {
        lock (_gate)
        {
            if (id == StatusBuffer.Id)
            {
                return false;
            }

            var match = _buffers.FirstOrDefault(entry => entry.Value.Id == id);
            return match.Value is not null && _buffers.Remove(match.Key);
        }
    }

    public void UpdateCaseMapping(IrcCaseMapping caseMapping)
    {
        lock (_gate)
        {
            if (caseMapping == CaseMapping)
            {
                return;
            }

            var reindexed = new Dictionary<string, BufferState>(new IrcNameComparer(caseMapping));
            foreach (var buffer in _buffers.Values)
            {
                if (!reindexed.TryAdd(buffer.Name, buffer))
                {
                    throw new InvalidOperationException(
                        $"Changing CASEMAPPING to {caseMapping} would merge distinct buffers including '{buffer.Name}'.");
                }
            }

            var reindexedChannels = new Dictionary<string, ChannelState>(new IrcNameComparer(caseMapping));
            foreach (var channel in _channels.Values)
            {
                channel.UpdateCaseMapping(caseMapping);
                if (!reindexedChannels.TryAdd(channel.Name, channel))
                {
                    throw new InvalidOperationException(
                        $"Changing CASEMAPPING to {caseMapping} would merge channels including '{channel.Name}'.");
                }
            }

            _buffers = reindexed;
            _channels = reindexedChannels;
            CaseMapping = caseMapping;
        }
    }

    internal void ApplyUserModes(string modes, bool reset = false)
    {
        ArgumentNullException.ThrowIfNull(modes);
        lock (_gate)
        {
            if (reset) _userModes.Clear();
            var adding = true;
            foreach (var mode in modes)
            {
                if (mode == '+')
                {
                    adding = true;
                }
                else if (mode == '-')
                {
                    adding = false;
                }
                else if (char.IsLetter(mode))
                {
                    if (adding) _userModes.Add(mode);
                    else _userModes.Remove(mode);
                }
            }
        }
    }

    internal void ResetForReconnect(bool clientTransportTls)
    {
        lock (_gate)
        {
            var knownBouncer = BouncerName;
            _channels.Clear();
            _userModes.Clear();
            IsAway = false;
            ServerName = null;
            AccountName = null;
            VisibleHost = null;
            BouncerName = knownBouncer;
            ClientTransportTls = clientTransportTls;
            UpstreamTls = knownBouncer is null ? clientTransportTls : null;
        }
    }

    internal void SetAway(bool away)
    {
        lock (_gate)
        {
            IsAway = away;
        }
    }

    internal void SetAccountName(string? accountName)
    {
        lock (_gate)
        {
            AccountName = string.IsNullOrWhiteSpace(accountName) ? null : accountName;
        }
    }

    internal void SetVisibleHost(string? visibleHost)
    {
        lock (_gate)
        {
            VisibleHost = string.IsNullOrWhiteSpace(visibleHost) ? null : visibleHost;
        }
    }

    internal void SetServerName(string? serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName)) return;
        lock (_gate)
        {
            ServerName = serverName.Trim();
        }
    }

    internal void SetBouncer(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            if (BouncerName is null)
            {
                UpstreamTls = null;
            }
            BouncerName = name.Trim();
        }
    }

    internal void SetUpstreamTls(bool secure)
    {
        lock (_gate)
        {
            UpstreamTls = secure;
        }
    }

    private BufferState CreateBuffer(BufferKind kind, string name)
    {
        var buffer = new BufferState(BufferId.New(), Id, kind, name);
        _buffers.Add(name, buffer);
        return buffer;
    }
}
