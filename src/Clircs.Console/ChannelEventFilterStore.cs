using System.Text.Json;
using System.Text.Json.Serialization;
using Clircs.Identity;
using Clircs.Infrastructure;
using Clircs.Protocol;

namespace Clircs.ConsoleClient;

[Flags]
internal enum ChannelEventFamily
{
    None = 0,
    Join = 1,
    Part = 2,
    Quit = 4,
    Nick = 8,
    All = Join | Part | Quit | Nick
}

internal sealed class ChannelEventFilterStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter<ChannelEventFamily>(
                JsonNamingPolicy.CamelCase, allowIntegerValues: false)
        }
    };

    private static readonly Dictionary<IrcCaseMapping, IrcNameComparer> ChannelNames =
        Enum.GetValues<IrcCaseMapping>()
            .ToDictionary(mapping => mapping, mapping => new IrcNameComparer(mapping));

    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<Guid, Dictionary<string, ChannelEventFamily>> _profileFilters = [];

    public ChannelEventFilterStore(string path)
    {
        _path = Path.GetFullPath(path);
        Load();
    }

    public string? LoadError { get; private set; }

    public ChannelEventFamily GetHidden(
        NetworkProfileId profileId,
        string channel,
        IrcCaseMapping caseMapping)
    {
        lock (_gate)
        {
            var channels = _profileFilters.GetValueOrDefault(profileId.Value);
            return GetHidden(channels, channel, ChannelNames[caseMapping]);
        }
    }

    public ChannelEventFamily ChangeHidden(
        NetworkProfileId profileId,
        string channel,
        IrcCaseMapping caseMapping,
        ChannelEventFamily families,
        bool hide)
    {
        lock (_gate)
        {
            if (LoadError is { } error)
            {
                throw new InvalidOperationException(error);
            }

            var channels = _profileFilters.GetValueOrDefault(profileId.Value);
            var comparer = ChannelNames[caseMapping];
            var hidden = GetHidden(channels, channel, comparer);
            var updated = hide ? hidden | families : hidden & ~families;
            if (updated == hidden)
            {
                return hidden;
            }

            var updatedChannels = channels?.Where(pair => !comparer.Equals(pair.Key, channel))
                .ToDictionary(pair => pair.Key, pair => pair.Value) ?? [];
            if (updated != ChannelEventFamily.None)
            {
                updatedChannels.Add(channel, updated);
            }

            var candidate = new Dictionary<Guid, Dictionary<string, ChannelEventFamily>>(_profileFilters);
            if (updatedChannels.Count == 0)
            {
                candidate.Remove(profileId.Value);
            }
            else
            {
                candidate[profileId.Value] = updatedChannels;
            }

            DurableFileWriter.Shared.WriteText(_path, JsonSerializer.Serialize(candidate, JsonOptions));
            _profileFilters = candidate;
            return updated;
        }
    }

    private static ChannelEventFamily GetHidden(
        Dictionary<string, ChannelEventFamily>? channels,
        string channel,
        IrcNameComparer comparer)
    {
        var hidden = ChannelEventFamily.None;
        if (channels is not null)
        {
            foreach (var (name, families) in channels)
            {
                if (comparer.Equals(name, channel))
                {
                    hidden |= families;
                }
            }
        }

        return hidden;
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var filters = JsonSerializer.Deserialize<Dictionary<Guid, Dictionary<string, ChannelEventFamily>>>(
                File.ReadAllText(_path), JsonOptions)
                ?? throw new InvalidDataException("Channel event filters are empty.");

            if (filters.Any(pair => pair.Key == Guid.Empty || pair.Value is null))
            {
                throw new InvalidDataException("A channel event filter has an invalid profile or channel list.");
            }

            _profileFilters = filters;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            LoadError = $"Could not load channel event filters from '{_path}'. {exception.Message}";
        }
    }
}
