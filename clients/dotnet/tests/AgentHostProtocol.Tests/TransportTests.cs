// Phase-1 parity tests for matrix group E (transport) — in-memory transport.
// Exercises the REAL in-memory ITransport pair (MemTransport, defined in
// ClientTests.cs) over the REAL SystemTextJsonAhpSerializer. No mocking of
// ITransport or the JSON engine — the transport pair is the production helper
// the client tests use, and frames flow through real channels.
#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AgentHostProtocol;
using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

public sealed class TransportTests
{
    private static readonly SystemTextJsonAhpSerializer Ser = SystemTextJsonAhpSerializer.Default;

    // ── E: in-mem both directions ─────────────────────────────────────────
    // A frame sent on A arrives on B, and a frame sent on B arrives on A.
    [Fact]
    public async Task InMemoryTransport_DeliversBothDirections()
    {
        var (a, b) = MemTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // A -> B
        await a.SendAsync(TransportMessage.FromText("a-to-b"), cts.Token);
        var onB = await b.ReceiveAsync(cts.Token);
        Assert.Equal(TransportFrame.Text, onB.Frame);
        Assert.Equal("a-to-b", onB.Text);

        // B -> A (a *different* payload, to prove the channels aren't crossed)
        await b.SendAsync(TransportMessage.FromText("b-to-a"), cts.Token);
        var onA = await a.ReceiveAsync(cts.Token);
        Assert.Equal(TransportFrame.Text, onA.Frame);
        Assert.Equal("b-to-a", onA.Text);

        await a.CloseAsync(cts.Token);
    }

    // ── E: close ends recv ────────────────────────────────────────────────
    // Closing either end unblocks a pending/subsequent ReceiveAsync on BOTH
    // ends with the closed signal (MemTransport throws AhpTransportException
    // "closed" — see ClientTests.cs MemTransport.ReceiveAsync).
    [Fact]
    public async Task InMemoryTransport_Close_EndsBothRecv()
    {
        var (a, b) = MemTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Start a receive on each end BEFORE closing, so we prove a *pending*
        // receive unblocks (not just a post-close one).
        var recvA = a.ReceiveAsync(cts.Token).AsTask();
        var recvB = b.ReceiveAsync(cts.Token).AsTask();

        // Give both receives a moment to actually park on the channel.
        await Task.Delay(50, cts.Token);

        await a.CloseAsync(cts.Token);

        // Both pending receives end with the closed signal.
        var exA = await Assert.ThrowsAsync<AhpTransportException>(() => recvA);
        Assert.Contains("closed", exA.Message, StringComparison.OrdinalIgnoreCase);
        var exB = await Assert.ThrowsAsync<AhpTransportException>(() => recvB);
        Assert.Contains("closed", exB.Message, StringComparison.OrdinalIgnoreCase);

        // A subsequent receive on either end also fails fast.
        await Assert.ThrowsAsync<AhpTransportException>(
            async () => await b.ReceiveAsync(cts.Token));
    }

    // ── E: send after close throws ────────────────────────────────────────
    [Fact]
    public async Task InMemoryTransport_SendAfterClose_Throws()
    {
        var (a, _) = MemTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await a.CloseAsync(cts.Token);

        var ex = await Assert.ThrowsAsync<AhpTransportException>(
            async () => await a.SendAsync(TransportMessage.FromText("nope"), cts.Token));
        Assert.Contains("closed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── E: TransportMessage round-trip ────────────────────────────────────
    // A JSON-RPC notification packed into a TransportMessage.FromText survives
    // the transport intact and decodes back to a notification with the same
    // method via the real serializer.
    [Fact]
    public async Task TransportMessage_RoundTrip_Notification()
    {
        var (a, b) = MemTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // A JSON-RPC notification: has "method", no "id".
        const string json = "{\"jsonrpc\":\"2.0\",\"method\":\"action\",\"params\":{}}";
        await a.SendAsync(TransportMessage.FromText(json), cts.Token);

        var received = await b.ReceiveAsync(cts.Token);
        Assert.Equal(TransportFrame.Text, received.Frame);
        Assert.Equal(json, received.Text);

        var decoded = Ser.DecodeMessage(received);
        Assert.NotNull(decoded.Notification);
        Assert.Null(decoded.Request);
        Assert.Equal("action", decoded.Notification!.Method);

        await a.CloseAsync(cts.Token);
    }
}
