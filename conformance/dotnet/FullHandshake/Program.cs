using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AgentHostProtocol;
using Microsoft.AgentHostProtocol.WebSockets;

var url = args[0];
const string channel = "ahp-session:/compliant";
var opts = AhpJson.Options;
Reducers.SetNowProvider(() => 9999);   // pin clock to match the host's pinned Date.now

using var finalDoc = JsonDocument.Parse(File.ReadAllText(args[1]));
var expectedFinal = finalDoc.RootElement.GetProperty("final");
var n = finalDoc.RootElement.GetProperty("count").GetInt32();

await using var transport = await WebSocketTransport.ConnectAsync(new Uri(url));
var client = AhpClient.Connect(transport);
var sub = client.AttachSubscription(channel);

// FULL HANDSHAKE: real `initialize` JSON-RPC request/response over the socket.
var init = await client.InitializeAsync("compliant-client", ProtocolVersion.Supported, new[] { channel });
Console.WriteLine($"[handshake] initialize -> protocolVersion={init.ProtocolVersion}, snapshots={init.Snapshots?.Count}");
if (init.ProtocolVersion != "0.3.0") { Console.WriteLine("bad protocolVersion"); return 1; }

// Seed from the snapshot the server returned in InitializeResult.
var state = init.Snapshots![0].State.Session!;
Console.WriteLine($"[seed] snapshot title='{state.Summary.Title}'");

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var applied = 0;
while (applied < n)
{
    var ev = await sub.Events.ReadAsync(cts.Token);
    if (ev is SubscriptionEventAction a) { Reducers.ApplyToSession(state, a.Envelope.Action); applied++; }
}
Console.WriteLine($"[stream] reduced {applied} live 'action' notifications");

var got = Canon(JsonSerializer.SerializeToElement(state, opts));
var want = Canon(expectedFinal);
Console.WriteLine($"[client] {got}");
Console.WriteLine($"[host  ] {want}");
var ok = got == want;
Console.WriteLine(ok ? "FULL-HANDSHAKE LIVE PASS — initialize + snapshot + live action stream converge with the canonical reducer"
                     : "DIVERGED");
await client.ShutdownAsync();
return ok ? 0 : 1;

static string Canon(JsonElement e) { var sb = new StringBuilder(); C(e, sb); return sb.ToString(); }
static void C(JsonElement e, StringBuilder sb)
{
    switch (e.ValueKind)
    {
        case JsonValueKind.Object:
            sb.Append('{'); var f = true;
            foreach (var p in e.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null).OrderBy(p => p.Name, StringComparer.Ordinal))
            { if (!f) sb.Append(','); f = false; sb.Append(JsonSerializer.Serialize(p.Name)).Append(':'); C(p.Value, sb); }
            sb.Append('}'); break;
        case JsonValueKind.Array:
            sb.Append('['); var f2 = true;
            foreach (var it in e.EnumerateArray()) { if (!f2) sb.Append(','); f2 = false; C(it, sb); }
            sb.Append(']'); break;
        case JsonValueKind.String: sb.Append(JsonSerializer.Serialize(e.GetString())); break;
        case JsonValueKind.Number: sb.Append(e.GetRawText()); break;
        case JsonValueKind.True: sb.Append("true"); break;
        case JsonValueKind.False: sb.Append("false"); break;
        default: sb.Append("null"); break;
    }
}
