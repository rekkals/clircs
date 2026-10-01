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

public enum PersonalProtectionAction
{
    Ignore,
    Monitor
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

public sealed record ChannelProtectionSettings(
    bool Enabled,
    bool ExemptOperators,
    bool ExemptProtected,
    Dictionary<ProtectionDetector, ProtectionRule> Rules,
    ChannelProtectionAction Action = ChannelProtectionAction.Kick,
    int BanSeconds = 1800)
{
    private static readonly ProtectionDetector[] Detectors =
    [
        ProtectionDetector.Text,
        ProtectionDetector.Repeat,
        ProtectionDetector.Join,
        ProtectionDetector.Nick,
        ProtectionDetector.MassKick,
        ProtectionDetector.MassDeop,
        ProtectionDetector.Caps,
        ProtectionDetector.Controls,
        ProtectionDetector.ChannelCtcp
    ];

    public ChannelProtectionSettings DeepCopy() =>
        this with { Rules = Rules.ToDictionary(entry => entry.Key, entry => entry.Value) };

    public ChannelProtectionSettings Validate()
    {
        if (!Enum.IsDefined(Action))
            throw new InvalidDataException("Unknown channel protection action.");
        if (BanSeconds is < 0 or > 2_592_000)
            throw new InvalidDataException("Protection ban time must be from 0 through 30 days.");
        if (Rules.Count != Detectors.Length || Detectors.Any(detector => !Rules.ContainsKey(detector)))
            throw new InvalidDataException("Channel protection rules are incomplete.");
        foreach (var rule in Rules.Values)
        {
            rule.Validate();
        }
        return this;
    }

    public static ChannelProtectionSettings Defaults() => new(
        false,
        true,
        true,
        new Dictionary<ProtectionDetector, ProtectionRule>
        {
            [ProtectionDetector.Text] = new(true, 6, 4),
            [ProtectionDetector.Repeat] = new(true, 3, 12),
            [ProtectionDetector.Join] = new(true, 5, 10),
            [ProtectionDetector.Nick] = new(true, 4, 15),
            [ProtectionDetector.MassKick] = new(true, 3, 10),
            [ProtectionDetector.MassDeop] = new(true, 3, 10),
            [ProtectionDetector.Caps] = new(true, 4, 10),
            [ProtectionDetector.Controls] = new(true, 3, 10),
            [ProtectionDetector.ChannelCtcp] = new(true, 4, 10)
        });
}

public sealed record PersonalProtectionSettings(
    bool Enabled,
    bool ExemptProtected,
    Dictionary<ProtectionDetector, ProtectionRule> Rules,
    PersonalProtectionAction Action = PersonalProtectionAction.Ignore,
    int IgnoreSeconds = 45)
{
    private static readonly ProtectionDetector[] Detectors =
    [
        ProtectionDetector.PrivateMessage,
        ProtectionDetector.PrivateNotice,
        ProtectionDetector.Ctcp,
        ProtectionDetector.Invite
    ];

    public PersonalProtectionSettings DeepCopy() =>
        this with { Rules = Rules.ToDictionary(entry => entry.Key, entry => entry.Value) };

    public PersonalProtectionSettings Validate()
    {
        if (!Enum.IsDefined(Action))
            throw new InvalidDataException("Unknown personal protection action.");
        if (IgnoreSeconds is < 1 or > 86_400)
            throw new InvalidDataException("Personal ignore time must be from 1 second through 1 day.");
        if (Rules.Count != Detectors.Length || Detectors.Any(detector => !Rules.ContainsKey(detector)))
            throw new InvalidDataException("Personal protection rules are incomplete.");
        foreach (var rule in Rules.Values)
        {
            rule.Validate();
        }
        return this;
    }

    public static PersonalProtectionSettings Defaults() => new(
        false,
        true,
        new Dictionary<ProtectionDetector, ProtectionRule>
        {
            [ProtectionDetector.PrivateMessage] = new(true, 6, 5),
            [ProtectionDetector.PrivateNotice] = new(true, 6, 5),
            [ProtectionDetector.Ctcp] = new(true, 4, 10),
            [ProtectionDetector.Invite] = new(true, 4, 30)
        });
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
