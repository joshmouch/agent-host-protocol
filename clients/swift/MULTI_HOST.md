# Connecting to Multiple Hosts

The Agent Host Protocol describes a single _client -> host_ connection. A real product often needs to talk to **two or more hosts at once**: a local sessions server and a tunnel-attached remote, a personal host and a teammate's, multiple project hosts in a desktop sidebar, and so on. The protocol itself does not say how to wire that up; it is a client SDK concern.

This page covers the Swift SDK's multi-host layer.

## Why a built-in abstraction?

Without one, every consumer ends up writing the same things:

- N independent `AHPClient` instances and their lifetimes
- N transports plus reconnect supervisors with backoff and cancellation
- A registry that keys per-host metadata (label, URL, connection state, last error, agents, `serverSeq`, subscriptions, default directory) for UX
- A fan-in of inbound events tagged with which host produced them
- Per-host scoping of channel URIs (`ahp-session:/s1` on Host A != `ahp-session:/s1` on Host B)
- Persistence of `clientId` per host so reconnect identity survives restarts
- A per-host root state mirror plus session summary cache so sidebars and inboxes do not degrade to "subscribe to everything"

The Swift SDK ships a `MultiHostClient` actor that wraps all of this. **Single-host = N=1 of multi-host**, so the same API works either way.

## Per-host UX surface

Every registered host appears as a `HostHandle` snapshot (a `Sendable` value type):

| Field | Notes |
|---|---|
| `id`, `label` | Stable identifier and human-readable display name |
| `state` | `disconnected`, `connecting`, `connected`, `reconnecting(attempt:)`, `failed(reason:)` |
| `lastError`, `lastConnectedAt` | Surface in your status bar / debug panel |
| `protocolVersion`, `defaultDirectory`, `completionTriggerCharacters` | From `InitializeResult` |
| `clientId` | The id actually sent on `initialize`/`reconnect` |
| `serverSeq` | Highest `serverSeq` seen for this host |
| `agents`, `activeSessions`, `terminals` | Mirrored from the host's `RootState` |
| `subscriptions` | URIs the supervisor will (re-)subscribe to across reconnects |
| `sessionSummaries` | Cached `[SessionSummary]` kept fresh by `listSessions` plus session-related notifications |
| `generation` | Bumped on every (re)connect; used to invalidate stale client handles |

To observe changes, listen to `MultiHostClient.hostEvents()` for connection-state events, or use the observable streams below to bind directly into SwiftUI `@Observable` models.

## Single-host

```swift
import AgentHostProtocol
import AgentHostProtocolClient

let config = HostConfig(id: "local", label: "Local sessions server") { _ in
    URLSessionWebSocketTransport(url: URL(string: "ws://localhost:12345")!)
}
let (multi, handle) = try await MultiHostClient.single(config)
print("connected to \(handle.label): \(handle.state)")
```

## Multi-host

```swift
let multi = MultiHostClient(clientIdStore: FileClientIdStore(directory: appSupportURL))
_ = try await multi.add(HostConfig(id: "local", label: "Local", transportFactory: openLocal))
_ = try await multi.add(HostConfig(id: "remote", label: "Remote", transportFactory: openRemote))

for hosted in await multi.aggregatedSessions() {
    print("[\(hosted.hostLabel)] \(hosted.summary.title)")
}
```

`MultiHostClient` is an `actor` and runs off the main thread. Wrap it in your `@MainActor` `@Observable` store to bind into SwiftUI.

## Reliable per-channel streams

`events()` is **lossy by design** (`.bufferingNewest(1024)`) and is for advisory consumption only. Reducer-critical action envelopes must be consumed via the unbounded per-channel stream — runtime-owned and surviving reconnects (replayed envelopes are fanned in too):

```swift
guard let stream = await multi.events(host: "local", uri: RootResourceURI) else {
    // host isn't registered — handle as appropriate for your app
    return
}
let snapshot = try await multi.subscribe(host: "local", uri: RootResourceURI)
for await event in stream {
    if case .action(let envelope) = event {
        await mirror.apply(host: "local", envelope: envelope)
    }
}
```

`MultiHostStateMirror` provides a host-aware reducer façade keyed by `HostedResourceKey { hostId; uri }` for the common case where channel URIs collide across hosts. Feed it from `events(host:uri:)` — never from the lossy `events()`.

## Observable host streams

For SwiftUI / `@Observable` consumers, `MultiHostClient` exposes derived streams that yield a current value immediately and re-yield on changes. Both use `.bufferingNewest(1)` since only the latest snapshot matters to a UI consumer:

```swift
guard let snapshots = await multi.hostSnapshots(host: "local") else { return }
for await snap in snapshots {
    // bind snap.state, snap.lastError, snap.serverSeq, ...
}

guard let summaries = await multi.sessionSummaries(host: "local") else { return }
for await list in summaries {
    // bind sidebar list
}
```

## Reconnect, generation, and ownership

Each host runs in its own internal task — a `HostRuntime` — that owns the current `AHPClient`, retries per the configured `ReconnectPolicy`, and re-subscribes to known URIs across reconnects.

Every successful (re)connect bumps a per-host **generation** counter. Any `HostClientHandle` you obtained from a previous connection refuses to dispatch on the new one and throws `HostError.hostReconnected` — request a fresh handle in that case. This prevents subtle bugs where a handle held across a reconnect silently writes to a different connection.

`MultiHostClient.reconnect(_:)` reconnects a single host; `reconnectAllUnavailable()` walks every host and reconnects those not in `.connected` or `.connecting` — handy for the iOS scene-phase pattern:

```swift
.onChange(of: scenePhase) { _, phase in
    if phase == .active {
        Task { await multi.reconnectAllUnavailable() }
    }
}
```

## Stable `clientId` per host

The protocol uses `clientId` to identify a logical client across reconnects. Each host gets its own `clientId`, generated by the SDK and stored in a pluggable `ClientIdStore`. The SDK ships two implementations:

- `InMemoryClientIdStore` — default; session-stable but lost on restart. Fine for tests and ephemeral CLIs.
- `FileClientIdStore(directory:)` — filesystem-backed; atomic writes, owner-only permissions on POSIX, percent-encoded filenames for arbitrary `HostId` strings. Cross-platform; recommended for command-line and desktop tools.

iOS apps wanting a higher-security profile should wrap Keychain in a `ClientIdStore` (a few lines of Security.framework). The SDK does not ship a Keychain implementation to keep `AgentHostProtocolClient` free of a `Security.framework` dependency on cross-platform builds.

## Task cancellation

`AHPClient.request` and `HostClientHandle.request` observe `Task.isCancelled`. Cancelling the surrounding `Task` throws `CancellationError()`; the local pending entry is removed so a late server response is harmlessly dropped. Cancellation only cancels the local wait — server-side execution isn't aborted (that's a higher-level concern).

Useful for typeahead / debounced flows where you previously had to write request-key bookkeeping to ignore stale results.

## Escape hatch for extension RPCs

For RPCs whose params or result types can't satisfy the typed `request<P, R>`'s `Sendable` constraint (e.g. when `SWIFT_DEFAULT_ACTOR_ISOLATION = MainActor` makes synthesised `Codable` conformances inherit `@MainActor`), use the raw-bytes variants `AHPClient.requestRaw(method:paramsData:) -> Data` / `HostClientHandle.requestRaw`. Encode and decode JSON yourself.

## Choosing single-host vs multi-host

You don't choose. Single-host consumers use `MultiHostClient.single(...)` and never see registry concepts. The SDK imposes no per-host overhead beyond a single supervisor task, and there is no separate single-host API to learn.

See `clients/swift/AgentHostProtocol/Sources/AgentHostProtocolClient/Hosts/` and `Tests/AgentHostProtocolClientTests/` for the full surface, plus `MultiHostExample.runDemo()` for a runnable demo.
