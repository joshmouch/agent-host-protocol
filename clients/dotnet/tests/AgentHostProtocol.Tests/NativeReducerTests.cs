// Port of the Swift ReducersTests "Dispatch Validation" cases. These exercise the
// SHIPPED production predicate `Reducers.IsClientDispatchable(StateAction)` — exactly
// like Swift's tests call its production `isClientDispatchable`. The canonical
// client-dispatchable set lives in production (`Reducers.ClientDispatchableActions`,
// mirroring Swift's `clientDispatchableActions`); there is intentionally no test-local
// copy of it here.
//
// The predicate derives each action's wire `type` by serializing a REAL StateAction
// through the REAL serializer and reading the emitted `type` field — exercising the
// generated union + serializer's [WireValue] mapping, not a hand-typed literal.
#nullable enable

using System.Text.Json;
using Microsoft.AgentHostProtocol;
using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

public sealed class NativeReducerTests
{
    // A: clientDispatchable true — a user-channel action (turnStarted) is dispatchable.
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
        Assert.True(Reducers.IsClientDispatchable(action));
    }

    // A: clientDispatchable false — a host-only action (session/ready) is NOT dispatchable.
    [Fact]
    public void ClientDispatchable_FalseForHostOnlyAction()
    {
        var action = new StateAction(new SessionReadyAction
        {
            Type = ActionType.SessionReady,
        });
        Assert.False(Reducers.IsClientDispatchable(action));
    }
}
