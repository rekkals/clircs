using System.Text.Json;

namespace Clircs.ConsoleClient;

internal sealed record DccDownloadIdentity(string Network, string Sender, long ExpectedBytes);

internal sealed record DccDownloadTarget(
    string Filename,
    string PartialPath,
    string FinalPath,
    long InitialOffset = 0)
{
    public bool IsResume => InitialOffset > 0;
}

internal sealed class DccDownloadStore
{
    private readonly string _root;

    public DccDownloadStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public string Root => _root;

    public DccDownloadTarget CreatePartial(string filename, DccDownloadIdentity? identity = null)
    {
        ValidateFilename(filename);
        Directory.CreateDirectory(_root);
        for (var suffix = 0; suffix < 10_000; suffix++)
        {
            var final = CandidatePath(filename, suffix);
            var partial = final + ".clircs-part";
            if (File.Exists(final)) continue;
            try
            {
                using var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var target = new DccDownloadTarget(filename, partial, final);
                if (identity is not null)
                {
                    try
                    {
                        WriteMetadata(target, identity);
                    }
                    catch
                    {
                        Discard(target);
                        throw;
                    }
                }
                return target;
            }
            catch (IOException) when (File.Exists(partial))
            {
            }
        }
        throw new IOException("Unable to allocate a temporary DCC download file.");
    }

    public DccDownloadTarget? FindResumeTarget(
        string filename,
        long expectedBytes,
        string network,
        string sender)
    {
        ValidateFilename(filename);
        if (expectedBytes <= 0) return null;
        Directory.CreateDirectory(_root);

        var candidates = Directory.EnumerateFiles(_root, "*.clircs-part", SearchOption.TopDirectoryOnly)
            .Select(path => ResumeCandidate(path, expectedBytes, network, sender))
            .Where(target => target is not null)
            .Cast<DccDownloadTarget>()
            .Where(target => ResumeNameMatches(filename, Path.GetFileName(target.FinalPath)))
            .OrderByDescending(target => target.InitialOffset)
            .ThenByDescending(target => File.GetLastWriteTimeUtc(target.PartialPath))
            .ToList();
        return candidates.FirstOrDefault();
    }

    public FileStream OpenPartial(DccDownloadTarget target)
    {
        if (target.IsResume)
        {
            var actualLength = new FileInfo(target.PartialPath).Length;
            if (actualLength != target.InitialOffset)
                throw new IOException("The partial DCC download changed after it was selected for resume");
        }
        var stream = new FileStream(
            target.PartialPath,
            target.IsResume ? FileMode.Open : FileMode.Truncate,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (target.IsResume) stream.Seek(target.InitialOffset, SeekOrigin.Begin);
        return stream;
    }

    public string Complete(DccDownloadTarget target)
    {
        if (string.Equals(target.PartialPath, target.FinalPath, StringComparison.OrdinalIgnoreCase))
            return target.FinalPath;
        if (!File.Exists(target.FinalPath))
        {
            File.Move(target.PartialPath, target.FinalPath, overwrite: false);
            DeleteMetadata(target);
            return target.FinalPath;
        }
        for (var suffix = 0; suffix < 10_000; suffix++)
        {
            var candidate = CandidatePath(target.Filename, suffix);
            try
            {
                File.Move(target.PartialPath, candidate, overwrite: false);
                DeleteMetadata(target);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
            }
        }
        throw new IOException("Unable to choose an unused name for the received DCC file.");
    }

    public static void Discard(DccDownloadTarget target)
    {
        try
        {
            if (File.Exists(target.PartialPath)) File.Delete(target.PartialPath);
            DeleteMetadata(target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static void DiscardIfEmpty(DccDownloadTarget target)
    {
        try
        {
            if (File.Exists(target.PartialPath) && new FileInfo(target.PartialPath).Length == 0)
            {
                File.Delete(target.PartialPath);
                DeleteMetadata(target);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string CandidatePath(string filename, int suffix)
    {
        if (suffix == 0) return Path.Combine(_root, filename);
        var extension = Path.GetExtension(filename);
        var name = Path.GetFileNameWithoutExtension(filename);
        return Path.Combine(_root, $"{name} ({suffix}){extension}");
    }

    private static DccDownloadTarget? ResumeCandidate(
        string path,
        long expectedBytes,
        string network,
        string sender)
    {
        var length = new FileInfo(path).Length;
        if (length <= 0 || length >= expectedBytes) return null;
        if (!TryReadMetadata(path, out var identity) || identity is null ||
            identity.ExpectedBytes != expectedBytes ||
            !identity.Network.Equals(network, StringComparison.OrdinalIgnoreCase) ||
            !identity.Sender.Equals(sender, StringComparison.OrdinalIgnoreCase))
            return null;
        var final = path[..^".clircs-part".Length];
        return new DccDownloadTarget(Path.GetFileName(final), path, final, length);
    }

    private static string MetadataPath(DccDownloadTarget target) => target.PartialPath + ".json";

    private static void WriteMetadata(DccDownloadTarget target, DccDownloadIdentity identity)
    {
        var json = JsonSerializer.Serialize(identity);
        File.WriteAllText(MetadataPath(target), json);
    }

    private static bool TryReadMetadata(string partialPath, out DccDownloadIdentity? identity)
    {
        try
        {
            var metadataPath = partialPath + ".json";
            if (!File.Exists(metadataPath))
            {
                identity = null;
                return false;
            }
            identity = JsonSerializer.Deserialize<DccDownloadIdentity>(File.ReadAllText(metadataPath));
            return identity is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            identity = null;
            return false;
        }
    }

    private static void DeleteMetadata(DccDownloadTarget target)
    {
        try
        {
            var path = MetadataPath(target);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool ResumeNameMatches(string offeredFilename, string candidateFilename)
    {
        if (candidateFilename.Equals(offeredFilename, StringComparison.OrdinalIgnoreCase)) return true;
        var extension = Path.GetExtension(offeredFilename);
        var name = Path.GetFileNameWithoutExtension(offeredFilename);
        if (!candidateFilename.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return false;
        var candidateName = Path.GetFileNameWithoutExtension(candidateFilename);
        return candidateName.StartsWith(name + " (", StringComparison.OrdinalIgnoreCase) &&
            candidateName.EndsWith(')');
    }

    private static void ValidateFilename(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename) || filename is "." or ".." ||
            Path.GetFileName(filename) != filename || Path.IsPathRooted(filename) ||
            filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The DCC filename is unsafe.", nameof(filename));
        }
    }
}
