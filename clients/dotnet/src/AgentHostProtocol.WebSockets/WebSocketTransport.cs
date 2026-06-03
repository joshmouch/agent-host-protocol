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
/// Options for <see cref="WebSocketTransport.ConnectAsync"/> and
/// <see cref="WebSocketTransport.FromClientWebSocket"/>.
/// </summary>
public sealed class WebSocketTransportOptions
{
    /// <summary>
    /// Optional callback invoked on the new <see cref="ClientWebSocket"/> before
    /// it connects. Use it to set request headers, sub-protocols, keep-alive,
    /// proxy settings, etc.
    /// </summary>
    public Action<ClientWebSocket>? ConfigureSocket { get; set; }

    /// <summary>
    /// Maximum number of bytes allowed in a single inbound message.
    /// A value ≤ 0 means unlimited. Defaults to 32 MiB.
    /// </summary>
    public long MaxMessageBytes { get; set; } = 32L * 1024 * 1024;
}

/// <summary>
/// A <see cref="ITransport"/> implementation backed by <see cref="ClientWebSocket"/>.
/// Use <see cref="ConnectAsync(Uri, WebSocketTransportOptions?, CancellationToken)"/> to dial,
/// or <see cref="FromClientWebSocket(ClientWebSocket, WebSocketTransportOptions?)"/> to wrap
/// an existing connection.
/// </summary>
public sealed class WebSocketTransport : ITransport
{
    private readonly ClientWebSocket _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly long _maxMessageBytes;

    // Receive buffer: 64 KiB initial, grows as needed.
    private byte[] _receiveBuffer = new byte[64 * 1024];

    private WebSocketTransport(ClientWebSocket ws, long maxMessageBytes)
    {
        _ws = ws;
        _maxMessageBytes = maxMessageBytes;
    }

    // ── Factory methods ───────────────────────────────────────────────────

    /// <summary>
    /// Dials <paramref name="uri"/> (must use <c>ws://</c> or <c>wss://</c>) and
    /// returns a ready-to-use <see cref="WebSocketTransport"/>.
    /// </summary>
    /// <param name="uri">The WebSocket server URI.</param>
    /// <param name="options">Optional configuration; see <see cref="WebSocketTransportOptions"/>.</param>
    /// <param name="cancellationToken">Cancellation token for the connect operation.</param>
    public static async Task<WebSocketTransport> ConnectAsync(
        Uri uri,
        WebSocketTransportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var ws = new ClientWebSocket();
        options?.ConfigureSocket?.Invoke(ws);
        await ws.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        var maxBytes = options?.MaxMessageBytes ?? (32L * 1024 * 1024);
        return new WebSocketTransport(ws, maxBytes);
    }

    /// <summary>
    /// Wraps an already-connected <see cref="ClientWebSocket"/> in a
    /// <see cref="WebSocketTransport"/>.
    /// The transport takes ownership of <paramref name="ws"/> and disposes it on
    /// <see cref="DisposeAsync"/>.
    /// </summary>
    /// <param name="ws">A connected <see cref="ClientWebSocket"/>.</param>
    /// <param name="options">Optional configuration; see <see cref="WebSocketTransportOptions"/>.</param>
    public static WebSocketTransport FromClientWebSocket(ClientWebSocket ws, WebSocketTransportOptions? options = null)
    {
        if (ws is null) throw new ArgumentNullException(nameof(ws));
        var maxBytes = options?.MaxMessageBytes ?? (32L * 1024 * 1024);
        return new WebSocketTransport(ws, maxBytes);
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
                // Perform the closing handshake (best effort).
                try
                {
                    await _ws.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "",
                        CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch { /* best effort */ }
                throw new TransportClosedException();
            }

            // Enforce the inbound message size cap.
            if (_maxMessageBytes > 0 && (builder.Length + result.Count) > _maxMessageBytes)
            {
                throw new TransportClosedException(
                    $"inbound message exceeds {_maxMessageBytes} bytes");
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
        if (_ws.State == WebSocketState.Open
            || _ws.State == WebSocketState.CloseReceived
            || _ws.State == WebSocketState.CloseSent)
        {
            try
            {
                await _ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "",
                    cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException) { /* best effort — state race */ }
            catch (InvalidOperationException) { /* best effort — state race */ }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        _ws.Dispose();
        _sendLock.Dispose();
    }
}
