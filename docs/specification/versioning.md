# Versioning

AI is an evolving space. Unlike LSP or DAP — which largely guarantee backwards compatibility in perpetuity — the design space for agent hosts is open-ended and moving quickly. Backwards-incompatible changes to AHP are inevitable. Versioning gives clients and hosts a shared vocabulary for negotiating which behaviors are safe to use on a given connection.

## Version Format

Protocol versions are [SemVer](https://semver.org) `MAJOR.MINOR.PATCH` strings (e.g. `"0.1.0"`). Pre-release and build metadata are not used.

## Negotiation

Version selection happens once, during the [`initialize`](/specification/lifecycle) handshake — modelled after WebSocket subprotocol negotiation:

1. The client sends `InitializeParams.protocolVersions`: an array of every protocol version it is willing to speak, ordered from most preferred to least preferred.
2. The server picks one entry it can speak and returns it as `InitializeResult.protocolVersion`. Servers SHOULD honor the client's preference order when multiple offered versions are acceptable.
3. If the server cannot speak any of the offered versions, it MUST respond with [`UnsupportedProtocolVersion`](/reference/error-codes) (`-32005`) instead of a result, and close the connection.

Both peers MUST use the selected version for the rest of the connection. There is no per-message renegotiation.

## Compatibility Guarantee

AHP follows standard SemVer compatibility:

- Two peers speaking versions `X.y.z` and `X.y'.z'` (same `MAJOR ≥ 1`) are compatible.
- Two peers speaking versions `0.X.y` and `0.X.y'` (same pre-1.0 `MINOR`) are compatible.
- Any other combination is **not** guaranteed to be compatible.

Within a compatible range, additive changes — new optional fields on existing types, new action types, new commands — are introduced in `PATCH` (or `MINOR`, while `MAJOR` is `0`) bumps and MUST be ignored by older peers that do not understand them.

## Capabilities First, Then Required

New behavior generally lands in two stages:

1. **Capability-gated.** A new feature is introduced as an opt-in capability advertised by a host or clients. Implementors check for the capability before exercising the feature. This lets hosts and clients adopt the feature on independent schedules without a version bump.
2. **Required.** Once a capability has matured, a future protocol version may promote it to baseline behavior and remove the capability flag. This reduces long-term implementation complexity.

## Client and Host Update Cadence

Agent hosts may be remote machines, cloud services, or other external APIs that the user does not control. Clients (IDEs, CLI tools, embedded UIs) are typically easier for a user to update than hosts.

As a result:

- **Clients SHOULD offer a wide range of protocol versions** when feasible so that older hosts can still pick a version they understand. Clients then degrade features gracefully when the negotiated version lacks a capability they would otherwise use.
- **Hosts SHOULD pick the highest offered version they implement.** Lower entries in the client's array are fallbacks for older hosts.
- **Hosts MUST refuse incompatible clients** by returning [`UnsupportedProtocolVersion`](/reference/error-codes) (`-32005`) when no offered version is acceptable.

## Forward Compatibility

When a newer client connects to an older host:

1. The client offers its full version list, including older versions it can fall back to.
2. The host picks the newest entry it understands and returns it.
3. The client checks the capability set advertised by the host before using newer features.
4. If a feature is unavailable, the client degrades gracefully — disabling UI affordances, falling back to older code paths, or surfacing a clear message to the user.
5. The host only sends action types known to the negotiated version. As a safety net, clients SHOULD silently ignore actions with unrecognized `type` values.

## Backward Compatibility

When an older client connects to a newer host:

1. The client offers only the versions it knows.
2. The host picks one of those (typically the newest the client offered) or returns `UnsupportedProtocolVersion` if it can no longer speak any of them.
3. On a successful negotiation the host MUST NOT use newer-version-only behaviors on that connection unless gated behind a capability the client has acknowledged.
