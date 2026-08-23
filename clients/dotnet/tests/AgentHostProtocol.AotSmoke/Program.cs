using Microsoft.AgentHostProtocol;

var serializer = SystemTextJsonAhpSerializer.Default;

var initialize = new InitializeParams
{
    Channel = "ahp-root://",
    ClientId = "native-aot-smoke",
    ProtocolVersions = new List<string> { "0.1.0" },
    InitialSubscriptions = new List<string> { "ahp-root://" },
};

var initializeJson = serializer.Serialize(initialize);
var initializeRoundTrip = serializer.Deserialize<InitializeParams>(initializeJson);
Require(initializeRoundTrip.ClientId == initialize.ClientId, "InitializeParams round trip failed.");

var request = new JsonRpcMessage
{
    Request = new JsonRpcRequest
    {
        Id = 1,
        Method = "initialize",
        Params = serializer.SerializeToElement(initialize),
    },
};
var decodedRequest = serializer.DecodeMessage(serializer.EncodeMessage(request));
Require(decodedRequest.Request?.Method == "initialize", "JSON-RPC framing round trip failed.");

var action = new StateAction(
    new SessionIsReadChangedAction
    {
        Type = ActionType.SessionIsReadChanged,
        IsRead = true,
    });
var actionJson = serializer.Serialize(action);
var actionRoundTrip = serializer.Deserialize<StateAction>(actionJson);
Require(
    actionRoundTrip.Value is SessionIsReadChangedAction { IsRead: true },
    "Discriminated action union round trip failed.");
Require(
    actionJson.Contains("\"type\":\"session/isReadChanged\"", StringComparison.Ordinal),
    "Wire enum conversion failed.");

var snapshot = new Snapshot
{
    Resource = "ahp-root://",
    FromSeq = 42,
    State = new SnapshotState
    {
        Root = new RootState { Agents = new List<AgentInfo>() },
    },
};
var snapshotRoundTrip = serializer.Deserialize<Snapshot>(serializer.Serialize(snapshot));
Require(snapshotRoundTrip.State.Root?.Agents.Count == 0, "Snapshot union round trip failed.");

var plainText = serializer.Deserialize<StringOrMarkdown>("\"hello\"");
Require(plainText.AsText() == "hello", "StringOrMarkdown scalar round trip failed.");

Require(
    AhpJson.Options.GetTypeInfo(typeof(ActionEnvelope)) is not null,
    "Generated metadata is missing ActionEnvelope.");

Console.WriteLine("Native AOT serialization smoke test passed.");

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
