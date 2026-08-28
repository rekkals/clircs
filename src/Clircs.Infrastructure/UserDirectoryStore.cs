using System.Text.Json;
using System.Text.Json.Serialization;
using Clircs.Identity;
using Clircs.Users;

namespace Clircs.Infrastructure;

public sealed class UserDirectoryStore
{
    private readonly string _directory;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly DurableFileWriter _files;

    public UserDirectoryStore(string directory, DurableFileWriter? files = null)
    {
        _directory = Path.GetFullPath(directory);
        _files = files ?? DurableFileWriter.Shared;
        Directory.CreateDirectory(_directory);
    }

    public NetworkUserDirectory Load(NetworkProfileId profileId)
    {
        var path = PathFor(profileId);
        return LoadFrom(profileId, path);
    }

    public NetworkUserDirectory LoadFrom(NetworkProfileId profileId, string sourcePath)
    {
        var path = Path.GetFullPath(sourcePath);
        if (!File.Exists(path))
        {
            if (path.Equals(PathFor(profileId), StringComparison.OrdinalIgnoreCase))
            {
                return new NetworkUserDirectory(profileId);
            }

            throw new FileNotFoundException("User-directory import file was not found.", path);
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<UserDirectoryEnvelope>(File.ReadAllText(path), _jsonOptions)
                ?? throw new InvalidDataException("User directory file is empty.");
            if (envelope.SchemaVersion is not (1 or 2))
            {
                throw new InvalidDataException($"Unsupported user directory schema {envelope.SchemaVersion}.");
            }

            if (envelope.NetworkProfileId != profileId.Value)
            {
                throw new InvalidDataException("User directory network profile ID does not match its filename.");
            }

            return new NetworkUserDirectory(profileId, envelope.Users.Select(user => new UserRecord(
                new UserRecordId(user.Id),
                user.Handle,
                user.Hostmasks,
                user.Roles,
                user.ChannelRoles,
                user.Comment,
                user.ChannelComments,
                user.CreatedAt,
                user.UpdatedAt)), envelope.PolicyBans.Select(ban => new PolicyBan(
                    ban.Id,
                    ban.Mask,
                    ban.Channels,
                    ban.Reason,
                    ban.CreatedAt)));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"User directory '{path}' is invalid and was left untouched: {exception.Message}", exception);
        }
    }

    public void Save(NetworkUserDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var path = PathFor(directory.NetworkProfileId);
        var envelope = new UserDirectoryEnvelope
        {
            SchemaVersion = 2,
            NetworkProfileId = directory.NetworkProfileId.Value,
            Users = directory.Users.Select(user => new StoredUser
            {
                Id = user.Id.Value,
                Handle = user.Handle,
                Hostmasks = user.Hostmasks.ToArray(),
                Roles = user.Roles,
                ChannelRoles = new Dictionary<string, UserRole>(user.ChannelRoles),
                Comment = user.Comment,
                ChannelComments = new Dictionary<string, string>(user.ChannelComments),
                CreatedAt = user.CreatedAt,
                UpdatedAt = user.UpdatedAt
            }).ToArray(),
            PolicyBans = directory.PolicyBans.Select(ban => new StoredPolicyBan
            {
                Id = ban.Id,
                Mask = ban.Mask,
                Channels = ban.Channels.ToArray(),
                Reason = ban.Reason,
                CreatedAt = ban.CreatedAt
            }).ToArray()
        };
        _files.WriteText(path, JsonSerializer.Serialize(envelope, _jsonOptions));
    }

    public void Export(NetworkUserDirectory directory, string destinationPath, bool overwrite = false)
    {
        Save(directory);
        var destination = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException("The export destination directory does not exist.");
        }

        File.Copy(PathFor(directory.NetworkProfileId), destination, overwrite);
    }

    public string PathFor(NetworkProfileId profileId) => Path.Combine(_directory, $"{profileId}.json");

    private sealed class UserDirectoryEnvelope
    {
        public int SchemaVersion { get; set; }

        public Guid NetworkProfileId { get; set; }

        public StoredUser[] Users { get; set; } = [];

        public StoredPolicyBan[] PolicyBans { get; set; } = [];
    }

    private sealed class StoredUser
    {
        public Guid Id { get; set; }

        public string Handle { get; set; } = string.Empty;

        public string[] Hostmasks { get; set; } = [];

        public UserRole Roles { get; set; }

        public Dictionary<string, UserRole> ChannelRoles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public string Comment { get; set; } = string.Empty;

        public Dictionary<string, string> ChannelComments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class StoredPolicyBan
    {
        public Guid Id { get; set; }

        public string Mask { get; set; } = string.Empty;

        public string[] Channels { get; set; } = [];

        public string Reason { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }
    }
}
