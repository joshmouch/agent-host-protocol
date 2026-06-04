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

    // ── Customization unknown discriminator does not throw ─────────────────

    [Fact]
    public void Customization_UnknownType_DoesNotThrow()
    {
        // The Customization union (discriminator "type") opts into allowUnknown,
        // so a `type` the union does not recognize must NOT throw and must
        // round-trip verbatim as a raw JsonElement — the same contract the
        // StateAction_UnknownVariant_PreservedVerbatim test asserts for actions.
        const string wire = """{"type":"future/unknownCustomization","path":"/x","extra":7}""";

        var customization = Ser.Deserialize<Customization>(wire);

        var rawEl = Assert.IsType<JsonElement>(customization.Value);
        Assert.Equal("future/unknownCustomization", rawEl.GetProperty("type").GetString());

        var out1 = Ser.Serialize(customization);
        Assert.Equal(wire, out1);
        Assert.Equal(wire, rawEl.GetRawText());
    }

    // ── Changeset-operation target dispatches on its `kind` discriminator ──

    [Fact]
    public void ChangesetOperationTarget_DispatchesOnKind()
    {
        // ChangesetOperationTarget is a union discriminated by "kind":
        //   "resource" → ChangesetOperationResourceTarget
        //   "range"    → ChangesetOperationRangeTarget
        const string resourceWire = """{"kind":"resource","resource":"file:///a.txt"}""";
        const string rangeWire =
            """{"kind":"range","resource":"file:///a.txt","range":{"start":2,"end":5}}""";

        var resourceTarget = Ser.Deserialize<ChangesetOperationTarget>(resourceWire);
        var range = Ser.Deserialize<ChangesetOperationTarget>(rangeWire);

        var res = Assert.IsType<ChangesetOperationResourceTarget>(resourceTarget.Value);
        Assert.Equal("resource", res.Kind);
        Assert.Equal("file:///a.txt", res.Resource);

        var rng = Assert.IsType<ChangesetOperationRangeTarget>(range.Value);
        Assert.Equal("range", rng.Kind);
        Assert.Equal(2, rng.Range.Start);
        Assert.Equal(5, rng.Range.End);

        // Re-encode the range variant and confirm the discriminator survives.
        var back = Ser.Deserialize<ChangesetOperationTarget>(Ser.Serialize(range));
        var backRng = Assert.IsType<ChangesetOperationRangeTarget>(back.Value);
        Assert.Equal(rng.Range.Start, backRng.Range.Start);
        Assert.Equal(rng.Range.End, backRng.Range.End);
    }

    // ── SessionInputQuestion "number" and "integer" both decode typed ─────

    [Fact]
    public void SessionInputQuestion_NumberAndIntegerKinds()
    {
        // Both "number" and "integer" kinds map to SessionInputNumberQuestion;
        // the typed Kind enum preserves which of the two the wire carried.
        const string numberWire =
            """{"kind":"number","id":"q1","message":"How many?","min":0,"max":10}""";
        const string integerWire =
            """{"kind":"integer","id":"q2","message":"How many whole?","defaultValue":3}""";

        var number = Ser.Deserialize<SessionInputQuestion>(numberWire);
        var integer = Ser.Deserialize<SessionInputQuestion>(integerWire);

        var nq = Assert.IsType<SessionInputNumberQuestion>(number.Value);
        Assert.Equal("q1", nq.Id);
        Assert.Equal(SessionInputQuestionKind.Number, nq.Kind);
        Assert.Equal(0d, nq.Min);
        Assert.Equal(10d, nq.Max);

        var iq = Assert.IsType<SessionInputNumberQuestion>(integer.Value);
        Assert.Equal("q2", iq.Id);
        Assert.Equal(SessionInputQuestionKind.Integer, iq.Kind);
        Assert.Equal(3d, iq.DefaultValue);
    }

    // ── 64-bit numeric field survives values above Int32.MaxValue ─────────

    [Fact]
    public void Number_LongAboveInt32Max_Preserved()
    {
        // ActionEnvelope.ServerSeq is a `long`; a value above int.MaxValue must
        // round-trip without truncation to 32 bits.
        const long bigSeq = (long)int.MaxValue + 1234567; // 2_148_131_814
        var wire = $$"""
            {
                "channel": "ahp-session:/s1",
                "action": { "type": "session/titleChanged", "title": "x" },
                "serverSeq": {{bigSeq}},
                "origin": null
            }
            """;

        var env = Ser.Deserialize<ActionEnvelope>(wire);
        Assert.Equal(bigSeq, env.ServerSeq);
        Assert.True(env.ServerSeq > int.MaxValue);

        var back = Ser.Deserialize<ActionEnvelope>(Ser.Serialize(env));
        Assert.Equal(bigSeq, back.ServerSeq);
    }

    // ── Unknown wire keys are ignored on decode ───────────────────────────

    [Fact]
    public void UnknownWireKeys_IgnoredOnDecode()
    {
        // A known type carrying EXTRA, unrecognized JSON keys decodes its known
        // fields and silently drops the unknown ones (System.Text.Json default).
        const string wire = """
            {
                "resource": "ahp-session:/s1",
                "provider": "demo",
                "title": "Hello",
                "status": 0,
                "createdAt": 1,
                "modifiedAt": 2,
                "unknownFutureKey": {"nested": true},
                "anotherUnknown": 42
            }
            """;

        var summary = Ser.Deserialize<SessionSummary>(wire);

        Assert.Equal("ahp-session:/s1", summary.Resource);
        Assert.Equal("demo", summary.Provider);
        Assert.Equal("Hello", summary.Title);
        Assert.Equal(1, summary.CreatedAt);
        Assert.Equal(2, summary.ModifiedAt);
    }

    // ── Nested optional struct round-trips when null ──────────────────────

    [Fact]
    public void NestedOptionalStruct_RoundTripsWhenNull()
    {
        // SessionSummary.Project is an optional nested struct; when null it is
        // omitted on serialize ([JsonIgnore(WhenWritingNull)]) and decodes back
        // as null. Round-trip must preserve the null.
        var summary = new SessionSummary
        {
            Resource = "ahp-session:/s1",
            Provider = "demo",
            Title = "No project",
            Status = SessionStatus.Idle,
            CreatedAt = 1,
            ModifiedAt = 2,
            Project = null,
        };

        var json = Ser.Serialize(summary);
        Assert.DoesNotContain("\"project\"", json);

        var back = Ser.Deserialize<SessionSummary>(json);
        Assert.Null(back.Project);
        Assert.Equal("No project", back.Title);
    }

    // ── Channel-scoped notification preserves its channel URI ─────────────

    [Fact]
    public void ChannelScopedNotification_CarriesUri()
    {
        // SessionAddedParams is a channel-scoped notification payload; the
        // `channel` field carries the subscription URI and must survive a
        // round-trip unchanged.
        const string channelUri = "ahp:/root";
        const string wire = $$"""
            {
                "channel": "{{channelUri}}",
                "session": "ahp-session:/s1"
            }
            """;

        var added = Ser.Deserialize<SessionAddedParams>(wire);
        Assert.Equal(channelUri, added.Channel);

        var back = Ser.Deserialize<SessionAddedParams>(Ser.Serialize(added));
        Assert.Equal(channelUri, back.Channel);
    }

    // ── Partial summary with all-null payload round-trips ─────────────────

    [Fact]
    public void PartialSummary_AllNullPayload_RoundTrips()
    {
        // PartialSessionSummary has every field optional (all [JsonIgnore(
        // WhenWritingNull)]); an all-null instance serializes to "{}" and
        // decodes back with every field still null — no exception.
        var partial = new PartialSessionSummary();

        var json = Ser.Serialize(partial);
        Assert.Equal("{}", json);

        var back = Ser.Deserialize<PartialSessionSummary>(json);
        Assert.Null(back.Resource);
        Assert.Null(back.Provider);
        Assert.Null(back.Title);
        Assert.Null(back.Status);
        Assert.Null(back.Activity);
        Assert.Null(back.CreatedAt);
        Assert.Null(back.ModifiedAt);
        Assert.Null(back.Project);
        Assert.Null(back.Model);
        Assert.Null(back.Agent);
        Assert.Null(back.WorkingDirectory);
    }
}
