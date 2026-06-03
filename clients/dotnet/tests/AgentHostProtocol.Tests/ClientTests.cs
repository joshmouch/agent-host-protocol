// Port of clients/go/ahp/client_test.go.
// Uses an in-memory transport pair (two linked channels) to exercise the real
// AhpClient over a real ITransport — no mocking of the client or JSON engine.
#nullable enable

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AgentHostProtocol;
using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

// ── In-memory transport pair ──────────────────────────────────────────────────

/// <summary>
/// Paired in-memory transport. The two ends share linked channels so frames
/// flow from one's outbox directly into the other's inbox, exactly as the Go
/// <c>memTransport</c> helper works.
/// </summary>
internal sealed class MemTransport : ITransport
{
    private readonly Channel<TransportMessage> _inbox;
    private readonly Channel<TransportMessage> _outbox;
    private readonly CancellationTokenSource _closeCts;

    private MemTransport(
        Channel<TransportMessage> inbox,
        Channel<TransportMessage> outbox,
        CancellationTokenSource closeCts)
    {
        _inbox = inbox;
        _outbox = outbox;
        _closeCts = closeCts;
    }

    /// <summary>Creates a linked pair. Frames sent to A appear on B's inbox and vice versa.</summary>
    public static (MemTransport A, MemTransport B) CreatePair()
    {
        var a2b = Channel.CreateBounded<TransportMessage>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.Wait });
        var b2a = Channel.CreateBounded<TransportMessage>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.Wait });
        var cts = new CancellationTokenSource(); // shared — closing either side closes both.
        return (new MemTransport(b2a, a2b, cts), new MemTransport(a2b, b2a, cts));
    }

    public async ValueTask SendAsync(TransportMessage message, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closeCts.Token);
        try { await _outbox.Writer.WriteAsync(message, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_closeCts.IsCancellationRequested)
        { throw new AhpTransportException("closed"); }
    }

    public async ValueTask<TransportMessage> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closeCts.Token);
        try { return await _inbox.Reader.ReadAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_closeCts.IsCancellationRequested)
        { throw new AhpTransportException("closed"); }
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        _closeCts.Cancel();
        _outbox.Writer.TryComplete();
        _inbox.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => CloseAsync();
}

// ── Helper: fake server ───────────────────────────────────────────────────────

internal static class FakeServer
{
    private static readonly SystemTextJsonAhpSerializer Ser = SystemTextJsonAhpSerializer.Default;

    /// <summary>
    /// Reads one <c>initialize</c> request and responds with a stub
    /// <see cref="InitializeResult"/>.
    /// </summary>
    public static async Task HandleOneInitialize(MemTransport serverSide, CancellationToken ct = default)
    {
        var frame = await serverSide.ReceiveAsync(ct).ConfigureAwait(false);
        var msg = Ser.DecodeMessage(frame);
        Assert.NotNull(msg.Request);
        Assert.Equal("initialize", msg.Request!.Method);

        var result = new InitializeResult { ProtocolVersion = ProtocolVersion.Current };
        var response = new JsonRpcMessage
        {
            SuccessResponse = new JsonRpcSuccessResponse
            {
                Id = msg.Request.Id,
                Result = JsonDocument.Parse(Ser.Serialize(result)).RootElement,
            }
        };
        await serverSide.SendAsync(Ser.EncodeMessage(response), ct).ConfigureAwait(false);
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public sealed class ClientTests
{
    private static readonly SystemTextJsonAhpSerializer Ser = SystemTextJsonAhpSerializer.Default;

    // ── Request round-trip ────────────────────────────────────────────────

    [Fact]
    public async Task RequestRoundTrip_InitializeReturnsProtocolVersion()
    {
        var (clientSide, serverSide) = MemTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Server goroutine: respond to one initialize request.
        var serverTask = Task.Run(() => FakeServer.HandleOneInitialize(serverSide, cts.Token), cts.Token);

        await using var client = await AhpClient.ConnectAsync(clientSide);
        var result = await client.InitializeAsync("test-client", cancellationToken: cts.Token);

        Assert.Equal(ProtocolVersion.Current, result.ProtocolVersion);
        await serverTask;
    }

    // ── Subscription fan-out ──────────────────────────────────────────────

    [Fact]
    public async Task SubscriptionFanOut_ActionReachesPerUriAndTopLevel()
    {
        var (clientSide, serverSide) = MemTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var client = await AhpClient.ConnectAsync(clientSide);
        var sub = client.AttachSubscription("ahp-session:/s1");
        var stream = client.CreateEventStream();

        // Push an `action` notification from the "server" side.
        var envelope = new ActionEnvelope
        {
            Channel = "ahp-session:/s1",
            ServerSeq = 1,
            Action = new StateAction(new SessionTitleChangedAction
            {
                Type = ActionType.SessionTitleChanged,
                Title = "Hello",
            }),
        };
        var notif = new JsonRpcMessage
        {
            Notification = new JsonRpcNotification
            {
                Method = "action",
                Params = JsonDocument.Parse(Ser.Serialize(envelope)).RootElement,
            }
        };
        await serverSide.SendAsync(Ser.EncodeMessage(notif), cts.Token);

        // Per-URI subscription receives the action.
        using var readSubCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var subEv = await sub.Events.ReadAsync(readSubCts.Token);
        var actionEv = Assert.IsType<SubscriptionEventAction>(subEv);
        Assert.Equal(1, actionEv.Envelope.ServerSeq);

        // Top-level stream also receives it.
        var clientEv = await stream.Events.ReadAsync(readSubCts.Token);
        Assert.Equal("ahp-session:/s1", clientEv.Channel);
        Assert.IsType<SubscriptionEventAction>(clientEv.Event);

        sub.Close();
        stream.Close();
    }

    // ── Shutdown fails in-flight request ──────────────────────────────────

    [Fact]
    public async Task Shutdown_FailsInFlightRequest()
    {
        var (clientSide, _) = MemTransport.CreatePair();
        // Don't set up a server — the request will hang until shutdown.

        var client = await AhpClient.ConnectAsync(clientSide);

        var requestTask = Task.Run(async () =>
        {
            try
            {
                await client.InitializeAsync("x", new[] { ProtocolVersion.Current });
                return (Exception?)null;
            }
            catch (Exception ex) { return ex; }
        });

        // Give the task time to enqueue the request.
        await Task.Delay(50);
        await client.ShutdownAsync();

        var err = await requestTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(err);
        // Either AhpClientClosedException or AhpRpcException (synthetic shutdown error).
        Assert.True(
            err is AhpClientClosedException || err is AhpRpcException,
            $"Expected AhpClientClosedException or AhpRpcException, got {err?.GetType().Name}: {err?.Message}");
    }

    // ── Done signalled on transport failure ───────────────────────────────

    [Fact]
    public async Task Done_SignalledOnTransportFailure()
    {
        var (clientSide, serverSide) = MemTransport.CreatePair();

        await using var client = await AhpClient.ConnectAsync(clientSide);

        // Closing the server end propagates as a receive error to the client.
        await serverSide.CloseAsync();

        // Client.Completion should fire within a reasonable time.
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(client.Error);
    }

    // ── Idempotent shutdown ───────────────────────────────────────────────

    [Fact]
    public async Task ShutdownIsIdempotent()
    {
        var (clientSide, _) = MemTransport.CreatePair();
        var client = await AhpClient.ConnectAsync(clientSide);

        // Concurrent shutdowns must not throw.
        var tasks = new Task[4];
        for (int i = 0; i < 4; i++)
        {
            var cap = i;
            tasks[cap] = Task.Run(() => client.ShutdownAsync());
        }
        await Task.WhenAll(tasks);
    }
}
