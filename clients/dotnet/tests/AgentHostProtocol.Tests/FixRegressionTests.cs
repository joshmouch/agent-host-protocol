// Regression tests pinning the behaviors fixed after the adversarial review, so a
// future refactor that reintroduces a bug FAILS here rather than silently shipping.
// Each test maps to a confirmed finding from the review.
#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AgentHostProtocol.Hosts;
using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

public sealed class FixRegressionTests
{
    // ── Subscription lifecycle: Close()/Dispose() must detach, regardless of teardown
    //    path (the subscriptions.active-gauge desync + _subscriptions registry leak fix). ──

    [Fact]
    public void Subscription_Close_RunsDetachHookExactlyOnce()
    {
        int detached = 0;
        var sub = new Subscription("ahp-session:/s1", 8);
        sub.OnClose(() => Interlocked.Increment(ref detached));
        sub.Close();
        sub.Close();   // idempotent
        sub.Dispose();
        Assert.Equal(1, detached);
    }

    [Fact]
    public async Task DirectSubscriptionClose_DetachesFromClientRegistry()
    {
        var (clientSide, _) = MemTransport.CreatePair();
        await using var client = AhpClient.Connect(clientSide);

        var sub = client.AttachSubscription("ahp-session:/s1");
        Assert.Equal(1, client.SubscriptionCount);

        sub.Close();   // a direct Close() (not UnsubscribeAsync) must still detach
        Assert.Equal(0, client.SubscriptionCount);
    }

    // ── Back-pressure: each drop-oldest eviction is counted EXACTLY once via the BCL
    //    ItemDropped callback (replacing the racy Count-then-write probe). ──

    [Fact]
    public void BoundedDropOldestChannel_ReportsEachEvictionExactlyOnce()
    {
        int dropped = 0;
        var channel = new BoundedDropOldestChannel<int>(2, _ => Interlocked.Increment(ref dropped));
        for (int i = 0; i < 5; i++) channel.TrySend(i);   // capacity 2, 5 sends, no reader -> 3 evictions
        Assert.Equal(3, dropped);
    }

    // ── ClientConfig.Default is a fresh instance per access (no cross-consumer bleed). ──

    [Fact]
    public void ClientConfigDefault_ReturnsDistinctInstances()
    {
        var a = ClientConfig.Default;
        var b = ClientConfig.Default;
        Assert.NotSame(a, b);
        a.DefaultRequestTimeout = TimeSpan.FromSeconds(99);
        Assert.NotEqual(a.DefaultRequestTimeout, b.DefaultRequestTimeout);
    }

    // ── A request timeout is recorded as ahp.outcome="timeout", distinct from a caller
    //    cancellation ("cancelled") or a success ("ok"). ──

    [Fact]
    public async Task RequestTimeout_RecordsOutcomeTimeout()
    {
        var sawTimeout = false;
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == AhpTelemetry.Name) l.EnableMeasurementEvents(inst);
            },
        };
        meterListener.SetMeasurementEventCallback<double>((inst, _, tags, _) =>
        {
            if (inst.Name != "ahp.client.request.duration") return;
            foreach (var tag in tags)
                if (tag.Key == "ahp.outcome" && (tag.Value as string) == "timeout") sawTimeout = true;
        });
        meterListener.Start();

        var (clientSide, _) = MemTransport.CreatePair();   // server never replies
        var cfg = new ClientConfig { DefaultRequestTimeout = TimeSpan.FromMilliseconds(50) };
        await using var client = AhpClient.Connect(clientSide, cfg);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.RequestAsync<object?, object?>("noop", null, TestContext.Current.CancellationToken));

        Assert.True(sawTimeout, "request.duration should carry ahp.outcome=timeout when the default timeout fires");
    }

    // ── Pre-existing fix: HostEntry.ApplySummaryChange is copy-on-write, so a snapshot
    //    already handed to a consumer is never mutated underneath it (torn-read fix). ──

    [Fact]
    public void ApplySummaryChange_DoesNotMutate_AlreadyTakenSnapshot()
    {
        var entry = new HostEntry(new HostId("h"), new HostConfig { Id = new HostId("h") }, "client-1");
        entry.PutSessionSummary(new SessionSummary
        {
            Resource = "ahp-session:/s1",
            Provider = "p",
            Title = "Original",
            CreatedAt = "2024-01-01T00:00:00.001Z",
            ModifiedAt = "2024-01-01T00:00:00.001Z",
        });

        var held = entry.Snapshot().SessionSummaries.Single(s => s.Resource == "ahp-session:/s1");
        Assert.Equal("Original", held.Title);

        entry.ApplySummaryChange("ahp-session:/s1", new PartialSessionSummary { Title = "Changed" });

        Assert.Equal("Original", held.Title);   // copy-on-write: the prior snapshot is immutable
        Assert.Equal("Changed",
            entry.Snapshot().SessionSummaries.Single(s => s.Resource == "ahp-session:/s1").Title);
    }

    // ── Upstream #254: SessionSummary._meta is a patchable field on
    //    root/sessionSummaryChanged — the merge overrides it when the patch carries
    //    it and otherwise carries the existing value over (mirrors the TS reducer's
    //    `if (changes._meta !== undefined) merged._meta = changes._meta`). ──

    [Fact]
    public void ApplySummaryChange_Meta_OverridesWhenPresent_CarriesOverWhenAbsent()
    {
        var entry = new HostEntry(new HostId("h"), new HostConfig { Id = new HostId("h") }, "client-1");
        var originalMeta = new Dictionary<string, JsonElement>
        {
            ["pinned"] = JsonDocument.Parse("true").RootElement,
        };
        entry.PutSessionSummary(new SessionSummary
        {
            Resource = "ahp-session:/s1",
            Provider = "p",
            Title = "Original",
            CreatedAt = "2024-01-01T00:00:00.001Z",
            ModifiedAt = "2024-01-01T00:00:00.001Z",
            Meta = originalMeta,
        });

        // Patch that omits _meta keeps the existing metadata.
        entry.ApplySummaryChange("ahp-session:/s1", new PartialSessionSummary { Title = "Changed" });
        var afterTitleOnly = entry.Snapshot().SessionSummaries.Single(s => s.Resource == "ahp-session:/s1");
        Assert.Equal("Changed", afterTitleOnly.Title);
        Assert.NotNull(afterTitleOnly.Meta);
        Assert.True(afterTitleOnly.Meta!["pinned"].GetBoolean());

        // Patch that carries _meta overrides it.
        var newMeta = new Dictionary<string, JsonElement>
        {
            ["pinned"] = JsonDocument.Parse("false").RootElement,
        };
        entry.ApplySummaryChange("ahp-session:/s1", new PartialSessionSummary { Meta = newMeta });
        var afterMetaPatch = entry.Snapshot().SessionSummaries.Single(s => s.Resource == "ahp-session:/s1");
        Assert.NotNull(afterMetaPatch.Meta);
        Assert.False(afterMetaPatch.Meta!["pinned"].GetBoolean());
    }

    // ── Upstream drift port (model config widened to JSON primitives; SessionModelInfo
    //    token-limit fields). ModelSelection.Config + ConfigPropertySchema.Enum carry
    //    arbitrary JSON primitives (not just strings), so a numeric/boolean picker value
    //    must round-trip as-is. Falsifies a revert to Dictionary<string,string> /
    //    List<string> (which can't hold a number) or a drop of the new token fields. ──

    [Fact]
    public void ModelSelection_Config_CarriesNonStringJsonPrimitives()
    {
        var selection = new ModelSelection
        {
            Id = "gpt-5",
            Config = new Dictionary<string, JsonElement>
            {
                ["preset"] = JsonDocument.Parse("\"fast\"").RootElement,
                ["temperature"] = JsonDocument.Parse("0.7").RootElement,
                ["stream"] = JsonDocument.Parse("true").RootElement,
            },
        };

        string json = SystemTextJsonAhpSerializer.Default.Serialize(selection);
        var back = SystemTextJsonAhpSerializer.Default.Deserialize<ModelSelection>(json);

        Assert.NotNull(back!.Config);
        Assert.Equal("fast", back.Config!["preset"].GetString());
        Assert.Equal(0.7, back.Config["temperature"].GetDouble());
        Assert.True(back.Config["stream"].GetBoolean());
    }

    [Fact]
    public void SessionModelInfo_RoundTripsOutputAndPromptTokenLimits()
    {
        var info = new SessionModelInfo
        {
            Id = "gpt-5",
            Provider = "openai",
            Name = "GPT-5",
            MaxContextWindow = 200_000,
            MaxOutputTokens = 32_000,
            MaxPromptTokens = 168_000,
        };

        string json = SystemTextJsonAhpSerializer.Default.Serialize(info);
        var back = SystemTextJsonAhpSerializer.Default.Deserialize<SessionModelInfo>(json);

        Assert.Equal(32_000, back!.MaxOutputTokens);
        Assert.Equal(168_000, back.MaxPromptTokens);
        Assert.Equal(200_000, back.MaxContextWindow);
    }

    // ── Pre-existing fix: MultiHostStateMirror carries the Chat dimension (Go parity),
    //    and both drop paths (DropResource, DropHost) cover it. ──

    [Fact]
    public void MultiHostStateMirror_StoresAndDropsChatSnapshots()
    {
        var mirror = new MultiHostStateMirror();
        var chat = new ChatState { Resource = "ahp-session:/s1#chat", Title = "c1", ModifiedAt = "0", Turns = new() };

        mirror.PutChat("host-a", "ahp-session:/s1#chat", chat);
        Assert.True(mirror.Chat("host-a", "ahp-session:/s1#chat").Found);
        Assert.Same(chat, mirror.Chat("host-a", "ahp-session:/s1#chat").Value);

        mirror.DropResource("host-a", "ahp-session:/s1#chat");
        Assert.False(mirror.Chat("host-a", "ahp-session:/s1#chat").Found);

        mirror.PutChat("host-b", "ahp-session:/s2#chat", chat);
        mirror.DropHost("host-b");
        Assert.False(mirror.Chat("host-b", "ahp-session:/s2#chat").Found);
    }

    // ── Pre-existing fix: CreateEventStream / CreateStateChangeStream detach on dispose,
    //    so an abandoned stream leaves the client's fan-out list (no per-stream leak). ──

    [Fact]
    public async Task EventStream_Dispose_DetachesFromClientFanout()
    {
        var (clientSide, _) = MemTransport.CreatePair();
        await using var client = AhpClient.Connect(clientSide);

        var stream = client.CreateEventStream();
        Assert.Equal(1, client.EventListenerCount);

        stream.Dispose();
        Assert.Equal(0, client.EventListenerCount);   // detached, not leaked
    }

    [Fact]
    public async Task StateChangeStream_Dispose_DetachesFromClientFanout()
    {
        var (clientSide, _) = MemTransport.CreatePair();
        await using var client = AhpClient.Connect(clientSide);

        var stream = client.CreateStateChangeStream();
        Assert.Equal(1, client.StateListenerCount);

        stream.Dispose();
        Assert.Equal(0, client.StateListenerCount);   // detached, not leaked
    }

    // ── Multiple active clients per session (microsoft/agent-host-protocol#261):
    //    activeClient? -> activeClients[]. Sequential session/activeClientSet upserts
    //    keyed by clientId build a multi-client list; session/activeClientRemoved
    //    removes by clientId (no-op on miss). Falsifies a revert to a single-value
    //    field or a broken upsert that appends duplicates instead of replacing. ──
    [Fact]
    public void SessionActiveClients_SetUpsertsByClientId_RemoveDropsByClientId()
    {
        var state = new SessionState
        {
            Provider = "copilot",
            Title = "s",
            Lifecycle = SessionLifecycle.Ready,
            ActiveClients = new(),
            Chats = new(),
        };

        SessionActiveClient Client(string id, string name) =>
            new() { ClientId = id, DisplayName = name, Tools = new() };

        // SET a, then SET b — both coexist (the headline #261 capability).
        Reducers.ApplyToSession(state, new StateAction(new SessionActiveClientSetAction
        {
            Type = ActionType.SessionActiveClientSet,
            ActiveClient = Client("vscode-1", "VS Code"),
        }));
        Reducers.ApplyToSession(state, new StateAction(new SessionActiveClientSetAction
        {
            Type = ActionType.SessionActiveClientSet,
            ActiveClient = Client("cli-1", "CLI"),
        }));
        Assert.Equal(new[] { "vscode-1", "cli-1" }, state.ActiveClients.Select(c => c.ClientId));

        // SET vscode-1 again — upsert replaces in place (length stays 2, not 3).
        Reducers.ApplyToSession(state, new StateAction(new SessionActiveClientSetAction
        {
            Type = ActionType.SessionActiveClientSet,
            ActiveClient = Client("vscode-1", "VS Code Insiders"),
        }));
        Assert.Equal(2, state.ActiveClients.Count);
        Assert.Equal("VS Code Insiders", state.ActiveClients.Single(c => c.ClientId == "vscode-1").DisplayName);

        // REMOVE vscode-1 — leaves cli-1.
        var removed = Reducers.ApplyToSession(state, new StateAction(new SessionActiveClientRemovedAction
        {
            Type = ActionType.SessionActiveClientRemoved,
            ClientId = "vscode-1",
        }));
        Assert.Equal(ReduceOutcome.Applied, removed);
        Assert.Equal(new[] { "cli-1" }, state.ActiveClients.Select(c => c.ClientId));

        // REMOVE unknown — no-op, list unchanged.
        var noop = Reducers.ApplyToSession(state, new StateAction(new SessionActiveClientRemovedAction
        {
            Type = ActionType.SessionActiveClientRemoved,
            ClientId = "ghost",
        }));
        Assert.Equal(ReduceOutcome.NoOp, noop);
        Assert.Single(state.ActiveClients);
    }
}
