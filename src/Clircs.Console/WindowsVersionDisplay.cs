using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Clircs.ConsoleClient;

internal static class WindowsVersionDisplay
{
    public static string Current => OperatingSystem.IsWindows()
        ? ReadWindowsVersion()
        : $"Running on {RuntimeInformation.OSDescription}";

    [SupportedOSPlatform("windows")]
    private static string ReadWindowsVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var displayVersion = key?.GetValue("DisplayVersion")?.ToString();
            var buildText = key?.GetValue("CurrentBuildNumber")?.ToString()
                ?? key?.GetValue("CurrentBuild")?.ToString();
            var ubrText = key?.GetValue("UBR")?.ToString();

            if (int.TryParse(buildText, out var build))
            {
                var product = build >= 22000 ? "Windows 11" : "Windows 10";
                var release = string.IsNullOrWhiteSpace(displayVersion) ? string.Empty : $" {displayVersion}";
                var fullBuild = int.TryParse(ubrText, out var ubr) ? $"{build}.{ubr}" : build.ToString();
                return $"Running on {product}{release} (OS Build {fullBuild})";
            }
        }
        catch (Exception exception) when (
            exception is IOException or System.Security.SecurityException or UnauthorizedAccessException)
        {
            // RuntimeInformation remains useful when the registry is unavailable.
        }

        return $"Running on {RuntimeInformation.OSDescription}";
    }
}
