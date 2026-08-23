// Wire-fidelity round-trips for the protocol types added upstream in the
// AgentCapabilities (microsoft/agent-host-protocol#292), subscription delivery
// preference (#293), listSessions cursor pagination (#295),
// ToolResultTerminalCompleteContent (#314), and PluginCustomization.version
// (#317) changes. Each test encodes a generated type through the REAL
// serializer, asserts the exact wire shape (key presence is significant — an
// omitted optional field must not serialize as `null`/absent-vs-present drift),
// and decodes it back.
#nullable enable

using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

public sealed class FidelityDriftTypesTests
{
    private static readonly SystemTextJsonAhpSerializer Ser = SystemTextJsonAhpSerializer.Default;

    // ── AgentCapabilities (#292) ──────────────────────────────────────────

    [Fact]
    public void AgentCapabilities_MultipleChatsWithFork_RoundTrips()
    {
        var caps = new AgentCapabilities
        {
            MultipleChats = new MultipleChatsCapability { Fork = true },
        };

        JsonElement wire = Ser.SerializeToElement(caps);
        Assert.True(wire.GetProperty("multipleChats").GetProperty("fork").GetBoolean());

        var back = Ser.Deserialize<AgentCapabilities>(Ser.Serialize(caps));
        Assert.NotNull(back.MultipleChats);
        Assert.True(back.MultipleChats!.Fork);
    }

    [Fact]
    public void AgentCapabilities_EmptyMultipleChats_SignalsSupportWithoutFork()
    {
        // Presence of an empty `{}` object advertises multi-chat WITHOUT forking:
        // `multipleChats` must serialize as an object, and `fork` must be absent
        // (not `false`), matching MCP-capability presence semantics.
        var caps = new AgentCapabilities { MultipleChats = new MultipleChatsCapability() };

        JsonElement wire = Ser.SerializeToElement(caps);
        JsonElement mc = wire.GetProperty("multipleChats");
        Assert.Equal(JsonValueKind.Object, mc.ValueKind);
        Assert.False(mc.TryGetProperty("fork", out _), "absent fork must not serialize a `fork` key");

        var back = Ser.Deserialize<AgentCapabilities>(Ser.Serialize(caps));
        Assert.NotNull(back.MultipleChats);
        Assert.Null(back.MultipleChats!.Fork);
    }

    [Fact]
    public void AgentInfo_WithoutCapabilities_OmitsKey()
    {
        var info = new AgentInfo
        {
            Provider = "copilot",
            DisplayName = "Copilot",
            Description = "desc",
            Models = new List<SessionModelInfo>(),
        };

        JsonElement wire = Ser.SerializeToElement(info);
        Assert.False(
            wire.TryGetProperty("capabilities", out _),
            "an agent that advertises no capabilities must not serialize a `capabilities` key");
    }

    [Fact]
    public void AgentInfo_WithCapabilities_RoundTrips()
    {
        var info = new AgentInfo
        {
            Provider = "copilot",
            DisplayName = "Copilot",
            Description = "desc",
            Models = new List<SessionModelInfo>(),
            Capabilities = new AgentCapabilities
            {
                MultipleChats = new MultipleChatsCapability { Fork = true },
            },
        };

        var back = Ser.Deserialize<AgentInfo>(Ser.Serialize(info));
        Assert.NotNull(back.Capabilities);
        Assert.NotNull(back.Capabilities!.MultipleChats);
        Assert.True(back.Capabilities.MultipleChats!.Fork);
    }

    // ── SubscriptionDeliveryOptions (#293) ────────────────────────────────

    [Fact]
    public void SubscribeParams_DeliveryMaxLatencyZero_IsPreserved()
    {
        // `maxLatencyMs: 0` is meaningful — it requests immediate delivery with
        // no coalescing — so 0 must survive the round-trip, not be dropped.
        var p = new SubscribeParams
        {
            Channel = "ahp-session:/s1",
            Delivery = new SubscriptionDeliveryOptions { MaxLatencyMs = 0 },
        };

        JsonElement wire = Ser.SerializeToElement(p);
        Assert.Equal(0, wire.GetProperty("delivery").GetProperty("maxLatencyMs").GetInt64());

        var back = Ser.Deserialize<SubscribeParams>(Ser.Serialize(p));
        Assert.Equal(0, back.Delivery!.MaxLatencyMs);
    }

    [Fact]
    public void SubscribeParams_NoDelivery_OmitsKey()
    {
        var p = new SubscribeParams { Channel = "ahp-session:/s1" };
        JsonElement wire = Ser.SerializeToElement(p);
        Assert.False(wire.TryGetProperty("delivery", out _));
    }

    // ── listSessions cursor pagination (#295) ─────────────────────────────

    [Fact]
    public void ListSessionsParams_Pagination_RoundTripsAndDropsFilter()
    {
        var p = new ListSessionsParams
        {
            Channel = "ahp-root://",
            Limit = 50,
            Cursor = "eyJvIjo1MH0=",
        };

        JsonElement wire = Ser.SerializeToElement(p);
        Assert.Equal(50, wire.GetProperty("limit").GetInt64());
        Assert.Equal("eyJvIjo1MH0=", wire.GetProperty("cursor").GetString());
        // The removed `filter` field must not resurface.
        Assert.False(wire.TryGetProperty("filter", out _));

        var back = Ser.Deserialize<ListSessionsParams>(Ser.Serialize(p));
        Assert.Equal(50, back.Limit);
        Assert.Equal("eyJvIjo1MH0=", back.Cursor);
    }

    [Fact]
    public void ListSessionsParams_FirstPage_OmitsPaginationKeys()
    {
        var p = new ListSessionsParams { Channel = "ahp-root://" };
        JsonElement wire = Ser.SerializeToElement(p);
        Assert.False(wire.TryGetProperty("limit", out _));
        Assert.False(wire.TryGetProperty("cursor", out _));
    }

    [Fact]
    public void ListSessionsResult_NextCursor_RoundTrips()
    {
        var withCursor = new ListSessionsResult
        {
            Items = new List<SessionSummary>(),
            NextCursor = "eyJvIjoxMDB9",
        };
        JsonElement wire = Ser.SerializeToElement(withCursor);
        Assert.Equal("eyJvIjoxMDB9", wire.GetProperty("nextCursor").GetString());
        Assert.Equal("eyJvIjoxMDB9", Ser.Deserialize<ListSessionsResult>(Ser.Serialize(withCursor)).NextCursor);

        // Last page: no cursor → the key is absent (end-of-collection signal).
        var lastPage = new ListSessionsResult { Items = new List<SessionSummary>() };
        Assert.False(Ser.SerializeToElement(lastPage).TryGetProperty("nextCursor", out _));
    }

    // ── ToolResultTerminalContent.result / isPty (#314, then #352) ────────
    //
    // Upstream #352 removed the standalone `terminalComplete` variant and folded
    // the completion into `ToolResultTerminalContent.result` (a
    // TerminalCommandResult), adding `isPty`. `cwd` did not survive the fold.

    [Fact]
    public void ToolResultTerminal_CompletedResult_RoundTripsThroughUnion()
    {
        // A completed shell command's outcome, now nested under `result`. exitCode 0
        // is meaningful (success, not "unset") and must survive the round-trip, and
        // the block still decodes back to the Terminal variant.
        var content = new ToolResultContent(new ToolResultTerminalContent
        {
            Type = ToolResultContentType.Terminal,
            Resource = "ahp-terminal:/t1",
            Title = "npm test",
            IsPty = false,
            Result = new TerminalCommandResult
            {
                ExitCode = 0,
                Preview = "done",
                Truncated = false,
            },
        });

        JsonElement wire = Ser.SerializeToElement(content);
        Assert.Equal("terminal", wire.GetProperty("type").GetString());
        Assert.Equal("ahp-terminal:/t1", wire.GetProperty("resource").GetString());
        Assert.False(wire.GetProperty("isPty").GetBoolean());

        JsonElement result = wire.GetProperty("result");
        Assert.Equal(0, result.GetProperty("exitCode").GetInt64());
        Assert.Equal("done", result.GetProperty("preview").GetString());
        Assert.False(result.GetProperty("truncated").GetBoolean());

        var back = Ser.Deserialize<ToolResultContent>(Ser.Serialize(content));
        var terminal = Assert.IsType<ToolResultTerminalContent>(back.Value);
        Assert.False(terminal.IsPty);
        Assert.NotNull(terminal.Result);
        Assert.Equal(0, terminal.Result!.ExitCode);
        Assert.Equal("done", terminal.Result.Preview);
        Assert.False(terminal.Result.Truncated);
    }

    [Fact]
    public void ToolResultTerminal_OmitsOptionalKeysWhenAbsent()
    {
        // A live terminal that has not exited yet: `result` and `isPty` must be
        // absent, not serialized as null.
        var content = new ToolResultContent(new ToolResultTerminalContent
        {
            Type = ToolResultContentType.Terminal,
            Resource = "ahp-terminal:/t1",
            Title = "npm test",
        });

        JsonElement wire = Ser.SerializeToElement(content);
        Assert.Equal("terminal", wire.GetProperty("type").GetString());
        foreach (string key in new[] { "isPty", "result" })
        {
            Assert.False(wire.TryGetProperty(key, out _), $"absent optional `{key}` must not serialize");
        }
    }

    [Fact]
    public void TerminalCommandResult_OmitsOptionalKeysWhenAbsent()
    {
        // A result block carrying nothing — every field on TerminalCommandResult is
        // optional, so an empty outcome must serialize as `{}`, never null-filled.
        var content = new ToolResultContent(new ToolResultTerminalContent
        {
            Type = ToolResultContentType.Terminal,
            Resource = "ahp-terminal:/t1",
            Title = "npm test",
            Result = new TerminalCommandResult(),
        });

        JsonElement result = Ser.SerializeToElement(content).GetProperty("result");
        foreach (string key in new[] { "exitCode", "preview", "truncated" })
        {
            Assert.False(result.TryGetProperty(key, out _), $"absent optional `{key}` must not serialize");
        }
    }

    // ── PluginCustomization.version (#317) ────────────────────────────────

    [Fact]
    public void PluginCustomization_Version_RoundTripsAndOmitsWhenAbsent()
    {
        var withVersion = new PluginCustomization
        {
            Type = CustomizationType.Plugin,
            Id = "plug-1",
            Uri = "https://open-plugins.com/p",
            Name = "My Plugin",
            Version = "1.2.0",
        };
        JsonElement wire = Ser.SerializeToElement(withVersion);
        Assert.Equal("1.2.0", wire.GetProperty("version").GetString());
        Assert.Equal("1.2.0", Ser.Deserialize<PluginCustomization>(Ser.Serialize(withVersion)).Version);

        // No manifest version → the key is absent (provenance is optional).
        var noVersion = new PluginCustomization
        {
            Type = CustomizationType.Plugin,
            Id = "plug-2",
            Uri = "https://open-plugins.com/q",
            Name = "Versionless",
        };
        Assert.False(Ser.SerializeToElement(noVersion).TryGetProperty("version", out _));
    }
}
