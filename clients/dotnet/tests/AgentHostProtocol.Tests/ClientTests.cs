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

    // ── Parity batch-a (matrix group D) ────────────────────────────────────
    // Phase-1 parity tests targeting ClientTests.cs. Each exercises the real
    // AhpClient over the real MemTransport + real SystemTextJsonAhpSerializer —
    // no SUT mocking. The "server" end is a real MemTransport endpoint driven
    // by hand: we decode the client's frame with Ser.DecodeMessage and reply
    // with a JsonRpc success/error frame via Ser.EncodeMessage.

    /// <summary>
    /// Reads one request whose method is <paramref name="expectedMethod"/> and replies
    /// with a JSON-RPC success response carrying <paramref name="result"/> serialized.
    /// Returns the decoded request so the caller can assert on it.
    /// </summary>
    private static async Task<JsonRpcRequest> AnswerOneRequestAsync<TResult>(
        MemTransport serverSide, string expectedMethod, TResult result, CancellationToken ct)
    {
        var frame = await serverSide.ReceiveAsync(ct).ConfigureAwait(false);
        var msg = Ser.DecodeMessage(frame);
        Assert.NotNull(msg.Request);
        Assert.Equal(expectedMethod, msg.Request!.Method);

        var response = new JsonRpcMessage
        {
            SuccessResponse = new JsonRpcSuccessResponse
            {
                Id = msg.Request.Id,
                Result = JsonDocument.Parse(Ser.Serialize(result)).RootElement,
            }
        };
        await serverSide.SendAsync(Ser.EncodeMessage(response), ct).ConfigureAwait(false);
        return msg.Request;
    }

    /// <summary>Builds an `action` notification frame for <paramref name="channel"/>.</summary>
    private static TransportMessage BuildActionNotification(string channel, long serverSeq, string title)
    {
        var envelope = new ActionEnvelope
        {
            Channel = channel,
            ServerSeq = serverSeq,
            Action = new StateAction(new SessionTitleChangedAction
            {
                Type = ActionType.SessionTitleChanged,
                Title = title,
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
        return Ser.EncodeMessage(notif);
    }

    // D: initialize snapshot in result.
    [Fact]
    public async Task Initialize_SnapshotDeliveredInResult()
    {
        var (clientSide, serverSide) = MemTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Server replies to `initialize` with a result carrying one snapshot.
        var initResult = new InitializeResult
        {
            ProtocolVersion = ProtocolVersion.Current,
            ServerSeq = 7,
            Snapshots = new System.Collections.Generic.List<Snapshot>
            {
                new Snapshot
                {
                    Resource = "ahp-session:/s1",
                    FromSeq = 7,
                    State = new SnapshotState
                    {
                        Root = new RootState { Agents = new System.Collections.Generic.List<AgentInfo>() },
                    },
                },
            },
        };
        var serverTask = Task.Run(
            () => AnswerOneRequestAsync(serverSide, "initialize", initResult, cts.Token), cts.Token);

        await using var client = await AhpClient.ConnectAsync(clientSide);
        var result = await client.InitializeAsync(
            "test-client",
            initialSubscriptions: new[] { "ahp-session:/s1" },
            cancellationToken: cts.Token);

        Assert.Equal(ProtocolVersion.Current, result.ProtocolVersion);
        Assert.NotNull(result.Snapshots);
        var snapshot = Assert.Single(result.Snapshots);
        Assert.Equal("ahp-session:/s1", snapshot.Resource);
        Assert.Equal(7, snapshot.FromSeq);
        await serverTask;
    }

    // D: subscribe round-trip + snapshot.
    [Fact]
    public async Task Subscribe_RoundTrip_DeliversSnapshot()
    {
        var (clientSide, serverSide) = MemTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var subResult = new SubscribeResult
        {
            Snapshot = new Snapshot
            {
                Resource = "ahp-session:/s1",
                FromSeq = 3,
                State = new SnapshotState
                {
                    Root = new RootState { Agents = new System.Collections.Generic.List<AgentInfo>() },
                },
            },
        };
        var serverTask = Task.Run(
            () => AnswerOneRequestAsync(serverSide, "subscribe", subResult, cts.Token), cts.Token);

        await using var client = await AhpClient.ConnectAsync(clientSide);
        var (result, sub) = await client.SubscribeAsync("ahp-session:/s1", cts.Token);

        // The SubscribeResult carries the snapshot...
        Assert.NotNull(result.Snapshot);
        Assert.Equal("ahp-session:/s1", result.Snapshot!.Resource);
        Assert.Equal(3, result.Snapshot.FromSeq);
        // ...and the returned Subscription is attached to the same URI.
        Assert.Equal("ahp-session:/s1", sub.Uri);

        sub.Close();
        await serverTask;
    }

    // D: attachSubscription (no round-trip subscribe request is sent).
    [Fact]
    public async Task AttachSubscription_DeliversWithoutRoundTrip()
    {
        var (clientSide, serverSide) = MemTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var client = await AhpClient.ConnectAsync(clientSide);
        var sub = client.AttachSubscription("ahp-session:/s1");

        // Push an `action` notification from the server; the attached sub receives it.
        await serverSide.SendAsync(BuildActionNotification("ahp-session:/s1", 1, "Hi"), cts.Token);

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var ev = await sub.Events.ReadAsync(readCts.Token);
        var actionEv = Assert.IsType<SubscriptionEventAction>(ev);
        Assert.Equal(1, actionEv.Envelope.ServerSeq);

        // No subscribe request must have been sent: the server side has no frame waiting.
        // Drain attempt with a short timeout — a frame here would mean a stray request.
        using var drainCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await serverSide.ReceiveAsync(drainCts.Token));

        sub.Close();
    }

    // D: multi-sub same uri — both subscriptions on one URI receive the event.
    [Fact]
    public async Task MultipleSubscriptions_SameUri_EachReceiveEvent()
    {
        var (clientSide, serverSide) = MemTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var client = await AhpClient.ConnectAsync(clientSide);
        var sub1 = client.AttachSubscription("ahp-session:/s1");
        var sub2 = client.AttachSubscription("ahp-session:/s1");

        await serverSide.SendAsync(BuildActionNotification("ahp-session:/s1", 9, "Both"), cts.Token);

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var ev1 = Assert.IsType<SubscriptionEventAction>(await sub1.Events.ReadAsync(readCts.Token));
        var ev2 = Assert.IsType<SubscriptionEventAction>(await sub2.Events.ReadAsync(readCts.Token));
        Assert.Equal(9, ev1.Envelope.ServerSeq);
        Assert.Equal(9, ev2.Envelope.ServerSeq);

        sub1.Close();
        sub2.Close();
    }

    // D: unsubscribe finishes stream — the subscription's channel completes.
    [Fact]
    public async Task Unsubscribe_FinishesStream()
    {
        var (clientSide, serverSide) = MemTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Drain the `unsubscribe` notification the client sends so the writer never blocks.
        var serverTask = Task.Run(async () =>
        {
            var frame = await serverSide.ReceiveAsync(cts.Token).ConfigureAwait(false);
            var msg = Ser.DecodeMessage(frame);
            Assert.NotNull(msg.Notification);
            Assert.Equal("unsubscribe", msg.Notification!.Method);
        }, cts.Token);

        await using var client = await AhpClient.ConnectAsync(clientSide);
        var sub = client.AttachSubscription("ahp-session:/s1");

        await client.UnsubscribeAsync("ahp-session:/s1", cts.Token);

        // The subscription channel is completed: ReadAllAsync finishes with no items,
        // and a direct ReadAsync throws ChannelClosedException.
        var received = 0;
        await foreach (var _ in sub.Events.ReadAllAsync(cts.Token))
            received++;
        Assert.Equal(0, received);
        await Assert.ThrowsAsync<System.Threading.Channels.ChannelClosedException>(
            async () => await sub.Events.ReadAsync(cts.Token));

        await serverTask;
    }

    // D: dispatch clientSeq — DispatchAsync emits a dispatchAction notif whose
    // clientSeq matches the returned DispatchHandle.ClientSeq.
    [Fact]
    public async Task Dispatch_EmitsActionNotification_WithClientSeq()
    {
        var (clientSide, serverSide) = MemTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var client = await AhpClient.ConnectAsync(clientSide);

        var action = new StateAction(new SessionTitleChangedAction
        {
            Type = ActionType.SessionTitleChanged,
            Title = "Dispatched",
        });
        var handle = await client.DispatchAsync("ahp-session:/s1", action, cts.Token);

        // The server reads the emitted frame and decodes the dispatchAction notification.
        var frame = await serverSide.ReceiveAsync(cts.Token);
        var msg = Ser.DecodeMessage(frame);
        Assert.NotNull(msg.Notification);
        Assert.Equal("dispatchAction", msg.Notification!.Method);
        Assert.NotNull(msg.Notification.Params);
        var dispatched = Ser.Deserialize<DispatchActionParams>(msg.Notification.Params.Value.GetRawText());
        Assert.Equal("ahp-session:/s1", dispatched.Channel);
        Assert.Equal(handle.ClientSeq, dispatched.ClientSeq);
    }

    // D: json-rpc error -> exception. A JsonRpcErrorResponse maps to AhpRpcException
    // carrying the same code.
    [Fact]
    public async Task RequestError_MapsToAhpRpcException()
    {
        var (clientSide, serverSide) = MemTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var serverTask = Task.Run(async () =>
        {
            var frame = await serverSide.ReceiveAsync(cts.Token).ConfigureAwait(false);
            var msg = Ser.DecodeMessage(frame);
            Assert.NotNull(msg.Request);
            var response = new JsonRpcMessage
            {
                ErrorResponse = new JsonRpcErrorResponse
                {
                    Id = msg.Request!.Id,
                    Error = new JsonRpcErrorObject { Code = -32601, Message = "method not found" },
                }
            };
            await serverSide.SendAsync(Ser.EncodeMessage(response), cts.Token).ConfigureAwait(false);
        }, cts.Token);

        await using var client = await AhpClient.ConnectAsync(clientSide);
        var ex = await Assert.ThrowsAsync<AhpRpcException>(
            async () => await client.InitializeAsync("x", cancellationToken: cts.Token));
        Assert.Equal(-32601, ex.Code);

        await serverTask;
    }

    // D: request timeout — a short DefaultRequestTimeout with no server reply throws.
    [Fact]
    public async Task Request_Timeout_ThrowsRpcTimeout()
    {
        var (clientSide, _) = MemTransport.CreatePair();
        // No server reply — the request must time out via the configured default timeout.
        var client = AhpClient.Connect(
            clientSide,
            new ClientConfig { DefaultRequestTimeout = TimeSpan.FromMilliseconds(50) });

        // RequestAsync's timeout path cancels the linked token, surfacing an
        // OperationCanceledException (TaskCanceledException derives from it).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await client.InitializeAsync("x"));

        await client.ShutdownAsync();
    }

    // D: inbound binary frame — a binary transport frame is decoded (not dropped)
    // and fanned out to subscribers.
    [Fact]
    public async Task InboundBinaryFrame_Decoded()
    {
        var (clientSide, serverSide) = MemTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var client = await AhpClient.ConnectAsync(clientSide);
        var sub = client.AttachSubscription("ahp-session:/s1");

        // Build the same `action` notification as UTF-8 bytes and send it as a BINARY frame.
        var textFrame = BuildActionNotification("ahp-session:/s1", 42, "Binary");
        Assert.NotNull(textFrame.Text);
        var bytes = System.Text.Encoding.UTF8.GetBytes(textFrame.Text!);
        await serverSide.SendAsync(TransportMessage.FromBinary(bytes), cts.Token);

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var ev = Assert.IsType<SubscriptionEventAction>(await sub.Events.ReadAsync(readCts.Token));
        Assert.Equal(42, ev.Envelope.ServerSeq);

        sub.Close();
    }

    // D: post-shutdown throws — operations after ShutdownAsync throw AhpClientClosedException.
    [Fact]
    public async Task PostShutdown_Operations_ThrowClientClosed()
    {
        var (clientSide, _) = MemTransport.CreatePair();
        var client = await AhpClient.ConnectAsync(clientSide);

        await client.ShutdownAsync();

        await Assert.ThrowsAsync<AhpClientClosedException>(
            async () => await client.RequestAsync<object?, InitializeResult>("initialize", null));
        await Assert.ThrowsAsync<AhpClientClosedException>(
            async () => await client.InitializeAsync("x"));
        await Assert.ThrowsAsync<AhpClientClosedException>(
            async () => await client.NotifyAsync<object?>("ping", null));
    }

    // D: server req -> MethodNotFound.
    // With no ServerRequestHandler installed, an inbound server-initiated request
    // is answered with a JSON-RPC MethodNotFound (-32601) error, not dropped.
    [Fact]
    public async Task ServerRequest_NoHandler_RepliesMethodNotFound()
    {
        var (clientSide, serverSide) = MemTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var client = await AhpClient.ConnectAsync(clientSide);
        // (no SetServerRequestHandler call)

        // Server sends a request (note: it HAS an id -> it's a request, not a notif).
        var req = new JsonRpcMessage
        {
            Request = new JsonRpcRequest { Id = 99, Method = "permission/request", Params = null },
        };
        await serverSide.SendAsync(Ser.EncodeMessage(req), cts.Token);

        // The client replies with an error frame carrying the same id + -32601.
        var replyFrame = await serverSide.ReceiveAsync(cts.Token);
        var reply = Ser.DecodeMessage(replyFrame);
        Assert.NotNull(reply.ErrorResponse);
        Assert.Equal(99UL, reply.ErrorResponse!.Id);
        Assert.Equal(JsonRpcErrorCodes.MethodNotFound, reply.ErrorResponse.Error.Code);
    }

    // D: server req -> handler result.
    // With a ServerRequestHandler installed, the client replies with the handler's
    // result for an inbound server-initiated request.
    [Fact]
    public async Task ServerRequest_Handler_RepliesResult()
    {
        var (clientSide, serverSide) = MemTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var client = await AhpClient.ConnectAsync(clientSide);
        client.SetServerRequestHandler((method, @params) =>
            Task.FromResult<object?>(new { ok = true, echoed = method }));

        var req = new JsonRpcMessage
        {
            Request = new JsonRpcRequest { Id = 7, Method = "permission/request", Params = null },
        };
        await serverSide.SendAsync(Ser.EncodeMessage(req), cts.Token);

        var replyFrame = await serverSide.ReceiveAsync(cts.Token);
        var reply = Ser.DecodeMessage(replyFrame);
        Assert.NotNull(reply.SuccessResponse);
        Assert.Equal(7UL, reply.SuccessResponse!.Id);
        // The handler's result object is serialized into the reply.
        var resultJson = reply.SuccessResponse.Result.GetRawText();
        Assert.Contains("\"ok\":true", resultJson);
        Assert.Contains("permission/request", resultJson);
    }
}
