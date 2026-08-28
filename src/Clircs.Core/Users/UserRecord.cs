using Clircs.Identity;
using Clircs.Protocol;

namespace Clircs.Users;

[Flags]
public enum UserRole
{
    None = 0,
    Bot = 1 << 0,
    OperatorEligible = 1 << 1,
    VoiceEligible = 1 << 2,
    AutoOp = 1 << 3,
    AutoVoice = 1 << 4,
    Protected = 1 << 5,
    Deop = 1 << 6,
    KickOnJoin = 1 << 7,
    ProtectionExempt = 1 << 8
}

public sealed class UserRecord
{
    private readonly List<string> _hostmasks;
    private readonly Dictionary<string, UserRole> _channelRoles;
    private readonly Dictionary<string, string> _channelComments;
    private readonly object _gate = new();
    private UserRole _roles;
    private string _comment;
    private DateTimeOffset _updatedAt;

    public UserRecord(
        UserRecordId id,
        string handle,
        IEnumerable<string>? hostmasks = null,
        UserRole roles = UserRole.None,
        IDictionary<string, UserRole>? channelRoles = null,
        string? comment = null,
        IDictionary<string, string>? channelComments = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        Handle = handle.Trim();
        if (Handle.Length > 64 || Handle.IndexOfAny([' ', '\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("User handles must be 1-64 characters without whitespace or controls.", nameof(handle));
        }

        Id = id;
        _hostmasks = (hostmasks ?? []).Select(ValidateMask).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _roles = roles;
        _channelRoles = new Dictionary<string, UserRole>(channelRoles ?? new Dictionary<string, UserRole>(), StringComparer.OrdinalIgnoreCase);
        _channelComments = new Dictionary<string, string>(channelComments ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        _comment = comment?.Trim() ?? string.Empty;
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
        _updatedAt = updatedAt ?? CreatedAt;
    }

    public UserRecordId Id { get; }

    public string Handle { get; }

    public IReadOnlyList<string> Hostmasks
    {
        get
        {
            lock (_gate)
            {
                return _hostmasks.ToArray();
            }
        }
    }

    public UserRole Roles
    {
        get
        {
            lock (_gate)
            {
                return _roles;
            }
        }
    }

    public IReadOnlyDictionary<string, UserRole> ChannelRoles
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, UserRole>(_channelRoles, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public string Comment
    {
        get
        {
            lock (_gate)
            {
                return _comment;
            }
        }
    }

    public IReadOnlyDictionary<string, string> ChannelComments
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_channelComments, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt
    {
        get
        {
            lock (_gate)
            {
                return _updatedAt;
            }
        }
    }

    public UserRole EffectiveRoles(string? channel, IrcCaseMapping mapping = IrcCaseMapping.Rfc1459)
    {
        lock (_gate)
        {
            if (channel is null)
            {
                return _roles;
            }

            var comparer = new IrcNameComparer(mapping);
            var match = _channelRoles.FirstOrDefault(pair => comparer.Equals(pair.Key, channel));
            return _roles | match.Value;
        }
    }

    public string? GetChannelComment(string channel, IrcCaseMapping mapping = IrcCaseMapping.Rfc1459)
    {
        lock (_gate)
        {
            var comparer = new IrcNameComparer(mapping);
            return _channelComments.FirstOrDefault(pair => comparer.Equals(pair.Key, channel)).Value;
        }
    }

    public void AddHostmask(string mask)
    {
        mask = ValidateMask(mask);
        lock (_gate)
        {
            if (!_hostmasks.Contains(mask, StringComparer.OrdinalIgnoreCase))
            {
                _hostmasks.Add(mask);
                Touch();
            }
        }
    }

    public bool RemoveHostmask(string mask)
    {
        lock (_gate)
        {
            var removed = _hostmasks.RemoveAll(value => value.Equals(mask, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                Touch();
            }

            return removed;
        }
    }

    public void ChangeRoles(
        UserRole add,
        UserRole remove,
        string? channel = null,
        IrcCaseMapping mapping = IrcCaseMapping.Rfc1459)
    {
        lock (_gate)
        {
            if (channel is null)
            {
                _roles = (_roles | add) & ~remove;
            }
            else
            {
                var comparer = new IrcNameComparer(mapping);
                var key = _channelRoles.Keys.FirstOrDefault(existing => comparer.Equals(existing, channel)) ?? channel;
                var current = _channelRoles.GetValueOrDefault(key);
                _channelRoles[key] = (current | add) & ~remove;
            }

            Touch();
        }
    }

    public void AddChannel(string channel, IrcCaseMapping mapping = IrcCaseMapping.Rfc1459)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        lock (_gate)
        {
            var comparer = new IrcNameComparer(mapping);
            if (!_channelRoles.Keys.Any(existing => comparer.Equals(existing, channel)))
            {
                _channelRoles.Add(channel, UserRole.None);
            }
            Touch();
        }
    }

    public bool RemoveChannel(string channel, IrcCaseMapping mapping = IrcCaseMapping.Rfc1459)
    {
        lock (_gate)
        {
            var comparer = new IrcNameComparer(mapping);
            var rolesKey = _channelRoles.Keys.FirstOrDefault(existing => comparer.Equals(existing, channel));
            var commentKey = _channelComments.Keys.FirstOrDefault(existing => comparer.Equals(existing, channel));
            var removed = (rolesKey is not null && _channelRoles.Remove(rolesKey)) |
                (commentKey is not null && _channelComments.Remove(commentKey));
            if (removed)
            {
                Touch();
            }

            return removed;
        }
    }

    public void SetComment(string comment, string? channel = null, IrcCaseMapping mapping = IrcCaseMapping.Rfc1459)
    {
        comment = comment.Trim();
        lock (_gate)
        {
            if (channel is null)
            {
                _comment = comment;
            }
            else
            {
                var comparer = new IrcNameComparer(mapping);
                var existingKey = _channelComments.Keys.FirstOrDefault(existing => comparer.Equals(existing, channel));
                if (comment.Length == 0)
                {
                    if (existingKey is not null) _channelComments.Remove(existingKey);
                }
                else
                {
                    _channelComments[existingKey ?? channel] = comment;
                }
            }

            Touch();
        }
    }

    private void Touch() => _updatedAt = DateTimeOffset.UtcNow;

    private static string ValidateMask(string mask)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mask);
        mask = mask.Trim();
        if (mask.Length > 300 || mask.IndexOfAny([' ', '\r', '\n', '\0']) >= 0 || !mask.Contains('!') || !mask.Contains('@'))
        {
            throw new ArgumentException("Hostmasks must use nick!user@host form without whitespace.", nameof(mask));
        }

        return mask;
    }
}
