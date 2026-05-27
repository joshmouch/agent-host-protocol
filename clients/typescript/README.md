# @microsoft/agent-host-protocol

TypeScript client for the [Agent Host Protocol (AHP)](https://microsoft.github.io/agent-host-protocol/).

[![npm](https://img.shields.io/npm/v/@microsoft/agent-host-protocol.svg)](https://www.npmjs.com/package/@microsoft/agent-host-protocol)

Browser-friendly client built on the global `WebSocket` API. Works in
modern browsers and Node 21+ without additional runtime dependencies.

## Entry points

The package exposes three subpath exports:

| Import path | What it gives you |
|---|---|
| `@microsoft/agent-host-protocol`        | Wire types, actions, commands, reducers, version constants. No I/O. |
| `@microsoft/agent-host-protocol/client` | `AhpClient`, `Subscription`, `AhpStateMirror`, the `AhpTransport` interface, `InMemoryTransport`, and the error taxonomy. |
| `@microsoft/agent-host-protocol/ws`     | `WebSocketTransport` — an `AhpTransport` implementation backed by the global `WebSocket`. |

The split mirrors the Rust SDK (`ahp-types`, `ahp`, `ahp-ws`) — wire
types and reducers are decoupled from the client, which is in turn
decoupled from a specific transport.

## Quickstart

```ts
import { ActionType, type ActionEnvelope } from '@microsoft/agent-host-protocol';
import { AhpClient, AhpStateMirror } from '@microsoft/agent-host-protocol/client';
import { WebSocketTransport } from '@microsoft/agent-host-protocol/ws';

const transport = await WebSocketTransport.connect('ws://localhost:12345');
const client = new AhpClient(transport);
const mirror = new AhpStateMirror();

client.connect();

const init = await client.initialize({
  clientId: 'my-client',
  protocolVersions: ['0.2.0'],
  initialSubscriptions: ['ahp-root://'],
});

for (const snapshot of init.snapshots) {
  mirror.applySnapshot(snapshot);
}

const root = client.attachSubscription('ahp-root://');
(async () => {
  for await (const event of root) {
    if (event.type === 'action') mirror.apply(event.params);
  }
})();

const sessionUri = `ahp-session:/${crypto.randomUUID()}`;
client.dispatch(sessionUri, {
  type: ActionType.SessionTurnStarted,
  // … remaining action fields
} as unknown as ActionEnvelope['action']);
```

## Pluggable transports

`AhpClient` is transport-agnostic. Any framed message stream — a
WebSocket, a Unix socket, stdio, or an in-memory pair for tests — can
back an `AhpTransport`:

```ts
import type { AhpTransport, TransportFrame, JsonRpcMessage } from '@microsoft/agent-host-protocol/client';

class MyTransport implements AhpTransport {
  send(message: JsonRpcMessage | string): void { /* … */ }
  async recv(): Promise<TransportFrame | null> { /* … */ }
  close(): void { /* … */ }
}
```

`InMemoryTransport.pair()` returns two connected halves that exchange
text frames — handy for unit tests that don't need a real socket.

## Reducers and state mirror

The reducer functions (`rootReducer`, `sessionReducer`,
`terminalReducer`, `changesetReducer`) are pure: replaying actions in
`serverSeq` order on any prior snapshot yields identical state. This
is the same property the Rust and Swift clients rely on for
reconnection.

`AhpStateMirror` is a convenience that holds one `RootState`, a
`Map<URI, SessionState>`, a `Map<URI, TerminalState>`, and a
`Map<URI, ChangesetState>`. Apply `Snapshot`s and `ActionEnvelope`s and
it keeps those maps up to date. Larger apps usually keep their own
state and call the reducers directly.

## Errors

| Class | When it's thrown |
|---|---|
| `RpcError`           | JSON-RPC error response from the server. Carries `code`, `message`, `data`. |
| `RpcTimeoutError`    | Client-side timeout fired before the server responded. Carries `method`, `timeoutMs`. Distinct from `RpcError`. |
| `TransportError`     | Failure of the underlying transport. `kind: 'closed' \| 'io' \| 'protocol'`. |
| `ClientClosedError`  | Request was in flight when the client was shut down. |
| `AhpClientError`     | Base class for every error this SDK throws — use `instanceof` to catch them all. |

Malformed inbound frames don't throw — they're logged via `console.warn` and the channel stays alive (matching the Rust client's `tracing::warn!` behavior). Pending requests still time out via `RpcTimeoutError` if the dropped frame would have been their reply.

## Server-initiated requests

Some AHP methods (currently `resourceRequest`) can be initiated by the
server. By default, the client responds with JSON-RPC `MethodNotFound`
so the server does not leak a pending request. Install a typed handler
to take over:

```ts
client.setServerRequestHandler(async (method, params) => {
  if (method === 'resourceRequest') {
    return { /* … */ };
  }
  throw new RpcError(JsonRpcErrorCodes.MethodNotFound, 'unhandled');
});
```

## Reconnection

`AhpClient.reconnect(...)` sends the typed AHP `reconnect` request on
an already-open transport. It does not decide when to reconnect, how
often to retry, whether authentication errors are terminal, or how to
update UI while reconnecting — those policies live in the app.

A typical app-level reconnect flow is:

1. Open a fresh transport and `AhpClient`.
2. Attach event streams before the handshake.
3. Call `connect()` and `reconnect({ clientId, lastSeenServerSeq, subscriptions })`.
4. Apply the returned replay actions or snapshots to your app store.
5. Re-fetch `listSessions` or other ephemeral data — protocol
   notifications are not replayed.

## Wire types

The wire types under `src/types/` are generated from `types/*.ts` at the
repository root and are **not committed** to the repo — avoiding a
byte-for-byte duplication of the canonical TypeScript sources. Regenerate
them whenever you pull or change the protocol:

```bash
npm run generate:typescript    # from the repo root
```

Generated files carry a banner; do not edit them by hand. The
`generate:typescript` script is also part of `npm run generate`, which
regenerates every language's client output.

## Development

From a fresh checkout:

```bash
# 1. Install the root tooling and generate the TS client's wire types.
npm install
npm run generate:typescript

# 2. Work in the client package.
cd clients/typescript
npm install
npm run typecheck
npm test
npm run build
```

CI runs the generate step automatically before the install/typecheck/test/build
sequence, so contributors only need to remember step 1 locally after pulling
protocol changes.

## License

MIT
