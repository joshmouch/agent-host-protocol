#nullable enable

using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AgentHostProtocol;
using Microsoft.AgentHostProtocol.WebSockets;
using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

/// <summary>
/// End-to-end integration over a REAL localhost WebSocket: the full
/// <see cref="AhpClient"/> + <see cref="WebSocketTransport"/> against a minimal
/// AHP server (BCL <see cref="HttpListener"/>, no external deps). Unlike the
/// MemTransport unit tests, this exercises the actual socket, the JSON-RPC
/// request/response correlation of <c>initialize</c>, and the routing of a live
/// <c>action</c> notification to a subscription — all over the wire.
/// </summary>
public sealed class LiveSocketIntegrationTests
{
    [Fact]
    public async Task FullClientHandshakeAndActionOverRealWebSocket()
    {
        const string channel = "ahp-session:/integration";
        var port = FreePort();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start(); // listening before the client connects

        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var serverTask = Task.Run(() => ServeOneClientAsync(listener, channel, serverCts.Token));

        // ── The real client over a real socket ──────────────────────────────
        await using var transport = await WebSocketTransport.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"));
        var client = AhpClient.Connect(transport);
        var sub = client.AttachSubscription(channel);

        // initialize is a real JSON-RPC request/response over the socket.
        var init = await client.InitializeAsync("integration-client", ProtocolVersion.Supported, new[] { channel });
        Assert.Equal("0.3.0", init.ProtocolVersion);

        // the server pushed an `action` notification; it must route to our sub.
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ev = await sub.Events.ReadAsync(readCts.Token);
        var actionEvent = Assert.IsType<SubscriptionEventAction>(ev);
        Assert.Equal(channel, actionEvent.Envelope.Channel);
        var title = Assert.IsType<SessionTitleChangedAction>(actionEvent.Envelope.Action.Value);
        Assert.Equal("Hello from the server", title.Title);

        // and it reduces like any other action.
        var state = new SessionState
        {
            Summary = new SessionSummary { Title = "old" },
            Lifecycle = SessionLifecycle.Ready,
            Turns = new System.Collections.Generic.List<Turn>(),
        };
        Reducers.ApplyToSession(state, actionEvent.Envelope.Action);
        Assert.Equal("Hello from the server", state.Summary.Title);

        await client.ShutdownAsync();
        serverCts.Cancel();
        try { await serverTask; } catch { /* server tears down */ }
    }

    /// <summary>A throwaway AHP server: answer <c>initialize</c>, then push one <c>action</c>.</summary>
    private static async Task ServeOneClientAsync(HttpListener listener, string channel, CancellationToken ct)
    {
        var ctx = await listener.GetContextAsync().ConfigureAwait(false);
        var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
        var ws = wsCtx.WebSocket;
        var buf = new byte[16 * 1024];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var sb = new StringBuilder();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) return;
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
            }
            while (!r.EndOfMessage);

            using var doc = JsonDocument.Parse(sb.ToString());
            var root = doc.RootElement;
            if (root.TryGetProperty("method", out var m) && m.GetString() == "initialize"
                && root.TryGetProperty("id", out var idEl))
            {
                var id = idEl.GetRawText();
                await SendAsync(ws,
                    $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"protocolVersion\":\"0.3.0\",\"serverSeq\":0,\"snapshots\":[]}}}}",
                    ct).ConfigureAwait(false);
                await SendAsync(ws,
                    "{\"jsonrpc\":\"2.0\",\"method\":\"action\",\"params\":{\"channel\":\"" + channel +
                    "\",\"action\":{\"type\":\"session/titleChanged\",\"title\":\"Hello from the server\"}," +
                    "\"serverSeq\":1,\"origin\":null}}",
                    ct).ConfigureAwait(false);
            }
        }
    }

    private static Task SendAsync(WebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, ct);

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
