using Clircs.Identity;

namespace Clircs.Protection;

public enum ProtectionDetector
{
    Text,
    Repeat,
    Join,
    Nick,
    MassKick,
    MassDeop,
    Caps,
    Controls,
    ServerOp,
    PrivateMessage,
    PrivateNotice,
    Ctcp,
    Invite,
    ChannelCtcp
}

public enum ChannelProtectionAction
{
    Monitor,
    Kick,
    KickBan
}

public sealed record ProtectionRule(bool Enabled, int Threshold, int WindowSeconds)
{
    public ProtectionRule Validate()
    {
        if (Threshold is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(Threshold));
        if (WindowSeconds is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(WindowSeconds));
        return this;
    }
}

public sealed record ProtectionSettings(
    bool ChannelEnabled,
    bool PersonalEnabled,
    bool MonitorOnly,
    bool ExemptOperators,
    bool ExemptProtected,
    bool ExemptProtectionExempt,
    Dictionary<ProtectionDetector, ProtectionRule> Rules,
    ChannelProtectionAction ChannelAction = ChannelProtectionAction.Kick,
    int BanSeconds = 1800,
    int PersonalIgnoreSeconds = 45)
{
    public ProtectionSettings DeepCopy() => this with { Rules = Rules.ToDictionary(entry => entry.Key, entry => entry.Value) };

    public ProtectionSettings Validate()
    {
        if (!Enum.IsDefined(ChannelAction)) throw new InvalidDataException("Unknown channel protection action.");
        if (BanSeconds is < 0 or > 2_592_000) throw new InvalidDataException("Protection ban time must be from 0 through 30 days.");
        if (PersonalIgnoreSeconds is < 1 or > 86_400) throw new InvalidDataException("Personal ignore time must be from 1 second through 1 day.");
        foreach (var detector in Enum.GetValues<ProtectionDetector>())
        {
            if (!Rules.TryGetValue(detector, out var rule))
            {
                throw new InvalidDataException($"Protection settings are missing {detector}.");
            }
            rule.Validate();
        }
        return this;
    }

    public static ProtectionSettings Defaults(ProtectionSettings? basis = null)
    {
        var rules = new Dictionary<ProtectionDetector, ProtectionRule>
        {
            [ProtectionDetector.Text] = new(true, 6, 4),
            [ProtectionDetector.Repeat] = new(true, 3, 12),
            [ProtectionDetector.Join] = new(true, 5, 10),
            [ProtectionDetector.Nick] = new(true, 4, 15),
            [ProtectionDetector.MassKick] = new(true, 3, 10),
            [ProtectionDetector.MassDeop] = new(true, 3, 10),
            [ProtectionDetector.Caps] = new(true, 4, 10),
            [ProtectionDetector.Controls] = new(true, 3, 10),
            [ProtectionDetector.ServerOp] = new(true, 1, 5),
            [ProtectionDetector.PrivateMessage] = new(true, 6, 5),
            [ProtectionDetector.PrivateNotice] = new(true, 6, 5),
            [ProtectionDetector.Ctcp] = new(true, 4, 10),
            [ProtectionDetector.Invite] = new(true, 4, 30),
            [ProtectionDetector.ChannelCtcp] = new(true, 4, 10)
        };
        return new ProtectionSettings(
            basis?.ChannelEnabled ?? false,
            basis?.PersonalEnabled ?? false,
            basis?.MonitorOnly ?? false,
            basis?.ExemptOperators ?? true,
            basis?.ExemptProtected ?? true,
            basis?.ExemptProtectionExempt ?? true,
            rules,
            basis?.ChannelAction ?? ChannelProtectionAction.Kick,
            basis?.BanSeconds ?? 1800,
            basis?.PersonalIgnoreSeconds ?? 45);
    }
}

public sealed record ProtectionEvidence(
    NetworkSessionId NetworkSessionId,
    ProtectionDetector Detector,
    string Actor,
    string? Channel,
    string? Text,
    DateTimeOffset Timestamp,
    int Weight = 1);

public sealed record ProtectionDetection(
    ProtectionEvidence Evidence,
    int Count,
    ProtectionRule Rule,
    TimeSpan Elapsed);

public sealed record ProtectionCounter(
    ProtectionDetector Detector,
    string Actor,
    string? Channel,
    int Count,
    DateTimeOffset ExpiresAt);
