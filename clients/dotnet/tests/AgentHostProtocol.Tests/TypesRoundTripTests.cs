// Port of clients/go/ahptypes/ahptypes_test.go.
// Tests wire-type round-trips without mocking the JSON engine.
#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.AgentHostProtocol;
using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

public sealed class TypesRoundTripTests
{
    private static readonly SystemTextJsonAhpSerializer Ser = SystemTextJsonAhpSerializer.Default;

    // ── ProtocolVersion ───────────────────────────────────────────────────

    [Fact]
    public void ProtocolVersion_CurrentIsNonEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(ProtocolVersion.Current));
    }

    [Fact]
    public void ProtocolVersion_SupportedIsNonEmpty()
    {
        var supported = ProtocolVersion.Supported;
        Assert.NotEmpty(supported);
    }

    [Fact]
    public void ProtocolVersion_FirstSupportedEqualsCurrentVersion()
    {
        Assert.Equal(ProtocolVersion.Current, ProtocolVersion.Supported[0]);
    }

    // ── ActionEnvelope round-trip ─────────────────────────────────────────

    [Fact]
    public void ActionEnvelope_RoundTrip_SessionTitleChanged()
    {
        const string wire = """
            {
                "channel": "ahp-session:/s1",
                "action": { "type": "session/titleChanged", "title": "Hello" },
                "serverSeq": 7,
                "origin": null
            }
            """;

        var env = Ser.Deserialize<ActionEnvelope>(wire);

        Assert.Equal("ahp-session:/s1", env.Channel);
        Assert.Equal(7, env.ServerSeq);

        var action = Assert.IsType<SessionTitleChangedAction>(env.Action.Value);
        Assert.Equal("Hello", action.Title);

        // Re-encode and re-decode; key fields must survive.
        var out1 = Ser.Serialize(env);
        var back = Ser.Deserialize<ActionEnvelope>(out1);

        Assert.Equal(env.Channel, back.Channel);
        Assert.Equal(env.ServerSeq, back.ServerSeq);
        var backAction = Assert.IsType<SessionTitleChangedAction>(back.Action.Value);
        Assert.Equal("Hello", backAction.Title);
    }

    // ── Unknown discriminator preserved verbatim ──────────────────────────

    [Fact]
    public void StateAction_UnknownVariant_PreservedVerbatim()
    {
        const string wire = """{"type":"future/newAction","foo":42}""";

        var action = Ser.Deserialize<StateAction>(wire);

        // Unknown variants are preserved as a raw JsonElement.
        var rawEl = Assert.IsType<JsonElement>(action.Value);

        var out1 = Ser.Serialize(action);
        Assert.Equal(wire, out1);
        // The raw JSON element preserves the original content.
        Assert.Equal(wire, rawEl.GetRawText());
    }

    // ── SessionStatus bitset ──────────────────────────────────────────────

    [Fact]
    public void SessionStatus_HasFlag_Works()
    {
        var s = SessionStatus.InProgress | SessionStatus.IsArchived;

        Assert.True(s.HasFlag(SessionStatus.InProgress));
        Assert.True(s.HasFlag(SessionStatus.IsArchived));
        Assert.False(s.HasFlag(SessionStatus.Idle));
    }

    [Fact]
    public void SessionStatus_UnknownBitsSurviveRoundTrip()
    {
        const SessionStatus unknownBit = (SessionStatus)(1u << 31);
        var s = SessionStatus.InProgress | SessionStatus.IsArchived | unknownBit;

        var json = Ser.Serialize(s);
        var back = Ser.Deserialize<SessionStatus>(json);

        Assert.Equal(s, back);
    }

    // ── StringOrMarkdown plain and object forms ───────────────────────────

    [Theory]
    [InlineData("plain", "\"hello\"")]
    [InlineData("object", """{"markdown":"# title"}""")]
    public void StringOrMarkdown_RoundTrip(string _, string wire)
    {
        var v = Ser.Deserialize<StringOrMarkdown>(wire);
        var out1 = Ser.Serialize(v);
        Assert.Equal(wire, out1);
    }

    // ── JsonRpcMessage all four shapes ────────────────────────────────────

    [Theory]
    [InlineData("request",      """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",      "request")]
    [InlineData("notification", """{"jsonrpc":"2.0","method":"action","params":{}}""",                 "notification")]
    [InlineData("success",      """{"jsonrpc":"2.0","id":1,"result":{}}""",                            "success")]
    [InlineData("error",        """{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"x"}}""",  "error")]
    public void JsonRpcMessage_Discriminator(string _, string wire, string wantKind)
    {
        var m = Ser.Deserialize<JsonRpcMessage>(wire);

        switch (wantKind)
        {
            case "request":      Assert.NotNull(m.Request);         break;
            case "notification": Assert.NotNull(m.Notification);    break;
            case "success":      Assert.NotNull(m.SuccessResponse);  break;
            case "error":        Assert.NotNull(m.ErrorResponse);    break;
            default: Assert.Fail($"unexpected kind {wantKind}");     break;
        }
    }
}
