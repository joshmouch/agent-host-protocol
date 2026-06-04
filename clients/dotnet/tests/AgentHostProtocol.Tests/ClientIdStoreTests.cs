// Port of the F-group client-id-store parity tests.
// Exercises the real InMemoryClientIdStore over real HostId keys — no mocking
// of the store, the IClientIdStore interface, or HostId.
#nullable enable

using System;
using System.Text.Json;          // mirror/client tests that build wire payloads
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AgentHostProtocol;
using Microsoft.AgentHostProtocol.Hosts;
using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

public sealed class ClientIdStoreTests
{
    // ── F: in-memory round-trip ───────────────────────────────────────────

    [Fact]
    public async Task InMemoryClientIdStore_RoundTrips()
    {
        var store = new InMemoryClientIdStore();

        await store.StoreAsync(new HostId("h1"), "cid-1");

        Assert.Equal("cid-1", await store.LoadAsync(new HostId("h1")));
        // A host that was never stored has no client ID.
        Assert.Null(await store.LoadAsync(new HostId("never-stored")));
    }

    // ── F: in-memory overwrite ────────────────────────────────────────────

    [Fact]
    public async Task InMemoryClientIdStore_Overwrites()
    {
        var store = new InMemoryClientIdStore();
        var host = new HostId("h1");

        await store.StoreAsync(host, "cid-1");
        await store.StoreAsync(host, "cid-2");

        // The second store for the same host wins; reads see the latest value.
        Assert.Equal("cid-2", await store.LoadAsync(host));
    }
}
