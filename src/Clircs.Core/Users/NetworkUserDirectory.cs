using Clircs.Identity;
using Clircs.Protocol;

namespace Clircs.Users;

public sealed record UserMatch(UserRecord? User, string? Hostmask, bool Conflict, IReadOnlyList<UserRecord> Candidates);

public sealed class NetworkUserDirectory
{
    private readonly List<UserRecord> _users;
    private readonly List<PolicyBan> _policyBans;
    private readonly object _gate = new();

    public NetworkUserDirectory(
        NetworkProfileId networkProfileId,
        IEnumerable<UserRecord>? users = null,
        IEnumerable<PolicyBan>? policyBans = null)
    {
        NetworkProfileId = networkProfileId;
        _users = (users ?? []).ToList();
        _policyBans = (policyBans ?? []).ToList();
        if (_users.Select(user => user.Handle).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _users.Count)
        {
            throw new InvalidDataException("User handles must be unique within a network profile.");
        }
    }

    public NetworkProfileId NetworkProfileId { get; }

    public IReadOnlyList<UserRecord> Users
    {
        get
        {
            lock (_gate)
            {
                return _users.ToArray();
            }
        }
    }

    public IReadOnlyList<PolicyBan> PolicyBans
    {
        get
        {
            lock (_gate)
            {
                return _policyBans.ToArray();
            }
        }
    }

    public NetworkUserDirectory DeepCopy()
    {
        lock (_gate)
        {
            return new NetworkUserDirectory(
                NetworkProfileId,
                _users.Select(user => new UserRecord(
                    user.Id,
                    user.Handle,
                    user.Hostmasks,
                    user.Roles,
                    new Dictionary<string, UserRole>(user.ChannelRoles),
                    user.Comment,
                    new Dictionary<string, string>(user.ChannelComments),
                    user.CreatedAt,
                    user.UpdatedAt)),
                _policyBans.Select(ban => new PolicyBan(ban.Id, ban.Mask, ban.Channels, ban.Reason, ban.CreatedAt)));
        }
    }

    public PolicyBan AddPolicyBan(string mask, IEnumerable<string> channels, string? reason = null)
    {
        lock (_gate)
        {
            var proposed = new PolicyBan(Guid.NewGuid(), mask, channels, reason);
            if (_policyBans.Any(existing =>
                existing.Mask.Equals(proposed.Mask, StringComparison.OrdinalIgnoreCase) &&
                existing.Channels.SequenceEqual(proposed.Channels, StringComparer.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("An identical policy ban already exists.");
            }

            _policyBans.Add(proposed);
            return proposed;
        }
    }

    public PolicyBan? RemovePolicyBan(string maskOrId)
    {
        lock (_gate)
        {
            var matches = _policyBans.Where(ban =>
                ban.Mask.Equals(maskOrId, StringComparison.OrdinalIgnoreCase) ||
                ban.Id.ToString("N").StartsWith(maskOrId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1)
            {
                throw new InvalidOperationException($"'{maskOrId}' matches more than one policy ban; use a longer ID.");
            }

            if (matches.Length == 0)
            {
                return null;
            }

            _policyBans.Remove(matches[0]);
            return matches[0];
        }
    }

    public UserRecord Add(string handle, string? hostmask = null, UserRole roles = UserRole.None)
    {
        lock (_gate)
        {
            if (_users.Any(user => user.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"User '{handle}' already exists.");
            }

            var user = new UserRecord(UserRecordId.New(), handle, hostmask is null ? [] : [hostmask], roles);
            if (hostmask is not null)
            {
                EnsureMaskUnique(hostmask, user: null, IrcCaseMapping.Rfc1459);
            }

            _users.Add(user);
            return user;
        }
    }

    public UserRecord? Find(string handle)
    {
        lock (_gate)
        {
            return _users.FirstOrDefault(user => user.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool Remove(string handle)
    {
        lock (_gate)
        {
            return _users.RemoveAll(user => user.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase)) > 0;
        }
    }

    public void AddHostmask(UserRecord user, string mask, IrcCaseMapping mapping)
    {
        lock (_gate)
        {
            EnsureMaskUnique(mask, user, mapping);
            user.AddHostmask(mask);
        }
    }

    public UserMatch Match(string fullMask, IrcCaseMapping mapping)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullMask);
        UserRecord[] snapshot;
        lock (_gate)
        {
            snapshot = _users.ToArray();
        }

        var matches = snapshot
            .SelectMany(user => user.Hostmasks
                .Where(mask => WildcardMatches(mask, fullMask, mapping))
                .Select(mask => (User: user, Mask: mask, Specificity: mask.Count(character => character is not ('*' or '?')))))
            .OrderByDescending(match => match.Specificity)
            .ToArray();
        if (matches.Length == 0)
        {
            return new UserMatch(null, null, false, []);
        }

        var best = matches[0];
        var tiedUsers = matches.Where(match => match.Specificity == best.Specificity).Select(match => match.User).Distinct().ToArray();
        return tiedUsers.Length == 1
            ? new UserMatch(best.User, best.Mask, false, tiedUsers)
            : new UserMatch(null, null, true, tiedUsers);
    }

    public static bool WildcardMatches(string pattern, string value, IrcCaseMapping mapping)
    {
        pattern = IrcCaseFold.Fold(pattern, mapping);
        value = IrcCaseFold.Fold(value, mapping);
        var previous = new bool[value.Length + 1];
        previous[0] = true;
        foreach (var patternCharacter in pattern)
        {
            var current = new bool[value.Length + 1];
            if (patternCharacter == '*')
            {
                current[0] = previous[0];
            }

            for (var index = 1; index <= value.Length; index++)
            {
                current[index] = patternCharacter switch
                {
                    '*' => current[index - 1] || previous[index],
                    '?' => previous[index - 1],
                    _ => previous[index - 1] && patternCharacter == value[index - 1]
                };
            }

            previous = current;
        }

        return previous[value.Length];
    }

    private void EnsureMaskUnique(string mask, UserRecord? user, IrcCaseMapping mapping)
    {
        var folded = IrcCaseFold.Fold(mask, mapping);
        var conflict = _users.FirstOrDefault(candidate => candidate != user && candidate.Hostmasks.Any(existing =>
            IrcCaseFold.Fold(existing, mapping) == folded));
        if (conflict is not null)
        {
            throw new InvalidOperationException($"Hostmask '{mask}' already belongs to '{conflict.Handle}'.");
        }
    }
}
