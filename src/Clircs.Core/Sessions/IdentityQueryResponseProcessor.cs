using Clircs.Protocol;
using Clircs.State;

namespace Clircs.Sessions;

internal sealed class IdentityQueryResponseProcessor
{
    private readonly NetworkSessionState _state;
    private readonly ServerFeatures _features;
    private readonly SessionEventBuilder _events;
    private readonly Queue<WhoRequest> _whoRequests = [];
    private readonly List<WhoResultRow> _untrackedWhoResults = [];
    private readonly Dictionary<Guid, WhoisRequest> _whoisRequests = [];
    private Dictionary<string, Queue<Guid>> _whoisRequestsByNick;
    private Dictionary<string, WhoisResult> _whoisResults;
    private Dictionary<string, List<WhowasResult>> _whowasResults;

    public IdentityQueryResponseProcessor(
        NetworkSessionState state,
        ServerFeatures features,
        SessionEventBuilder events)
    {
        _state = state;
        _features = features;
        _events = events;
        _whoisRequestsByNick = new Dictionary<string, Queue<Guid>>(NameComparer());
        _whoisResults = new Dictionary<string, WhoisResult>(NameComparer());
        _whowasResults = new Dictionary<string, List<WhowasResult>>(NameComparer());
    }

    public Guid BeginWho(IReadOnlyList<string> arguments, bool automatic)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var target = arguments.Count == 0 ? "*" : arguments[0];
        var input = arguments.Count == 0 ? target : string.Join(' ', arguments);
        var kind = _features.IsChannel(target)
            ? WhoRequestKind.Channel
            : target.Equals("0", StringComparison.Ordinal) || target.IndexOfAny(['*', '?']) >= 0
                ? WhoRequestKind.Broad
                : WhoRequestKind.Single;
        var request = new WhoRequest(Guid.NewGuid(), input, target, kind, automatic);
        _whoRequests.Enqueue(request);
        return request.Id;
    }

    public void CancelWho(Guid requestId)
    {
        if (_whoRequests.Count == 0) return;
        var retained = _whoRequests.Where(request => request.Id != requestId).ToArray();
        _whoRequests.Clear();
        foreach (var request in retained) _whoRequests.Enqueue(request);
    }

    public Guid BeginWhois(string nickname, bool includeIdle, bool automatic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);
        var request = new WhoisRequest(Guid.NewGuid(), nickname, includeIdle, automatic);
        _whoisRequests.Add(request.Id, request);
        if (!_whoisRequestsByNick.TryGetValue(nickname, out var requests))
        {
            requests = [];
            _whoisRequestsByNick.Add(nickname, requests);
        }
        requests.Enqueue(request.Id);
        return request.Id;
    }

    public void CancelWhois(Guid requestId)
    {
        if (!_whoisRequests.Remove(requestId, out var request) ||
            !_whoisRequestsByNick.TryGetValue(request.Nickname, out var requests)) return;
        var retained = requests.Where(id => id != requestId).ToArray();
        requests.Clear();
        foreach (var id in retained) requests.Enqueue(id);
        if (requests.Count == 0) _whoisRequestsByNick.Remove(request.Nickname);
    }

    public void Reset()
    {
        _whoRequests.Clear();
        _untrackedWhoResults.Clear();
        _whoisRequests.Clear();
        _whoisRequestsByNick = new Dictionary<string, Queue<Guid>>(NameComparer());
        _whoisResults = new Dictionary<string, WhoisResult>(NameComparer());
        _whowasResults = new Dictionary<string, List<WhowasResult>>(NameComparer());
    }

    public void ReindexNames()
    {
        _whoisRequestsByNick = Reindex(_whoisRequestsByNick, NameComparer());
        _whoisResults = Reindex(_whoisResults, NameComparer());
        _whowasResults = Reindex(_whowasResults, NameComparer());
    }

    public bool TryProcess(
        IrcMessage message,
        DateTimeOffset now,
        out IReadOnlyList<SessionEvent> sessionEvents)
    {
        var results = new List<SessionEvent>();
        switch (message.Command)
        {
            case "352":
                ApplyWho(message);
                if (message.Parameters.Count >= 8) AddWhoResult(message);
                break;
            case "315":
                CompleteWho(message, now, results);
                break;
            case "314":
                CollectWhowas(message);
                break;
            case "369":
                var whowas = CompleteWhowas(message, now);
                if (whowas is not null) results.Add(whowas);
                break;
            case "401":
                CompleteMissingWhois(message, now, results);
                break;
            case var command when IsWhoisNumeric(command):
                var whois = ProcessWhois(message, now);
                if (whois is not null) results.Add(whois);
                break;
            default:
                sessionEvents = [];
                return false;
        }

        sessionEvents = results;
        return true;
    }

    public bool TryCollectUnknownWhois(IrcMessage message)
    {
        if (!message.Command.All(char.IsDigit) || message.Parameters.Count < 3) return false;
        var nick = message.Parameters[1];
        if (PeekWhoisRequest(nick) is null) return false;
        if (!_whoisResults.TryGetValue(nick, out var result))
        {
            result = new WhoisResult(nick) { IncludeIdle = PeekWhoisRequest(nick)?.IncludeIdle ?? false };
            _whoisResults.Add(nick, result);
        }
        result.Extra.Add(new PresentationField(
            $"Info ({message.Command})", CleanWhoisText(string.Join(' ', message.Parameters.Skip(2)))));
        return true;
    }

    private void CompleteMissingWhois(IrcMessage message, DateTimeOffset now, ICollection<SessionEvent> results)
    {
        var missingNick = message.Parameters.Count >= 2 ? message.Parameters[1] : "unknown";
        var request = PeekWhoisRequest(missingNick);
        _whoisResults.Remove(missingNick);
        CompleteWhoisRequest(missingNick);
        if (request?.Automatic == true) return;
        results.Add(_events.Status(
            SessionEventKind.Error,
            $"No such nickname: {missingNick}",
            now,
            SessionEventBuilder.Fields(
                ("outputFamily", "whois"), ("outputTarget", missingNick),
                ("outputRequestId", request?.Id.ToString("D")),
                ("numeric", "401"), ("outputEnd", "true"))));
    }

    private void CompleteWho(IrcMessage message, DateTimeOffset now, ICollection<SessionEvent> results)
    {
        if (message.Parameters.Count >= 2 && _state.TryGetChannel(message.Parameters[1], out var whoChannel))
        {
            whoChannel!.WhoSynchronized = true;
        }
        var responseTarget = message.Parameters.Count >= 2 ? message.Parameters[1] : "*";
        var request = _whoRequests.Count > 0
            ? _whoRequests.Dequeue()
            : new WhoRequest(Guid.Empty, responseTarget, responseTarget,
                _features.IsChannel(responseTarget) ? WhoRequestKind.Channel :
                responseTarget.Equals("0", StringComparison.Ordinal) || responseTarget.IndexOfAny(['*', '?']) >= 0
                    ? WhoRequestKind.Broad : WhoRequestKind.Single,
                Automatic: false);
        var rows = request.Id == Guid.Empty ? _untrackedWhoResults.ToArray() : request.Rows.ToArray();
        _untrackedWhoResults.Clear();
        var here = rows.Count(row => row.Status.StartsWith("here", StringComparison.Ordinal));
        var away = rows.Length - here;
        PresentationBlock presentation;
        if (rows.Length == 0)
        {
            presentation = new PresentationBlock("WHO:", Summary: "No matching users.", TitleHighlight: request.Input);
        }
        else if (request.Kind == WhoRequestKind.Single)
        {
            presentation = new PresentationBlock(
                "WHO:", Table: WhoTable(rows, includeChannel: true), TitleHighlight: request.Input);
        }
        else
        {
            var operators = rows.Count(row => row.Status.EndsWith('*'));
            var noun = rows.Length == 1 ? "user" : "users";
            presentation = new PresentationBlock(
                "WHO:",
                Table: WhoTable(rows, includeChannel: request.Kind != WhoRequestKind.Channel),
                Summary: $"{rows.Length} {noun}: {here} here, {away} away, {operators} IRC operators",
                TitleHighlight: request.Input);
        }
        results.Add(_events.Status(
            SessionEventKind.Server,
            $"WHO {request.Input}: {rows.Length} result(s)",
            now,
            SessionEventBuilder.Fields(
                ("outputFamily", "who"), ("outputTarget", request.Target),
                ("outputRequestId", request.Id == Guid.Empty ? null : request.Id.ToString("D")),
                ("numeric", "315"), ("outputEnd", "true"),
                ("automatic", request.Automatic ? "true" : null)),
            presentation));
    }

    private void AddWhoResult(IrcMessage message)
    {
        var target = message.Parameters[1];
        var flags = message.Parameters[6];
        var status = flags.Contains('G') ? "away" : "here";
        if (flags.Contains('*')) status += "*";
        var privilege = new string(flags.Where(symbol => _features.TryGetPrefixMode(symbol, out _)).ToArray());
        var trailing = message.Parameters[7];
        var separator = trailing.IndexOf(' ');
        var realName = separator < 0 ? string.Empty : trailing[(separator + 1)..];
        var row = new WhoResultRow(
            message.Parameters[5], status, privilege, target,
            $"{message.Parameters[2]}@{message.Parameters[3]}", message.Parameters[4], realName);
        if (_whoRequests.Count > 0) _whoRequests.Peek().Rows.Add(row);
        else _untrackedWhoResults.Add(row);
    }

    private void ApplyWho(IrcMessage message)
    {
        if (message.Parameters.Count < 7) return;
        var channelName = message.Parameters[1];
        if (!_features.IsChannel(channelName) || !_state.TryGetChannel(channelName, out var channel)) return;
        var trailing = message.Parameters.Count >= 8 ? message.Parameters[7] : string.Empty;
        var separator = trailing.IndexOf(' ');
        var realName = separator < 0 ? null : trailing[(separator + 1)..];
        var member = channel!.GetOrAddMember(
            message.Parameters[5], message.Parameters[2], message.Parameters[3], realName);
        foreach (var symbol in message.Parameters[6])
        {
            if (_features.TryGetPrefixMode(symbol, out var mode)) member.AddPrefixMode(mode);
        }
    }

    private static string FormatWhoNick(WhoResultRow row) => $"{row.Privilege.FirstOrDefault()}" switch
    {
        "\0" => row.Nick,
        var prefix => prefix + row.Nick
    };

    private static string FormatWhoStatus(WhoResultRow row) =>
        row.Status.EndsWith('*') ? $"{row.Status.TrimEnd('*')} (IRCop)" : row.Status;

    private static PresentationTable WhoTable(IReadOnlyList<WhoResultRow> rows, bool includeChannel)
    {
        if (!includeChannel)
        {
            return new PresentationTable(
                ["Nick", "Status", "Address", "Server", "Name"],
                rows.Select(row => (IReadOnlyList<string>)new[]
                {
                    FormatWhoNick(row), FormatWhoStatus(row), row.UserHost, row.Server, row.RealName
                }).ToArray(),
                KeepAllColumns: true,
                MaximumWidths: [24, 12, PresentationTable.UnboundedWidth, 28, PresentationTable.UnboundedWidth]);
        }
        return new PresentationTable(
            ["Nick", "Status", "Address", "Channel", "Server", "Name"],
            rows.Select(row => (IReadOnlyList<string>)new[]
            {
                FormatWhoNick(row), FormatWhoStatus(row), row.UserHost, row.Channel, row.Server, row.RealName
            }).ToArray(),
            KeepAllColumns: true,
            MaximumWidths: [24, 12, PresentationTable.UnboundedWidth, 20, 28, PresentationTable.UnboundedWidth]);
    }

    private SessionEvent? ProcessWhois(IrcMessage message, DateTimeOffset now)
    {
        if (message.Parameters.Count < 2) return null;
        var nick = message.Parameters[1];
        if (message.Command == "312" && PeekWhoisRequest(nick) is null &&
            _whowasResults.TryGetValue(nick, out var whowas) && whowas.Count > 0)
        {
            var current = whowas[^1];
            whowas[^1] = current with
            {
                Server = message.Parameters.Count >= 3 ? message.Parameters[2] : null,
                Seen = message.Parameters.Count >= 4 ? message.Parameters[3] : null
            };
            return null;
        }
        if (message.Command == "318" && !_whoisResults.ContainsKey(nick) && PeekWhoisRequest(nick) is null)
        {
            return null;
        }
        if (!_whoisResults.TryGetValue(nick, out var result))
        {
            result = new WhoisResult(nick) { IncludeIdle = PeekWhoisRequest(nick)?.IncludeIdle ?? false };
            _whoisResults.Add(nick, result);
        }

        switch (message.Command)
        {
            case "311" when message.Parameters.Count >= 6:
                result.Nick = message.Parameters[1];
                result.User = message.Parameters[2];
                result.Host = message.Parameters[3];
                result.RealName = message.Parameters[5];
                break;
            case "312" when message.Parameters.Count >= 4:
                result.Server = message.Parameters[2];
                result.ServerInfo = message.Parameters[3];
                break;
            case "319" when message.Parameters.Count >= 3: result.Channels = message.Parameters[2]; break;
            case "301" when message.Parameters.Count >= 3: result.Away = message.Parameters[2]; break;
            case "313" when message.Parameters.Count >= 3: result.Operator = message.Parameters[2]; break;
            case "330" when message.Parameters.Count >= 3: result.Account = message.Parameters[2]; break;
            case "307": result.Registered = true; break;
            case "335": result.Bot = true; break;
            case "275" or "671": result.Secure = true; break;
            case "338" when message.Parameters.Count >= 3:
                result.ActualHost = NormalizeActualConnection([message.Parameters[2]]);
                break;
            case "378" when message.Parameters.Count >= 3:
                result.ActualHost = NormalizeActualConnection(message.Parameters.Skip(2));
                break;
            case "276" when message.Parameters.Count >= 3:
                result.Extra.Add(new PresentationField("Certificate", CleanWhoisText(message.Parameters[^1])));
                break;
            case "379" when message.Parameters.Count >= 3:
                var (modes, authFlags) = NormalizeWhoisModes(message.Parameters.Skip(2));
                if (modes is not null) result.Extra.Add(new PresentationField("Modes", modes));
                if (authFlags is not null) result.Extra.Add(new PresentationField("Auth flags", authFlags));
                break;
            case "344" when message.Parameters.Count >= 3:
                result.Extra.Add(new PresentationField("Country", CleanWhoisText(message.Parameters[^1])));
                break;
            case "569" when message.Parameters.Count >= 3:
                result.Extra.Add(new PresentationField("ASN", CleanWhoisText(message.Parameters[^1])));
                break;
            case "350" when message.Parameters.Count >= 3:
                result.Extra.Add(new PresentationField("Gateway", CleanWhoisText(message.Parameters[^1])));
                break;
            case "651" when message.Parameters.Count >= 3:
                result.Extra.Add(new PresentationField("Private channels", CleanWhoisText(message.Parameters[^1])));
                break;
            case "308" or "309" or "310" or "320" or "337" or "339" when message.Parameters.Count >= 3:
                result.Extra.Add(new PresentationField("Info", CleanWhoisText(message.Parameters[^1])));
                break;
            case "317" when message.Parameters.Count >= 4:
                if (long.TryParse(message.Parameters[2], out var idle)) result.IdleSeconds = idle;
                if (long.TryParse(message.Parameters[3], out var signOn))
                    result.SignOn = DateTimeOffset.FromUnixTimeSeconds(signOn).ToLocalTime();
                break;
            case "318":
                return CompleteWhois(nick, result, now);
        }
        return null;
    }

    private SessionEvent? CompleteWhois(string nick, WhoisResult result, DateTimeOffset now)
    {
        var request = PeekWhoisRequest(nick);
        _whoisResults.Remove(nick);
        CompleteWhoisRequest(nick);
        if (request?.Automatic == true) return null;
        var fields = new List<PresentationField>();
        Add("Address", result.User is null || result.Host is null ? null : $"{result.User}@{result.Host}");
        Add("Name", result.RealName);
        Add("Channels", result.Channels);
        var serverText = result.Server is null
            ? null
            : result.ServerInfo is null ? result.Server : $"{result.Server} [{result.ServerInfo}]";
        if (serverText is not null && result.Secure) serverText += " [TLS]";
        Add("Server", serverText);
        if (result.Away is not null) Add("Away", result.Away);
        if (result.IncludeIdle)
        {
            Add("Idle", result.IdleSeconds is null ? null : FormatElapsed(result.IdleSeconds.Value));
            Add("Sign-on", result.SignOn?.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        Add("Operator", result.Operator);
        Add("Account", result.Account);
        if (result.Registered) Add("Registered", "yes");
        if (result.Bot) Add("Bot", "yes");
        if (result.ActualHost is not null && !result.ActualHost.Equals(result.Host, StringComparison.OrdinalIgnoreCase))
            Add("Actual host", result.ActualHost);
        foreach (var extra in result.Extra) Add(extra.Label, extra.Value);
        return _events.Status(
            SessionEventKind.Server,
            $"WHOIS {result.Nick}",
            now,
            SessionEventBuilder.Fields(
                ("outputFamily", "whois"), ("outputTarget", result.Nick),
                ("outputRequestId", request?.Id.ToString("D")),
                ("numeric", "318"), ("outputEnd", "true")),
            new PresentationBlock("WHOIS:", fields, TitleHighlight: result.Nick));

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) fields.Add(new PresentationField(label, value));
        }
    }

    private void CollectWhowas(IrcMessage message)
    {
        if (message.Parameters.Count < 6) return;
        var nick = message.Parameters[1];
        if (!_whowasResults.TryGetValue(nick, out var results))
        {
            results = [];
            _whowasResults.Add(nick, results);
        }
        results.Add(new WhowasResult(nick, $"{message.Parameters[2]}@{message.Parameters[3]}", message.Parameters[5]));
    }

    private SessionEvent? CompleteWhowas(IrcMessage message, DateTimeOffset now)
    {
        if (message.Parameters.Count < 2) return null;
        var nick = message.Parameters[1];
        _whowasResults.Remove(nick, out var results);
        results ??= [];
        PresentationBlock presentation;
        if (results.Count == 0)
        {
            presentation = new PresentationBlock("WHOWAS:", Summary: "No history found.", TitleHighlight: nick);
        }
        else if (results.Count == 1)
        {
            var result = results[0];
            var fields = new List<PresentationField>
            {
                new("Address", result.Address),
                new("Name", result.RealName)
            };
            if (result.Server is not null) fields.Add(new PresentationField("Server", result.Server));
            if (result.Seen is not null) fields.Add(new PresentationField("Seen", result.Seen));
            presentation = new PresentationBlock("WHOWAS:", fields, TitleHighlight: nick);
        }
        else
        {
            presentation = new PresentationBlock(
                "WHOWAS:",
                Table: new PresentationTable(
                    ["Nick", "Address", "Name", "Server", "Seen"],
                    results.Select(result => (IReadOnlyList<string>)new[]
                    {
                        result.Nick, result.Address, result.RealName, result.Server ?? string.Empty,
                        result.Seen ?? string.Empty
                    }).ToArray(),
                    KeepAllColumns: true,
                    MaximumWidths: [24, 40, 32, 28, 28]),
                TitleHighlight: nick);
        }
        return _events.Status(
            SessionEventKind.Server,
            $"WHOWAS {nick}",
            now,
            SessionEventBuilder.Fields(
                ("outputFamily", "whowas"), ("outputTarget", nick),
                ("numeric", "369"), ("outputEnd", "true")),
            presentation);
    }

    private WhoisRequest? PeekWhoisRequest(string nickname)
    {
        if (!_whoisRequestsByNick.TryGetValue(nickname, out var requests)) return null;
        while (requests.Count > 0)
        {
            if (_whoisRequests.TryGetValue(requests.Peek(), out var request)) return request;
            requests.Dequeue();
        }
        _whoisRequestsByNick.Remove(nickname);
        return null;
    }

    private void CompleteWhoisRequest(string nickname)
    {
        if (!_whoisRequestsByNick.TryGetValue(nickname, out var requests) || requests.Count == 0) return;
        _whoisRequests.Remove(requests.Dequeue());
        if (requests.Count == 0) _whoisRequestsByNick.Remove(nickname);
    }

    private IEqualityComparer<string> NameComparer() => new IrcNameComparer(_state.CaseMapping);

    private static Dictionary<string, TValue> Reindex<TValue>(
        Dictionary<string, TValue> source,
        IEqualityComparer<string> comparer)
    {
        var reindexed = new Dictionary<string, TValue>(comparer);
        foreach (var (key, value) in source) reindexed[key] = value;
        return reindexed;
    }

    private static bool IsWhoisNumeric(string command) => command is
        "275" or "276" or "301" or "307" or "308" or "309" or "310" or "311" or "312" or "313" or
        "317" or "318" or "319" or "320" or "330" or "335" or "337" or "338" or "339" or "344" or
        "350" or "378" or "379" or "569" or "651" or "671";

    private static string CleanWhoisText(string value)
    {
        var cleaned = value.Trim();
        foreach (var prefix in new[] { "is ", "has ", "was " })
        {
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return cleaned[prefix.Length..];
        }
        return cleaned;
    }

    private static string NormalizeActualConnection(IEnumerable<string> parameters)
    {
        var text = string.Join(' ', parameters).Trim();
        foreach (var prefix in new[]
        {
            "is connecting from ", "is actually using host ", "actually using host ",
            "is actually ", "actually "
        })
        {
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            text = text[prefix.Length..];
            break;
        }
        var values = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value =>
            {
                var at = value.LastIndexOf('@');
                return (at >= 0 ? value[(at + 1)..] : value).Trim('[', ']', '(', ')');
            })
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return string.Join(" [", values.Select((value, index) => index == 0 ? value : value + "]"));
    }

    private static (string? Modes, string? AuthFlags) NormalizeWhoisModes(IEnumerable<string> parameters)
    {
        var text = string.Join(' ', parameters).Trim();
        foreach (var prefix in new[] { "is using modes ", "using modes " })
        {
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            text = text[prefix.Length..];
            break;
        }
        const string authMarker = " authflags:";
        var authIndex = text.IndexOf(authMarker, StringComparison.OrdinalIgnoreCase);
        var modes = (authIndex < 0 ? text : text[..authIndex]).Trim();
        var authFlags = authIndex < 0 ? null : text[(authIndex + authMarker.Length)..].Trim();
        if (authFlags is "" or "[none]" || authFlags?.Equals("none", StringComparison.OrdinalIgnoreCase) == true)
            authFlags = null;
        return (modes.Length == 0 ? null : modes, authFlags);
    }

    private static string FormatElapsed(long seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        var parts = new List<string>();
        if (span.Days > 0) parts.Add($"{span.Days}d");
        if (span.Hours > 0) parts.Add($"{span.Hours}h");
        if (span.Minutes > 0) parts.Add($"{span.Minutes}m");
        parts.Add($"{span.Seconds}s");
        return string.Join(' ', parts);
    }

    private enum WhoRequestKind { Single, Channel, Broad }

    private sealed record WhoRequest(
        Guid Id, string Input, string Target, WhoRequestKind Kind, bool Automatic)
    {
        public List<WhoResultRow> Rows { get; } = [];
    }

    private sealed record WhoResultRow(
        string Nick, string Status, string Privilege, string Channel,
        string UserHost, string Server, string RealName);

    private sealed record WhoisRequest(Guid Id, string Nickname, bool IncludeIdle, bool Automatic);

    private sealed record WhowasResult(
        string Nick, string Address, string RealName, string? Server = null, string? Seen = null);

    private sealed class WhoisResult(string nick)
    {
        public string Nick { get; set; } = nick;
        public string? User { get; set; }
        public string? Host { get; set; }
        public string? RealName { get; set; }
        public string? Channels { get; set; }
        public string? Server { get; set; }
        public string? ServerInfo { get; set; }
        public string? Away { get; set; }
        public long? IdleSeconds { get; set; }
        public DateTimeOffset? SignOn { get; set; }
        public string? Operator { get; set; }
        public string? Account { get; set; }
        public bool Registered { get; set; }
        public bool Bot { get; set; }
        public bool Secure { get; set; }
        public bool IncludeIdle { get; set; }
        public string? ActualHost { get; set; }
        public List<PresentationField> Extra { get; } = [];
    }
}
