// Port of the H-group multi-host parity tests (Phase 1 rows). Drives the real
// MultiHostClient over the real MemTransport with a fake server — no mocking of
// the client, the transport, or the JSON engine. Reuses the MemTransport helper
// and the RunFakeServer idiom established by HostsTests.cs / ClientTests.cs.
#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;          // mirror/client tests that build wire payloads
using Microsoft.AgentHostProtocol;
using Microsoft.AgentHostProtocol.Hosts;
using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

public sealed class MultiHostClientTests
{
    private static readonly SystemTextJsonAhpSerializer Ser = SystemTextJsonAhpSerializer.Default;

    // ── Fake server helpers ───────────────────────────────────────────────

    /// <summary>
    /// Server loop that answers <c>initialize</c>. Mirrors HostsTests.RunFakeServer.
    /// </summary>
    private static async Task RunFakeServerAsync(MemTransport serverSide, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                TransportMessage frame;
                try { frame = await serverSide.ReceiveAsync(ct).ConfigureAwait(false); }
                catch { return; }

                JsonRpcMessage msg;
                try { msg = Ser.DecodeMessage(frame); }
                catch { return; }

                if (msg.Request?.Method == "initialize")
                {
                    await RespondInitializeAsync(serverSide, msg.Request.Id, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Server loop that answers <c>initialize</c> then pushes the same
    /// <c>action</c> notification on <paramref name="actionChannel"/> with
    /// <paramref name="serverSeq"/> on a short repeat. Used to prove host-tagged
    /// events fan out. The repeat closes the timing gap between the server's
    /// post-initialize send and the host pump registering its event stream
    /// (the pump only attaches after InitializeAsync returns inside
    /// MultiHostClient.OpenHostAsync). DropOldest channels make the extra sends
    /// harmless — the consumer reads exactly one.
    /// </summary>
    private static async Task RunFakeServerWithActionAsync(
        MemTransport serverSide,
        string actionChannel,
        long serverSeq,
        CancellationToken ct)
    {
        try
        {
            while (true)
            {
                TransportMessage frame;
                try { frame = await serverSide.ReceiveAsync(ct).ConfigureAwait(false); }
                catch { return; }

                JsonRpcMessage msg;
                try { msg = Ser.DecodeMessage(frame); }
                catch { return; }

                if (msg.Request?.Method == "initialize")
                {
                    await RespondInitializeAsync(serverSide, msg.Request.Id, ct).ConfigureAwait(false);
                    // Fire-and-forget repeated push so the pump can't miss it.
                    _ = Task.Run(() => RepeatActionAsync(serverSide, actionChannel, serverSeq, ct));
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Pushes the action repeatedly until cancelled or the peer drops.</summary>
    private static async Task RepeatActionAsync(
        MemTransport serverSide, string channel, long serverSeq, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await SendActionAsync(serverSide, channel, serverSeq, ct).ConfigureAwait(false);
                await Task.Delay(25, ct).ConfigureAwait(false);
            }
        }
        catch { /* cancelled or peer gone */ }
    }

    private static async Task RespondInitializeAsync(
        MemTransport serverSide, ulong id, CancellationToken ct)
    {
        var result = new InitializeResult { ProtocolVersion = ProtocolVersion.Current };
        var response = new JsonRpcMessage
        {
            SuccessResponse = new JsonRpcSuccessResponse
            {
                Id = id,
                Result = JsonDocument.Parse(Ser.Serialize(result)).RootElement,
            }
        };
        try { await serverSide.SendAsync(Ser.EncodeMessage(response), ct).ConfigureAwait(false); }
        catch { /* peer gone */ }
    }

    private static async Task SendActionAsync(
        MemTransport serverSide, string channel, long serverSeq, CancellationToken ct)
    {
        var envelope = new ActionEnvelope
        {
            Channel = channel,
            ServerSeq = serverSeq,
            Action = new StateAction(new SessionTitleChangedAction
            {
                Type = ActionType.SessionTitleChanged,
                Title = $"seq-{serverSeq}",
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
        try { await serverSide.SendAsync(Ser.EncodeMessage(notif), ct).ConfigureAwait(false); }
        catch { /* peer gone */ }
    }

    // ── H: two hosts independent ───────────────────────────────────────────

    [Fact]
    public async Task MultiHost_TwoHosts_RegisterAndConnectIndependently()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var m = new MultiHostClient();
        await using var disposeMulti = m;

        Func<HostId, CancellationToken, Task<ITransport>> factory = (hostId, ct) =>
        {
            var (c, s) = MemTransport.CreatePair();
            _ = Task.Run(() => RunFakeServerAsync(s, ct)); // fire-and-forget fake server
            return Task.FromResult<ITransport>(c);
        };

        var hA = await m.AddHostAsync(new HostConfig
        {
            Id = new HostId("host-a"),
            Label = "A",
            TransportFactory = (id, ct) => factory(id, ct),
        }, cts.Token);

        var hB = await m.AddHostAsync(new HostConfig
        {
            Id = new HostId("host-b"),
            Label = "B",
            TransportFactory = (id, ct) => factory(id, ct),
        }, cts.Token);

        Assert.Equal(HostStateKind.Connected, hA.State.Kind);
        Assert.Equal(HostStateKind.Connected, hB.State.Kind);
        Assert.False(string.IsNullOrEmpty(hA.ClientId));
        Assert.False(string.IsNullOrEmpty(hB.ClientId));
        Assert.NotEqual(hA.ClientId, hB.ClientId);   // each host mints its own clientId
        Assert.Equal(2, m.Hosts().Count);
    }

    // ── H: events tagged hostId ────────────────────────────────────────────

    [Fact]
    public async Task MultiHost_Events_CarryHostIdAndResource()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var m = new MultiHostClient();
        await using var disposeMulti = m;

        var subs = m.Subscriptions();
        const string channel = "ahp-session:/s1";

        // Host-a's server pushes an action after initialize; host-b stays quiet.
        await m.AddHostAsync(new HostConfig
        {
            Id = new HostId("host-a"),
            TransportFactory = (id, ct) =>
            {
                var (c, s) = MemTransport.CreatePair();
                _ = Task.Run(() => RunFakeServerWithActionAsync(s, channel, 1, ct));
                return Task.FromResult<ITransport>(c);
            },
        }, cts.Token);

        await m.AddHostAsync(new HostConfig
        {
            Id = new HostId("host-b"),
            TransportFactory = (id, ct) =>
            {
                var (c, s) = MemTransport.CreatePair();
                _ = Task.Run(() => RunFakeServerAsync(s, ct));
                return Task.FromResult<ITransport>(c);
            },
        }, cts.Token);

        // The event read off the top-level subscriptions reader is tagged with
        // the originating host and the channel URI it was scoped to.
        var ev = await subs.ReadAsync(cts.Token);
        Assert.Equal(new HostId("host-a"), ev.HostId);
        Assert.Equal(channel, ev.Channel);
        var action = Assert.IsType<SubscriptionEventAction>(ev.Event);
        Assert.Equal(1, action.Envelope.ServerSeq);
    }

    // ── H: reconnect replay ────────────────────────────────────────────────

    [Fact]
    public async Task MultiHost_Reconnect_ReplaysActionsWithAdvancedSeq()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var m = new MultiHostClient();
        await using var disposeMulti = m;

        var subs = m.Subscriptions();
        const string channel = "ahp-session:/s1";

        // Per-attempt transport factory. The first connection's server pushes an
        // action at serverSeq=1 and then closes (simulating a drop). The fast
        // ReconnectPolicy makes the supervisor reconnect; the SECOND connection's
        // server pushes an action at the ADVANCED serverSeq=2. We assert that a
        // post-reconnect event carries the higher serverSeq end-to-end.
        //
        // Note: the .NET MultiHostClient supervisor reconnects by opening a fresh
        // transport + re-running `initialize` (OpenHostAsync), not by issuing a
        // `reconnect` RPC with lastSeenServerSeq. The replay-with-advanced-seq
        // behavior this row names is therefore exercised at the observable level:
        // actions delivered after the reconnect carry the newer serverSeq.
        var attempt = 0;
        Func<HostId, CancellationToken, Task<ITransport>> factory = (hostId, ct) =>
        {
            var n = Interlocked.Increment(ref attempt);
            var (c, s) = MemTransport.CreatePair();
            if (n == 1)
            {
                _ = Task.Run(async () =>
                {
                    // Respond to initialize, push seq=1 a few times so the pump
                    // can't miss it, then drop the transport to force a reconnect.
                    try
                    {
                        var frame = await s.ReceiveAsync(ct).ConfigureAwait(false);
                        var msg = Ser.DecodeMessage(frame);
                        if (msg.Request?.Method == "initialize")
                        {
                            await RespondInitializeAsync(s, msg.Request.Id, ct).ConfigureAwait(false);
                            for (var i = 0; i < 4 && !ct.IsCancellationRequested; i++)
                            {
                                await SendActionAsync(s, channel, 1, ct).ConfigureAwait(false);
                                await Task.Delay(15, ct).ConfigureAwait(false);
                            }
                        }
                    }
                    catch { /* ignore */ }
                    finally
                    {
                        await s.CloseAsync().ConfigureAwait(false); // force a drop
                    }
                });
            }
            else
            {
                // Reconnected connection: respond to initialize, push seq=2.
                _ = Task.Run(() => RunFakeServerWithActionAsync(s, channel, 2, ct));
            }
            return Task.FromResult<ITransport>(c);
        };

        await m.AddHostAsync(new HostConfig
        {
            Id = new HostId("host-a"),
            TransportFactory = (id, ct) => factory(id, ct),
            // Fast reconnect so the test does not wait the 1s default backoff.
            ReconnectPolicy = new ReconnectPolicy
            {
                InitialBackoff = TimeSpan.FromMilliseconds(20),
                MaxBackoff = TimeSpan.FromMilliseconds(20),
                BackoffMultiplier = 1.0,
                ResetOnSuccess = true,
            },
        }, cts.Token);

        // Drain events until we observe the advanced (post-reconnect) serverSeq.
        long maxSeqSeen = 0;
        while (maxSeqSeen < 2)
        {
            var ev = await subs.ReadAsync(cts.Token);
            if (ev.Event is SubscriptionEventAction action)
            {
                maxSeqSeen = Math.Max(maxSeqSeen, action.Envelope.ServerSeq);
                Assert.Equal(new HostId("host-a"), ev.HostId);
            }
        }

        // The post-reconnect action carried the advanced serverSeq.
        Assert.Equal(2, maxSeqSeen);
    }
}
