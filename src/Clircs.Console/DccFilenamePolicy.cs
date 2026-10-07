using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Clircs.ConsoleClient;

internal sealed record DccFilenameAssessment(
    string LocalFilename,
    bool RequiresExecutableWarning);

internal static class DccFilenamePolicy
{
    private static readonly HashSet<string> ReservedDeviceNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "CLOCK$", "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5",
            "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5",
            "LPT6", "LPT7", "LPT8", "LPT9",
            "COM¹", "COM²", "COM³",
            "LPT¹", "LPT²", "LPT³"
        };

    // This particular set of file types is a subset of what Microsoft classifies
    // as blocked/dangerous in their Outlook and Edge docs.
    private static readonly HashSet<string> DangerousExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".com", ".scr", ".pif", ".cpl",
            ".msi", ".msp", ".mst", ".msix", ".msixbundle",
            ".appx", ".appxbundle", ".appinstaller",
            ".application", ".appref-ms", ".appcontent-ms",
            ".bat", ".cmd", ".ps1",
            ".vb", ".vbs", ".vbe", ".js", ".jse",
            ".ws", ".wsc", ".wsf", ".wsh", ".hta", ".sct",
            ".lnk", ".url", ".scf", ".shb", ".shs",
            ".search-ms", ".settingcontent-ms",
            ".reg", ".inf", ".msc", ".chm", ".gadget", ".jar"
        };

    public static bool TryAssess(
        string offeredFilename,
        [NotNullWhen(true)] out DccFilenameAssessment? assessment)
    {
        assessment = null;

        if (string.IsNullOrWhiteSpace(offeredFilename) ||
            offeredFilename is "." or ".." ||
            Path.IsPathRooted(offeredFilename) ||
            !string.Equals(
                Path.GetFileName(offeredFilename),
                offeredFilename,
                StringComparison.Ordinal) ||
            offeredFilename.IndexOfAny(['/', '\\', ':']) >= 0 ||
            offeredFilename.Any(char.IsControl))
        {
            return false;
        }

        var localFilename = RemoveFormatCharacters(offeredFilename)
            .TrimEnd(' ', '.');

        if (string.IsNullOrWhiteSpace(localFilename) ||
            localFilename is "." or ".." ||
            localFilename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        var period = localFilename.IndexOf('.');
        var stem = (period < 0 ? localFilename : localFilename[..period])
            .TrimEnd(' ', '.');

        if (ReservedDeviceNames.Contains(stem))
        {
            localFilename = "_" + localFilename;
        }

        assessment = new DccFilenameAssessment(
            localFilename,
            DangerousExtensions.Contains(Path.GetExtension(localFilename)));

        return true;
    }

    private static string RemoveFormatCharacters(string value)
    {
        var result = new StringBuilder(value.Length);

        for (var index = 0; index < value.Length;)
        {
            var length = char.IsSurrogatePair(value, index) ? 2 : 1;

            if (CharUnicodeInfo.GetUnicodeCategory(value, index) != UnicodeCategory.Format)
            {
                result.Append(value, index, length);
            }

            index += length;
        }

        return result.ToString();
    }
}
