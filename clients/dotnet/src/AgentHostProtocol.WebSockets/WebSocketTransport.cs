// WebSocket-backed ITransport implementation.
// Port of clients/go/ahpws/transport.go, adapted to BCL ClientWebSocket.
// No external NuGet dependencies — uses System.Net.WebSockets only.
#nullable enable

using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AgentHostProtocol;

namespace Microsoft.AgentHostProtocol.WebSockets;

/// <summary>
/// A <see cref="ITransport"/> implementation backed by <see cref="ClientWebSocket"/>.
/// Use <see cref="ConnectAsync(Uri, ClientWebSocketOptions?, CancellationToken)"/> to dial,
/// or <see cref="FromClientWebSocket(ClientWebSocket)"/> to wrap an existing connection.
/// </summary>
public sealed class WebSocketTransport : ITransport
{
    private readonly ClientWebSocket _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Receive buffer: 64 KiB initial, grows as needed.
    private byte[] _receiveBuffer = new byte[64 * 1024];

    private WebSocketTransport(ClientWebSocket ws) => _ws = ws;

    // ── Factory methods ───────────────────────────────────────────────────

    /// <summary>
    /// Dials <paramref name="uri"/> (must use <c>ws://</c> or <c>wss://</c>) and
    /// returns a ready-to-use <see cref="WebSocketTransport"/>.
    /// </summary>
    public static async Task<WebSocketTransport> ConnectAsync(
        Uri uri,
        ClientWebSocketOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var ws = new ClientWebSocket();
        if (options is not null)
        {
            // Copy commonly-needed options.
            foreach (var header in options.HttpVersion.ToString() is { } v ? Array.Empty<(string, string)>() : Array.Empty<(string, string)>())
            {
                // placeholder: user passes a pre-configured ws instead via FromClientWebSocket.
            }
        }
        await ws.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        return new WebSocketTransport(ws);
    }

    /// <summary>
    /// Wraps an already-connected <see cref="ClientWebSocket"/> in a <see cref="WebSocketTransport"/>.
    /// </summary>
    public static WebSocketTransport FromClientWebSocket(ClientWebSocket ws)
    {
        if (ws is null) throw new ArgumentNullException(nameof(ws));
        return new WebSocketTransport(ws);
    }

    // ── ITransport ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask SendAsync(TransportMessage message, CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (message.Frame == TransportFrame.Text)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(message.Text ?? "");
                await _ws.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var mem = message.Binary;
                await _ws.SendAsync(
                    mem,
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<TransportMessage> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        // Assemble fragments into a complete message.
        var builder = new System.IO.MemoryStream();
        WebSocketMessageType msgType = WebSocketMessageType.Text;

        while (true)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await _ws.ReceiveAsync(
                    new Memory<byte>(_receiveBuffer),
                    cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException ex)
            {
                throw new Exception($"ahp: websocket closed: {ex.Message}", ex);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Perform the closing handshake.
                try
                {
                    await _ws.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "",
                        CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch { /* best effort */ }
                throw new Exception("ahp: transport closed");
            }

            // Grow receive buffer if needed.
            if (result.Count == _receiveBuffer.Length && !result.EndOfMessage)
            {
                var bigger = new byte[_receiveBuffer.Length * 2];
                _receiveBuffer = bigger;
            }

            builder.Write(_receiveBuffer, 0, result.Count);
            msgType = result.MessageType;

            if (result.EndOfMessage)
                break;
        }

        var bytes = builder.ToArray();

        if (msgType == WebSocketMessageType.Binary)
            return TransportMessage.FromBinary(bytes);

        return TransportMessage.FromText(System.Text.Encoding.UTF8.GetString(bytes));
    }

    /// <inheritdoc />
    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
        {
            try
            {
                await _ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "",
                    cancellationToken)
                    .ConfigureAwait(false);
            }
            catch { /* best effort */ }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        _ws.Dispose();
    }
}
