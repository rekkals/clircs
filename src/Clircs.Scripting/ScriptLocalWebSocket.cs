using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Serialization;

namespace Clircs.Scripting;

internal sealed record ScriptWebSocketEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("data")] string? Data = null,
    [property: JsonPropertyName("message")] string? Message = null);

internal sealed class ScriptLocalWebSocket : IAsyncDisposable
{
    private const int MaximumMessageBytes = 1024 * 1024;
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action<ScriptWebSocketEvent> _publish;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private Task? _run;

    public ScriptLocalWebSocket(Action<ScriptWebSocketEvent> publish)
    {
        _publish = publish;
        _socket.Options.Proxy = null;
    }

    public Task Completion => _run ?? Task.CompletedTask;

    public void Start(string address)
    {
        var uri = ValidateAddress(address);
        _run = RunAsync(uri);
    }

    public async Task SendAsync(string text)
    {
        if (_socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("The local WebSocket is not open.");
        }

        var data = Encoding.UTF8.GetBytes(text);
        if (data.Length > MaximumMessageBytes)
        {
            throw new InvalidOperationException("Local WebSocket messages cannot exceed one megabyte.");
        }

        await _sendGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(data, WebSocketMessageType.Text, true, _lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "script unloaded",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Cancellation and a disappearing local process are normal during unload.
            }
        }

        if (_run is not null)
        {
            try
            {
                await _run.ConfigureAwait(false);
            }
            catch
            {
                // RunAsync publishes useful failures before it completes.
            }
        }

        _socket.Dispose();
        _sendGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task RunAsync(Uri uri)
    {
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await _socket.ConnectAsync(uri, connectTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
            {
                throw new TimeoutException("The local WebSocket connection timed out.");
            }
            _publish(new ScriptWebSocketEvent("open"));
            await ReceiveAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _publish(new ScriptWebSocketEvent("error", Message: FriendlyMessage(exception)));
        }
        finally
        {
            _publish(new ScriptWebSocketEvent(
                "close",
                Message: _socket.CloseStatusDescription));
        }
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (!_lifetime.IsCancellationRequested)
        {
            var result = await _socket.ReceiveAsync(buffer, _lifetime.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("Only text messages are available to scripts.");
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaximumMessageBytes)
            {
                throw new InvalidDataException("A local WebSocket message exceeded one megabyte.");
            }

            if (!result.EndOfMessage)
            {
                continue;
            }

            _publish(new ScriptWebSocketEvent("message", Encoding.UTF8.GetString(message.ToArray())));
            message.SetLength(0);
        }
    }

    internal static Uri ValidateAddress(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("ws" or "wss"))
        {
            throw new InvalidOperationException("Local WebSocket addresses must use ws:// or wss://.");
        }

        var loopback = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            System.Net.IPAddress.TryParse(uri.Host, out var parsed) && System.Net.IPAddress.IsLoopback(parsed);
        if (!loopback)
        {
            throw new UnauthorizedAccessException("Script local WebSockets are restricted to localhost.");
        }

        return uri;
    }

    private static string FriendlyMessage(Exception exception) => exception switch
    {
        WebSocketException webSocket when webSocket.InnerException is not null => webSocket.InnerException.Message,
        _ => exception.Message
    };
}
