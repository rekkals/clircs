using System.Globalization;
using Clircs.Protocol;
using Clircs.State;

namespace Clircs.Sessions;

internal sealed class ChannelListResponseProcessor(
    NetworkSessionState state,
    SessionEventBuilder events)
{
    private static readonly IReadOnlyDictionary<string, char> EntryNumerics =
        new Dictionary<string, char>(StringComparer.Ordinal)
        {
            ["367"] = 'b',
            ["348"] = 'e',
            ["346"] = 'I',
            ["728"] = 'q'
        };

    private static readonly IReadOnlyDictionary<string, char> EndNumerics =
        new Dictionary<string, char>(StringComparer.Ordinal)
        {
            ["368"] = 'b',
            ["349"] = 'e',
            ["347"] = 'I',
            ["729"] = 'q'
        };

    public bool TryProcess(IrcMessage message, DateTimeOffset now, out IReadOnlyList<SessionEvent> results)
    {
        if (EntryNumerics.TryGetValue(message.Command, out var entryMode))
        {
            AddEntry(message, entryMode);
            results = [];
            return true;
        }

        if (EndNumerics.TryGetValue(message.Command, out var endMode))
        {
            results = Complete(message, endMode, now);
            return true;
        }

        results = [];
        return false;
    }

    private void AddEntry(IrcMessage message, char mode)
    {
        if (message.Parameters.Count < 3) return;

        var channel = state.GetOrCreateChannel(message.Parameters[1]);
        if (!channel.IsChannelListSynchronizing(mode))
        {
            channel.BeginChannelListSynchronization(mode);
        }

        var maskIndex = mode == 'q' && message.Parameters.Count >= 4 &&
            message.Parameters[2].Length == 1 && message.Parameters[2][0] == 'q'
                ? 3
                : 2;
        if (maskIndex >= message.Parameters.Count) return;

        var setBy = maskIndex + 1 < message.Parameters.Count
            ? message.Parameters[maskIndex + 1]
            : null;
        DateTimeOffset? setAt = null;
        if (maskIndex + 2 < message.Parameters.Count &&
            long.TryParse(message.Parameters[maskIndex + 2], NumberStyles.None, CultureInfo.InvariantCulture,
                out var timestamp))
        {
            try
            {
                setAt = DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime();
            }
            catch (ArgumentOutOfRangeException)
            {
                setAt = null;
            }
        }

        channel.AddChannelListEntry(new ChannelListEntry(
            mode,
            message.Parameters[maskIndex],
            setBy,
            setAt));
    }

    private IReadOnlyList<SessionEvent> Complete(IrcMessage message, char mode, DateTimeOffset now)
    {
        if (message.Parameters.Count < 2) return [];

        var channelName = message.Parameters[1];
        var channel = state.GetOrCreateChannel(channelName);
        if (!channel.IsChannelListSynchronizing(mode))
        {
            channel.BeginChannelListSynchronization(mode);
        }
        channel.CompleteChannelListSynchronization(mode);

        var entries = channel.ChannelList(mode).ToArray();
        var buffer = state.TryGetBuffer(channelName, out var existingBuffer)
            ? existingBuffer!
            : state.StatusBuffer;
        var description = Description(mode);
        var fields = SessionEventBuilder.Fields(
            ("numeric", message.Command),
            ("channel", channelName),
            ("listMode", mode.ToString()),
            ("outputEnd", "true"));

        if (entries.Length == 0)
        {
            return
            [
                events.Create(buffer, SessionEventKind.Server, $"No {description.PluralLower} set", now, fields)
            ];
        }

        var rows = entries.Select(entry => (IReadOnlyList<string>)
        [
            entry.Mask,
            string.IsNullOrWhiteSpace(entry.SetBy) ? "unknown" : entry.SetBy,
            entry.SetAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "unknown"
        ]).ToArray();
        var noun = entries.Length == 1 ? description.SingularLower : description.PluralLower;
        var presentation = new PresentationBlock(
            description.Title,
            Table: new PresentationTable(
                ["Mask", "Set by", "Set"],
                rows,
                PreserveColumns: new HashSet<int> { 0 },
                MaximumWidths: [PresentationTable.UnboundedWidth, 28, 19]),
            Summary: $"{entries.Length} {noun}",
            TitleHighlight: channelName);

        return
        [
            events.Create(buffer, SessionEventKind.Server,
                $"{description.TitleText} {channelName}: {entries.Length} {noun}", now, fields, presentation)
        ];
    }

    private static ListDescription Description(char mode) => mode switch
    {
        'b' => new("BANS:", "Bans", "ban", "bans"),
        'e' => new("BAN EXCEPTIONS:", "Ban exceptions", "ban exception", "ban exceptions"),
        'I' => new("INVITE EXCEPTIONS:", "Invite exceptions", "invite exception", "invite exceptions"),
        'q' => new("QUIETS:", "Quiets", "quiet", "quiets"),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private sealed record ListDescription(
        string Title,
        string TitleText,
        string SingularLower,
        string PluralLower);
}
