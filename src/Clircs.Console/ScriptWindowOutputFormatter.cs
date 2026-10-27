using System.Text;
using Clircs.Scripting;
using Clircs.Sessions;

namespace Clircs.ConsoleClient;

internal static class ScriptWindowOutputFormatter
{
    public const int MaximumTextLength = 4096;
    public const int MaximumParts = MaximumTextLength;

    public static IrcFormattedText Format(ScriptWindowOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(output.Parts);

        if (output.Parts.Count > MaximumParts)
        {
            throw new InvalidOperationException(
                $"Script output cannot contain more than {MaximumParts} text parts.");
        }

        ValidateStyle(output.Style);

        var parts = output.Parts.ToArray();
        var totalLength = 0;
        foreach (var part in parts)
        {
            ArgumentNullException.ThrowIfNull(part);
            ArgumentNullException.ThrowIfNull(part.Text);

            if (part.Text.Length > MaximumTextLength - totalLength)
            {
                throw new InvalidOperationException(
                    $"Script output cannot exceed {MaximumTextLength} characters per call.");
            }

            totalLength += part.Text.Length;
            ValidateStyle(part.Style);
        }

        var plain = new StringBuilder(totalLength);
        var runs = new List<IrcTextRun>();

        foreach (var part in parts)
        {
            var selectedStyle = part.Style ?? output.Style;
            var color = selectedStyle?.Color;
            TextColorRole? role = selectedStyle?.Role;
            if (role is null && color is null)
            {
                role = TextColorRole.Normal;
            }

            var parsed = IrcTextFormatting.Parse(part.Text);
            plain.Append(parsed.PlainText);

            foreach (var run in parsed.Runs)
            {
                runs.Add(new IrcTextRun(
                    run.Text,
                    run.Style with
                    {
                        ForegroundRole = role,
                        BaseForeground = color
                    }));
            }
        }

        return new IrcFormattedText(plain.ToString(), runs.ToArray());
    }

    private static void ValidateStyle(ScriptOutputStyle? style)
    {
        if (style is null) return;

        if (style.Role is not null && style.Color is not null)
        {
            throw new ArgumentException(
                "Script output styles cannot specify both a theme role and a fixed color.",
                nameof(style));
        }

        if (style.Role is { } role && !Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(
                nameof(style), role, "Unknown text color role.");
        }

        if (style.Color is { } color && !Enum.IsDefined(color))
        {
            throw new ArgumentOutOfRangeException(
                nameof(style), color, "Unknown console color.");
        }
    }
}
