using Clircs.Protocol;

namespace Clircs.State;

public sealed class ChannelState
{
    private readonly object _gate = new();
    private Dictionary<string, ChannelMemberState> _members;
    private readonly Dictionary<char, string?> _modes = [];
    private readonly Dictionary<char, List<ChannelListEntry>> _channelLists = [];
    private readonly HashSet<char> _synchronizedChannelLists = [];
    private readonly HashSet<char> _synchronizingChannelLists = [];

    internal ChannelState(string name, IrcCaseMapping caseMapping)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        _members = new Dictionary<string, ChannelMemberState>(new IrcNameComparer(caseMapping));
    }

    public string Name { get; }

    public bool NamesSynchronized { get; internal set; }

    public bool WhoSynchronized { get; internal set; }

    public bool BanListSynchronized => IsChannelListSynchronized('b');

    public string? Topic { get; internal set; }

    public string? TopicSetBy { get; internal set; }

    public DateTimeOffset? TopicSetAt { get; internal set; }

    public DateTimeOffset? CreatedAt { get; internal set; }

    public IReadOnlyCollection<ChannelMemberState> Members
    {
        get
        {
            lock (_gate)
            {
                return _members.Values.ToArray();
            }
        }
    }

    public IReadOnlyDictionary<char, string?> Modes
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<char, string?>(_modes);
            }
        }
    }

    public IReadOnlyCollection<string> Bans
    {
        get => ChannelList('b').Select(entry => entry.Mask).ToArray();
    }

    public IReadOnlyCollection<ChannelListEntry> ChannelList(char mode)
    {
        lock (_gate)
        {
            return _channelLists.TryGetValue(mode, out var entries)
                ? entries.ToArray()
                : [];
        }
    }

    public bool IsChannelListSynchronized(char mode)
    {
        lock (_gate)
        {
            return _synchronizedChannelLists.Contains(mode);
        }
    }

    internal bool IsChannelListSynchronizing(char mode)
    {
        lock (_gate)
        {
            return _synchronizingChannelLists.Contains(mode);
        }
    }

    public ChannelMemberState GetOrAddMember(
        string nickname,
        string? username = null,
        string? host = null,
        string? realName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);
        lock (_gate)
        {
            if (_members.TryGetValue(nickname, out var member))
            {
                member.SetIdentity(username, host, realName);
                return member;
            }

            member = new ChannelMemberState(nickname, username, host, realName);
            _members.Add(nickname, member);
            return member;
        }
    }

    public bool TryGetMember(string nickname, out ChannelMemberState? member)
    {
        lock (_gate)
        {
            return _members.TryGetValue(nickname, out member);
        }
    }

    internal bool RemoveMember(string nickname)
    {
        lock (_gate)
        {
            return _members.Remove(nickname);
        }
    }

    internal bool RenameMember(string oldNickname, string newNickname)
    {
        lock (_gate)
        {
            if (!_members.Remove(oldNickname, out var member))
            {
                return false;
            }

            member.Nickname = newNickname;
            _members[newNickname] = member;
            return true;
        }
    }

    internal void BeginNames()
    {
        lock (_gate)
        {
            if (NamesSynchronized)
            {
                _members.Clear();
            }

            NamesSynchronized = false;
            WhoSynchronized = false;
        }
    }

    internal void UpdateCaseMapping(IrcCaseMapping caseMapping)
    {
        lock (_gate)
        {
            var reindexed = new Dictionary<string, ChannelMemberState>(new IrcNameComparer(caseMapping));
            foreach (var member in _members.Values)
            {
                if (!reindexed.TryAdd(member.Nickname, member))
                {
                    throw new InvalidOperationException($"CASEMAPPING would merge members in '{Name}'.");
                }
            }

            _members = reindexed;
        }
    }

    internal void SetMode(char mode, bool adding, string? parameter)
    {
        lock (_gate)
        {
            if (adding)
            {
                _modes[mode] = parameter;
            }
            else
            {
                _modes.Remove(mode);
            }
        }
    }

    internal void ResetModes()
    {
        lock (_gate)
        {
            _modes.Clear();
        }
    }

    internal void SetMemberPrefix(string nickname, char mode, bool adding)
    {
        var member = GetOrAddMember(nickname);
        if (adding)
        {
            member.AddPrefixMode(mode);
        }
        else
        {
            member.RemovePrefixMode(mode);
        }
    }

    internal void AddChannelListEntry(ChannelListEntry entry)
    {
        lock (_gate)
        {
            if (!_channelLists.TryGetValue(entry.Mode, out var entries))
            {
                entries = [];
                _channelLists[entry.Mode] = entries;
            }

            var existing = entries.FindIndex(item =>
                item.Mask.Equals(entry.Mask, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                entries[existing] = entry;
            }
            else
            {
                entries.Add(entry);
            }
        }
    }

    internal void RemoveChannelListEntry(char mode, string mask)
    {
        lock (_gate)
        {
            if (_channelLists.TryGetValue(mode, out var entries))
            {
                entries.RemoveAll(entry => entry.Mask.Equals(mask, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    public void BeginChannelListSynchronization(char mode)
    {
        lock (_gate)
        {
            _channelLists.Remove(mode);
            _synchronizedChannelLists.Remove(mode);
            _synchronizingChannelLists.Add(mode);
        }
    }

    public void BeginBanListSynchronization() => BeginChannelListSynchronization('b');

    internal void CompleteChannelListSynchronization(char mode)
    {
        lock (_gate)
        {
            _synchronizingChannelLists.Remove(mode);
            _synchronizedChannelLists.Add(mode);
        }
    }
}
