using Clircs.ConsoleClient;
using Clircs.Identity;
using Clircs.Sessions;

namespace Clircs.Core.Tests;

internal static class PresentationTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("event presentation metadata has one typed interpretation", MetadataIsTyped);
        suite.Add("presentation sanitization retains layout metadata", SanitizationRetainsLayout);
        suite.Add("extended IRC colors map to the nearest console color", ExtendedColorsAreVisible);
    }

    private static void MetadataIsTyped()
    {
        var sessionEvent = new SessionEvent(
            NetworkSessionId.New(), BufferId.New(), SessionEventKind.Part, "ignored", DateTimeOffset.Now,
            new Dictionary<string, string?>
            {
                ["event"] = "quit", ["nick"] = "slakker", ["reason"] = "gone",
                ["clientResult"] = "TRUE", ["history.transientKey"] = "transfer.1"
            });
        var metadata = SessionEventPresentation.From(sessionEvent);
        Assert.Equal(SessionEventSubtype.Quit, metadata.Subtype);
        Assert.Equal("slakker", metadata.Nick!);
        Assert.True(metadata.IsClientResult);
        Assert.True(metadata.IsTransientHistory);
    }

    private static void SanitizationRetainsLayout()
    {
        var block = new PresentationBlock(
            "Title\u001b[31m", [new PresentationField("Label", "Value")],
            Grid: ["one"], BracketGridCells: true, GridColumns: 3, GridColumnWidth: 12,
            FieldLabelWidth: 19);
        var sanitized = SessionEventBuilder.SanitizePresentation(block)!;
        Assert.Equal("Title[31m", sanitized.Title);
        Assert.True(sanitized.BracketGridCells);
        Assert.Equal(3, sanitized.GridColumns!.Value);
        Assert.Equal(12, sanitized.GridColumnWidth!.Value);
        Assert.Equal(19, sanitized.FieldLabelWidth!.Value);
    }

    private static void ExtendedColorsAreVisible()
    {
        Assert.Equal(ConsoleColor.Red, ConsolePresenter.IrcColor(52, ConsoleColor.Gray));
        Assert.Equal(ConsoleColor.Blue, ConsolePresenter.IrcColor(60, ConsoleColor.Gray));
        Assert.Equal(ConsoleColor.Black, ConsolePresenter.IrcColor(88, ConsoleColor.Gray));
        Assert.Equal(ConsoleColor.White, ConsolePresenter.IrcColor(98, ConsoleColor.Gray));
    }
}
