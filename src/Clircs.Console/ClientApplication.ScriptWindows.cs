using Clircs.Identity;
using Clircs.Networking;
using Clircs.Scripting;
using Clircs.State;
using Clircs.Sessions;

namespace Clircs.ConsoleClient;

internal sealed partial class ClientApplication
{
    private BufferId? ScriptActiveWindow()
    {
        lock (_windowTransactionGate)
        {
            return ActiveDisplayWindowSnapshot().Buffer?.Id;
        }
    }

    private BufferId? ScriptStatusWindow(NetworkSessionId? networkId)
    {
        lock (_windowTransactionGate)
        {
            return networkId is { } id
                ? _liveSessions.Find(id)?.State.StatusBuffer.Id
                : _localStatusBuffer.Id;
        }
    }

    private BufferId? FindScriptWindow(string name, NetworkSessionId? networkId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_windowTransactionGate)
        {
            if (networkId is { } id)
            {
                var session = _liveSessions.Find(id);
                return session is not null &&
                    session.State.TryGetBuffer(name, out var buffer)
                        ? buffer!.Id
                        : null;
            }

            if (_localStatusBuffer.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return _localStatusBuffer.Id;
            }

            return _scriptWindows.Find(name, null)?.Id;
        }
    }

    private BufferId OpenScriptWindow(
        string scriptId,
        string name,
        NetworkSessionId? networkId)
    {
        BufferId? opened = null;
        _applicationEvents.Dispatch(() =>
        {
            lock (_windowTransactionGate)
            {
                if (_exitRequested || _lifetime.IsCancellationRequested)
                {
                    throw new InvalidOperationException("The client is shutting down.");
                }

                IrcNetworkSession? session = null;
                if (networkId is { } id)
                {
                    session = _liveSessions.Find(id)
                        ?? throw new InvalidOperationException(
                            "The requested network session no longer exists.");
                }

                var buffer = _scriptWindows.Open(scriptId, name, session?.State);
                _windowStates.AssignNumber(buffer.Id);
                opened = buffer.Id;
            }

            RefreshWindowChrome();
        });

        return opened
            ?? throw new InvalidOperationException("The client is shutting down.");
    }

    internal ScriptWindowHostServices CreateScriptWindowHostServices() => new(
        ScriptActiveWindow,
        ScriptStatusWindow,
        FindScriptWindow,
        OpenScriptWindow,
        WriteScriptWindow);

    internal SessionEvent[] WindowHistorySnapshot(BufferId bufferId) =>
        _windowStates.HistorySnapshot(bufferId);

    private bool WriteScriptWindow(
        string scriptId,
        BufferId bufferId,
        ScriptWindowOutput output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        var formatted = ScriptWindowOutputFormatter.Format(output);
        var written = false;

        _applicationEvents.Dispatch(() =>
        {
            BufferState buffer;
            SessionEvent sessionEvent;
            StoredWindowEvent stored;

            lock (_windowTransactionGate)
            {
                if (_exitRequested || _lifetime.IsCancellationRequested) return;

                var target = ResolveScriptOutputWindowUnsafe(bufferId);
                if (target.Buffer is not { } destination) return;
                buffer = destination;

                var now = DateTimeOffset.Now;
                sessionEvent = new SessionEvent(
                    buffer.NetworkSessionId,
                    buffer.Id,
                    SessionEventKind.Status,
                    formatted.PlainText,
                    output.Timestamp ?? now,
                    new Dictionary<string, string?>
                    {
                        ["event"] = "script.output",
                        ["scriptId"] = scriptId
                    },
                    FormattedContent: formatted)
                {
                    ReceivedAt = now
                };

                if (LogSessionEvent(sessionEvent, target.Session, buffer) !=
                    ResourceQueueWriteResult.Accepted)
                {
                    throw new InvalidOperationException(
                        "Script output could not be queued for logging.");
                }

                stored = StoreWindowEventUnsafe(
                    sessionEvent,
                    buffer.Name,
                    isReplay: false,
                    trackUnread: true,
                    assignNumber: buffer.Id != _localStatusBuffer.Id);

                if (!stored.Stored) return;
                written = true;
            }

            if (stored.IsActive && !stored.IsScrolled)
            {
                if (stored.Replaced)
                {
                    RedrawActiveBuffer();
                }
                else
                {
                    PresentWindowEvent(sessionEvent, buffer.Name, stored.DateChange);
                }
            }

            RefreshWindowChrome();
        });

        return written;
    }

    private (IrcNetworkSession? Session, BufferState? Buffer) ResolveScriptOutputWindowUnsafe(
        BufferId bufferId)
    {
        if (bufferId == _localStatusBuffer.Id)
        {
            return (null, _localStatusBuffer);
        }

        foreach (var session in _liveSessions.SessionSnapshot())
        {
            if (session.State.TryGetBuffer(bufferId, out var buffer))
            {
                return (session, buffer);
            }
        }

        return (null, _scriptWindows.FindLocal(bufferId));
    }
}
