using System.Text;

namespace Clircs.ConsoleClient;

internal sealed class ThemeManager
{
    private readonly string _directory;
    private readonly Dictionary<string, TerminalTheme> _themes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _errors = [];

    public ThemeManager(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        Reload();
    }

    public string DirectoryPath => _directory;
    public IReadOnlyCollection<TerminalTheme> Themes => _themes.Values.OrderBy(theme => theme.Name).ToArray();
    public IReadOnlyList<string> Errors => _errors.ToArray();

    public void Reload()
    {
        var loaded = TerminalTheme.BuiltIns.Values.ToDictionary(theme => theme.Name, StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.toml", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var theme = LoadTheme(path);
                if (TerminalTheme.BuiltIns.ContainsKey(theme.Name))
                    throw new InvalidDataException($"User theme '{theme.Name}' cannot replace a compiled built-in theme.");
                if (!loaded.TryAdd(theme.Name, theme))
                    throw new InvalidDataException($"Theme name '{theme.Name}' is already provided by another file.");
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }

        _themes.Clear();
        foreach (var theme in loaded) _themes.Add(theme.Key, theme.Value);
        _errors.Clear();
        _errors.AddRange(errors);
    }

    public bool TryGet(string name, out TerminalTheme? theme) =>
        _themes.TryGetValue(NormalizeLegacyName(name), out theme);

    private static TerminalTheme LoadTheme(string path)
    {
        var values = Parse(path);
        var baseName = NormalizeLegacyName(values.GetValueOrDefault("base", "clircs"));
        if (!TerminalTheme.BuiltIns.TryGetValue(baseName, out var basis))
            throw new InvalidDataException($"Theme '{path}' has unknown built-in base '{baseName}'.");
        var name = values.GetValueOrDefault("name", Path.GetFileNameWithoutExtension(path));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.Any(char.IsControl))
            throw new InvalidDataException($"Theme '{path}' has an invalid name.");

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name", "base", "palette.normal", "palette.dim", "palette.accent", "palette.label",
            "palette.message", "palette.highlight", "palette.action", "palette.notice", "palette.join", "palette.part",
            "palette.kick", "palette.nick", "palette.mode", "palette.warning", "palette.error", "palette.status_foreground",
            "palette.status_background", "markers.join", "markers.part", "layout.info_top", "layout.info_side",
            "palette.topic_foreground", "palette.topic_background", "layout.grid_open", "layout.grid_close",
            "layout.info_bottom", "layout.status_separator", "layout.header_separator",
            "layout.show_buffer_name", "layout.show_nick_prefix"
        };
        var unknown = values.Keys.FirstOrDefault(key => !allowed.Contains(key));
        if (unknown is not null) throw new InvalidDataException($"Theme '{path}' contains unknown setting '{unknown}'.");

        return basis with
        {
            Name = name,
            Normal = Color("palette.normal", basis.Normal), Dim = Color("palette.dim", basis.Dim),
            Accent = Color("palette.accent", basis.Accent), Label = Color("palette.label", basis.Label),
            Message = Color("palette.message", basis.Message), Highlight = Color("palette.highlight", basis.Highlight),
            Action = Color("palette.action", basis.Action), Notice = Color("palette.notice", basis.Notice),
            Join = Color("palette.join", basis.Join), Part = Color("palette.part", basis.Part),
            Kick = Color("palette.kick", basis.Kick), Nick = Color("palette.nick", basis.Nick),
            Mode = Color("palette.mode", basis.Mode), Warning = Color("palette.warning", basis.Warning),
            Error = Color("palette.error", basis.Error),
            StatusForeground = Color("palette.status_foreground", basis.StatusForeground),
            StatusBackground = Color("palette.status_background", basis.StatusBackground),
            TopicForeground = Color("palette.topic_foreground", basis.TopicForeground),
            TopicBackground = Color("palette.topic_background", basis.TopicBackground),
            JoinMarker = Marker("markers.join", basis.JoinMarker), PartMarker = Marker("markers.part", basis.PartMarker),
            InfoTop = Marker("layout.info_top", basis.InfoTop, true),
            InfoSide = Marker("layout.info_side", basis.InfoSide, true),
            InfoBottom = Marker("layout.info_bottom", basis.InfoBottom, true),
            GridOpen = Marker("layout.grid_open", basis.GridOpen), GridClose = Marker("layout.grid_close", basis.GridClose),
            StatusSeparator = Marker("layout.status_separator", basis.StatusSeparator),
            HeaderSeparator = Marker("layout.header_separator", basis.HeaderSeparator),
            ShowBufferName = Boolean("layout.show_buffer_name", basis.ShowBufferName),
            ShowNickPrefix = Boolean("layout.show_nick_prefix", basis.ShowNickPrefix)
        };

        ConsoleColor Color(string key, ConsoleColor fallback)
        {
            if (!values.TryGetValue(key, out var value)) return fallback;
            if (Enum.TryParse<ConsoleColor>(value, true, out var parsed)) return parsed;
            throw new InvalidDataException($"Theme '{path}' has invalid ConsoleColor '{value}' for '{key}'.");
        }
        string Marker(string key, string fallback, bool allowEmpty = false)
        {
            var value = values.GetValueOrDefault(key, fallback);
            if ((!allowEmpty && value.Length == 0) || value.Length > 8 ||
                value.Any(character => char.IsControl(character) && character != '\t'))
                throw new InvalidDataException($"Theme '{path}' has invalid marker '{key}'.");
            return value;
        }
        bool Boolean(string key, bool fallback)
        {
            if (!values.TryGetValue(key, out var value)) return fallback;
            if (bool.TryParse(value, out var parsed)) return parsed;
            throw new InvalidDataException($"Theme '{path}' has invalid Boolean '{value}' for '{key}'.");
        }
    }

    private static Dictionary<string, string> Parse(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;
        var lineNumber = 0;
        foreach (var rawLine in File.ReadLines(path))
        {
            lineNumber++;
            var line = StripComment(rawLine, path, lineNumber).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('['))
            {
                if (!line.EndsWith(']') || line.Count(character => character == '[') != 1 ||
                    line.Count(character => character == ']') != 1)
                    throw InvalidLine(path, lineNumber);
                section = line[1..^1].Trim();
                if (section.Length == 0) throw InvalidLine(path, lineNumber);
                continue;
            }
            var equals = IndexOfUnquoted(line, '=');
            if (equals <= 0) throw InvalidLine(path, lineNumber);
            var key = line[..equals].Trim();
            if (key.Length == 0) throw InvalidLine(path, lineNumber);
            var fullKey = $"{section}.{key}".TrimStart('.');
            var value = ParseValue(line[(equals + 1)..].Trim(), path, lineNumber);
            if (!values.TryAdd(fullKey, value))
                throw new InvalidDataException($"Theme '{path}' repeats setting '{fullKey}' on line {lineNumber}.");
        }
        return values;
    }

    private static string StripComment(string line, string path, int lineNumber)
    {
        var quoted = false;
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (escaped) { escaped = false; continue; }
            if (quoted && character == '\\') { escaped = true; continue; }
            if (character == '"') quoted = !quoted;
            else if (!quoted && character == '#') return line[..index];
        }
        if (quoted) throw InvalidLine(path, lineNumber);
        return line;
    }

    private static int IndexOfUnquoted(string line, char target)
    {
        var quoted = false;
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (escaped) { escaped = false; continue; }
            if (quoted && character == '\\') { escaped = true; continue; }
            if (character == '"') quoted = !quoted;
            else if (!quoted && character == target) return index;
        }
        return -1;
    }

    private static string ParseValue(string value, string path, int lineNumber)
    {
        if (!value.StartsWith('"')) return value;
        if (value.Length < 2 || !value.EndsWith('"')) throw InvalidLine(path, lineNumber);
        var result = new StringBuilder();
        for (var index = 1; index < value.Length - 1; index++)
        {
            var character = value[index];
            if (character != '\\') { result.Append(character); continue; }
            if (++index >= value.Length - 1) throw InvalidLine(path, lineNumber);
            result.Append(value[index] switch
            {
                '\\' => '\\', '"' => '"', 't' => '\t', 'n' => '\n', 'r' => '\r',
                _ => throw InvalidLine(path, lineNumber)
            });
        }
        return result.ToString();
    }

    private static InvalidDataException InvalidLine(string path, int line) =>
        new($"Invalid theme syntax in '{path}' on line {line}.");

    private static string NormalizeLegacyName(string name) =>
        name.Equals("clirc", StringComparison.OrdinalIgnoreCase) ? "clircs" : name;
}
