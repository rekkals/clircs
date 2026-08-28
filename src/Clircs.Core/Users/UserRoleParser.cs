namespace Clircs.Users;

public static class UserRoleParser
{
    private static readonly Dictionary<string, UserRole> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bot"] = UserRole.Bot,
        ["operator"] = UserRole.OperatorEligible,
        ["operatoreligible"] = UserRole.OperatorEligible,
        ["voice"] = UserRole.VoiceEligible,
        ["voiceeligible"] = UserRole.VoiceEligible,
        ["autoop"] = UserRole.AutoOp,
        ["autovoice"] = UserRole.AutoVoice,
        ["protected"] = UserRole.Protected,
        ["deop"] = UserRole.Deop,
        ["kickonjoin"] = UserRole.KickOnJoin,
        ["exempt"] = UserRole.ProtectionExempt,
        ["protectionexempt"] = UserRole.ProtectionExempt
    };

    public static (UserRole Add, UserRole Remove) ParseChanges(string changes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(changes);
        var add = UserRole.None;
        var remove = UserRole.None;
        foreach (var token in changes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Length < 2 || token[0] is not ('+' or '-'))
            {
                throw new ArgumentException("Role changes must begin with + or -.", nameof(changes));
            }

            var roles = ParseToken(token[1..]);
            if (token[0] == '+')
            {
                add |= roles;
                remove &= ~roles;
            }
            else
            {
                remove |= roles;
                add &= ~roles;
            }
        }

        return (add, remove);
    }

    public static string Format(UserRole roles) => roles == UserRole.None
        ? "none"
        : string.Join(',', Enum.GetValues<UserRole>().Where(role => role != UserRole.None && roles.HasFlag(role)).Select(role => role.ToString()));

    public static string FormatFlags(UserRole roles)
    {
        var flags = new List<char>();
        if (roles.HasFlag(UserRole.Bot)) flags.Add('b');
        if (roles.HasFlag(UserRole.AutoOp)) flags.Add('a');
        if (roles.HasFlag(UserRole.AutoVoice)) flags.Add('v');
        if (roles.HasFlag(UserRole.Protected)) flags.Add('p');
        if (roles.HasFlag(UserRole.Deop)) flags.Add('d');
        if (roles.HasFlag(UserRole.KickOnJoin)) flags.Add('k');
        if (roles.HasFlag(UserRole.ProtectionExempt)) flags.Add('e');
        return flags.Count == 0 ? "none" : $"+{new string([.. flags])}";
    }

    public static string FormatEligibility(UserRole roles)
    {
        var eligible = new List<string>();
        if (roles.HasFlag(UserRole.OperatorEligible)) eligible.Add("operator");
        if (roles.HasFlag(UserRole.VoiceEligible)) eligible.Add("voice");
        return eligible.Count == 0 ? "none" : string.Join(',', eligible);
    }

    private static UserRole ParseToken(string value)
    {
        if (Names.TryGetValue(value, out var named))
        {
            return named;
        }

        var roles = UserRole.None;
        foreach (var letter in value)
        {
            roles |= letter switch
            {
                'b' => UserRole.Bot,
                'o' => UserRole.OperatorEligible,
                'v' => UserRole.VoiceEligible | UserRole.AutoVoice,
                'a' => UserRole.AutoOp,
                'f' => UserRole.Protected,
                'd' => UserRole.Deop,
                'k' => UserRole.KickOnJoin,
                'e' => UserRole.ProtectionExempt,
                _ => throw new ArgumentException($"Unknown compact user role '{letter}'.")
            };
        }

        return roles;
    }
}
