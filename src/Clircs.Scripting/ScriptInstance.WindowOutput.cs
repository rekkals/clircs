using System.Globalization;
using System.Text.Json;
using Clircs.Identity;
using Clircs.Sessions;
using Jint;
using Jint.Native;

namespace Clircs.Scripting;

internal sealed partial class ScriptInstance
{
    private const int MaximumWindowOutputJsonLength = 262_144;
    private const int MaximumWindowOutputTextLength = 4096;
    private const int MaximumWindowOutputParts = MaximumWindowOutputTextLength;

    private static readonly string[] WindowTimestampFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:ss'Z'"
    ];

    private bool WindowWrite(JsValue windowId, string outputJson)
    {
        Demand(ScriptPermission.Output);

        if (!windowId.IsString() ||
            !Guid.TryParse(windowId.AsString(), out var id) ||
            id == Guid.Empty)
        {
            throw new ArgumentException(
                "windowId must be a valid window ID returned by clircs.");
        }

        var output = ReadWindowOutput(outputJson);
        return _services.Windows.Write(_script.Manifest.Id, new BufferId(id), output);
    }

    private string WindowStyle(JsValue role, JsValue text)
    {
        Demand(ScriptPermission.Output);
        if (!role.IsString() || !text.IsString())
        {
            throw new ArgumentException("clircs.style requires a role name and text.");
        }

        var selected = ReadWindowEnum<TextColorRole>(role.AsString(), "role");
        var content = text.AsString();
        if (content.Length > MaximumWindowOutputTextLength)
        {
            throw new InvalidOperationException(
                $"Script output cannot exceed {MaximumWindowOutputTextLength} characters per call.");
        }

        return JsonSerializer.Serialize(new
        {
            text = content,
            role = selected.ToString().ToLowerInvariant()
        });
    }

    private static ScriptWindowOutput ReadWindowOutput(string json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > MaximumWindowOutputJsonLength)
        {
            throw new ArgumentException("Script output JSON is missing or too large.");
        }

        using var document = JsonDocument.Parse(
            json, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        ValidateWindowProperties(root, "parts", "role", "color", "timestamp");

        if (!root.TryGetProperty("parts", out var partsElement) ||
            partsElement.ValueKind != JsonValueKind.Array ||
            partsElement.GetArrayLength() > MaximumWindowOutputParts)
        {
            throw new ArgumentException(
                $"Script output requires an array of at most {MaximumWindowOutputParts} text parts.");
        }

        var style = ReadWindowOutputStyle(root);
        var parts = new List<ScriptOutputPart>();
        var totalLength = 0;

        foreach (var element in partsElement.EnumerateArray())
        {
            string text;
            ScriptOutputStyle? partStyle = null;
            if (element.ValueKind == JsonValueKind.String)
            {
                text = element.GetString()!;
            }
            else
            {
                ValidateWindowProperties(element, "text", "role", "color");
                text = ReadWindowOutputString(element, "text", required: true)!;
                partStyle = ReadWindowOutputStyle(element);
            }

            if (text.Length > MaximumWindowOutputTextLength - totalLength)
            {
                throw new InvalidOperationException(
                    $"Script output cannot exceed {MaximumWindowOutputTextLength} characters per call.");
            }

            totalLength += text.Length;
            parts.Add(new ScriptOutputPart(text, partStyle));
        }

        DateTimeOffset? timestamp = null;
        var timestampText = ReadWindowOutputString(root, "timestamp");
        if (timestampText is not null)
        {
            var decimalPoint = timestampText.IndexOf('.');
            var missingFractionDigits = decimalPoint >= 0 &&
                (decimalPoint + 1 == timestampText.Length ||
                 !char.IsAsciiDigit(timestampText[decimalPoint + 1]));

            if (missingFractionDigits ||
                !DateTimeOffset.TryParseExact(
                    timestampText,
                    WindowTimestampFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                throw new ArgumentException(
                    "timestamp must be an ISO 8601 timestamp with a UTC offset.");
            }

            timestamp = parsed;
        }

        return new ScriptWindowOutput(parts.ToArray(), style, timestamp);
    }

    private static ScriptOutputStyle? ReadWindowOutputStyle(JsonElement element)
    {
        var roleText = ReadWindowOutputString(element, "role");
        var colorText = ReadWindowOutputString(element, "color");
        if (roleText is not null && colorText is not null)
        {
            throw new ArgumentException(
                "Output styles cannot specify both a theme role and a fixed color.");
        }

        if (roleText is null && colorText is null) return null;

        TextColorRole? role = roleText is null
            ? null
            : ReadWindowEnum<TextColorRole>(roleText, "role");
        ConsoleColor? color = colorText is null
            ? null
            : ReadWindowEnum<ConsoleColor>(colorText, "color");

        return new ScriptOutputStyle(role, color);
    }

    private static string? ReadWindowOutputString(
        JsonElement element,
        string name,
        bool required = false)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            if (required)
            {
                throw new ArgumentException($"Script output requires '{name}' text.");
            }

            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Script output '{name}' must be text.");
        }

        return value.GetString()!;
    }

    private static T ReadWindowEnum<T>(string text, string name)
        where T : struct, Enum
    {
        if (!Enum.TryParse<T>(text, ignoreCase: true, out var parsed) ||
            !Enum.IsDefined(parsed) ||
            !string.Equals(Enum.GetName(parsed), text, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unknown output {name}: {text}");
        }

        return parsed;
    }

    private static void ValidateWindowProperties(
        JsonElement element,
        params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Script output objects must contain named fields.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) ||
                !seen.Add(property.Name))
            {
                throw new ArgumentException(
                    $"Unknown or duplicate script output field: {property.Name}");
            }
        }
    }
}
