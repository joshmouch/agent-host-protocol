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

    // ── F: key unreserved pass-through ────────────────────────────────────
    // HostedResourceKey.PercentEscape leaves RFC-3986 unreserved characters
    // (ALPHA / DIGIT / - . _ ~) untouched.
    [Fact]
    public void HostedResourceKey_UnreservedPassThrough()
    {
        var key = new HostedResourceKey(new HostId("h1"), "abcXYZ-._~0189");
        // The URI component survives verbatim in the stable key (no % anywhere
        // in the URI portion).
        Assert.Equal("abcXYZ-._~0189", HostedResourceKey.PercentEscape("abcXYZ-._~0189"));
        Assert.Contains("abcXYZ-._~0189", key.ToStableKey());
        Assert.DoesNotContain('%', HostedResourceKey.PercentEscape("abcXYZ-._~0189"));
    }

    // ── F: key reserved %-escaped ─────────────────────────────────────────
    // Reserved/sub-delim/gen-delim characters get percent-escaped (uppercase
    // hex), so a URI like "ahp-session:/s1?x=1" can't collide with the key
    // delimiter.
    [Fact]
    public void HostedResourceKey_ReservedPercentEscaped()
    {
        // ':' -> %3A, '/' -> %2F, '?' -> %3F, '=' -> %3D, ' ' -> %20
        Assert.Equal("%3A", HostedResourceKey.PercentEscape(":"));
        Assert.Equal("%2F", HostedResourceKey.PercentEscape("/"));
        Assert.Equal("a%3Fb%3Dc", HostedResourceKey.PercentEscape("a?b=c"));
        Assert.Equal("x%20y", HostedResourceKey.PercentEscape("x y"));

        // Two distinct URIs that differ only in a reserved char produce distinct
        // stable keys (no clobber).
        var k1 = new HostedResourceKey(new HostId("h1"), "ahp-session:/s1");
        var k2 = new HostedResourceKey(new HostId("h1"), "ahp-session:/s2");
        Assert.NotEqual(k1.ToStableKey(), k2.ToStableKey());
    }
}
