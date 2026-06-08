# Agent Host Protocol — .NET client

The [Agent Host Protocol](https://microsoft.github.io/agent-host-protocol/)
(AHP) client for .NET: the wire types, the pure state reducers, an async
JSON-RPC client, a `ClientWebSocket` transport, and the multi-host runtime.

## Install

```bash
dotnet add package Microsoft.AgentHostProtocol
dotnet add package Microsoft.AgentHostProtocol.WebSockets   # ClientWebSocket transport
```

| Package | Use it for |
| --- | --- |
| `Microsoft.AgentHostProtocol.Abstractions` | Wire types + reducers' data contracts + the `ITransport` / `IAhpSerializer` interfaces. No I/O, no dependencies. Reference this alone to parse / construct AHP messages or implement a transport. |
| `Microsoft.AgentHostProtocol` | The async `AhpClient`, the pure reducers, the default System.Text.Json serializer, and the `MultiHostClient`. |
| `Microsoft.AgentHostProtocol.WebSockets` | A `System.Net.WebSockets.ClientWebSocket`-based `ITransport`. |

(`Microsoft.AgentHostProtocol` references `.Abstractions` transitively, so most
consumers add the two packages above.)

## Quickstart

```csharp
using Microsoft.AgentHostProtocol;
using Microsoft.AgentHostProtocol.WebSockets;

await using var transport = await WebSocketTransport.ConnectAsync(new Uri("ws://localhost:5172"));
var client = AhpClient.Connect(transport);

await client.InitializeAsync(
    clientId: "ahp-dotnet-example",
    protocolVersions: ProtocolVersion.Supported,
    initialSubscriptions: new[] { ProtocolVersion.RootResourceUri });

var root = client.AttachSubscription(ProtocolVersion.RootResourceUri);
await foreach (var evt in root.Events())
{
    Console.WriteLine(evt);
}
```

The pure reducers need no client at all:

```csharp
var state = new SessionState { /* ... */ };
Reducers.ApplyToSession(state, action);   // mutates `state` in place
```

See [`examples/`](examples/) for runnable `ConnectWs` and `ReducersDemo`
console apps.

## Code generation

The wire types under
`src/AgentHostProtocol.Abstractions/Generated/*.generated.cs` are generated
from the canonical TypeScript protocol definitions in `types/`. Do not edit
them by hand. From the repository root:

```bash
npm install
npm run generate:dotnet
```

CI re-runs the generator and fails on any diff, so generated sources always
match the protocol definitions. Hand-written support lives alongside the
generated files (`Json/`, `Transport/`) and in the `Microsoft.AgentHostProtocol`
project.

## Serialization is pluggable

The client talks to the JSON engine through the `IAhpSerializer` seam; the
default is `SystemTextJsonAhpSerializer` (System.Text.Json). An alternative
implementation can swap the engine or decorate it with JSON-Schema validation
(against the schemas the repository generates under `schema/`) without changing
the client or transport.

## Releasing

1. Bump [`VERSION`](VERSION).
2. From the repo root, run `npm run generate:metadata` and commit the updated
   [`release-metadata.json`](release-metadata.json).
3. Rotate the `## [Unreleased]` section of [`CHANGELOG.md`](CHANGELOG.md) to
   `## [X.Y.Z]`.
4. Merge to `main`, then push the tag `dotnet/vX.Y.Z`. The
   `publish-dotnet.yml` workflow validates the tag, packs, and pushes the
   packages to NuGet.org.

## License

MIT
