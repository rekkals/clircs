using Clircs.Protocol;
using Clircs.State;

namespace Clircs.Sessions;

public sealed class ServerFeatures
{
    private const string DefaultChannelTypes = "#&";
    private const int DefaultModesPerCommand = 3;
    private const string DefaultChannelModesA = "b";
    private const string DefaultChannelModesB = "k";
    private const string DefaultChannelModesC = "l";
    private const string DefaultChannelModesD = "imnpst";
    private readonly Dictionary<string, string?> _isupport =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<char, char> _prefixSymbols = new() { ['@'] = 'o', ['+'] = 'v' };
    private readonly Dictionary<char, char> _prefixModes = new() { ['o'] = '@', ['v'] = '+' };

    public IrcCaseMapping CaseMapping { get; private set; } = IrcCaseMapping.Rfc1459;

    public string ChannelTypes { get; private set; } = DefaultChannelTypes;

    public int ModesPerCommand { get; private set; } = DefaultModesPerCommand;

    public string? NetworkName { get; private set; }

    public string ChannelModesA { get; private set; } = DefaultChannelModesA;

    public string ChannelModesB { get; private set; } = DefaultChannelModesB;

    public string ChannelModesC { get; private set; } = DefaultChannelModesC;

    public string ChannelModesD { get; private set; } = DefaultChannelModesD;

    public string StatusMessagePrefixes { get; private set; } = string.Empty;

    public IrcDaemonFamily DaemonFamily { get; private set; } = IrcDaemonFamily.Unknown;

    public string? ServerSoftware { get; private set; }

    public IReadOnlyDictionary<string, string?> Isupport => _isupport;

    public IReadOnlyDictionary<char, char> PrefixSymbols => _prefixSymbols;

    public IReadOnlyDictionary<char, char> PrefixModes => _prefixModes;

    public char CallerIdMode =>
        TryGetIsupportValue("CALLERID", out var value) && !string.IsNullOrEmpty(value)
            ? value[0]
            : 'g';

    public void ApplyIsupport(IrcMessage message, NetworkSessionState state)
    {
        if (message.Command != "005")
        {
            throw new ArgumentException("Server features can only be populated from numeric 005.", nameof(message));
        }

        for (var index = 1; index < message.Parameters.Count; index++)
        {
            var token = message.Parameters[index];
            if (token.Contains(' '))
            {
                break;
            }

            var removing = token.Length > 1 && token[0] == '-';
            if (removing)
            {
                token = token[1..];
            }

            var separator = token.IndexOf('=');
            var name = (separator < 0 ? token : token[..separator]).ToUpperInvariant();
            if (name.Length == 0)
            {
                continue;
            }

            var value = separator < 0 ? null : token[(separator + 1)..];
            if (removing)
            {
                _isupport.Remove(name);
                RemoveKnownFeature(name, state);
                continue;
            }

            if (!TryApplyKnownFeature(name, value, state)) continue;
            _isupport[name] = value;
        }

        DetectDaemonFamily();
    }

    public bool IsChannel(string target) => target.Length > 0 && ChannelTypes.Contains(target[0]);

    public bool TryGetChannelTarget(string target, out string channel)
    {
        ArgumentNullException.ThrowIfNull(target);
        var index = 0;
        while (index < target.Length && StatusMessagePrefixes.Contains(target[index]))
        {
            index++;
        }

        channel = target[index..];
        return IsChannel(channel);
    }

    public string NormalizeMessageTarget(string target) =>
        TryGetChannelTarget(target, out var channel) ? channel : target;

    public bool Supports(string name) => _isupport.ContainsKey(name);

    public bool TryGetIsupportValue(string name, out string? value) =>
        _isupport.TryGetValue(name, out value);

    public void ObserveServerSoftware(string? software)
    {
        if (string.IsNullOrWhiteSpace(software))
        {
            return;
        }

        ServerSoftware = software.Trim();
        DetectDaemonFamily();
    }

    public bool TryGetPrefixMode(char symbol, out char mode) => _prefixSymbols.TryGetValue(symbol, out mode);

    public bool IsPrefixMode(char mode) => _prefixModes.ContainsKey(mode);

    public char? HighestPrefix(IReadOnlySet<char> modes)
    {
        foreach (var (mode, symbol) in _prefixModes)
        {
            if (modes.Contains(mode)) return symbol;
        }
        return null;
    }

    public bool ModeTakesParameter(char mode, bool adding) =>
        IsPrefixMode(mode) || ChannelModesA.Contains(mode) || ChannelModesB.Contains(mode) || (adding && ChannelModesC.Contains(mode));

    public void Reset()
    {
        CaseMapping = IrcCaseMapping.Rfc1459;
        ChannelTypes = DefaultChannelTypes;
        ModesPerCommand = DefaultModesPerCommand;
        NetworkName = null;
        ChannelModesA = DefaultChannelModesA;
        ChannelModesB = DefaultChannelModesB;
        ChannelModesC = DefaultChannelModesC;
        ChannelModesD = DefaultChannelModesD;
        StatusMessagePrefixes = string.Empty;
        DaemonFamily = IrcDaemonFamily.Unknown;
        ServerSoftware = null;
        _isupport.Clear();
        ResetPrefix();
    }

    private void RemoveKnownFeature(string name, NetworkSessionState state)
    {
        switch (name)
        {
            case "CASEMAPPING":
                CaseMapping = IrcCaseMapping.Rfc1459;
                state.UpdateCaseMapping(CaseMapping);
                break;
            case "CHANTYPES":
                ChannelTypes = DefaultChannelTypes;
                break;
            case "MODES":
                ModesPerCommand = DefaultModesPerCommand;
                break;
            case "NETWORK":
                NetworkName = null;
                break;
            case "PREFIX":
                ResetPrefix();
                break;
            case "CHANMODES":
                ChannelModesA = DefaultChannelModesA;
                ChannelModesB = DefaultChannelModesB;
                ChannelModesC = DefaultChannelModesC;
                ChannelModesD = DefaultChannelModesD;
                break;
            case "STATUSMSG":
                StatusMessagePrefixes = string.Empty;
                break;
        }
    }

    private void ResetPrefix()
    {
        _prefixSymbols.Clear();
        _prefixSymbols['@'] = 'o';
        _prefixSymbols['+'] = 'v';
        _prefixModes.Clear();
        _prefixModes['o'] = '@';
        _prefixModes['v'] = '+';
    }

    private void DetectDaemonFamily()
    {
        var evidence = string.Join(' ', new[]
        {
            ServerSoftware,
            _isupport.TryGetValue("IRCD", out var ircd) ? ircd : null
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        DaemonFamily = evidence.ToLowerInvariant() switch
        {
            var text when text.Contains("unreal") => IrcDaemonFamily.Unreal,
            var text when text.Contains("inspircd") => IrcDaemonFamily.InspIRCd,
            var text when text.Contains("ngircd") => IrcDaemonFamily.NgIRCd,
            var text when text.Contains("solanum") => IrcDaemonFamily.Solanum,
            var text when text.Contains("bahamut") => IrcDaemonFamily.Bahamut,
            var text when text.Contains("hybrid") => IrcDaemonFamily.Hybrid,
            var text when text.Contains("ratbox") => IrcDaemonFamily.Ratbox,
            var text when text.StartsWith("u2.", StringComparison.Ordinal) ||
                text.Contains(" u2.", StringComparison.Ordinal) => IrcDaemonFamily.UndernetIrcu,
            _ => IrcDaemonFamily.Unknown
        };
    }

    private bool TryApplyKnownFeature(string name, string? value, NetworkSessionState state)
    {
        switch (name)
        {
            case "CASEMAPPING":
                var mapping = value?.ToLowerInvariant() switch
                {
                    "ascii" => IrcCaseMapping.Ascii,
                    "strict-rfc1459" => IrcCaseMapping.StrictRfc1459,
                    "rfc1459" => IrcCaseMapping.Rfc1459,
                    _ => (IrcCaseMapping?)null
                };
                if (mapping is null) return false;
                state.UpdateCaseMapping(mapping.Value);
                CaseMapping = mapping.Value;
                return true;
            case "CHANTYPES":
                if (string.IsNullOrEmpty(value)) return false;
                ChannelTypes = value;
                return true;
            case "MODES":
                if (!int.TryParse(value, out var modes) || modes <= 0) return false;
                ModesPerCommand = modes;
                return true;
            case "NETWORK":
                if (string.IsNullOrEmpty(value)) return false;
                NetworkName = value;
                return true;
            case "PREFIX":
                return value is not null && TryApplyPrefix(value);
            case "CHANMODES":
                return value is not null && TryApplyChannelModes(value);
            case "STATUSMSG":
                StatusMessagePrefixes = value ?? string.Empty;
                return true;
            default:
                return true;
        }
    }

    private bool TryApplyPrefix(string value)
    {
        if (value.Length < 4 || value[0] != '(')
        {
            return false;
        }

        var close = value.IndexOf(')');
        if (close <= 1 || close == value.Length - 1)
        {
            return false;
        }

        var modes = value[1..close];
        var symbols = value[(close + 1)..];
        if (modes.Length != symbols.Length)
        {
            return false;
        }

        var prefixSymbols = new Dictionary<char, char>();
        var prefixModes = new Dictionary<char, char>();
        for (var index = 0; index < modes.Length; index++)
        {
            if (!prefixSymbols.TryAdd(symbols[index], modes[index]) ||
                !prefixModes.TryAdd(modes[index], symbols[index])) return false;
        }

        _prefixSymbols.Clear();
        _prefixModes.Clear();
        foreach (var (symbol, mode) in prefixSymbols)
        {
            _prefixSymbols[symbol] = mode;
        }
        foreach (var (mode, symbol) in prefixModes) _prefixModes[mode] = symbol;
        return true;
    }

    private bool TryApplyChannelModes(string value)
    {
        var groups = value.Split(',');
        if (groups.Length != 4)
        {
            return false;
        }

        ChannelModesA = groups[0];
        ChannelModesB = groups[1];
        ChannelModesC = groups[2];
        ChannelModesD = groups[3];
        return true;
    }
}

public enum IrcDaemonFamily
{
    Unknown,
    Ratbox,
    Solanum,
    Hybrid,
    Bahamut,
    NgIRCd,
    InspIRCd,
    Unreal,
    UndernetIrcu
}
