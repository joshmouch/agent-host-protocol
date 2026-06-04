// Port of the Swift ReducersTests "Dispatch Validation" cases. ClientDispatchable
// is asserted as MEMBERSHIP of an action's wire `type` in the cross-language
// client-dispatchable set — there is intentionally NO production predicate in the
// .NET client (Director ruling: test-only). The canonical set is Swift's
// `clientDispatchableActions` (clients/swift/.../Reducers.swift). If that set
// changes, this test's copy must be updated in lockstep.
//
// The wire `type` for each ActionType is derived by serializing a REAL StateAction
// through the REAL SystemTextJsonAhpSerializer and reading the emitted `type`
// field — exercising the generated union + serializer, NOT a hand-typed literal on
// the action side. There is no public WireEnum accessor for ActionType, so this is
// the faithful way to read the generated [WireValue] mapping.
#nullable enable

using System.Collections.Generic;
using System.Text.Json;
using Microsoft.AgentHostProtocol;
using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

public sealed class NativeReducerTests
{
    private static readonly SystemTextJsonAhpSerializer Ser = SystemTextJsonAhpSerializer.Default;

    // The set of action wire-`type` strings a client is allowed to dispatch.
    // Mirrors Swift's `clientDispatchableActions` (the cross-language contract).
    private static readonly HashSet<string> ClientDispatchableTypes = new()
    {
        "session/turnStarted",
        "session/toolCallConfirmed",
        "session/toolCallComplete",
        "session/toolCallResultConfirmed",
        "session/turnCancelled",
        "session/modelChanged",
        "session/activeClientChanged",
        "session/activeClientToolsChanged",
        "session/pendingMessageSet",
        "session/pendingMessageRemoved",
        "session/queuedMessagesReordered",
        "session/inputAnswerChanged",
        "session/inputCompleted",
        "session/customizationToggled",
        "session/isReadChanged",
        "session/isArchivedChanged",
    };

    // Reads the wire `type` string of a concrete StateAction by serializing it
    // through the real serializer and reading the emitted top-level `type`. This
    // binds the test to the GENERATED union's [WireValue] mapping, not to a string
    // hand-typed on the action side.
    private static string WireType(StateAction action)
    {
        using var doc = JsonDocument.Parse(Ser.Serialize(action));
        return doc.RootElement.GetProperty("type").GetString()!;
    }

    // A: clientDispatchable true — a user-channel action (turnStarted) is in the set.
    [Fact]
    public void ClientDispatchable_TrueForUserChannelAction()
    {
        var action = new StateAction(new SessionTurnStartedAction
        {
            Type = ActionType.SessionTurnStarted,
            TurnId = "t1",
            // Message.Origin is a required (non-nullable) JsonElement — give it a
            // valid value ("host", per the interop fixtures) so the action
            // serializes. A default(JsonElement) is Undefined and unserializable.
            Message = new Message
            {
                Text = "hi",
                Origin = JsonDocument.Parse("\"host\"").RootElement.Clone(),
            },
        });
        Assert.Contains(WireType(action), ClientDispatchableTypes);
    }

    // A: clientDispatchable false — a host-only action (session/ready) is NOT in the set.
    [Fact]
    public void ClientDispatchable_FalseForHostOnlyAction()
    {
        var action = new StateAction(new SessionReadyAction
        {
            Type = ActionType.SessionReady,
        });
        Assert.DoesNotContain(WireType(action), ClientDispatchableTypes);
    }
}
