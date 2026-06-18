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

using Microsoft.AgentHostProtocol;
using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

public sealed class NativeReducerTests
{
    // A: clientDispatchable true — a chat-channel action (chat/turnStarted) is dispatchable.
    [Fact]
    public void ClientDispatchable_TrueForUserChannelAction()
    {
        var action = new StateAction(new ChatTurnStartedAction
        {
            Type = ActionType.ChatTurnStarted,
            TurnId = "t1",
            // Message.Origin is a required (non-nullable) MessageOrigin — give it a
            // valid value so the action serializes.
            Message = new Message
            {
                Text = "hi",
                Origin = new MessageOrigin { Kind = MessageKind.User },
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

    // AHP 0.5.0 (#264): chat/draftChanged is the new client-dispatchable action
    // (a client syncs its in-progress draft); chat/activityChanged is server-only.
    [Fact]
    public void ClientDispatchable_TrueForChatDraftChanged()
    {
        var action = new StateAction(new ChatDraftChangedAction
        {
            Type = ActionType.ChatDraftChanged,
            Draft = new Message
            {
                Text = "in progress…",
                Origin = new MessageOrigin { Kind = MessageKind.User },
            },
        });
        Assert.True(Reducers.IsClientDispatchable(action));
    }

    [Fact]
    public void ClientDispatchable_FalseForChatActivityChanged()
    {
        var action = new StateAction(new ChatActivityChangedAction
        {
            Type = ActionType.ChatActivityChanged,
            Activity = "running a tool",
        });
        Assert.False(Reducers.IsClientDispatchable(action));
    }
}
