// Applies a handful of session actions to an empty SessionState to illustrate
// the public reducer API. Port of clients/go/examples/reducers_demo/main.go.
#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.AgentHostProtocol;

var state = new SessionState
{
    Summary = new SessionSummary
    {
        Resource = "ahp-session:/demo",
        Provider = "demo",
        Title = "Demo",
        Status = SessionStatus.Idle,
        CreatedAt = 1,
    },
    Lifecycle = SessionLifecycle.Ready,
    Turns = new System.Collections.Generic.List<Turn>(),
};

var actions = new List<StateAction>
{
    new StateAction(new SessionTurnStartedAction
    {
        Type = ActionType.SessionTurnStarted,
        TurnId = "t1",
        Message = new Message
        {
            Text = "Hello!",
            Origin = System.Text.Json.JsonDocument.Parse("""{"role":"user"}""").RootElement,
        },
    }),
    new StateAction(new SessionResponsePartAction
    {
        Type = ActionType.SessionResponsePart,
        TurnId = "t1",
        Part = new ResponsePart(new MarkdownResponsePart
        {
            Kind = ResponsePartKind.Markdown,
            Id = "p1",
            Content = "Hi ",
        }),
    }),
    new StateAction(new SessionDeltaAction
    {
        Type = ActionType.SessionDelta,
        TurnId = "t1",
        PartId = "p1",
        Content = "there!",
    }),
    new StateAction(new SessionTurnCompleteAction
    {
        Type = ActionType.SessionTurnComplete,
        TurnId = "t1",
    }),
};

foreach (var action in actions)
{
    var outcome = Reducers.ApplyToSession(state, action);
    Console.WriteLine($"applied {action.Value?.GetType().Name} → {OutcomeName(outcome)}");
}

var options = new JsonSerializerOptions { WriteIndented = true };
var pretty = JsonSerializer.Serialize(state, options);
Console.WriteLine("final state:");
Console.WriteLine(pretty);

static string OutcomeName(ReduceOutcome o) => o switch
{
    ReduceOutcome.Applied => "Applied",
    ReduceOutcome.NoOp => "NoOp",
    ReduceOutcome.OutOfScope => "OutOfScope",
    _ => "?",
};
