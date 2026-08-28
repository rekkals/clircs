namespace Clircs.ConsoleClient;

internal sealed record WindowChromeModel(
    BufferHeaderModel Header,
    StatusBarModel Status,
    string Prompt);
