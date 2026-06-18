// Connect to an AHP server over WebSocket, run the initialize handshake,
// attach a root subscription, and print every inbound event as JSON until
// the connection drops or CTRL+C is pressed.
//
// Usage: dotnet run --project examples/ConnectWs -- ws://host:port
#nullable enable

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AgentHostProtocol;
using Microsoft.AgentHostProtocol.WebSockets;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: ConnectWs ws://host:port");
    return 2;
}

var url = new Uri(args[0]);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

WebSocketTransport transport;
try
{
    transport = await WebSocketTransport.ConnectAsync(url, cancellationToken: cts.Token);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"connect: {ex.Message}");
    return 1;
}

await using var client = AhpClient.Connect(transport);

InitializeResult init;
try
{
    init = await client.InitializeAsync(
        "ahp-dotnet-example",
        ProtocolVersion.Supported,
        new[] { ProtocolVersion.RootResourceUri },
        cts.Token);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"initialize: {ex.Message}");
    return 1;
}

Console.Error.WriteLine($"negotiated protocol version: {init.ProtocolVersion}");

var sub = client.AttachSubscription(ProtocolVersion.RootResourceUri);
var options = new JsonSerializerOptions { WriteIndented = true };

try
{
    await foreach (var ev in sub.Events.ReadAllAsync(cts.Token))
    {
        var json = JsonSerializer.Serialize<object>(ev, options);
        Console.WriteLine($"{ev.GetType().Name}:");
        Console.WriteLine(json);
        Console.WriteLine();
    }
}
catch (OperationCanceledException) { /* CTRL+C */ }

sub.Close();
await client.ShutdownAsync(CancellationToken.None);
return 0;
