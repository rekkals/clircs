using System.Security.Cryptography;
using Clircs.Sessions;

namespace Clircs.ConsoleClient;

internal sealed class QuoteProvider
{
    private const string RetiredQuote = "What? Something like 36?";
    private const string ReplacementQuote = "Look at you two whipping out your Preciouses.";
    private readonly string _path;

    public QuoteProvider(string dataDirectory, string bundledPath)
    {
        _path = System.IO.Path.Combine(System.IO.Path.GetFullPath(dataDirectory), "quotes.txt");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        if (!File.Exists(_path) && File.Exists(bundledPath))
        {
            File.Copy(bundledPath, _path);
        }
        else if (File.Exists(_path))
        {
            ReplaceRetiredBundledQuote();
        }
    }

    public string Path => _path;

    private void ReplaceRetiredBundledQuote()
    {
        try
        {
            var lines = File.ReadAllLines(_path);
            var changed = false;
            for (var index = 0; index < lines.Length; index++)
            {
                if (!lines[index].Equals(RetiredQuote, StringComparison.Ordinal)) continue;
                lines[index] = ReplacementQuote;
                changed = true;
            }
            if (changed) File.WriteAllLines(_path, lines);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A read-only customized quote file remains usable as-is.
        }
    }

    public string Next(int maximumCharacters = 300)
    {
        if (maximumCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        try
        {
            var choices = File.Exists(_path)
                ? File.ReadLines(_path)
                    .Select(TerminalTextSanitizer.Sanitize)
                    .Select(line => line.Trim())
                    .Where(line => line.Length is > 0 && line.Length <= maximumCharacters)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                : [];
            return choices.Length == 0 ? "Leaving" : choices[RandomNumberGenerator.GetInt32(choices.Length)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "Leaving";
        }
    }
}
