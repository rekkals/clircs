using System.Security.Cryptography;
using Clircs.Sessions;

namespace Clircs.ConsoleClient;

internal sealed class QuoteProvider
{
    private readonly string _path;

    public QuoteProvider(string dataDirectory, string bundledPath)
    {
        _path = System.IO.Path.Combine(System.IO.Path.GetFullPath(dataDirectory), "quotes.txt");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        if (!File.Exists(_path) && File.Exists(bundledPath))
        {
            File.Copy(bundledPath, _path);
        }
    }

    public string Path => _path;

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
