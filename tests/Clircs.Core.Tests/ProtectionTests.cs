using System.Reflection;
using Clircs.ConsoleClient;
using Clircs.Identity;
using Clircs.Protection;
using Clircs.Protocol;
using Clircs.Users;

namespace Clircs.Core.Tests;

internal static class ProtectionTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("protection defaults are explicit and start disabled", DefaultsAreSafeAndExplicit);
        suite.Add("protection monitor triggers once per evidence window", MonitorTriggersWithCooldown);
        suite.Add("protection detections retain observed elapsed time and readable kick reasons", DetectionReasonsUseObservedTiming);
        suite.Add("temporary protection state expires and remains network scoped", TemporaryStateExpires);
        suite.Add("protection monitor state can be cleared for one network", MonitorStateClearsPerNetwork);
        suite.Add("temporary protection actions reserve atomically", TemporaryActionsReserveAtomically);
        suite.Add("user and channel policy runtime has one session lifecycle", PolicyRuntimeHasOneLifecycle);
        suite.Add("manual ignore entries are isolated and cleared with their session", SessionIgnoreIsIsolatedAndCleared);
        suite.Add("repeat detection isolates distinct text", RepeatDetectionIsTextSpecific);
        suite.Add("join detection isolates complete client prefixes", JoinDetectionIsolatesActors);
        suite.Add("batched mode changes contribute one event per affected user", BatchedModesUseAffectedUserCount);
        suite.Add("privileged abuse detectors are not neutralized by operator exemption", PrivilegedDetectorsIgnoreOperatorExemption);
        suite.Add("friendly detector names are canonical", FriendlyDetectorNamesAreCanonical);
        suite.Add("friendly protection status shows only actionable settings", FriendlyStatusShowsActionableSettings);
        suite.Add("friendly protection counters remain separate", FriendlyProtectionCountersRemainSeparate);
        suite.Add("protection settings persist global network and channel inheritance", SettingsPersistAndInherit);
        suite.Add("channel and personal protection reset independently", ChannelAndPersonalProtectionResetIndependently);
        suite.Add("advanced protection changes remain sparse overrides", AdvancedChangesRemainSparseOverrides);
        suite.Add("channel protection resolves live and offline case forms", ChannelScopeResolvesCaseForms);
        suite.Add("unsupported development protection settings are rejected", UnsupportedSettingsVersionIsRejected);
        suite.Add("user-facing versions include a lowercase v prefix", VersionHasPrefix);
        suite.Add("product version comes from assembly metadata", ProductVersionUsesAssemblyMetadata);
        suite.Add("public .NET assemblies and namespaces use Clircs naming", DotNetSurfaceUsesClircsNaming);
    }

    private static void PolicyRuntimeHasOneLifecycle()
    {
        var runtime = new UserAndChannelPolicyCoordinator();
        var profileId = NetworkProfileId.New();
        var sessionId = NetworkSessionId.New();
        var loads = 0;
        NetworkUserDirectory Load()
        {
            loads++;
            return new NetworkUserDirectory(profileId);
        }

        var first = runtime.GetDirectory(profileId, Load);
        var second = runtime.GetDirectory(profileId, Load);
        Assert.True(ReferenceEquals(first, second));
        Assert.Equal(1, loads);

        var now = DateTimeOffset.UtcNow;
        runtime.IgnorePersonally(sessionId, "user@host", now.AddMinutes(1));
        Assert.True(runtime.IsPersonallyIgnored(sessionId, "user@host", now));
        Assert.True(runtime.TryBeginProtectionAction(sessionId, "#channel\0nick", now, now.AddMinutes(1)));
        Assert.False(runtime.TryBeginProtectionAction(sessionId, "#channel\0nick", now, now.AddMinutes(1)));
        var oldGate = runtime.ChannelGate(sessionId, "#channel");

        runtime.ClearSession(sessionId);

        Assert.False(runtime.IsPersonallyIgnored(sessionId, "user@host", now));
        Assert.True(runtime.TryBeginProtectionAction(sessionId, "#channel\0nick", now, now.AddMinutes(1)));
        Assert.False(ReferenceEquals(oldGate, runtime.ChannelGate(sessionId, "#channel")));
        Assert.True(ReferenceEquals(first, runtime.GetDirectory(profileId, Load)));
    }

    private static void SessionIgnoreIsIsolatedAndCleared()
    {
        var runtime = new UserAndChannelPolicyCoordinator();
        var firstSession = NetworkSessionId.New();
        var secondSession = NetworkSessionId.New();
        var first = runtime.GetSessionIgnoreDirectory(firstSession);
        var second = runtime.GetSessionIgnoreDirectory(secondSession);

        first.AddIgnore("Trouble", IrcCaseMapping.Ascii);

        Assert.True(first.IsIgnored("trouble", null, null, IrcCaseMapping.Ascii));
        Assert.False(second.IsIgnored("trouble", null, null, IrcCaseMapping.Ascii));
        Assert.True(ReferenceEquals(first, runtime.FindSessionIgnoreDirectory(firstSession)));

        runtime.ClearSession(firstSession);

        Assert.True(runtime.FindSessionIgnoreDirectory(firstSession) is null);
        Assert.True(ReferenceEquals(second, runtime.FindSessionIgnoreDirectory(secondSession)));
        Assert.False(ReferenceEquals(first, runtime.GetSessionIgnoreDirectory(firstSession)));
    }

    private static void UnsupportedSettingsVersionIsRejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"clirc-protection-version-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "protection.json");
            var store = new ProtectionSettingsStore(path);
            store.SetPersonalEnabled(new ProtectionScope(ProtectionScopeKind.Global), true);

            var unsupported = File.ReadAllText(path)
                .Replace("\"Version\": 6", "\"Version\": 5", StringComparison.Ordinal);
            File.WriteAllText(path, unsupported);

            var rejected = new ProtectionSettingsStore(path);
            Assert.True(rejected.LoadError is not null);
            Assert.True(rejected.LoadError!.Contains(
                "Unsupported protection settings version 5.",
                StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void DefaultsAreSafeAndExplicit()
    {
        var channel = ChannelProtectionSettings.Defaults();
        var personal = PersonalProtectionSettings.Defaults();

        Assert.False(channel.Enabled);
        Assert.False(personal.Enabled);
        Assert.Equal(ChannelProtectionAction.Kick, channel.Action);
        Assert.Equal(PersonalProtectionAction.Ignore, personal.Action);

        Assert.Equal(9, channel.Rules.Count);
        Assert.Equal(4, personal.Rules.Count);

        Assert.Equal(new ProtectionRule(true, 6, 4),
            channel.Rules[ProtectionDetector.Text]);
        Assert.Equal(new ProtectionRule(true, 3, 10),
            channel.Rules[ProtectionDetector.MassKick]);
        Assert.Equal(new ProtectionRule(true, 4, 30),
            personal.Rules[ProtectionDetector.Invite]);
    }

    private static void MonitorTriggersWithCooldown()
    {
        var monitor = new ProtectionMonitor();
        var network = NetworkSessionId.New();
        var rule = new ProtectionRule(true, 3, 10);
        var now = DateTimeOffset.UtcNow;
        Assert.True(monitor.Evaluate(Evidence(network, "alice", now), rule) is null);
        Assert.True(monitor.Evaluate(Evidence(network, "alice", now.AddSeconds(1)), rule) is null);
        Assert.Equal(3, monitor.Evaluate(Evidence(network, "alice", now.AddSeconds(2)), rule)!.Count);
        Assert.True(monitor.Evaluate(Evidence(network, "alice", now.AddSeconds(3)), rule) is null);
        Assert.True(monitor.Evaluate(Evidence(network, "alice", now.AddSeconds(13)), rule) is null);
    }

    private static void DetectionReasonsUseObservedTiming()
    {
        var monitor = new ProtectionMonitor();
        var network = NetworkSessionId.New();
        var rule = new ProtectionRule(true, 3, 10);
        var now = DateTimeOffset.UtcNow;
        Assert.True(monitor.Evaluate(Evidence(network, "alice", now), rule) is null);
        Assert.True(monitor.Evaluate(Evidence(network, "alice", now.AddSeconds(1)), rule) is null);
        var detection = monitor.Evaluate(Evidence(network, "alice", now.AddSeconds(2.4)), rule)!;

        Assert.True(Math.Abs((detection.Elapsed - TimeSpan.FromSeconds(2.4)).Ticks) <= 1);
        Assert.Equal("Text flood: 3 messages in 2.4 seconds", ClientApplication.ProtectionKickReason(detection));

        var deop = new ProtectionDetection(
            Evidence(network, "alice", now, ProtectionDetector.MassDeop),
            4,
            rule,
            TimeSpan.Zero);
        Assert.Equal("Mass deop: 4 deops in 0.0 seconds", ClientApplication.ProtectionKickReason(deop));
    }

    private static void TemporaryStateExpires()
    {
        var tracker = new ProtectionExpiryTracker();
        var first = NetworkSessionId.New();
        var second = NetworkSessionId.New();
        var now = DateTimeOffset.UtcNow;
        tracker.Set(first, "alice!u@h", now.AddSeconds(30));

        Assert.True(tracker.Contains(first, "alice!u@h", now));
        Assert.False(tracker.Contains(second, "alice!u@h", now));
        Assert.False(tracker.Contains(first, "alice!u@h", now.AddSeconds(31)));

        tracker.Set(first, "bob!u@h", now.AddSeconds(30));
        tracker.Clear(first);
        Assert.False(tracker.Contains(first, "bob!u@h", now));
    }

    private static void MonitorStateClearsPerNetwork()
    {
        var monitor = new ProtectionMonitor();
        var first = NetworkSessionId.New();
        var second = NetworkSessionId.New();
        var now = DateTimeOffset.UtcNow;
        var rule = new ProtectionRule(true, 3, 30);
        monitor.Evaluate(Evidence(first, "alice", now), rule);
        monitor.Evaluate(Evidence(second, "bob", now), rule);

        monitor.Clear(first);

        Assert.False(monitor.Counters(first, now).Any());
        Assert.True(monitor.Counters(second, now)
            .Any(counter => counter.Actor == "bob"));
    }

    private static void TemporaryActionsReserveAtomically()
    {
        var tracker = new ProtectionExpiryTracker();
        var network = NetworkSessionId.New();
        var now = DateTimeOffset.UtcNow;

        Assert.True(tracker.TryReserve(network, "#channel\0+o\0alice", now, now.AddSeconds(5)));
        Assert.False(tracker.TryReserve(network, "#channel\0+o\0alice", now, now.AddSeconds(5)));
        Assert.True(tracker.TryReserve(network, "#channel\0+o\0alice", now.AddSeconds(6), now.AddSeconds(11)));
    }

    private static void RepeatDetectionIsTextSpecific()
    {
        var monitor = new ProtectionMonitor();
        var network = NetworkSessionId.New();
        var rule = new ProtectionRule(true, 2, 10);
        var now = DateTimeOffset.UtcNow;
        Assert.True(monitor.Evaluate(Evidence(network, "alice", now, ProtectionDetector.Repeat, "one"), rule) is null);
        Assert.True(monitor.Evaluate(Evidence(network, "alice", now, ProtectionDetector.Repeat, "two"), rule) is null);
        Assert.Equal(2, monitor.Evaluate(Evidence(network, "alice", now.AddSeconds(1), ProtectionDetector.Repeat, " ONE "), rule)!.Count);
    }

    private static void JoinDetectionIsolatesActors()
    {
        var monitor = new ProtectionMonitor();
        var network = NetworkSessionId.New();
        var rule = new ProtectionRule(true, 3, 10);
        var now = DateTimeOffset.UtcNow;
        Assert.True(monitor.Evaluate(Evidence(network, "alice!a@web.example", now, ProtectionDetector.Join), rule) is null);
        Assert.True(monitor.Evaluate(Evidence(network, "bob!b@web.example", now.AddSeconds(1), ProtectionDetector.Join), rule) is null);
        Assert.True(monitor.Evaluate(Evidence(network, "carol!c@web.example", now.AddSeconds(2), ProtectionDetector.Join), rule) is null);
        Assert.True(monitor.Evaluate(Evidence(network, "alice!a@web.example", now.AddSeconds(3), ProtectionDetector.Join), rule) is null);
        Assert.Equal(3, monitor.Evaluate(
            Evidence(network, "alice!a@web.example", now.AddSeconds(4), ProtectionDetector.Join), rule)!.Count);
    }

    private static void BatchedModesUseAffectedUserCount()
    {
        Assert.Equal(4, ClientApplication.CountModeChanges("-oooo", 'o', adding: false));
        Assert.Equal(1, ClientApplication.CountModeChanges("+ov-o", 'o', adding: false));

        var monitor = new ProtectionMonitor();
        var evidence = Evidence(
            NetworkSessionId.New(), "ChanOp", DateTimeOffset.UtcNow, ProtectionDetector.MassDeop) with { Weight = 4 };
        Assert.Equal(4, monitor.Evaluate(evidence, new ProtectionRule(true, 3, 10))!.Count);
    }

    private static void PrivilegedDetectorsIgnoreOperatorExemption()
    {
        Assert.False(ClientApplication.OperatorExemptionApplies(ProtectionDetector.MassKick));
        Assert.False(ClientApplication.OperatorExemptionApplies(ProtectionDetector.MassDeop));
        Assert.True(ClientApplication.OperatorExemptionApplies(ProtectionDetector.Text));
    }

    private static void FriendlyDetectorNamesAreCanonical()
    {
        Assert.Equal("kick",
            ClientApplication.DetectorName(ProtectionDetector.MassKick));
        Assert.Equal("deop",
            ClientApplication.DetectorName(ProtectionDetector.MassDeop));
        Assert.Equal("message",
            ClientApplication.DetectorName(ProtectionDetector.PrivateMessage));
        Assert.Equal("notice",
            ClientApplication.DetectorName(ProtectionDetector.PrivateNotice));
        Assert.Equal("ctcp",
            ClientApplication.DetectorName(ProtectionDetector.ChannelCtcp));
        Assert.Equal("ctcp",
            ClientApplication.DetectorName(ProtectionDetector.Ctcp));

        Assert.Equal(ProtectionDetector.MassKick,
            ClientApplication.ParseFriendlyProtectionDetector(
                "kick",
                personal: false)!.Value);
        Assert.Equal(ProtectionDetector.MassDeop,
            ClientApplication.ParseFriendlyProtectionDetector(
                "deop",
                personal: false)!.Value);
        Assert.Equal(ProtectionDetector.ChannelCtcp,
            ClientApplication.ParseFriendlyProtectionDetector(
                "ctcp",
                personal: false)!.Value);
        Assert.Equal(ProtectionDetector.Ctcp,
            ClientApplication.ParseFriendlyProtectionDetector(
                "ctcp",
                personal: true)!.Value);
        Assert.True(ClientApplication.ParseFriendlyProtectionDetector(
            "ctcp.channel",
            personal: false) is null);
        Assert.True(ClientApplication.ParseFriendlyProtectionDetector(
            "ctcp.user",
            personal: true) is null);
        Assert.True(ClientApplication.ParseFriendlyProtectionDetector(
            "privateMessage",
            personal: true) is null);
        Assert.True(ClientApplication.ParseFriendlyProtectionDetector(
            "privateNotice",
            personal: true) is null);
        Assert.True(ClientApplication.ParseFriendlyProtectionDetector(
            "channelCtcp",
            personal: false) is null);
        Assert.True(ClientApplication.ParseFriendlyProtectionDetector(
            "mass.kick",
            personal: false) is null);
        Assert.True(ClientApplication.ParseFriendlyProtectionDetector(
            "mass.deop",
            personal: false) is null);
    }

    private static void FriendlyStatusShowsActionableSettings()
    {
        var channel = ChannelProtectionSettings.Defaults() with
        {
            Enabled = true,
            Action = ChannelProtectionAction.Kick
        };
        var kick = ClientApplication.ChannelProtectionPresentation(
            "Channel protection: EFNet #clircs", channel);
        Assert.Equal(
            "Protection,on;Action,kick;Exempt chanops,yes;Exempt friends,yes",
            string.Join(';',
                kick.Fields!.SkipLast(1).Select(field => $"{field.Label},{field.Value}")));

        Assert.Equal(string.Empty, kick.Fields!.Last().Label);
        Assert.Equal(string.Empty, kick.Fields!.Last().Value);

        var kickBan = ClientApplication.ChannelProtectionPresentation(
            "Channel protection: EFNet #clircs",
            channel with { Action = ChannelProtectionAction.KickBan, BanSeconds = 1800 });
        Assert.Equal(
            "Protection,on;Action,kickban;Ban time,30m;Exempt chanops,yes;Exempt friends,yes",
            string.Join(';',
                kickBan.Fields!.SkipLast(1).Select(field => $"{field.Label},{field.Value}")));

        var personal = ClientApplication.PersonalProtectionPresentation(
            "Personal protection: EFNet",
            PersonalProtectionSettings.Defaults() with
            {
                Enabled = true,
                Action = PersonalProtectionAction.Monitor
            });
        Assert.Equal(
            "Protection,on;Action,monitor;Exempt friends,yes",
            string.Join(';',
                personal.Fields!.SkipLast(1).Select(field => $"{field.Label},{field.Value}")));

        Assert.Equal(string.Empty, personal.Fields!.Last().Label);
        Assert.Equal(string.Empty, personal.Fields!.Last().Value);

        var ignore = ClientApplication.PersonalProtectionPresentation(
            "Personal protection: EFNet",
            PersonalProtectionSettings.Defaults() with
            {
                Enabled = true,
                Action = PersonalProtectionAction.Ignore,
                IgnoreSeconds = 90
            });
        Assert.Equal(
            "Protection,on;Action,ignore;Ignore time,1m 30s;Exempt friends,yes",
            string.Join(';',
                ignore.Fields!.SkipLast(1).Select(field => $"{field.Label},{field.Value}")));
    }

    private static void FriendlyProtectionCountersRemainSeparate()
    {
        var now = new DateTimeOffset(
            2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        ProtectionCounter[] counters =
        [
            new(
                ProtectionDetector.Text,
                "alice",
                "#clircs",
                2,
                now.AddSeconds(4)),
            new(
                ProtectionDetector.PrivateMessage,
                "bob",
                null,
                3,
                now.AddSeconds(5))
        ];

        var channel = ClientApplication.ProtectionCountersPresentation(
            "Channel Protection Counters",
            counters,
            now,
            personal: false);
        Assert.Equal(
            "Rule,Actor,Channel,Events,Expires in",
            string.Join(',', channel.Table!.Columns));
        Assert.Equal(
            "text,alice,#clircs,2,4s",
            string.Join(',', channel.Table.Rows.Single()));

        var personal = ClientApplication.ProtectionCountersPresentation(
            "Personal Protection Counters",
            counters,
            now,
            personal: true);
        Assert.Equal(
            "Rule,Actor,Events,Expires in",
            string.Join(',', personal.Table!.Columns));
        Assert.Equal(
            "message,bob,3,5s",
            string.Join(',', personal.Table.Rows.Single()));
    }

    private static void SettingsPersistAndInherit()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"clirc-protection-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "protection.json");
            var store = new ProtectionSettingsStore(path);
            var network = "network-1";
            var networkScope = new ProtectionScope(ProtectionScopeKind.Network, network);
            store.SetPersonalEnabled(networkScope, true);
            store.SetPersonalAction(networkScope, PersonalProtectionAction.Monitor);
            var channelScope = new ProtectionScope(ProtectionScopeKind.Channel, network, "#clirc");
            store.SetChannelEnabled(channelScope, true);
            store.SetChannelRule(networkScope, ProtectionDetector.Text, threshold: 9, windowSeconds: 7);
            store.SetChannelAction(networkScope, ChannelProtectionAction.KickBan);
            store.SetChannelBanSeconds(networkScope, 900);
            store.SetPersonalIgnoreSeconds(networkScope, 60);

            var reloaded = new ProtectionSettingsStore(path);
            var channelSettings = reloaded.EffectiveChannel(network, "#clirc");
            var personalSettings = reloaded.EffectivePersonal(network);

            Assert.True(personalSettings.Settings.Enabled);
            Assert.Equal(PersonalProtectionAction.Monitor, personalSettings.Settings.Action);
            Assert.Equal(60, personalSettings.Settings.IgnoreSeconds);

            Assert.True(channelSettings.Settings.Enabled);
            Assert.Equal(9, channelSettings.Settings.Rules[ProtectionDetector.Text].Threshold);
            Assert.Equal(7, channelSettings.Settings.Rules[ProtectionDetector.Text].WindowSeconds);
            Assert.Equal(ChannelProtectionAction.KickBan, channelSettings.Settings.Action);
            Assert.Equal(900, channelSettings.Settings.BanSeconds);
            Assert.Equal(ProtectionScopeKind.Channel, channelSettings.Source.Kind);

            Assert.False(
                reloaded.EffectiveChannel("other", "#clirc").Settings.Enabled);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void ChannelAndPersonalProtectionResetIndependently()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"clirc-protection-reset-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var path = Path.Combine(directory, "protection.json");
            var global = new ProtectionScope(ProtectionScopeKind.Global);
            var store = new ProtectionSettingsStore(path);

            store.SetChannelEnabled(global, true);
            store.SetChannelExemptOperators(global, false);
            store.SetPersonalEnabled(global, true);
            store.SetPersonalAction(
                global,
                PersonalProtectionAction.Monitor);

            store.ResetChannel(global);

            var afterChannelReset = new ProtectionSettingsStore(path);
            Assert.False(
                afterChannelReset.EffectiveChannel(null, null).Settings.Enabled);
            Assert.True(
                afterChannelReset.EffectiveChannel(null, null)
                    .Settings.ExemptOperators);
            Assert.True(
                afterChannelReset.EffectivePersonal(null).Settings.Enabled);
            Assert.Equal(
                PersonalProtectionAction.Monitor,
                afterChannelReset.EffectivePersonal(null).Settings.Action);

            afterChannelReset.ResetPersonal(global);

            var afterPersonalReset = new ProtectionSettingsStore(path);
            Assert.False(
                afterPersonalReset.EffectivePersonal(null).Settings.Enabled);
            Assert.Equal(
                PersonalProtectionAction.Ignore,
                afterPersonalReset.EffectivePersonal(null).Settings.Action);
            Assert.False(
                afterPersonalReset.EffectiveChannel(null, null).Settings.Enabled);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AdvancedChangesRemainSparseOverrides()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"clirc-protection-sparse-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ProtectionSettingsStore(Path.Combine(directory, "protection.json"));
            var network = new ProtectionScope(ProtectionScopeKind.Network, "network-1");
            var channel = new ProtectionScope(ProtectionScopeKind.Channel, "network-1", "#clircs");
            store.SetChannelExemptOperators(channel, false);
            store.SetChannelBanSeconds(network, 600);
            store.SetChannelRule(network, ProtectionDetector.Text, threshold: 11);

            var effective = store.EffectiveChannel("network-1", "#clircs").Settings;
            Assert.False(effective.ExemptOperators);
            Assert.Equal(600, effective.BanSeconds);
            Assert.Equal(11, effective.Rules[ProtectionDetector.Text].Threshold);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void ChannelScopeResolvesCaseForms()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"clirc-protection-channel-key-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ProtectionSettingsStore(Path.Combine(directory, "protection.json"));
            store.SetChannelEnabled(
                new ProtectionScope(ProtectionScopeKind.Channel, "network-1", "#[ops]"),
                true);

            Assert.True(store.EffectiveChannel("network-1", "#{ops}", "#[ops]").Settings.Enabled);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VersionHasPrefix()
    {
        Assert.True(ProductInfo.DisplayName.StartsWith("clircs v", StringComparison.Ordinal));
        Assert.False(ProductInfo.Version.StartsWith('v'));
    }

    private static void ProductVersionUsesAssemblyMetadata()
    {
        var informationalVersion = typeof(ProductInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        var metadataSeparator = informationalVersion.IndexOf('+');
        var expected = metadataSeparator < 0
            ? informationalVersion
            : informationalVersion[..metadataSeparator];

        Assert.Equal(expected, ProductInfo.Version);
    }

    private static void DotNetSurfaceUsesClircsNaming()
    {
        Assert.Equal("Clircs", typeof(ProductInfo).Namespace!);
        Assert.Equal("Clircs.Core", typeof(ProductInfo).Assembly.GetName().Name!);
        Assert.Equal("Clircs.Commands", typeof(global::Clircs.Commands.CommandLineParser).Namespace!);
        Assert.Equal("clircs", typeof(ClientApplication).Assembly.GetName().Name!);
    }

    private static ProtectionEvidence Evidence(
        NetworkSessionId network,
        string actor,
        DateTimeOffset timestamp,
        ProtectionDetector detector = ProtectionDetector.Text,
        string text = "hello") =>
        new(network, detector, actor, "#clirc", text, timestamp);
}
