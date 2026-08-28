using Clircs.ConsoleClient;
using Clircs.Identity;
using Clircs.Protection;
using Clircs.Users;
using System.Text.Json;

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
        suite.Add("repeat detection isolates distinct text", RepeatDetectionIsTextSpecific);
        suite.Add("join detection isolates complete client prefixes", JoinDetectionIsolatesActors);
        suite.Add("batched mode changes contribute one event per affected user", BatchedModesUseAffectedUserCount);
        suite.Add("privileged abuse detectors are not neutralized by operator exemption", PrivilegedDetectorsIgnoreOperatorExemption);
        suite.Add("friendly detector names use dotted mass operations and servop", FriendlyDetectorNamesAreCanonical);
        suite.Add("friendly protection status shows only actionable settings", FriendlyStatusShowsActionableSettings);
        suite.Add("protection settings persist global network and channel inheritance", SettingsPersistAndInherit);
        suite.Add("advanced protection changes remain sparse overrides", AdvancedChangesRemainSparseOverrides);
        suite.Add("channel protection resolves live and offline case forms", ChannelScopeResolvesCaseForms);
        suite.Add("version one protection settings migrate without losing overrides", VersionOneSettingsMigrate);
        suite.Add("monitor-only preview settings migrate into enforcement", MonitorPreviewMigratesToEnforcement);
        suite.Add("user-facing versions include a lowercase v prefix", VersionHasPrefix);
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

    private static void VersionOneSettingsMigrate()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"clirc-protection-migration-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "protection.json");
            var global = ProtectionSettings.Defaults();
            var network = global with { ChannelEnabled = true };
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                Version = 1,
                Global = global,
                Scopes = new Dictionary<string, ProtectionSettings> { ["network:legacy"] = network }
            }));

            var migrated = new ProtectionSettingsStore(path);
            Assert.True(migrated.Effective("legacy", "#clircs").Settings.ChannelEnabled);
            Assert.True(File.ReadAllText(path).Contains("\"Version\": 4", StringComparison.Ordinal));
            Assert.False(migrated.Effective("legacy", "#clircs").Settings.MonitorOnly);
            Assert.True(migrated.Effective("legacy", "#clircs").Settings.Rules.ContainsKey(ProtectionDetector.ChannelCtcp));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void DefaultsAreSafeAndExplicit()
    {
        var settings = ProtectionSettings.Defaults();

        Assert.False(settings.MonitorOnly);
        Assert.False(settings.ChannelEnabled);
        Assert.False(settings.PersonalEnabled);
        Assert.Equal(Enum.GetValues<ProtectionDetector>().Length, settings.Rules.Count);
        Assert.Equal(new ProtectionRule(true, 6, 4), settings.Rules[ProtectionDetector.Text]);
        Assert.Equal(new ProtectionRule(true, 3, 10), settings.Rules[ProtectionDetector.MassKick]);
        Assert.Equal(new ProtectionRule(true, 4, 30), settings.Rules[ProtectionDetector.Invite]);
    }

    private static void MonitorPreviewMigratesToEnforcement()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"clircs-protection-enforcement-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "protection.json");
            var networkScope = new ProtectionScope(ProtectionScopeKind.Network, "network-1");
            var store = new ProtectionSettingsStore(path);
            store.SetChannelEnabled(networkScope, true);
            store.SetChannelAction(networkScope, ChannelProtectionAction.KickBan);
            var preview = File.ReadAllText(path)
                .Replace("\"Version\": 4", "\"Version\": 3", StringComparison.Ordinal)
                .Replace("\"MonitorOnly\": false", "\"MonitorOnly\": true", StringComparison.Ordinal);
            File.WriteAllText(path, preview);

            var migrated = new ProtectionSettingsStore(path);
            var settings = migrated.Effective("network-1", "#clircs").Settings;
            Assert.True(settings.ChannelEnabled);
            Assert.Equal(ChannelProtectionAction.KickBan, settings.ChannelAction);
            Assert.False(settings.MonitorOnly);
            Assert.True(File.ReadAllText(path).Contains("\"Version\": 4", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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

        var counters = monitor.Counters(now);
        Assert.False(counters.Any(counter => counter.Actor == "alice"));
        Assert.True(counters.Any(counter => counter.Actor == "bob"));
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
        Assert.False(ClientApplication.OperatorExemptionApplies(ProtectionDetector.ServerOp));
        Assert.True(ClientApplication.OperatorExemptionApplies(ProtectionDetector.Text));
    }

    private static void FriendlyDetectorNamesAreCanonical()
    {
        Assert.Equal("mass.kick", ClientApplication.DetectorName(ProtectionDetector.MassKick));
        Assert.Equal("mass.deop", ClientApplication.DetectorName(ProtectionDetector.MassDeop));
        Assert.Equal("servop", ClientApplication.DetectorName(ProtectionDetector.ServerOp));
        Assert.Equal("ctcp.channel", ClientApplication.DetectorName(ProtectionDetector.ChannelCtcp));
        Assert.Equal("ctcp.user", ClientApplication.DetectorName(ProtectionDetector.Ctcp));
        Assert.Equal(ProtectionDetector.MassKick,
            ClientApplication.ParseFriendlyProtectionDetector("mass.kick", personal: false)!.Value);
        Assert.Equal(ProtectionDetector.MassDeop,
            ClientApplication.ParseFriendlyProtectionDetector("massDeop", personal: false)!.Value);
        Assert.Equal(ProtectionDetector.ServerOp,
            ClientApplication.ParseFriendlyProtectionDetector("servop", personal: false)!.Value);
        Assert.Equal(ProtectionDetector.ChannelCtcp,
            ClientApplication.ParseFriendlyProtectionDetector("ctcp.channel", personal: false)!.Value);
        Assert.Equal(ProtectionDetector.Ctcp,
            ClientApplication.ParseFriendlyProtectionDetector("ctcp.user", personal: true)!.Value);
    }

    private static void FriendlyStatusShowsActionableSettings()
    {
        var channel = ProtectionSettings.Defaults() with
        {
            ChannelEnabled = true,
            ChannelAction = ChannelProtectionAction.Kick
        };
        var kick = ClientApplication.FriendlyProtectionPresentation(
            "Channel protection: EFNet #clircs", channel, [ProtectionDetector.Text]);
        Assert.Equal("Protection,on;Action,kick", string.Join(';',
            kick.Fields!.Select(field => $"{field.Label},{field.Value}")));

        var kickBan = ClientApplication.FriendlyProtectionPresentation(
            "Channel protection: EFNet #clircs",
            channel with { ChannelAction = ChannelProtectionAction.KickBan, BanSeconds = 1800 },
            [ProtectionDetector.Text]);
        Assert.Equal("Protection,on;Action,kickban;Ban time,30m", string.Join(';',
            kickBan.Fields!.Select(field => $"{field.Label},{field.Value}")));

        var personal = ClientApplication.FriendlyProtectionPresentation(
            "Personal protection: EFNet",
            ProtectionSettings.Defaults() with { PersonalEnabled = true, MonitorOnly = true },
            [ProtectionDetector.PrivateMessage]);
        Assert.Equal("Protection,monitor only;Ignore time,45s", string.Join(';',
            personal.Fields!.Select(field => $"{field.Label},{field.Value}")));
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
            var channelScope = new ProtectionScope(ProtectionScopeKind.Channel, network, "#clirc");
            store.SetChannelEnabled(channelScope, true);
            store.SetRule(networkScope, ProtectionDetector.Text, threshold: 9, windowSeconds: 7);
            store.SetChannelAction(networkScope, ChannelProtectionAction.KickBan);
            store.SetBanSeconds(networkScope, 900);
            store.SetPersonalIgnoreSeconds(networkScope, 60);

            var reloaded = new ProtectionSettingsStore(path);
            Assert.True(reloaded.Effective(network, null).Settings.PersonalEnabled);
            Assert.True(reloaded.Effective(network, "#clirc").Settings.ChannelEnabled);
            Assert.Equal(9, reloaded.Effective(network, "#clirc").Settings.Rules[ProtectionDetector.Text].Threshold);
            Assert.Equal(7, reloaded.Effective(network, "#clirc").Settings.Rules[ProtectionDetector.Text].WindowSeconds);
            Assert.Equal(ChannelProtectionAction.KickBan, reloaded.Effective(network, "#clirc").Settings.ChannelAction);
            Assert.Equal(900, reloaded.Effective(network, "#clirc").Settings.BanSeconds);
            Assert.Equal(60, reloaded.Effective(network, "#clirc").Settings.PersonalIgnoreSeconds);
            Assert.Equal(ProtectionScopeKind.Channel, reloaded.Effective(network, "#clirc").Source.Kind);
            Assert.False(reloaded.Effective("other", "#clirc").Settings.ChannelEnabled);
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
            store.SetExemptOperators(channel, false);
            store.SetBanSeconds(network, 600);
            store.SetRule(network, ProtectionDetector.Text, threshold: 11);

            var effective = store.Effective("network-1", "#clircs").Settings;
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

            Assert.True(store.Effective("network-1", "#{ops}", "#[ops]").Settings.ChannelEnabled);
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
