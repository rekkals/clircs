using Clircs.Sessions;

namespace Clircs.ConsoleClient;

internal sealed record TerminalTheme(
    string Name,
    ConsoleColor Normal,
    ConsoleColor Dim,
    ConsoleColor Accent,
    ConsoleColor Label,
    ConsoleColor Message,
    ConsoleColor Highlight,
    ConsoleColor Action,
    ConsoleColor Notice,
    ConsoleColor Join,
    ConsoleColor Part,
    ConsoleColor Nick,
    ConsoleColor Mode,
    ConsoleColor Warning,
    ConsoleColor Error,
    ConsoleColor StatusForeground,
    ConsoleColor StatusBackground,
    string JoinMarker,
    string PartMarker,
    string InfoTop,
    string InfoSide,
    string InfoBottom,
    string StatusSeparator,
    bool ShowBufferName,
    bool ShowNickPrefix)
{
    public ConsoleColor Kick { get; init; } = ConsoleColor.Red;

    public ConsoleColor TopicForeground { get; init; } = ConsoleColor.Gray;

    public ConsoleColor TopicBackground { get; init; } = ConsoleColor.DarkBlue;

    public string GridOpen { get; init; } = "[ ";

    public string GridClose { get; init; } = " ]";

    public string HeaderSeparator { get; init; } = " | ";

    public ConsoleColor EventColor(SessionEventKind kind) => kind switch
    {
        SessionEventKind.Message => Message,
        SessionEventKind.Highlight => Highlight,
        SessionEventKind.Action => Action,
        SessionEventKind.Notice => Notice,
        SessionEventKind.Join => Join,
        SessionEventKind.Part => Part,
        SessionEventKind.Nick => Nick,
        SessionEventKind.Topic => Accent,
        SessionEventKind.ChannelInfo => Accent,
        SessionEventKind.ChannelSync => Label,
        SessionEventKind.MessageGuard => Notice,
        SessionEventKind.Mode => Mode,
        SessionEventKind.Protection => Warning,
        SessionEventKind.Error => Error,
        SessionEventKind.Diagnostic => Dim,
        _ => Normal
    };

    public ConsoleColor EventColor(SessionEvent sessionEvent)
    {
        var presentation = SessionEventPresentation.From(sessionEvent);
        if (presentation.IsClientResult) return Dim;
        return sessionEvent.Kind == SessionEventKind.Part && presentation.Subtype == SessionEventSubtype.Kick
            ? Kick
            : EventColor(sessionEvent.Kind);
    }

    public static IReadOnlyDictionary<string, TerminalTheme> BuiltIns { get; } =
        new[]
        {
            new TerminalTheme(
                "clircs", ConsoleColor.Gray, ConsoleColor.DarkGray, ConsoleColor.Cyan, ConsoleColor.DarkCyan,
                ConsoleColor.Gray, ConsoleColor.Yellow, ConsoleColor.Magenta, ConsoleColor.Yellow, ConsoleColor.Cyan, ConsoleColor.DarkCyan,
                ConsoleColor.Green, ConsoleColor.Cyan, ConsoleColor.Yellow, ConsoleColor.Red,
                ConsoleColor.Black, ConsoleColor.DarkCyan, "-->", "<--", "┌─ ", "│ ", "└─", " | ", false, true),
            new TerminalTheme(
                "phosphor", ConsoleColor.Gray, ConsoleColor.DarkGray, ConsoleColor.Green, ConsoleColor.DarkGreen,
                ConsoleColor.Green, ConsoleColor.White, ConsoleColor.Green, ConsoleColor.DarkGreen, ConsoleColor.Green, ConsoleColor.DarkGreen,
                ConsoleColor.White, ConsoleColor.DarkGreen, ConsoleColor.Yellow, ConsoleColor.Red,
                ConsoleColor.Black, ConsoleColor.DarkGreen, ">>>", "<<<", "+-- ", "| ", "+--", " :: ", false, true)
            {
                TopicForeground = ConsoleColor.Black,
                TopicBackground = ConsoleColor.DarkGreen
            },
            new TerminalTheme(
                "plain", ConsoleColor.Gray, ConsoleColor.Gray, ConsoleColor.Gray, ConsoleColor.Gray,
                ConsoleColor.Gray, ConsoleColor.Gray, ConsoleColor.Gray, ConsoleColor.Gray, ConsoleColor.Gray, ConsoleColor.Gray,
                ConsoleColor.Gray, ConsoleColor.Gray, ConsoleColor.Gray, ConsoleColor.Gray,
                ConsoleColor.Gray, ConsoleColor.Black, "-->", "<--", "", "", "", " | ", false, true)
            {
                TopicForeground = ConsoleColor.Black,
                TopicBackground = ConsoleColor.White
            }
        }.ToDictionary(theme => theme.Name, StringComparer.OrdinalIgnoreCase);
}

internal sealed record StatusActivity(int Number, SessionEventKind Kind);

internal sealed record StatusBarModel(IReadOnlyList<string> Fields, IReadOnlyList<StatusActivity> Activity);

internal enum HostmaskVisibility
{
    Full,
    UserHost,
    Host,
    Off
}
