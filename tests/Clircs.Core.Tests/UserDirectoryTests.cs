using Clircs.ConsoleClient;
using Clircs.Identity;
using Clircs.Infrastructure;
using Clircs.Protocol;
using Clircs.Users;

namespace Clircs.Core.Tests;

internal static class UserDirectoryTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("network user directories persist roles masks comments and channel policy", PersistenceRoundTrip);
        suite.Add("hostmask matching uses IRC case folding and deterministic specificity", MatchingIsDeterministic);
        suite.Add("role changes accept descriptive and ircN-style shorthand", RoleChangesParse);
        suite.Add("user flags and eligibility have distinct readable output", UserRoleOutputIsReadable);
        suite.Add("user list rows show matching masks first", UserRowsShowMatchingMasksFirst);
        suite.Add("damaged user directories are preserved", DamagedFileIsPreserved);
    }

    private static void PersistenceRoundTrip()
    {
        using var temporary = new TemporaryDirectory();
        var profileId = NetworkProfileId.New();
        var store = new UserDirectoryStore(temporary.Path);
        var directory = new NetworkUserDirectory(profileId);
        var alice = directory.Add("Alice", "*!user@example.test", UserRole.OperatorEligible);
        alice.ChangeRoles(UserRole.AutoOp | UserRole.Protected, UserRole.None, "#[ops]");
        alice.SetComment("trusted operator");
        alice.SetComment("channel owner", "#[ops]");
        var policyBan = directory.AddPolicyBan("*!*@blocked.example", ["#[ops]"], "repeat abuse");
        store.Save(directory);

        var loaded = store.Load(profileId);
        var reloaded = loaded.Find("alice");
        Assert.True(reloaded is not null);
        Assert.Equal(alice.Id, reloaded!.Id);
        Assert.Equal("*!user@example.test", reloaded.Hostmasks[0]);
        Assert.True(reloaded.EffectiveRoles("#{OPS}").HasFlag(UserRole.OperatorEligible));
        Assert.True(reloaded.EffectiveRoles("#{OPS}").HasFlag(UserRole.AutoOp));
        Assert.Equal("trusted operator", reloaded.Comment);
        Assert.Equal("channel owner", reloaded.GetChannelComment("#{OPS}")!);
        reloaded.SetComment(string.Empty, "#{OPS}");
        Assert.True(reloaded.GetChannelComment("#[ops]") is null);
        Assert.Equal(1, loaded.PolicyBans.Count);
        Assert.Equal(policyBan.Id, loaded.PolicyBans[0].Id);
        Assert.Equal("repeat abuse", loaded.PolicyBans[0].Reason);
    }

    private static void MatchingIsDeterministic()
    {
        var directory = new NetworkUserDirectory(NetworkProfileId.New());
        var broad = directory.Add("Broad", "*!*@*.example.test");
        var exact = directory.Add("Exact", "Nick!*@staff.example.test");

        var match = directory.Match("nICK!user@staff.example.test", IrcCaseMapping.Rfc1459);
        Assert.Equal(exact, match.User!);
        Assert.Equal("Nick!*@staff.example.test", match.Hostmask!);
        Assert.False(match.Conflict);
        Assert.False(directory.Match("nobody!u@elsewhere", IrcCaseMapping.Rfc1459).User is not null);
        Assert.True(NetworkUserDirectory.WildcardMatches("#[OPS]!*@*", "#{ops}!u@h", IrcCaseMapping.Rfc1459));
        Assert.True(broad != exact);
    }

    private static void RoleChangesParse()
    {
        var compact = UserRoleParser.ParseChanges("+oavf,-k");
        Assert.True(compact.Add.HasFlag(UserRole.OperatorEligible));
        Assert.True(compact.Add.HasFlag(UserRole.AutoOp));
        Assert.True(compact.Add.HasFlag(UserRole.VoiceEligible));
        Assert.True(compact.Add.HasFlag(UserRole.AutoVoice));
        Assert.True(compact.Add.HasFlag(UserRole.Protected));
        Assert.True(compact.Remove.HasFlag(UserRole.KickOnJoin));

        var descriptive = UserRoleParser.ParseChanges("+operator,+autoop,-protected");
        Assert.True(descriptive.Add.HasFlag(UserRole.OperatorEligible));
        Assert.True(descriptive.Add.HasFlag(UserRole.AutoOp));
        Assert.True(descriptive.Remove.HasFlag(UserRole.Protected));
    }

    private static void UserRoleOutputIsReadable()
    {
        var roles = UserRole.Protected | UserRole.AutoOp | UserRole.OperatorEligible;
        Assert.Equal("+ap", UserRoleParser.FormatFlags(roles));
        Assert.Equal("operator", UserRoleParser.FormatEligibility(roles));
        Assert.Equal("none", UserRoleParser.FormatFlags(UserRole.None));
        Assert.Equal("none", UserRoleParser.FormatEligibility(UserRole.None));
    }

    private static void UserRowsShowMatchingMasksFirst()
    {
        var user = new UserRecord(
            UserRecordId.New(),
            "rekkals",
            ["*!~rekkals@old.example", "*!~slakker@current.example"],
            UserRole.Protected);
        var rows = ClientApplication.UserRows(
            user,
            ["rekkals!~slakker@current.example"],
            IrcCaseMapping.Rfc1459);

        Assert.Equal(2, rows.Count);
        Assert.Equal("rekkals", rows[0][0]);
        Assert.Equal("*!~slakker@current.example", rows[0][1]);
        Assert.Equal("+p", rows[0][2]);
        Assert.Equal(string.Empty, rows[1][0]);
        Assert.Equal("*!~rekkals@old.example", rows[1][1]);
    }

    private static void DamagedFileIsPreserved()
    {
        using var temporary = new TemporaryDirectory();
        var profileId = NetworkProfileId.New();
        var store = new UserDirectoryStore(temporary.Path);
        var path = store.PathFor(profileId);
        File.WriteAllText(path, "{broken");

        Assert.Throws<InvalidDataException>(() => store.Load(profileId));
        Assert.Equal("{broken", File.ReadAllText(path));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"clirc-user-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
