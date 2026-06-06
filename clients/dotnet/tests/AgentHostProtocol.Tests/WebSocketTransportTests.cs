// Phase-1 parity tests for matrix group E (transport) — real WebSocket path.
// These are the no-mock centrepiece: a REAL OS loopback socket via
// System.Net.HttpListener, a REAL WebSocket handshake, and the REAL
// WebSocketTransport + ClientWebSocket. Nothing here is faked — the server is
// a genuine HttpListener accepting a genuine WebSocket upgrade.
#nullable enable

using System;
using System.Net;                 // HttpListener, IPEndPoint
using System.Net.Sockets;         // TcpListener (free-port picking)
using System.Net.WebSockets;      // WebSocket, WebSocketMessageType, ...
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AgentHostProtocol;
using Microsoft.AgentHostProtocol.WebSockets;
using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

public sealed class WebSocketTransportTests
{
    // ── Loopback WebSocket server harness ─────────────────────────────────

    /// <summary>
    /// A real loopback WebSocket server built on <see cref="HttpListener"/>.
    /// Picks a free 127.0.0.1 port (HttpListener can't bind ephemeral port 0),
    /// accepts exactly one WebSocket upgrade, and hands the accepted server
    /// <see cref="WebSocket"/> to the supplied handler.
    /// </summary>
    private sealed class LoopbackWsServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        public int Port { get; }
        public Uri WsUri => new($"ws://127.0.0.1:{Port}/");

        private LoopbackWsServer(HttpListener listener, int port)
        {
            _listener = listener;
            Port = port;
        }

        /// <summary>Picks a free loopback port, starts an HttpListener on it, and returns the server.</summary>
        public static LoopbackWsServer Start()
        {
            int port = FreeLoopbackPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            // If HttpListener can't bind (locked-down box / perms), this throws
            // HttpListenerException — surfaced to the caller, NOT swallowed.
            listener.Start();
            return new LoopbackWsServer(listener, port);
        }

        /// <summary>
        /// Accepts exactly one connection; if it's a WebSocket upgrade, invokes
        /// <paramref name="handler"/> with the accepted server socket, then
        /// disposes it. Returns a Task that completes when the handler returns.
        /// </summary>
        public Task AcceptOneAsync(Func<WebSocket, CancellationToken, Task> handler, CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                if (!ctx.Request.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                    throw new InvalidOperationException("expected a WebSocket upgrade request");
                }

                var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
                var serverWs = wsCtx.WebSocket;
                try
                {
                    await handler(serverWs, ct).ConfigureAwait(false);
                }
                finally
                {
                    serverWs.Dispose();
                }
            }, ct);
        }

        /// <summary>
        /// Binds a TcpListener on 127.0.0.1:0, reads the OS-assigned port, then
        /// releases it. Small race window, acceptable for a loopback test.
        /// </summary>
        private static int FreeLoopbackPort()
        {
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            try { return ((IPEndPoint)tcp.LocalEndpoint).Port; }
            finally { tcp.Stop(); }
        }

        public ValueTask DisposeAsync()
        {
            try { _listener.Stop(); } catch { /* best effort */ }
            try { ((IDisposable)_listener).Dispose(); } catch { /* best effort */ }
            return ValueTask.CompletedTask;
        }
    }

    // ── E: real-socket handshake (HttpListener loopback) ──────────────────
    // Stands up a real loopback WebSocket server, dials it with the production
    // WebSocketTransport.ConnectAsync (real ClientWebSocket + real handshake),
    // round-trips one text frame (server echoes), and asserts the payload.
    [Fact]
    public async Task NativeTransport_PerformsHandshakeAndRoundTripsText()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = LoopbackWsServer.Start();

        // Server: receive one text frame and echo it back.
        var serverTask = server.AcceptOneAsync(async (serverWs, ct) =>
        {
            var buf = new byte[4096];
            var result = await serverWs.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            await serverWs.SendAsync(
                new ArraySegment<byte>(buf, 0, result.Count),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct).ConfigureAwait(false);
            // Cleanly close after the echo.
            await serverWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct).ConfigureAwait(false);
        }, cts.Token);

        const string payload = "{\"jsonrpc\":\"2.0\",\"method\":\"ping\"}";

        await using var transport = await WebSocketTransport.ConnectAsync(server.WsUri, cancellationToken: cts.Token);
        await transport.SendAsync(TransportMessage.FromText(payload), cts.Token);

        var got = await transport.ReceiveAsync(cts.Token);
        Assert.Equal(TransportFrame.Text, got.Frame);
        Assert.Equal(payload, got.Text);

        await transport.CloseAsync(cts.Token);
        await serverTask;
    }

    // ── E: reject unsupported scheme ──────────────────────────────────────
    // ClientWebSocket rejects non-ws/wss URIs. A short-timeout CTS guards
    // against any hang. We catch broadly and assert an exception was raised.
    [Fact]
    public async Task NativeTransport_RejectsUnsupportedScheme()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var ex = await Record.ExceptionAsync(async () =>
            await WebSocketTransport.ConnectAsync(new Uri("http://localhost:1/"), cancellationToken: cts.Token));

        Assert.NotNull(ex);
        // ClientWebSocket.ConnectAsync throws ArgumentException for a non-ws
        // scheme (it may surface wrapped in other exception types across
        // runtimes); accept the broad transport-error family but NOT a clean
        // return.
        Assert.True(
            ex is ArgumentException
                or InvalidOperationException
                or WebSocketException
                or NotSupportedException,
            $"Expected a scheme-rejection exception, got {ex.GetType().Name}: {ex.Message}");
    }

    // ── E: clean close drains null ────────────────────────────────────────
    // Method name is historical (mirrors the Go test name). The .NET contract
    // is throw-not-null: on a CLEAN remote close, WebSocketTransport.ReceiveAsync
    // throws TransportClosedException (see WebSocketTransport.cs ~line 164), it
    // does NOT return null. Assert the actual .NET behaviour.
    [Fact]
    public async Task WsTransport_CleanClose_DrainsRecvNull()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = LoopbackWsServer.Start();

        // Server cleanly closes immediately after the handshake.
        var serverTask = server.AcceptOneAsync(async (serverWs, ct) =>
        {
            await serverWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct).ConfigureAwait(false);
        }, cts.Token);

        await using var transport = await WebSocketTransport.ConnectAsync(server.WsUri, cancellationToken: cts.Token);

        await Assert.ThrowsAsync<TransportClosedException>(
            async () => await transport.ReceiveAsync(cts.Token));

        await serverTask;
    }

    // ── E: abnormal close error ───────────────────────────────────────────
    // On an ABNORMAL close (server aborts the socket without a close frame),
    // WebSocketTransport.ReceiveAsync wraps the WebSocketException into a thrown
    // Exception ("ahp: websocket closed: ...", see WebSocketTransport.cs ~145).
    // Assert that an exception is raised — i.e. NOT a clean TransportClosedException
    // drain.
    [Fact]
    public async Task WsTransport_AbnormalClose_RaisesTransportError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = LoopbackWsServer.Start();

        // Server abruptly aborts the socket (no close handshake) right after accept.
        var serverTask = server.AcceptOneAsync((serverWs, ct) =>
        {
            serverWs.Abort();
            return Task.CompletedTask;
        }, cts.Token);

        await using var transport = await WebSocketTransport.ConnectAsync(server.WsUri, cancellationToken: cts.Token);

        var ex = await Record.ExceptionAsync(async () => await transport.ReceiveAsync(cts.Token));
        Assert.NotNull(ex);
        // An abnormal close must surface as a fault, not a clean
        // TransportClosedException drain. WebSocketTransport.ReceiveAsync wraps
        // WebSocketException into a plain Exception ("ahp: websocket closed:").
        Assert.IsNotType<TransportClosedException>(ex);

        await serverTask;
    }
}
