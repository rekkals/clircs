using Clircs.Identity;
using Clircs.Protocol;
using Clircs.Protocol.Testing;
using Clircs.Sessions;
using Clircs.State;

namespace Clircs.Core.Tests;

internal static class SessionTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("005 updates only its network's server features", IsupportUpdatesFeatures);
        suite.Add("005 retains advertised flags and values", IsupportRetainsAllFeatures);
        suite.Add("005 removals restore safe feature defaults", IsupportRemovalsRestoreDefaults);
        suite.Add("malformed known 005 values do not replace valid negotiated state", MalformedIsupportIsRejectedAtomically);
        suite.Add("CASEMAPPING changes reindex pending IRC nickname state", PendingNamesFollowNegotiatedCaseMapping);
        suite.Add("STATUSMSG channel messages route to the underlying channel", StatusMessageRouting);
        suite.Add("daemon detection is advisory and connection-specific", DaemonDetectionIsAdvisory);
        suite.Add("major daemon registration signatures retain their advertised capabilities", MajorDaemonSignatures);
        suite.Add("major daemon WHOIS transcripts complete without raw numeric leakage", MajorDaemonWhoisTranscripts);
        suite.Add("channel messages route to channel buffers", ChannelMessageRouting);
        suite.Add("channel nickname mentions become highlight activity", ChannelHighlightRouting);
        suite.Add("private messages route to sender query buffers", QueryMessageRouting);
        suite.Add("CTCP ACTION becomes a semantic action", CtcpActionRouting);
        suite.Add("ordinary CTCP requests do not create query buffers", CtcpRequestsDoNotCreateQueries);
        suite.Add("INVITE carries structured personal-protection evidence", InviteCarriesProtectionEvidence);
        suite.Add("numeric 341 becomes a routed invite confirmation", InviteConfirmationIsFormatted);
        suite.Add("remote terminal escape characters are removed", TerminalEscapesAreRemoved);
        suite.Add("remote bidirectional controls cannot reorder terminal output", BidirectionalControlsAreRemoved);
        suite.Add("self nickname changes update session identity", SelfNickUpdatesIdentity);
        suite.Add("nickname failures are formatted without raw numerics", NicknameFailuresAreFormatted);
        suite.Add("channel chat carries the speaker's highest privilege prefix", ChannelChatCarriesPrivilegePrefix);
        suite.Add("MODE events render in the affected channel", ModeEventsRouteToChannel);
        suite.Add("numeric 324 silently synchronizes current channel modes", ModeNumericSynchronizesSilently);
        suite.Add("server-confirmed user modes remain synchronized", UserModesRemainSynchronized);
        suite.Add("JOIN failures are formatted without raw numerics", JoinFailuresAreFormatted);
        suite.Add("ISON replies remain internal instead of rendering raw 303", IsonRepliesAreSilent);
        suite.Add("MONITOR replies remain internal instead of rendering raw numerics", MonitorRepliesAreSilent);
        suite.Add("caller-ID permission and ACCEPT replies are formatted", CallerIdRepliesAreFormatted);
        suite.Add("incoming caller-ID blocks are formatted and routed", IncomingCallerIdBlockIsFormatted);
        suite.Add("channel permission errors are formatted without raw numerics", PermissionErrorsAreFormatted);
        suite.Add("numeric 333 renders topic setter metadata in the channel", TopicSetterRoutesToChannel);
        suite.Add("numeric 329 renders channel creation time in the channel", ChannelCreationRoutesToChannel);
        suite.Add("LINKS replies produce a formatted table without raw numerics", LinksProducesInformationBox);
        suite.Add("MOTD replies render without numeric prefixes", MotdNumericsAreHidden);
        suite.Add("WHOIS WHO and CTCP replies carry semantic routing fields", ResultRepliesAreTagged);
        suite.Add("WHOIS status appears only for away users", WhoisStatusIsAwayOnly);
        suite.Add("nickname WHO uses the labeled table without totals", NicknameWhoUsesTable);
        suite.Add("WHO formatting follows the requested input kind", WhoFormattingFollowsInputKind);
        suite.Add("WHOIS idle host and TLS fields are concise", WhoisFieldsAreConcise);
        suite.Add("overlapping WHOIS replies retain their request identities", OverlappingWhoisRequestsRetainIdentity);
        suite.Add("unknown WHOIS numerics retain all significant parameters", UnknownWhoisNumericsAreLossless);
        suite.Add("known numerics take precedence over WHOIS extension collection", KnownNumericPrecedesWhoisExtension);
        suite.Add("query response state is discarded on reconnect", QueryResponseStateResetsOnReconnect);
        suite.Add("Solanum connecting-from WHOIS is normalized as an actual host", SolanumWhoisActualHostIsNormalized);
        suite.Add("Plexus WHOIS host and modes are normalized", PlexusWhoisFieldsAreNormalized);
        suite.Add("failed WHOIS does not produce an empty trailing box", FailedWhoisHasNoTrailingBox);
        suite.Add("channel forwarding and URLs are formatted without raw numerics", ForwardAndChannelUrlAreFormatted);
        suite.Add("WHOWAS replies produce a formatted result without raw numerics", WhowasIsFormatted);
        suite.Add("716 and 717 combine into one routed message-guard notice", MessageGuardNumericsCombine);
        suite.Add("ordinary notices carry configurable notice routing", OrdinaryNoticesAreTagged);
        suite.Add("channel notices remain routed to their channel", ChannelNoticesRemainInChannel);
        suite.Add("traditional IRC colors are parsed without leaking control parameters", IrcColorsAreParsed);
        suite.Add("channel topics retain IRC formatting for history and the topic bar", TopicColorsArePreserved);
        suite.Add("structured IRC output preserves colors while measuring plain text", StructuredOutputPreservesIrcColors);
        suite.Add("outbound echo tracking consumes only exact self echoes", OutboundEchoTrackingIsNarrow);
        suite.Add("outbound notices never create conversation buffers", OutboundNoticesDoNotCreateBuffers);
        suite.Add("NAMES and WHO build synchronized channel member state", NamesAndWhoBuildMemberState);
        suite.Add("NAMES completion produces a semantic information box", NamesProducesInformationBox);
        suite.Add("JOIN PART QUIT KICK and NICK maintain channel membership", MembershipEventsMaintainState);
        suite.Add("self kicks use personal wording and semantic fields", SelfKickUsesPersonalWording);
        suite.Add("self KILL messages are formatted and classified", SelfKillIsFormatted);
        suite.Add("server away confirmations synchronize without raw numerics", AwayConfirmationsAreFormatted);
        suite.Add("authentication failure is formatted without raw 464", AuthenticationFailureIsFormatted);
        suite.Add("numeric 502 becomes a readable user-mode error", UserModeFailureIsFormatted);
        suite.Add("account login and hidden host numerics are formatted and synchronized", AccountAndHiddenHostAreFormatted);
        suite.Add("LIST replies produce a formatted routed channel table", ListProducesChannelTable);
        suite.Add("bouncer metadata separates client and upstream TLS", BouncerMetadataSeparatesTlsHops);
        suite.Add("PREFIX CHANMODES and ban changes update channel policy state", ModesAndBansMaintainState);
        suite.Add("channel list numerics produce formatted b e I and q results", ChannelListsAreFormatted);
        suite.Add("empty channel lists produce one concise result", EmptyChannelListsAreConcise);
    }

    private static void MalformedIsupportIsRejectedAtomically()
    {
        var (state, processor) = CreateProcessor();
        processor.Process(IrcMessageParser.Parse(
            ":server 005 me CASEMAPPING=ascii PREFIX=(qaohv)~&@%+ CHANMODES=beI,k,l,imnpst MODES=6 :supported"));

        processor.Process(IrcMessageParser.Parse(
            ":server 005 me CASEMAPPING=broken PREFIX=(ov)@ CHANMODES=b,k,l MODES=zero :supported"));

        Assert.Equal(IrcCaseMapping.Ascii, state.CaseMapping);
        Assert.Equal(IrcCaseMapping.Ascii, processor.Features.CaseMapping);
        Assert.Equal("(qaohv)~&@%+", processor.Features.Isupport["PREFIX"]!);
        Assert.Equal("beI,k,l,imnpst", processor.Features.Isupport["CHANMODES"]!);
        Assert.Equal("6", processor.Features.Isupport["MODES"]!);
        Assert.Equal('~', processor.Features.PrefixModes['q']);
        Assert.Equal("beI", processor.Features.ChannelModesA);
        Assert.Equal(6, processor.Features.ModesPerCommand);
    }

    private static void PendingNamesFollowNegotiatedCaseMapping()
    {
        var (_, processor) = CreateProcessor();
        processor.Process(IrcMessageParser.Parse(":server 005 me CASEMAPPING=ascii :supported"));
        var requestId = processor.BeginWhoisRequest("[Alice]", includeIdle: false);
        processor.Process(IrcMessageParser.Parse(":server 005 me CASEMAPPING=rfc1459 :supported"));
        processor.Process(IrcMessageParser.Parse(":server 311 me {Alice} user host * :Alice Person"));
        var completed = processor.Process(IrcMessageParser.Parse(":server 318 me {Alice} :End of WHOIS"));

        Assert.Equal(1, completed.Count);
        Assert.Equal(requestId.ToString("D"), completed[0].Fields!["outputRequestId"]!);
        Assert.Equal("Alice Person", completed[0].Presentation!.Fields!.Single(field => field.Label == "Name").Value);
    }

    private static void BouncerMetadataSeparatesTlsHops()
    {
        var (state, processor) = CreateProcessor();
        state.ResetForReconnect(clientTransportTls: true);

        processor.Process(IrcMessageParser.Parse(":irc.deft.com 001 me :Welcome"));
        Assert.Equal("irc.deft.com", state.ServerName!);
        Assert.True(state.ClientTransportTls);
        Assert.True(state.UpstreamTls == true);

        processor.Process(IrcMessageParser.Parse(":irc.deft.com 005 me NETWORK=EFNet ZNC=1.10 :are supported"));
        Assert.Equal("ZNC", state.BouncerName!);
        Assert.True(state.UpstreamTls is null);

        processor.BeginWhoisRequest("me", includeIdle: false, automatic: true);
        Assert.Equal(0, processor.Process(
            IrcMessageParser.Parse(":irc.deft.com 311 me me user host * :Test User")).Count);
        Assert.Equal(0, processor.Process(
            IrcMessageParser.Parse(":irc.deft.com 671 me me :is using a secure connection")).Count);
        Assert.Equal(0, processor.Process(
            IrcMessageParser.Parse(":irc.deft.com 318 me me :End of WHOIS")).Count);
        Assert.True(state.UpstreamTls == true);

        state.ResetForReconnect(clientTransportTls: false);
        Assert.Equal("ZNC", state.BouncerName!);
        Assert.False(state.ClientTransportTls);
        Assert.True(state.UpstreamTls is null);
    }

    private static void SelfKillIsFormatted()
    {
        var (state, processor) = CreateProcessor();
        var events = processor.Process(IrcMessageParser.Parse(":OperNick!oper@example KILL me :testing reconnect"));

        Assert.Equal(1, events.Count);
        Assert.Equal(state.StatusBuffer.Id, events[0].BufferId);
        Assert.Equal(SessionEventKind.Error, events[0].Kind);
        Assert.Equal("You were killed by OperNick: testing reconnect", events[0].Text);
        Assert.Equal("kill", events[0].Fields!["event"]!);
        Assert.Equal("OperNick", events[0].Fields!["actor"]!);
    }

    private static void AwayConfirmationsAreFormatted()
    {
        var (state, processor) = CreateProcessor();
        var away = processor.Process(IrcMessageParser.Parse(":server 306 me :You have been marked as being away"));
        Assert.True(state.IsAway);
        Assert.Equal("You are now marked away", away[0].Text);
        Assert.False(away[0].Text.Contains("306", StringComparison.Ordinal));

        var back = processor.Process(IrcMessageParser.Parse(":server 305 me :You are no longer marked as being away"));
        Assert.False(state.IsAway);
        Assert.Equal("You are no longer marked away", back[0].Text);
        Assert.False(back[0].Text.Contains("305", StringComparison.Ordinal));
    }

    private static void AuthenticationFailureIsFormatted()
    {
        var (_, processor) = CreateProcessor();
        var events = processor.Process(IrcMessageParser.Parse(":server 464 me :Password incorrect"));

        Assert.Equal(1, events.Count);
        Assert.Equal(SessionEventKind.Error, events[0].Kind);
        Assert.Equal("Authentication failed: Password incorrect Automatic reconnect was not started", events[0].Text);
        Assert.False(events[0].Text.Contains("[464]", StringComparison.Ordinal));
    }

    private static void AccountAndHiddenHostAreFormatted()
    {
        var (state, processor) = CreateProcessor();
        var login = processor.Process(IrcMessageParser.Parse(
            ":server 900 slakker slakker!~slakker@2603:8081::1 slakker :You are now logged in as slakker"));
        var hidden = processor.Process(IrcMessageParser.Parse(
            ":server 396 slakker user/slakker :is now your hidden host (set by services.)"));

        Assert.Equal("Logged in as slakker", login[0].Text);
        Assert.Equal("slakker", state.AccountName!);
        Assert.Equal("Hidden host is now user/slakker (set by services)", hidden[0].Text);
        Assert.Equal("user/slakker", state.VisibleHost!);
        Assert.False(login[0].Text.Contains("900", StringComparison.Ordinal));
        Assert.False(hidden[0].Text.Contains("396", StringComparison.Ordinal));
    }

    private static void ListProducesChannelTable()
    {
        var (_, processor) = CreateProcessor();
        Assert.Equal(0, processor.Process(
            IrcMessageParser.Parse(":server 321 me Channel :Users Name")).Count);
        Assert.Equal(0, processor.Process(
            IrcMessageParser.Parse(":server 322 me ##chat 793 :Welcome to chat")).Count);
        Assert.Equal(0, processor.Process(
            IrcMessageParser.Parse(":server 322 me #clircs 12 :Command line IRC")).Count);
        var completed = processor.Process(
            IrcMessageParser.Parse(":server 323 me :End of /LIST"));

        Assert.Equal(1, completed.Count);
        Assert.Equal("list", completed[0].Fields!["outputFamily"]!);
        Assert.Equal("true", completed[0].Fields!["outputEnd"]!);
        Assert.Equal("Channels", completed[0].Presentation!.Title);
        Assert.True(completed[0].Presentation!.Table!.Columns.SequenceEqual(
            new[] { "Channel", "Users", "Topic" }));
        Assert.Equal("##chat", completed[0].Presentation!.Table!.Rows[0][0]);
        Assert.Equal("793", completed[0].Presentation!.Table!.Rows[0][1]);
        Assert.Equal("2 channel(s)", completed[0].Presentation!.Summary!);
        Assert.False(completed[0].Text.Contains("323", StringComparison.Ordinal));
    }

    private static void UserModeFailureIsFormatted()
    {
        var (_, processor) = CreateProcessor();
        var events = processor.Process(IrcMessageParser.Parse(":server 502 me :Can't change mode for other users"));

        Assert.Equal(1, events.Count);
        Assert.Equal(SessionEventKind.Error, events[0].Kind);
        Assert.Equal("Could not set user modes: Can't change mode for other users", events[0].Text);
        Assert.False(events[0].Text.Contains("[502]", StringComparison.Ordinal));
    }

    private static void IsupportUpdatesFeatures()
    {
        var (state, processor) = CreateProcessor();
        processor.Process(IrcMessageParser.Parse(":server 005 me CASEMAPPING=ascii CHANTYPES=#&+ MODES=4 NETWORK=TestNet PREFIX=(qaohv)~&@%+ CHANMODES=beI,k,l,imnpst :supported"));

        Assert.Equal(IrcCaseMapping.Ascii, state.CaseMapping);
        Assert.Equal(IrcCaseMapping.Ascii, processor.Features.CaseMapping);
        Assert.Equal("#&+", processor.Features.ChannelTypes);
        Assert.Equal(4, processor.Features.ModesPerCommand);
        Assert.Equal("TestNet", processor.Features.NetworkName!);
        Assert.True(processor.Features.TryGetPrefixMode('~', out var ownerMode));
        Assert.Equal('q', ownerMode);
        Assert.Equal('~', processor.Features.HighestPrefix(new HashSet<char> { 'v', 'q' })!.Value);
        Assert.True(processor.Features.ModeTakesParameter('I', adding: false));
    }

    private static void IsupportRetainsAllFeatures()
    {
        var (_, processor) = CreateProcessor();
        processor.Process(IrcMessageParser.Parse(
            ":server 005 me EXCEPTS INVEX STATUSMSG=@+ TARGMAX=NAMES:1,WHOIS:1 MONITOR=100 :are supported"));

        Assert.True(processor.Features.Supports("EXCEPTS"));
        Assert.True(processor.Features.TryGetIsupportValue("EXCEPTS", out var flagValue));
        Assert.True(flagValue is null);
        Assert.True(processor.Features.TryGetIsupportValue("TARGMAX", out var targmax));
        Assert.Equal("NAMES:1,WHOIS:1", targmax!);
        Assert.Equal("@+", processor.Features.StatusMessagePrefixes);
    }

    private static void IsupportRemovalsRestoreDefaults()
    {
        var (state, processor) = CreateProcessor();
        processor.Process(IrcMessageParser.Parse(
            ":server 005 me CASEMAPPING=ascii CHANTYPES=#! MODES=6 PREFIX=(qaohv)~&@%+ STATUSMSG=@+ :supported"));
        processor.Process(IrcMessageParser.Parse(
            ":server 005 me -CASEMAPPING -CHANTYPES -MODES -PREFIX -STATUSMSG :supported"));

        Assert.Equal(IrcCaseMapping.Rfc1459, state.CaseMapping);
        Assert.Equal(IrcCaseMapping.Rfc1459, processor.Features.CaseMapping);
        Assert.Equal("#&", processor.Features.ChannelTypes);
        Assert.Equal(3, processor.Features.ModesPerCommand);
        Assert.Equal(string.Empty, processor.Features.StatusMessagePrefixes);
        Assert.False(processor.Features.Supports("PREFIX"));
        Assert.True(processor.Features.TryGetPrefixMode('@', out var opMode));
        Assert.Equal('o', opMode);
        Assert.False(processor.Features.TryGetPrefixMode('~', out _));
    }

    private static void StatusMessageRouting()
    {
        var (state, processor) = CreateProcessor();
        processor.Process(IrcMessageParser.Parse(":server 005 me STATUSMSG=@+ :supported"));

        var message = processor.Process(
            IrcMessageParser.Parse(":alice!u@h PRIVMSG @#clirc :operators only"));
        var notice = processor.Process(
            IrcMessageParser.Parse(":alice!u@h NOTICE +#clirc :voiced users"));

        Assert.True(state.TryGetBuffer("#clirc", out var channel));
        Assert.Equal(channel!.Id, message[0].BufferId);
        Assert.Equal(channel.Id, notice[0].BufferId);
        Assert.False(state.TryGetBuffer("@#clirc", out _));
        Assert.False(state.TryGetBuffer("alice", out _));
        Assert.Equal("#clirc", message[0].Fields!["channel"]!);
        Assert.Equal("#clirc", notice[0].Fields!["channel"]!);
    }

    private static void DaemonDetectionIsAdvisory()
    {
        var (_, processor) = CreateProcessor();
        processor.Process(IrcMessageParser.Parse(
            ":irc.example 004 me irc.example unrealircd-6.2.6 iowghraAsORTVSxNCWqBzvdHtGp lvhopsmntikrRcaqOALQbSeIKVfMCuzNTGjZ"));
        Assert.Equal(IrcDaemonFamily.Unreal, processor.Features.DaemonFamily);
        Assert.Equal("unrealircd-6.2.6", processor.Features.ServerSoftware!);

        processor.ResetForReconnect("me");
        processor.Process(IrcMessageParser.Parse(
            ":irc.example 005 me IRCD=ngIRCd PREFIX=(qaohv)~&@%+ :supported"));
        Assert.Equal(IrcDaemonFamily.NgIRCd, processor.Features.DaemonFamily);
        Assert.Equal(string.Empty, processor.Features.ServerSoftware ?? string.Empty);
        Assert.True(processor.Features.TryGetPrefixMode('%', out var halfopMode));
        Assert.Equal('h', halfopMode);
    }

    private static void MajorDaemonSignatures()
    {
        var signatures = new[]
        {
            (IrcDaemonFamily.Ratbox, "ircd-ratbox-3.0.10", "PREFIX=(ov)@+ CHANMODES=eIbq,k,flj,CFLMPQScgimnprstz STATUSMSG=@+"),
            (IrcDaemonFamily.Solanum, "solanum-1.0", "PREFIX=(ov)@+ CHANMODES=beI,k,l,imnpst STATUSMSG=@+"),
            (IrcDaemonFamily.Hybrid, "ircd-hybrid-8.2.47", "PREFIX=(qaohv)~&@%+ CHANMODES=beI,k,l,imnpst STATUSMSG=~&@%+"),
            (IrcDaemonFamily.Bahamut, "bahamut-2.1.5", "PREFIX=(ohv)@%+ CHANMODES=beI,k,l,imnpst STATUSMSG=@%+"),
            (IrcDaemonFamily.NgIRCd, "ngIRCd-28", "PREFIX=(qaohv)~&@%+ CHANMODES=beI,k,l,imnpst"),
            (IrcDaemonFamily.InspIRCd, "InspIRCd-4.11.0", "PREFIX=(qaohv)~&@%+ CHANMODES=beI,k,l,imnpst STATUSMSG=~&@%+"),
            (IrcDaemonFamily.Unreal, "UnrealIRCd-6.2.6", "PREFIX=(qaohv)~&@%+ CHANMODES=beI,k,l,imnpst STATUSMSG=~&@%+"),
            (IrcDaemonFamily.UndernetIrcu, "u2.10.12.19", "PREFIX=(ov)@+ CHANMODES=b,k,l,imnpst STATUSMSG=@+")
        };

        foreach (var (expected, software, tokens) in signatures)
        {
            var (_, processor) = CreateProcessor();
            processor.Process(IrcMessageParser.Parse(
                $":irc.example 004 me irc.example {software} iowghraAsORTVSxNCWqBzvdHtGp lvhopsmntikrRcaqOALQbSeIKVfMCuzNTGjZ"));
            processor.Process(IrcMessageParser.Parse($":irc.example 005 me {tokens} :are supported"));

            Assert.Equal(expected, processor.Features.DaemonFamily);
            Assert.True(processor.Features.Supports("PREFIX"));
            Assert.True(processor.Features.Supports("CHANMODES"));
            Assert.True(processor.Features.TryGetPrefixMode('@', out var opMode));
            Assert.Equal('o', opMode);
        }
    }

    private static void MajorDaemonWhoisTranscripts()
    {
        var transcripts = new[]
        {
            new[]
            {
                "< :irc.example 004 me irc.example ircd-ratbox-3.0.10 modes channelmodes",
                "< :irc.example 005 me CASEMAPPING=rfc1459 PREFIX=(ov)@+ CHANMODES=eIbq,k,flj,CFLMPQScgimnprstz :supported",
                "< :irc.example 311 me Alice user cloak * :Alice Person",
                "< :irc.example 338 me Alice real.example 192.0.2.1 :actually using host",
                "< :irc.example 318 me Alice :End of WHOIS"
            },
            new[]
            {
                "< :irc.example 004 me irc.example solanum-1.0 modes channelmodes",
                "< :irc.example 005 me CASEMAPPING=rfc1459 PREFIX=(ov)@+ CHANMODES=beI,k,l,imnpst :supported",
                "< :irc.example 311 me Alice user cloak * :Alice Person",
                "< :irc.example 330 me Alice account :is logged in as",
                "< :irc.example 378 me Alice :is connecting from user@real.example 192.0.2.1",
                "< :irc.example 671 me Alice :is using a secure connection",
                "< :irc.example 318 me Alice :End of WHOIS"
            },
            new[]
            {
                "< :irc.example 004 me irc.example ircd-hybrid-8.2.47 modes channelmodes",
                "< :irc.example 005 me CASEMAPPING=rfc1459 PREFIX=(qaohv)~&@%+ CHANMODES=beI,k,l,imnpst :supported",
                "< :irc.example 311 me Alice user cloak * :Alice Person",
                "< :irc.example 307 me Alice :is a registered nick",
                "< :irc.example 320 me Alice :is identified for this nick",
                "< :irc.example 318 me Alice :End of WHOIS"
            },
            new[]
            {
                "< :irc.example 004 me irc.example bahamut-2.1.5 modes channelmodes",
                "< :irc.example 005 me CASEMAPPING=ascii PREFIX=(ohv)@%+ CHANMODES=beI,k,l,imnpst :supported",
                "< :irc.example 311 me Alice user cloak * :Alice Person",
                "< :irc.example 307 me Alice :is a registered nick",
                "< :irc.example 318 me Alice :End of WHOIS"
            },
            new[]
            {
                "< :irc.example 004 me irc.example ngIRCd-28 modes channelmodes",
                "< :irc.example 005 me CASEMAPPING=ascii PREFIX=(qaohv)~&@%+ CHANMODES=beI,k,l,imnpst :supported",
                "< :irc.example 311 me Alice user cloak * :Alice Person",
                "< :irc.example 312 me Alice irc.example :Example server",
                "< :irc.example 318 me Alice :End of WHOIS"
            },
            new[]
            {
                "< :irc.example 004 me irc.example InspIRCd-4.11.0 modes channelmodes",
                "< :irc.example 005 me CASEMAPPING=rfc1459 PREFIX=(qaohv)~&@%+ CHANMODES=beI,k,l,imnpst :supported",
                "< :irc.example 311 me Alice user cloak * :Alice Person",
                "< :irc.example 379 me Alice :is using modes +iwx",
                "< :irc.example 318 me Alice :End of WHOIS"
            },
            new[]
            {
                "< :irc.example 004 me irc.example UnrealIRCd-6.2.6 modes channelmodes",
                "< :irc.example 005 me CASEMAPPING=rfc1459 PREFIX=(qaohv)~&@%+ CHANMODES=beI,k,l,imnpst :supported",
                "< :irc.example 311 me Alice user cloak * :Alice Person",
                "< :irc.example 330 me Alice account :is logged in as",
                "< :irc.example 671 me Alice :is using a secure connection",
                "< :irc.example 318 me Alice :End of WHOIS"
            }
        };

        foreach (var transcript in transcripts)
        {
            var (_, processor) = CreateProcessor();
            processor.BeginWhoisRequest("Alice", includeIdle: false);
            var events = new IrcTranscriptHarness().Replay(transcript)
                .SelectMany(processor.Process)
                .ToArray();
            var completed = events.Single(item => item.Fields?.GetValueOrDefault("outputEnd") == "true");
            Assert.Equal("WHOIS:", completed.Presentation!.Title);
            Assert.False(events.Any(item => item.Text.StartsWith("[", StringComparison.Ordinal)));
        }
    }

    private static void ChannelMessageRouting()
    {
        var (state, processor) = CreateProcessor();
        var events = processor.Process(IrcMessageParser.Parse(":alice!u@h PRIVMSG #clirc :hello channel"));

        Assert.Equal(1, events.Count);
        Assert.Equal(SessionEventKind.Message, events[0].Kind);
        Assert.Equal("<alice> hello channel", events[0].Text);
        Assert.True(state.TryGetBuffer("#clirc", out var buffer));
        Assert.Equal(buffer!.Id, events[0].BufferId);
    }

    private static void ChannelHighlightRouting()
    {
        var (_, processor) = CreateProcessor();
        var highlighted = processor.Process(IrcMessageParser.Parse(":alice!u@h PRIVMSG #clirc :hello, me!"));
        var embedded = processor.Process(IrcMessageParser.Parse(":alice!u@h PRIVMSG #clirc :someone is here"));

        Assert.Equal(SessionEventKind.Highlight, highlighted[0].Kind);
        Assert.Equal(SessionEventKind.Message, embedded[0].Kind);
    }

    private static void QueryMessageRouting()
    {
        var (state, processor) = CreateProcessor();
        var events = processor.Process(IrcMessageParser.Parse(":alice!u@h PRIVMSG me :hello privately"));

        Assert.True(state.TryGetBuffer("alice", out var buffer));
        Assert.Equal(BufferKind.Query, buffer!.Kind);
        Assert.Equal(buffer.Id, events[0].BufferId);
        Assert.Equal("true", events[0].Fields!["private"]!);
        Assert.Equal("u", events[0].Fields!["username"]!);
        Assert.Equal("h", events[0].Fields!["host"]!);
    }

    private static void CtcpActionRouting()
    {
        var (_, processor) = CreateProcessor();
        var events = processor.Process(IrcMessageParser.Parse(":alice!u@h PRIVMSG #clirc :\u0001ACTION waves\u0001"));

        Assert.Equal(SessionEventKind.Action, events[0].Kind);
        Assert.Equal("* alice waves", events[0].Text);
        Assert.Equal("waves", events[0].Fields!["message"]!);
    }

    private static void CtcpRequestsDoNotCreateQueries()
    {
        var (state, processor) = CreateProcessor();
        var request = processor.Process(IrcMessageParser.Parse(":alice!u@h PRIVMSG me :\u0001VERSION\u0001"));
        var pingRequest = processor.Process(IrcMessageParser.Parse(":alice!u@h PRIVMSG me :\u0001PING 1784866498\u0001"));
        var sentAt = DateTimeOffset.UtcNow.AddMilliseconds(-1250).ToUnixTimeMilliseconds();
        var reply = processor.Process(IrcMessageParser.Parse($":alice!u@h NOTICE me :\u0001PING {sentAt}\u0001"));

        Assert.False(state.TryGetBuffer("alice", out _));
        Assert.Equal(state.StatusBuffer.Id, request[0].BufferId);
        Assert.Equal("ctcp", request[0].Fields!["outputFamily"]!);
        Assert.Equal("true", request[0].Fields!["routeConfigured"]!);
        Assert.Equal("CTCP from alice: PING", pingRequest[0].Text);
        Assert.Equal("PING 1784866498", pingRequest[0].Fields!["message"]!);
        Assert.True(reply[0].Text.StartsWith("CTCP PING reply from alice: 1.", StringComparison.Ordinal));
        Assert.True(reply[0].Text.EndsWith(" seconds", StringComparison.Ordinal));
    }

    private static void InviteCarriesProtectionEvidence()
    {
        var (state, processor) = CreateProcessor();
        var events = processor.Process(IrcMessageParser.Parse(":alice!u@h INVITE me :#clirc"));

        Assert.Equal(1, events.Count);
        Assert.Equal(state.StatusBuffer.Id, events[0].BufferId);
        Assert.Equal("invite", events[0].Fields!["event"]!);
        Assert.Equal("true", events[0].Fields!["private"]!);
        Assert.Equal("#clirc", events[0].Fields!["channel"]!);
        Assert.Equal("invite", events[0].Fields!["outputFamily"]!);
        Assert.Equal("true", events[0].Fields!["routeConfigured"]!);
    }

    private static void InviteConfirmationIsFormatted()
    {
        var (state, processor) = CreateProcessor();
        var events = processor.Process(IrcMessageParser.Parse(":irc.example 341 me alice #clircs"));

        Assert.Equal(1, events.Count);
        Assert.Equal(state.StatusBuffer.Id, events[0].BufferId);
        Assert.Equal(SessionEventKind.Notice, events[0].Kind);
        Assert.Equal("Invited alice to #clircs", events[0].Text);
        Assert.Equal("341", events[0].Fields!["numeric"]!);
        Assert.Equal("invite", events[0].Fields!["outputFamily"]!);
        Assert.Equal("true", events[0].Fields!["routeConfigured"]!);
        Assert.Equal("true", events[0].Fields!["outputEnd"]!);
    }

    private static void TerminalEscapesAreRemoved()
    {
        var (_, processor) = CreateProcessor();
        var events = processor.Process(IrcMessageParser.Parse(":alice!u@h PRIVMSG #clirc :hello \u001b[31mred"));

        Assert.False(events[0].Text.Contains('\u001b'));
        Assert.Equal("<alice> hello [31mred", events[0].Text);
    }

    private static void BidirectionalControlsAreRemoved()
    {
        var (_, processor) = CreateProcessor();
        var events = processor.Process(IrcMessageParser.Parse(
            ":alice!u@h PRIVMSG #clirc :invoice.txt\u202Egpj.exe\u2069"));

        Assert.Equal("<alice> invoice.txtgpj.exe", events[0].Text);
        Assert.False(events[0].Text.Contains('\u202e'));
        Assert.False(events[0].Text.Contains('\u2069'));
    }

    private static void IrcColorsAreParsed()
    {
        var (_, processor) = CreateProcessor();
        var events = processor.Process(IrcMessageParser.Parse(
            ":Chat!service@example NOTICE #clirc :now serving \u000304,06color \u000308,09induced \u000311,04headaches\u000f today"));

        Assert.Equal(1, events.Count);
        Assert.Equal("-Chat- now serving color induced headaches today", events[0].Text);
        Assert.Equal("now serving color induced headaches today", events[0].Fields!["message"]!);
        var formatted = events[0].FormattedContent!;
        var color = formatted.Runs.Single(run => run.Text.Contains("color", StringComparison.Ordinal));
        var induced = formatted.Runs.Single(run => run.Text.Contains("induced", StringComparison.Ordinal));
        var headaches = formatted.Runs.Single(run => run.Text.Contains("headaches", StringComparison.Ordinal));
        Assert.Equal(4, color.Style.Foreground!.Value);
        Assert.Equal(6, color.Style.Background!.Value);
        Assert.Equal(8, induced.Style.Foreground!.Value);
        Assert.Equal(9, induced.Style.Background!.Value);
        Assert.Equal(11, headaches.Style.Foreground!.Value);
        Assert.Equal(4, headaches.Style.Background!.Value);
    }

    private static void TopicColorsArePreserved()
    {
        var (state, processor) = CreateProcessor();
        var events = processor.Process(IrcMessageParser.Parse(
            ":server 332 me #clircs :\u000304Red\u0003 and plain"));

        Assert.Equal(1, events.Count);
        Assert.Equal("Topic: Red and plain", events[0].Text);
        Assert.Equal("Red and plain", events[0].FormattedContent!.PlainText);
        Assert.Equal(4, events[0].FormattedContent!.Runs[0].Style.Foreground!.Value);
        Assert.True(state.TryGetChannel("#clircs", out var channel));
        Assert.True(channel!.Topic!.Contains('\u0003'));
        var header = IrcTextFormatting.Parse($"[#clircs] {channel.Topic}");
        Assert.Equal("[#clircs] Red and plain", header.PlainText);
        Assert.True(header.Runs.Any(run => run.Style.Foreground == 4));
    }

    private static void StructuredOutputPreservesIrcColors()
    {
        var (_, processor) = CreateProcessor();
        processor.BeginWhoRequest(["He-Man"]);
        processor.Process(IrcMessageParser.Parse(
            ":server 352 me * user host server He-Man H :0 \u000307,12I Have The Power!\u000f"));
        var presentation = processor.Process(
            IrcMessageParser.Parse(":server 315 me He-Man :End of WHO"))[0].Presentation!;

        var table = presentation.Table!;
        Assert.Equal("I Have The Power!", table.Rows[0][5]);
        var formatted = table.FormattedRows![0][5]!;
        Assert.Equal("I Have The Power!", formatted.PlainText);
        Assert.Equal(7, formatted.Runs[0].Style.Foreground!.Value);
        Assert.Equal(12, formatted.Runs[0].Style.Background!.Value);
    }

    private static void SelfNickUpdatesIdentity()
    {
        var (state, processor) = CreateProcessor();
        var channel = state.GetOrCreateBuffer(BufferKind.Channel, "#clircs");
        var query = state.GetOrCreateBuffer(BufferKind.Query, "Alice");
        var results = state.GetOrCreateBuffer(BufferKind.Results, "=whois");
        state.GetOrCreateBuffer(BufferKind.Diagnostics, "=debug");
        state.GetOrCreateBuffer(BufferKind.DccChat, "=Alice_");
        var events = processor.Process(IrcMessageParser.Parse(":me!u@h NICK :newnick"));
        Assert.Equal("newnick", processor.CurrentNickname);
        Assert.Equal(4, events.Count);
        Assert.True(events.All(entry => entry.Text == "You are now known as newnick"));
        Assert.True(events.All(entry => entry.Fields!["self"] == "true"));
        Assert.True(events.Select(entry => entry.BufferId).ToHashSet().SetEquals(
            [state.StatusBuffer.Id, channel.Id, query.Id, results.Id]));
    }

    private static void NicknameFailuresAreFormatted()
    {
        var (_, processor) = CreateProcessor();
        var inUse = processor.Process(IrcMessageParser.Parse(":server 433 me Taken :Nickname is already in use"));
        var unavailable = processor.Process(IrcMessageParser.Parse(":server 437 me Reserved :Nick/channel is temporarily unavailable"));

        Assert.Equal("Nickname is already in use: Taken", inUse[0].Text);
        Assert.Equal("Nickname is temporarily unavailable: Reserved", unavailable[0].Text);
        Assert.False(inUse[0].Text.Contains("433", StringComparison.Ordinal));
        Assert.Equal("true", unavailable[0].Fields!["routeActive"]!);
    }

    private static void ChannelChatCarriesPrivilegePrefix()
    {
        var (_, processor) = CreateProcessor();
        processor.Process(IrcMessageParser.Parse(":server 005 me PREFIX=(ov)@+ :supported"));
        processor.Process(IrcMessageParser.Parse(":server 353 me = #clirc :@Alice"));
        processor.Process(IrcMessageParser.Parse(":server 366 me #clirc :End of NAMES"));
        var events = processor.Process(IrcMessageParser.Parse(":Alice!u@h PRIVMSG #clirc :hello"));

        Assert.Equal("@", events[0].Fields!["nickPrefix"]!);
        Assert.Equal("<@Alice> hello", events[0].Text);
    }

    private static void ModeEventsRouteToChannel()
    {
        var (state, processor) = CreateProcessor();
        var events = processor.Process(IrcMessageParser.Parse(":ChanOp!u@h MODE #clirc +o Alice"));

        Assert.Equal(1, events.Count);
        Assert.Equal(SessionEventKind.Mode, events[0].Kind);
        Assert.Equal("ChanOp sets mode +o Alice", events[0].Text);
        Assert.True(state.TryGetBuffer("#clirc", out var buffer));
        Assert.Equal(buffer!.Id, events[0].BufferId);
    }

    private static void ModeNumericSynchronizesSilently()
    {
        var (state, processor) = CreateProcessor();
        var events = processor.Process(IrcMessageParser.Parse(":server 324 me #clirc +nt"));

        Assert.Equal(0, events.Count);
        Assert.True(state.TryGetChannel("#clirc", out var channel));
        Assert.True(channel!.Modes.ContainsKey('n'));
        Assert.True(channel.Modes.ContainsKey('t'));
    }

    private static void JoinFailuresAreFormatted()
    {
        var (_, processor) = CreateProcessor();
        var cases = new[]
        {
            ("471", "channel is full"),
            ("473", "channel is invite-only"),
            ("474", "you are banned from that channel"),
            ("475", "incorrect channel key"),
            ("476", "invalid channel name or mask")
        };

        foreach (var (numeric, reason) in cases)
        {
            var events = processor.Process(IrcMessageParser.Parse($":server {numeric} me #secret :server wording"));
            Assert.Equal(1, events.Count);
            Assert.Equal(SessionEventKind.Error, events[0].Kind);
            Assert.Equal($"Cannot join #secret: {reason}", events[0].Text);
            Assert.Equal("true", events[0].Fields!["joinError"]!);
            Assert.Equal("#secret", events[0].Fields!["channel"]!);
            Assert.False(events[0].Text.Contains(numeric, StringComparison.Ordinal));
        }
    }

    private static void IsonRepliesAreSilent()
    {
        var (_, processor) = CreateProcessor();
        var events = processor.Process(IrcMessageParser.Parse(":server 303 me :Alice Bob"));
        Assert.Equal(0, events.Count);
    }

    private static void MonitorRepliesAreSilent()
    {
        var (_, processor) = CreateProcessor();
        Assert.Equal(0, processor.Process(IrcMessageParser.Parse(":server 730 me :Alice!u@h,Bob!u@h")).Count);
        Assert.Equal(0, processor.Process(IrcMessageParser.Parse(":server 731 me :Carol!u@h")).Count);
        Assert.Equal(0, processor.Process(IrcMessageParser.Parse(":server 732 me :Alice,Bob")).Count);
        Assert.Equal(0, processor.Process(IrcMessageParser.Parse(":server 733 me :End of MONITOR list")).Count);
    }

    private static void CallerIdRepliesAreFormatted()
    {
        var (_, processor) = CreateProcessor();
        processor.Process(IrcMessageParser.Parse(":server 005 me CALLERID=g ACCEPT=20 :supported"));
        Assert.Equal(0, processor.Process(IrcMessageParser.Parse(":server 281 me :Alice Bob")).Count);
        var completed = processor.Process(IrcMessageParser.Parse(":server 282 me :End of ACCEPT list"));
        var full = processor.Process(IrcMessageParser.Parse(":server 456 me :Accept list is full"));
        var exists = processor.Process(IrcMessageParser.Parse(":server 457 me Alice :is already on your accept list"));
        var missing = processor.Process(IrcMessageParser.Parse(":server 458 me Bob :is not on your accept list"));

        Assert.Equal(1, completed.Count);
        Assert.Equal("ACCEPT", completed[0].Presentation!.Title);
        Assert.True(completed[0].Presentation!.Grid!.SequenceEqual(new[] { "Alice", "Bob" }));
        Assert.Equal("The server ACCEPT list is full", full[0].Text);
        Assert.Equal("Alice is already on your ACCEPT list", exists[0].Text);
        Assert.Equal("Bob is not on your ACCEPT list", missing[0].Text);
        Assert.Equal("true", full[0].Fields!["routeActive"]!);
        Assert.Equal("true", exists[0].Fields!["routeActive"]!);
        Assert.Equal("true", missing[0].Fields!["routeActive"]!);
    }

    private static void IncomingCallerIdBlockIsFormatted()
    {
        var (_, processor) = CreateProcessor();
        processor.Process(IrcMessageParser.Parse(":server 005 me CALLERID=R ACCEPT=20 :supported"));
        var events = processor.Process(IrcMessageParser.Parse(
            ":server 718 me Alice ~alice@example.test :is messaging you, and you have umode +R."));

        Assert.Equal(1, events.Count);
        Assert.Equal(SessionEventKind.MessageGuard, events[0].Kind);
        Assert.Equal("Alice [~alice@example.test] tried to message you while you have user mode +R", events[0].Text);
        Assert.Equal("messageguard", events[0].Fields!["outputFamily"]!);
        Assert.Equal("true", events[0].Fields!["routeConfigured"]!);
        Assert.Equal("718", events[0].Fields!["numeric"]!);
    }

    private static void PermissionErrorsAreFormatted()
    {
        var (_, processor) = CreateProcessor();
        var channel = processor.Process(IrcMessageParser.Parse(":server 482 me #clircs :You're not channel operator"));
        var general = processor.Process(IrcMessageParser.Parse(":server 481 me :Permission Denied"));
        var alreadyJoined = processor.Process(IrcMessageParser.Parse(":server 443 me slak ##chat :is already on channel"));

        Assert.Equal("You are not a channel operator in #clircs", channel[0].Text);
        Assert.Equal("Permission denied", general[0].Text);
        Assert.Equal("slak is already on ##chat", alreadyJoined[0].Text);
        Assert.False(channel[0].Text.Contains("482", StringComparison.Ordinal));
        Assert.False(general[0].Text.Contains("481", StringComparison.Ordinal));
        Assert.False(alreadyJoined[0].Text.Contains("443", StringComparison.Ordinal));
    }

    private static void MotdNumericsAreHidden()
    {
        var (state, processor) = CreateProcessor();
        var start = processor.Process(IrcMessageParser.Parse(":server 375 me :- server Message of the Day -"));
        var line = processor.Process(IrcMessageParser.Parse(":server 372 me :- Welcome to this server"));
        var end = processor.Process(IrcMessageParser.Parse(":server 376 me :End of MOTD"));

        Assert.Equal("- server Message of the Day -", start[0].Text);
        Assert.Equal("- Welcome to this server", line[0].Text);
        Assert.Equal("End of MOTD", end[0].Text);
        Assert.Equal(state.StatusBuffer.Id, line[0].BufferId);
        Assert.False(start[0].Text.Contains("375", StringComparison.Ordinal));
        Assert.False(line[0].Text.Contains("372", StringComparison.Ordinal));
        Assert.False(end[0].Text.Contains("376", StringComparison.Ordinal));
    }

    private static void TopicSetterRoutesToChannel()
    {
        var (state, processor) = CreateProcessor();
        var events = processor.Process(IrcMessageParser.Parse(":server 333 me #clirc Alice!user@host 1700000000"));

        Assert.True(state.TryGetChannel("#clirc", out var channel));
        Assert.Equal("Alice", channel!.TopicSetBy!);
        Assert.True(channel.TopicSetAt is not null);
        Assert.True(events[0].Text.StartsWith("Set by: Alice on ", StringComparison.Ordinal));
        Assert.False(events[0].Text.Contains("333", StringComparison.Ordinal));
        Assert.Equal(state.GetOrCreateBuffer(BufferKind.Channel, "#clirc").Id, events[0].BufferId);
    }

    private static void ChannelCreationRoutesToChannel()
    {
        var (state, processor) = CreateProcessor();
        var events = processor.Process(IrcMessageParser.Parse(":server 329 me #clirc 1700000000"));

        Assert.True(state.TryGetChannel("#clirc", out var channel));
        Assert.True(channel!.CreatedAt is not null);
        Assert.Equal(SessionEventKind.ChannelInfo, events[0].Kind);
        Assert.True(events[0].Text.StartsWith("Created: ", StringComparison.Ordinal));
        Assert.False(events[0].Text.Contains("329", StringComparison.Ordinal));
        Assert.Equal(state.GetOrCreateBuffer(BufferKind.Channel, "#clirc").Id, events[0].BufferId);
    }

    private static void LinksProducesInformationBox()
    {
        var (_, processor) = CreateProcessor();
        Assert.Equal(0, processor.Process(IrcMessageParser.Parse(":server 364 me irc.example.test hub.example.test :1 Example server")).Count);
        Assert.Equal(0, processor.Process(IrcMessageParser.Parse(":server 364 me hub.example.test hub.example.test :0 Root server")).Count);
        var events = processor.Process(IrcMessageParser.Parse(":server 365 me * :End of LINKS"));

        Assert.Equal(1, events.Count);
        Assert.Equal("links", events[0].Fields!["outputFamily"]!);
        Assert.Equal("true", events[0].Fields!["outputEnd"]!);
        Assert.Equal("Server links", events[0].Presentation!.Title);
        Assert.Equal("hub.example.test (0)", events[0].Presentation!.Table!.Rows[0][0]);
        Assert.Equal("Root server", events[0].Presentation!.Table!.Rows[0][1]);
        Assert.Equal("  irc.example.test (1)", events[0].Presentation!.Table!.Rows[1][0]);
        Assert.Equal("Example server", events[0].Presentation!.Table!.Rows[1][1]);
        Assert.False(events[0].Text.Contains("365", StringComparison.Ordinal));
    }

    private static void ResultRepliesAreTagged()
    {
        var (_, processor) = CreateProcessor();
        Assert.Equal(0, processor.Process(IrcMessageParser.Parse(":server 311 me Alice user host * :Alice \u001b[31mPerson")).Count);
        var whois = processor.Process(IrcMessageParser.Parse(":server 318 me Alice :End of WHOIS"));
        processor.BeginWhoRequest(["#clirc"]);
        processor.Process(IrcMessageParser.Parse(":server 352 me #clirc user1 host1 server Alice H@ :0 Alice Here"));
        processor.Process(IrcMessageParser.Parse(":server 352 me #clirc user2 host2 server Bob G*+ :1 Bob Away"));
        var whoEnd = processor.Process(IrcMessageParser.Parse(":server 315 me #clirc :End of WHO"));
        var ctcp = processor.Process(IrcMessageParser.Parse(":Alice!user@host NOTICE me :\u0001VERSION other-client\u0001"));

        Assert.Equal("whois", whois[0].Fields!["outputFamily"]!);
        Assert.Equal("318", whois[0].Fields!["numeric"]!);
        Assert.Equal("WHOIS:", whois[0].Presentation!.Title);
        Assert.Equal("Alice", whois[0].Presentation!.TitleHighlight!);
        Assert.Equal("Alice [31mPerson", whois[0].Presentation!.Fields!.Single(field => field.Label == "Name").Value);
        Assert.False(whois[0].Presentation!.Fields!.Any(field => field.Label == "Status"));
        Assert.Equal("who", whoEnd[0].Fields!["outputFamily"]!);
        Assert.Equal("true", whoEnd[0].Fields!["outputEnd"]!);
        Assert.Equal("here", whoEnd[0].Presentation!.Table!.Rows[0][1]);
        Assert.Equal("away (IRCop)", whoEnd[0].Presentation!.Table!.Rows[1][1]);
        Assert.Equal("ctcp", ctcp[0].Fields!["outputFamily"]!);
        Assert.Equal("CTCP VERSION reply from Alice: other-client", ctcp[0].Text);
    }

    private static void NicknameWhoUsesTable()
    {
        var (_, processor) = CreateProcessor();
        processor.BeginWhoRequest(["Alice"]);
        processor.Process(IrcMessageParser.Parse(":server 352 me * user host server Alice H* :0 Alice Person"));
        var events = processor.Process(IrcMessageParser.Parse(":server 315 me Alice :End of WHO"));

        Assert.Equal("WHO:", events[0].Presentation!.Title);
        Assert.Equal("Alice", events[0].Presentation!.TitleHighlight!);
        Assert.True(events[0].Presentation!.Table!.Columns.SequenceEqual(
            new[] { "Nick", "Status", "Address", "Channel", "Server", "Name" }));
        Assert.Equal("Alice", events[0].Presentation!.Table!.Rows[0][0]);
        Assert.Equal("here (IRCop)", events[0].Presentation!.Table!.Rows[0][1]);
        Assert.Equal("user@host", events[0].Presentation!.Table!.Rows[0][2]);
        Assert.Equal("Alice Person", events[0].Presentation!.Table!.Rows[0][5]);
        Assert.True(events[0].Presentation!.Table!.KeepAllColumns);
        Assert.True(events[0].Presentation!.Summary is null);
    }

    private static void WhoFormattingFollowsInputKind()
    {
        var (_, processor) = CreateProcessor();
        processor.BeginWhoRequest(["Al*", "o"]);
        processor.Process(IrcMessageParser.Parse(":server 352 me * user host server Alice H* :0 Alice Person"));
        var wildcard = processor.Process(IrcMessageParser.Parse(":server 315 me Al* :End of WHO"))[0].Presentation!;

        Assert.Equal("Al* o", wildcard.TitleHighlight!);
        Assert.True(wildcard.Table is not null);
        Assert.True(wildcard.Summary!.StartsWith("1 user:", StringComparison.Ordinal));
        Assert.Equal("*", wildcard.Table!.Rows[0][3]);

        processor.BeginWhoRequest(["#clirc"]);
        processor.Process(IrcMessageParser.Parse(":server 352 me #clirc user very.long.host.example irc.example Alice H@ :0 Alice Person"));
        var channel = processor.Process(IrcMessageParser.Parse(":server 315 me #clirc :End of WHO"))[0].Presentation!;
        Assert.True(channel.Table!.Columns.SequenceEqual(new[] { "Nick", "Status", "Address", "Server", "Name" }));
        Assert.Equal("user@very.long.host.example", channel.Table.Rows[0][2]);
        Assert.Equal("irc.example", channel.Table.Rows[0][3]);
        Assert.True(channel.Table.KeepAllColumns);
        Assert.Equal(PresentationTable.UnboundedWidth, channel.Table.MaximumWidths![2]);
        Assert.Equal(PresentationTable.UnboundedWidth, channel.Table.MaximumWidths![4]);

        processor.BeginWhoRequest(["Nobody"]);
        var empty = processor.Process(IrcMessageParser.Parse(":server 315 me Nobody :End of WHO"))[0].Presentation!;
        Assert.Equal("Nobody", empty.TitleHighlight!);
        Assert.Equal("No matching users.", empty.Summary!);
    }

    private static void WhoisFieldsAreConcise()
    {
        var (_, processor) = CreateProcessor();
        processor.BeginWhoisRequest("Alice", includeIdle: false);
        processor.Process(IrcMessageParser.Parse(":server 311 me Alice user cloak.example * :Alice Person"));
        processor.Process(IrcMessageParser.Parse(":server 312 me Alice irc.example :Example server"));
        processor.Process(IrcMessageParser.Parse(":server 317 me Alice 90 1700000000 :seconds idle, signon time"));
        processor.Process(IrcMessageParser.Parse(":server 338 me Alice cloak.example 192.0.2.1 :actually using host"));
        processor.Process(IrcMessageParser.Parse(":server 671 me Alice :is using a secure connection"));
        var ordinary = processor.Process(IrcMessageParser.Parse(":server 318 me Alice :End of WHOIS"))[0].Presentation!;

        Assert.False(ordinary.Fields!.Any(field => field.Label is "Nick" or "Idle" or "Sign-on" or "Secure" or "Actual host"));
        Assert.True(ordinary.Fields!.Single(field => field.Label == "Server").Value.EndsWith("[TLS]", StringComparison.Ordinal));

        processor.BeginWhoisRequest("Alice", includeIdle: true);
        processor.Process(IrcMessageParser.Parse(":server 311 me Alice user cloak.example * :Alice Person"));
        processor.Process(IrcMessageParser.Parse(":server 317 me Alice 90 1700000000 :seconds idle, signon time"));
        processor.Process(IrcMessageParser.Parse(":server 338 me Alice real.example 192.0.2.1 :actually using host"));
        var idle = processor.Process(IrcMessageParser.Parse(":server 318 me Alice :End of WHOIS"))[0].Presentation!;

        Assert.Equal("1m 30s", idle.Fields!.Single(field => field.Label == "Idle").Value);
        Assert.True(idle.Fields!.Any(field => field.Label == "Sign-on"));
        Assert.Equal("real.example", idle.Fields!.Single(field => field.Label == "Actual host").Value);
    }

    private static void OverlappingWhoisRequestsRetainIdentity()
    {
        var (_, processor) = CreateProcessor();
        var aliceRequest = processor.BeginWhoisRequest("Alice", includeIdle: false);
        var bobRequest = processor.BeginWhoisRequest("Bob", includeIdle: false);

        processor.Process(IrcMessageParser.Parse(":server 311 me Alice alice host.example * :Alice Person"));
        var alice = processor.Process(IrcMessageParser.Parse(":server 318 me Alice :End of WHOIS"));
        processor.Process(IrcMessageParser.Parse(":server 311 me Bob bob host.example * :Bob Person"));
        var bob = processor.Process(IrcMessageParser.Parse(":server 318 me Bob :End of WHOIS"));

        Assert.Equal(aliceRequest.ToString("D"), alice[0].Fields!["outputRequestId"]!);
        Assert.Equal(bobRequest.ToString("D"), bob[0].Fields!["outputRequestId"]!);
        Assert.Equal("Alice", alice[0].Presentation!.TitleHighlight!);
        Assert.Equal("Bob", bob[0].Presentation!.TitleHighlight!);
    }

    private static void UnknownWhoisNumericsAreLossless()
    {
        var (_, processor) = CreateProcessor();
        processor.BeginWhoisRequest("Alice", includeIdle: false);
        processor.Process(IrcMessageParser.Parse(":server 311 me Alice user host * :Alice Person"));
        processor.Process(IrcMessageParser.Parse(":server 799 me Alice account-name :has special status"));
        var completed = processor.Process(IrcMessageParser.Parse(":server 318 me Alice :End of WHOIS"));

        Assert.Equal(
            "account-name has special status",
            completed[0].Presentation!.Fields!.Single(field => field.Label == "Info (799)").Value);
    }

    private static void KnownNumericPrecedesWhoisExtension()
    {
        var (_, processor) = CreateProcessor();
        processor.BeginWhoisRequest("Alice", includeIdle: false);

        var channelError = processor.Process(
            IrcMessageParser.Parse(":server 443 me Alice #channel :is already on channel"));
        processor.Process(IrcMessageParser.Parse(":server 311 me Alice user host * :Alice Person"));
        var completed = processor.Process(IrcMessageParser.Parse(":server 318 me Alice :End of WHOIS"));

        Assert.Equal("Alice is already on #channel", channelError.Single().Text);
        Assert.False(completed.Single().Presentation!.Fields!
            .Any(field => field.Label == "Info (443)"));
    }

    private static void QueryResponseStateResetsOnReconnect()
    {
        var (_, processor) = CreateProcessor();
        processor.BeginWhoisRequest("Alice", includeIdle: false);
        processor.Process(IrcMessageParser.Parse(":server 311 me Alice old old.example * :Old Identity"));
        processor.Process(IrcMessageParser.Parse(":server 321 me Channel :Users Name"));
        processor.Process(IrcMessageParser.Parse(":server 322 me #old 10 :Old channel"));

        processor.ResetForReconnect("me");
        processor.BeginWhoisRequest("Alice", includeIdle: false);
        processor.Process(IrcMessageParser.Parse(":server 311 me Alice new new.example * :New Identity"));
        var whois = processor.Process(IrcMessageParser.Parse(":server 318 me Alice :End of WHOIS")).Single();
        processor.Process(IrcMessageParser.Parse(":server 321 me Channel :Users Name"));
        processor.Process(IrcMessageParser.Parse(":server 322 me #new 20 :New channel"));
        var list = processor.Process(IrcMessageParser.Parse(":server 323 me :End of LIST")).Single();

        Assert.Equal("New Identity", whois.Presentation!.Fields!.Single(field => field.Label == "Name").Value);
        Assert.Equal(1, list.Presentation!.Table!.Rows.Count);
        Assert.Equal("#new", list.Presentation.Table.Rows[0][0]);
    }

    private static void SolanumWhoisActualHostIsNormalized()
    {
        var (_, processor) = CreateProcessor();
        processor.BeginWhoisRequest("slakker", includeIdle: false);
        processor.Process(IrcMessageParser.Parse(
            ":server 311 me slakker ~slakker user/slakker * :slakker"));
        processor.Process(IrcMessageParser.Parse(
            ":server 378 me slakker :is connecting from *@2603:8081:3000:48b3:3290:ea00:a628:d912 2603:8081:3000:48b3:3290:ea00:a628:d912"));
        var presentation = processor.Process(
            IrcMessageParser.Parse(":server 318 me slakker :End of WHOIS"))[0].Presentation!;

        Assert.Equal(
            "2603:8081:3000:48b3:3290:ea00:a628:d912",
            presentation.Fields!.Single(field => field.Label == "Actual host").Value);
        Assert.False(presentation.Fields!.Any(field => field.Label == "Server info"));
    }

    private static void PlexusWhoisFieldsAreNormalized()
    {
        var (_, processor) = CreateProcessor();
        processor.BeginWhoisRequest("slakker", includeIdle: false);
        processor.Process(IrcMessageParser.Parse(
            ":server 311 me slakker ~slakker BC7D64BB:7B099910:94F236F:IP * :slakker"));
        processor.Process(IrcMessageParser.Parse(
            ":server 378 me slakker :is actually ~slakker@2603:8081:3000:48b3:fd59:d414:385c:3e70 [2603:8081:3000:48b3:fd59:d414:385c:3e70]"));
        processor.Process(IrcMessageParser.Parse(
            ":server 379 me slakker :is using modes +Sirx authflags: [none]"));
        var presentation = processor.Process(
            IrcMessageParser.Parse(":server 318 me slakker :End of WHOIS"))[0].Presentation!;

        Assert.Equal(
            "2603:8081:3000:48b3:fd59:d414:385c:3e70",
            presentation.Fields!.Single(field => field.Label == "Actual host").Value);
        Assert.Equal("+Sirx", presentation.Fields!.Single(field => field.Label == "Modes").Value);
        Assert.False(presentation.Fields!.Any(field => field.Label is "Server info" or "Auth flags"));
    }

    private static void ForwardAndChannelUrlAreFormatted()
    {
        var (state, processor) = CreateProcessor();
        var forward = processor.Process(
            IrcMessageParser.Parse(":server 470 me #chat ##chat :Forwarding to another channel"));
        var url = processor.Process(
            IrcMessageParser.Parse(":server 328 me ##chat :https://pastebin.com/raw/example"));

        Assert.Equal("Forwarded from #chat to ##chat", forward[0].Text);
        Assert.Equal("true", forward[0].Fields!["joinForward"]!);
        Assert.Equal(state.GetOrCreateBuffer(BufferKind.Channel, "##chat").Id, forward[0].BufferId);
        Assert.Equal("URL: https://pastebin.com/raw/example", url[0].Text);
        Assert.False(forward[0].Text.Contains("470", StringComparison.Ordinal));
        Assert.False(url[0].Text.Contains("328", StringComparison.Ordinal));
    }

    private static void WhowasIsFormatted()
    {
        var (_, processor) = CreateProcessor();
        Assert.Equal(0, processor.Process(
            IrcMessageParser.Parse(":server 314 me crwx ~crwx user/crwx * :crwx")).Count);
        Assert.Equal(0, processor.Process(
            IrcMessageParser.Parse(":server 312 me crwx lithium.libera.chat :Wed Jul 29 19:39:56 2026")).Count);
        var completed = processor.Process(
            IrcMessageParser.Parse(":server 369 me crwx :End of WHOWAS"));

        Assert.Equal(1, completed.Count);
        Assert.Equal("whowas", completed[0].Fields!["outputFamily"]!);
        Assert.Equal("WHOWAS:", completed[0].Presentation!.Title);
        Assert.Equal("crwx", completed[0].Presentation!.TitleHighlight!);
        Assert.Equal("~crwx@user/crwx",
            completed[0].Presentation!.Fields!.Single(field => field.Label == "Address").Value);
        Assert.Equal("lithium.libera.chat",
            completed[0].Presentation!.Fields!.Single(field => field.Label == "Server").Value);
        Assert.False(completed[0].Text.Contains("369", StringComparison.Ordinal));
    }

    private static void OrdinaryNoticesAreTagged()
    {
        var (_, processor) = CreateProcessor();
        var notice = processor.Process(
            IrcMessageParser.Parse(":NickServ!service@services NOTICE me :Registration complete"));

        Assert.Equal(1, notice.Count);
        Assert.Equal("notice", notice[0].Fields!["outputFamily"]!);
        Assert.Equal("true", notice[0].Fields!["routeConfigured"]!);
    }

    private static void ChannelNoticesRemainInChannel()
    {
        var (state, processor) = CreateProcessor();
        var notice = processor.Process(
            IrcMessageParser.Parse(":service!service@example NOTICE #clircs :Channel announcement"));

        Assert.Equal(1, notice.Count);
        Assert.Equal(state.GetOrCreateBuffer(BufferKind.Channel, "#clircs").Id, notice[0].BufferId);
        var fields = notice[0].Fields!;
        Assert.True(fields.GetValueOrDefault("outputFamily") is null);
        Assert.True(fields.GetValueOrDefault("routeConfigured") is null);
    }

    private static void FailedWhoisHasNoTrailingBox()
    {
        var (_, processor) = CreateProcessor();
        processor.BeginWhoisRequest("Nobody", includeIdle: false);

        var failure = processor.Process(IrcMessageParser.Parse(":server 401 me Nobody :No such nick/channel"));
        var trailing = processor.Process(IrcMessageParser.Parse(":server 318 me Nobody :End of WHOIS"));

        Assert.Equal(1, failure.Count);
        Assert.Equal("No such nickname: Nobody", failure[0].Text);
        Assert.Equal("whois", failure[0].Fields!["outputFamily"]!);
        Assert.Equal("true", failure[0].Fields!["outputEnd"]!);
        Assert.Equal(0, trailing.Count);
    }

    private static void UserModesRemainSynchronized()
    {
        var (state, processor) = CreateProcessor();

        processor.Process(IrcMessageParser.Parse(":server 221 me +iw"));
        Assert.Equal("+iw", state.UserModes);

        processor.Process(IrcMessageParser.Parse(":me MODE me -w+s"));
        Assert.Equal("+is", state.UserModes);

        processor.Process(IrcMessageParser.Parse(":server MODE somebody +o"));
        Assert.Equal("+is", state.UserModes);
    }

    private static void MessageGuardNumericsCombine()
    {
        var (_, processor) = CreateProcessor();
        processor.Process(IrcMessageParser.Parse(":server 005 me CALLERID=R ACCEPT=20 :supported"));
        var blocked = processor.Process(IrcMessageParser.Parse(
            ":server 716 me Alice :Alice is in +R mode (server-side ignore)."));
        var notified = processor.Process(IrcMessageParser.Parse(
            ":server 717 me Alice :Alice has been informed that you messaged them."));

        Assert.Equal(0, blocked.Count);
        Assert.Equal(1, notified.Count);
        Assert.Equal(SessionEventKind.MessageGuard, notified[0].Kind);
        Assert.Equal("Message to Alice was blocked by server-side ignore (+R); they were notified", notified[0].Text);
        Assert.Equal("messageguard", notified[0].Fields!["outputFamily"]!);
        Assert.Equal("true", notified[0].Fields!["routeConfigured"]!);
    }

    private static void OutboundEchoTrackingIsNarrow()
    {
        var tracker = new OutboundEchoTracker();
        tracker.Track("PRIVMSG", "#[ops]", "hello");
        tracker.Track("PRIVMSG", "#[ops]", "hello");
        var selfEcho = IrcMessageParser.Parse(":me!u@h PRIVMSG #{OPS} :hello");

        Assert.True(tracker.TryConsume(selfEcho, "me", IrcCaseMapping.Rfc1459));
        Assert.True(tracker.TryConsume(selfEcho, "me", IrcCaseMapping.Rfc1459));
        Assert.False(tracker.TryConsume(selfEcho, "me", IrcCaseMapping.Rfc1459));

        tracker.Track("PRIVMSG", "#clirc", "another");
        Assert.False(tracker.TryConsume(
            IrcMessageParser.Parse(":somebody!u@h PRIVMSG #clirc :another"),
            "me",
            IrcCaseMapping.Rfc1459));
        Assert.False(tracker.TryConsume(
            IrcMessageParser.Parse(":me!u@h PRIVMSG #other :another"),
            "me",
            IrcCaseMapping.Rfc1459));
        Assert.True(tracker.TryConsume(
            IrcMessageParser.Parse(":me!u@h PRIVMSG #clirc :another"),
            "me",
            IrcCaseMapping.Rfc1459));
    }

    private static void WhoisStatusIsAwayOnly()
    {
        var (_, processor) = CreateProcessor();
        processor.Process(IrcMessageParser.Parse(":server 311 me Alice user host * :Alice Person"));
        processor.Process(IrcMessageParser.Parse(":server 301 me Alice :out to lunch"));
        var whois = processor.Process(IrcMessageParser.Parse(":server 318 me Alice :End of WHOIS"));

        Assert.Equal("away — out to lunch",
            whois[0].Presentation!.Fields!.Single(field => field.Label == "Status").Value);
        Assert.False(whois[0].Presentation!.Fields!.Any(field => field.Label == "Away"));
    }

    private static async ValueTask OutboundNoticesDoNotCreateBuffers()
    {
        var options = new Clircs.Networking.IrcConnectionOptions(
            new Clircs.Networking.IrcEndpoint("example.test", 6667, useTls: false),
            new Clircs.Networking.IrcIdentity(["me"], "test", "Test User"));
        await using var session = new IrcNetworkSession("test", options, new Clircs.Transport.TcpIrcTransportFactory());

        Assert.Equal(session.State.StatusBuffer.Id, session.ResolveNoticeBuffer("SomeNick").Id);
        Assert.Equal(session.State.StatusBuffer.Id, session.ResolveNoticeBuffer("#not-joined").Id);
        Assert.Equal(1, session.State.Buffers.Count);

        var joined = session.State.GetOrCreateBuffer(BufferKind.Channel, "#joined");
        Assert.Equal(joined.Id, session.ResolveNoticeBuffer("#joined").Id);
        Assert.Equal(2, session.State.Buffers.Count);
    }

    private static void NamesAndWhoBuildMemberState()
    {
        var (state, processor) = CreateProcessor();
        processor.Process(IrcMessageParser.Parse(":server 005 me PREFIX=(ov)@+ :supported"));
        processor.Process(IrcMessageParser.Parse(":me!self@localhost JOIN #clirc"));
        processor.Process(IrcMessageParser.Parse(":server 353 me = #clirc :@me @+Alice Bob"));
        processor.Process(IrcMessageParser.Parse(":server 366 me #clirc :End of NAMES"));
        processor.Process(IrcMessageParser.Parse(":server 352 me #clirc alice example.test server Alice H+ :0 Alice"));
        processor.Process(IrcMessageParser.Parse(":server 315 me #clirc :End of WHO"));

        Assert.True(state.TryGetChannel("#clirc", out var channel));
        Assert.True(channel!.NamesSynchronized);
        Assert.True(channel.WhoSynchronized);
        Assert.Equal(3, channel.Members.Count);
        Assert.True(channel.TryGetMember("alice", out var alice));
        Assert.Equal("alice", alice!.Username!);
        Assert.Equal("example.test", alice.Host!);
        Assert.Equal("Alice", alice.RealName!);
        Assert.True(alice.PrefixModes.Contains('o'));
        Assert.True(alice.PrefixModes.Contains('v'));
        processor.Process(IrcMessageParser.Parse(":server MODE #clirc -o Alice"));
        Assert.False(alice.PrefixModes.Contains('o'));
        Assert.True(alice.PrefixModes.Contains('v'));
        Assert.True(channel.TryGetMember("ME", out var self));
        Assert.True(self!.PrefixModes.Contains('o'));
    }

    private static void NamesProducesInformationBox()
    {
        var (_, processor) = CreateProcessor();
        processor.Process(IrcMessageParser.Parse(":server 353 me = #clirc :@me +Alice Bob"));
        var events = processor.Process(IrcMessageParser.Parse(":server 366 me #clirc :End of NAMES"));

        Assert.Equal("Users (#clirc): 3, Ops: 1, Voice: 1, Normal: 1", events[0].Presentation!.Title);
        Assert.Equal("#clirc", events[0].Presentation!.TitleHighlight!);
        Assert.Equal(3, events[0].Presentation!.Grid!.Count);
        Assert.True(events[0].Presentation!.Grid!.Contains("@me"));
        Assert.True(events[0].Presentation!.Grid!.Contains("+Alice"));
        Assert.True(events[0].Presentation!.BracketGridCells);
        Assert.True(events[0].Presentation!.Table is null);
        Assert.True(events[0].Presentation!.Summary is null);
    }

    private static void MembershipEventsMaintainState()
    {
        var (state, processor) = CreateProcessor();
        processor.Process(IrcMessageParser.Parse(":me!self@localhost JOIN #clirc"));
        var joinEvents = processor.Process(IrcMessageParser.Parse(":Alice!user@host JOIN #clirc"));
        processor.Process(IrcMessageParser.Parse(":Bob!user@elsewhere JOIN #clirc"));
        Assert.True(state.TryGetChannel("#clirc", out var channel));
        Assert.Equal(3, channel!.Members.Count);
        Assert.Equal("Alice", joinEvents[0].Fields!["nick"]!);
        Assert.Equal("user", joinEvents[0].Fields!["username"]!);
        Assert.Equal("host", joinEvents[0].Fields!["host"]!);

        var nickEvents = processor.Process(IrcMessageParser.Parse(":Alice!user@host NICK Alicia"));
        Assert.True(channel.TryGetMember("Alicia", out _));
        Assert.False(channel.TryGetMember("Alice", out _));
        Assert.Equal(channel.Name, state.Buffers.Single(buffer => buffer.Id == nickEvents[0].BufferId).Name);

        processor.Process(IrcMessageParser.Parse(":Bob!user@elsewhere QUIT :gone"));
        Assert.False(channel.TryGetMember("Bob", out _));
        processor.Process(IrcMessageParser.Parse(":ChanOp!op@host KICK #clirc Alicia :bye"));
        Assert.False(channel.TryGetMember("Alicia", out _));
        var selfPart = processor.Process(IrcMessageParser.Parse(":me!self@localhost PART #clirc :leaving"));
        Assert.False(state.TryGetChannel("#clirc", out _));
        Assert.Equal("self", selfPart[0].Fields!["username"]!);
        Assert.Equal("localhost", selfPart[0].Fields!["host"]!);
        Assert.Equal("true", selfPart[0].Fields!["self"]!);
    }

    private static void ModesAndBansMaintainState()
    {
        var (state, processor) = CreateProcessor();
        processor.Process(IrcMessageParser.Parse(":server 005 me PREFIX=(ov)@+ CHANMODES=beI,k,l,imnpst :supported"));
        processor.Process(IrcMessageParser.Parse(":me!self@localhost JOIN #clirc"));
        processor.Process(IrcMessageParser.Parse(":Alice!user@host JOIN #clirc"));
        processor.Process(IrcMessageParser.Parse(":ChanOp!op@host MODE #clirc +ov me Alice"));
        processor.Process(IrcMessageParser.Parse(":ChanOp!op@host MODE #clirc +ntkl secret 50"));
        processor.Process(IrcMessageParser.Parse(":ChanOp!op@host MODE #clirc +b *!*@bad.host"));

        Assert.True(state.TryGetChannel("#clirc", out var channel));
        Assert.True(channel!.TryGetMember("me", out var self));
        Assert.True(self!.PrefixModes.Contains('o'));
        Assert.True(channel.TryGetMember("Alice", out var alice));
        Assert.True(alice!.PrefixModes.Contains('v'));
        Assert.True(channel.Modes.ContainsKey('n'));
        Assert.Equal("secret", channel.Modes['k']!);
        Assert.Equal("50", channel.Modes['l']!);
        Assert.True(channel.Bans.Contains("*!*@bad.host"));

        processor.Process(IrcMessageParser.Parse(":ChanOp!op@host MODE #clirc -vb Alice *!*@bad.host"));
        Assert.False(alice.PrefixModes.Contains('v'));
        Assert.False(channel.Bans.Contains("*!*@bad.host"));
        processor.Process(IrcMessageParser.Parse(":server 367 me #clirc *!*@listed.host ChanOp 1"));
        processor.Process(IrcMessageParser.Parse(":server 368 me #clirc :End of channel ban list"));
        Assert.True(channel.BanListSynchronized);
        Assert.True(channel.Bans.Contains("*!*@listed.host"));
    }

    private static void ChannelListsAreFormatted()
    {
        var (state, processor) = CreateProcessor();
        processor.Process(IrcMessageParser.Parse(
            ":server 005 me PREFIX=(ov)@+ CHANMODES=beIq,k,l,imnpst :supported"));
        processor.Process(IrcMessageParser.Parse(":me!self@localhost JOIN #clirc"));

        var cases = new[]
        {
            (Entry: ":server 367 me #clirc *!*@banned.host ChanOp 1784330926",
                End: ":server 368 me #clirc :End of Channel Ban List", Mode: 'b', Title: "BANS:"),
            (Entry: ":server 348 me #clirc *!*@excepted.host ChanOp 1784330926",
                End: ":server 349 me #clirc :End of Channel Exception List", Mode: 'e', Title: "BAN EXCEPTIONS:"),
            (Entry: ":server 346 me #clirc *!*@invited.host ChanOp 1784330926",
                End: ":server 347 me #clirc :End of Channel Invite List", Mode: 'I', Title: "INVITE EXCEPTIONS:"),
            (Entry: ":server 728 me #clirc q *!*@quiet.host ChanOp 1784330926",
                End: ":server 729 me #clirc :End of Channel Quiet List", Mode: 'q', Title: "QUIETS:")
        };

        foreach (var item in cases)
        {
            Assert.Equal(0, processor.Process(IrcMessageParser.Parse(item.Entry)).Count);
            var completed = processor.Process(IrcMessageParser.Parse(item.End));
            Assert.Equal(1, completed.Count);
            Assert.Equal(item.Title, completed[0].Presentation!.Title);
            Assert.Equal("#clirc", completed[0].Presentation!.TitleHighlight!);
            Assert.Equal(1, completed[0].Presentation!.Table!.Rows.Count);
            Assert.Equal(item.Mode.ToString(), completed[0].Fields!["listMode"]!);
            Assert.Equal(1, state.GetOrCreateChannel("#clirc").ChannelList(item.Mode).Count);
            Assert.Equal("ChanOp", state.GetOrCreateChannel("#clirc").ChannelList(item.Mode).Single().SetBy!);
        }
    }

    private static void EmptyChannelListsAreConcise()
    {
        var (_, processor) = CreateProcessor();
        processor.Process(IrcMessageParser.Parse(":me!self@localhost JOIN #clirc"));

        var completed = processor.Process(IrcMessageParser.Parse(
            ":server 368 me #clirc :End of Channel Ban List"));

        Assert.Equal(1, completed.Count);
        Assert.Equal("No bans set", completed[0].Text);
        Assert.True(completed[0].Presentation is null);
    }

    private static void SelfKickUsesPersonalWording()
    {
        var (state, processor) = CreateProcessor();
        processor.Process(IrcMessageParser.Parse(":me!self@localhost JOIN #clirc"));
        var events = processor.Process(IrcMessageParser.Parse(":ChanOp!op@host KICK #clirc me :take a break"));

        Assert.Equal("You were kicked by ChanOp (take a break)", events[0].Text);
        Assert.Equal("true", events[0].Fields!["self"]!);
        Assert.Equal("kick", events[0].Fields!["event"]!);
        Assert.False(state.TryGetChannel("#clirc", out _));
        Assert.True(state.TryGetBuffer("#clirc", out _));
    }

    private static (NetworkSessionState State, IrcSessionProcessor Processor) CreateProcessor()
    {
        var state = new NetworkSessionState(NetworkSessionId.New(), "test", IrcCaseMapping.Rfc1459);
        return (state, new IrcSessionProcessor(state, "me"));
    }
}
