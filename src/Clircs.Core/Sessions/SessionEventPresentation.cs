namespace Clircs.Sessions;

public enum SessionEventSubtype
{
    None,
    Startup,
    Kick,
    Quit,
    Ctcp
}

public readonly record struct SessionEventPresentation(
    SessionEventSubtype Subtype,
    string? Nick,
    string? Message,
    string? NickPrefix,
    string? Username,
    string? Host,
    string? Channel,
    string? Reason,
    string? Source,
    bool IsClientResult,
    bool IsHighlightEcho,
    bool IsTransientHistory)
{
    public static SessionEventPresentation From(SessionEvent sessionEvent)
    {
        var fields = sessionEvent.Fields;
        if (fields is null) return default;

        var eventName = fields.GetValueOrDefault("event");
        var subtype = eventName switch
        {
            "startup" => SessionEventSubtype.Startup,
            "kick" => SessionEventSubtype.Kick,
            "quit" => SessionEventSubtype.Quit,
            "ctcp" => SessionEventSubtype.Ctcp,
            _ => SessionEventSubtype.None
        };

        return new SessionEventPresentation(
            subtype,
            fields.GetValueOrDefault("nick"),
            fields.GetValueOrDefault("message"),
            fields.GetValueOrDefault("nickPrefix"),
            fields.GetValueOrDefault("username"),
            fields.GetValueOrDefault("host"),
            fields.GetValueOrDefault("channel"),
            fields.GetValueOrDefault("reason"),
            fields.GetValueOrDefault("source"),
            IsTrue(fields, "clientResult"),
            IsTrue(fields, "highlightEcho"),
            !string.IsNullOrWhiteSpace(fields.GetValueOrDefault("history.transientKey")));
    }

    private static bool IsTrue(IReadOnlyDictionary<string, string?> fields, string name) =>
        string.Equals(fields.GetValueOrDefault(name), "true", StringComparison.OrdinalIgnoreCase);
}
