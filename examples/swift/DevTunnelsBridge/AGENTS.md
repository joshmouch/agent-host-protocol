# DevTunnelsBridge — Agent Guide

> This file is the single source of truth for any AI agent working on this package.
> Read this **first** before making changes.

## What is DevTunnelsBridge?

DevTunnelsBridge is a Swift package that provides Dev Tunnels connectivity for iOS/macOS apps. It wraps the Microsoft Dev Tunnels **Rust SDK** via UniFFI to expose tunnel discovery, authentication, and management to Swift.

The primary consumer is the **AHPClient** iOS app (in `../AHPClient/`), which uses this to connect to AHP servers running behind Dev Tunnels.

## Current State & Approach

### What's implemented

- ✅ **Tunnel listing** — `listTunnels()` via management API
- ✅ **Tunnel detail** — `getTunnelDetail()` with relay URI, ports, and connect access token
- ✅ **Device code auth** — Full GitHub device code flow (`startDeviceCodeAuth` / `pollDeviceCodeAuth`)
- ✅ **Connect access token** — Requested with `tokenScopes=["connect"]` for tunnel proxy auth

### Connection approach: Direct HTTPS (not relay)

We use the **public forwarded port endpoint** (`{tunnelId}-{port}.{clusterId}.devtunnels.ms`) instead of the relay client. This is a deliberate shortcut because the Rust SDK lacks a client-side relay (see gap analysis below).

**How it works:**
1. App calls `getTunnelDetail()` → gets `connectAccessToken` (JWT with connect scope)
2. App opens WebSocket to `wss://{tunnelId}-31546.{clusterId}.devtunnels.ms`
3. Sends `X-Tunnel-Authorization: tunnel <connect-jwt>` header in HTTP upgrade
4. devtunnels.ms proxy validates JWT and forwards WebSocket to the tunnel host's port 31546
5. AHP JSON-RPC handshake proceeds over the WebSocket

**Tradeoffs vs relay approach (what el/Node.js does):**

| | **Direct HTTPS (our approach)** | **SSH relay (el's approach)** |
|---|---|---|
| Complexity | Simple — standard WebSocket | Complex — SSH-over-WS, port forwarding |
| Private tunnels | ❌ Only publicly exposed ports | ✅ Works with any tunnel |
| Auth | Connect JWT in header per request | SDK handles internally |
| Port flexibility | One port per connection | Multiplexed, any port |
| Dependencies | Just management API | Full relay client SDK |

### Multi-layer authentication

There are **three** separate auth concerns when connecting to an AHP server through a Dev Tunnel:

1. **GitHub OAuth token** — For the management API (list tunnels, get details). Obtained via device code flow, stored in iOS Keychain.
2. **Tunnel connect JWT** — For the devtunnels.ms forwarded port proxy. Obtained from management API with `tokenScopes=["connect"]`. Sent as `X-Tunnel-Authorization: tunnel <jwt>`. Short-lived, must be refreshed on reconnect.
3. **AHP server token** — For the AHP server itself behind the tunnel. Convention is `?tkn=SHA256(tunnelId)` as a query parameter. ⚠️ Not yet implemented in iOS — may be needed if the AHP server validates it.

### Rust SDK Gap

**The `tunnels` Rust crate only has HOST-side relay support.**

The `connections` module exports only `relay_tunnel_host.rs` — there is no `TunnelRelayTunnelClient` equivalent. The CLIENT-side relay exists in C#, Java, TypeScript, and Go SDKs but **NOT in Rust**.

**What works in Rust:**
- ✅ Management API — list, get, create, delete tunnels (`TunnelManagementClient`)
- ✅ WebSocket utilities — `connect_directly`, `connect_via_proxy` (in `connections/ws.rs`)
- ✅ Contract types — `Tunnel`, `TunnelEndpoint`, `TunnelRelayTunnelEndpoint`, etc.
- ✅ Access token scoping — request `connect` scope, extract from `tunnel.access_tokens`

**What's missing in Rust:**
- ❌ `TunnelRelayTunnelClient` — SSH client session over WebSocket relay
- ❌ `connectToForwardedPort` — port forwarding channel over SSH
- ❌ V2 protocol support (`tunnel-relay-client-v2-dev`)

**Options to close the gap (if needed later):**
1. **Implement client relay in Rust** — Use `russh` (already a dep of the host) to build SSH client over WebSocket. ~500 lines based on TS reference.
2. **Port the Go SDK** — Go has full `TunnelRelayTunnelClient`.
3. **Keep direct HTTPS** — Current approach works for publicly exposed tunnels. Only pursue relay if private tunnel support is needed.

## Architecture

```
Swift Layer (AHPClient app)
  ├─ TunnelListView       — UI for auth + tunnel browsing + one-tap connect
  ├─ TunnelTokenStore     — Keychain persistence for GitHub token
  ├─ AppStore             — Fetches fresh connect JWT on connect/reconnect
  └─ AHPConnection        — WebSocket with X-Tunnel-Authorization header
       ↓
FFI Layer (UniFFI-generated)
  └─ DevTunnelsFFI (Generated/DevTunnelsFFI.swift)
       ↓
Rust Layer (rust/)
  ├─ lib.rs               — UniFFI exports: listTunnels, getTunnelDetail, device code auth
  └─ Cargo.toml           — depends on tunnels crate (local vendor)
```

## How It Works

### Management layer (list tunnels, auth)

1. **Authenticate** — GitHub device code flow → access token
2. **List tunnels** — `TunnelManagementClient.list_all_tunnels()` → `Vec<TunnelInfo>`
3. **Get detail** — `client.get_tunnel()` with `token_scopes=["connect"]` → `TunnelDetail` with connect JWT

### Connection layer (current: direct HTTPS)

1. Build URL: `wss://{tunnelId}-31546.{clusterId}.devtunnels.ms`
2. Send `X-Tunnel-Authorization: tunnel <connectAccessToken>` in WebSocket upgrade
3. AHP JSON-RPC protocol over the WebSocket

### Connection layer (future: relay, if needed)

1. **Get relay URI** — From `TunnelRelayTunnelEndpoint.clientRelayUri`
2. **WebSocket connect** — Open WebSocket to relay URI with `tunnel-relay-client` subprotocol
3. **SSH session** — SSH client session over WebSocket stream
4. **Port forwarding** — Open SSH channel for port 31546
5. **AHP WebSocket** — AHP JSON-RPC over the forwarded port stream

## Project Structure

```
DevTunnelsBridge/
├── Package.swift                          # Swift Package manifest
├── AGENTS.md                              # This file
├── .gitignore                             # Ignores vendor/, .build/, target/, Frameworks/, Generated/
├── Sources/DevTunnelsBridge/
│   ├── DevTunnelsBridge.swift             # Public API re-exports
│   └── Generated/
│       ├── DevTunnelsFFI.swift            # UniFFI-generated Swift bindings
│       ├── DevTunnelsFFIFFI.h             # C header for FFI
│       └── DevTunnelsFFIFFI.modulemap     # Clang module map
├── Tests/DevTunnelsBridgeTests/
│   └── DevTunnelsBridgeTests.swift        # 10 tests (struct init, auth, constants)
├── rust/
│   ├── Cargo.toml                         # Rust deps (tunnels crate via local vendor)
│   ├── uniffi.toml                        # UniFFI configuration
│   └── src/lib.rs                         # FFI exports:
│       │                                  #   - listTunnels(accessToken)
│       │                                  #   - getTunnelDetail(accessToken, clusterId, tunnelId)
│       │                                  #   - startDeviceCodeAuth() → DeviceCodeResponse
│       │                                  #   - pollDeviceCodeAuth(deviceCode) → AuthPollResult
│       │                                  # Types: TunnelInfo, TunnelDetail, DeviceCodeResponse,
│       │                                  #        AuthPollResult (enum: accessToken/pending/expired/error)
├── vendor/                                # Vendored dependencies (gitignored)
│   └── dev-tunnels/                       # Clone of microsoft/dev-tunnels
├── Frameworks/                            # XCFramework output (gitignored)
│   └── DevTunnelsFFI.xcframework/         # Built by scripts/build-xcframework.sh
└── scripts/
    └── build-xcframework.sh               # Build Rust → XCFramework
```

## Build & Run

### Prerequisites

- Xcode 15+ with iOS 16+ SDK
- Rust toolchain (`rustup`)
- iOS cross-compilation targets:
  ```bash
  rustup target add aarch64-apple-ios aarch64-apple-ios-sim
  ```

### Setup vendored dependencies

```bash
git clone --depth 1 https://github.com/microsoft/dev-tunnels.git vendor/dev-tunnels
```

### Build Rust (all targets)

```bash
cd rust
cargo build                                    # macOS (for tests)
cargo build --target aarch64-apple-ios-sim     # iOS Simulator
cargo build --target aarch64-apple-ios         # iOS Device
```

### Regenerate UniFFI Swift bindings

After changing Rust structs/functions, regenerate the Swift bindings:

```bash
cd rust
cargo run --features uniffi/cli -- generate \
  --library target/debug/libdev_tunnels_bridge.a \
  --language swift \
  --out-dir ../Sources/DevTunnelsBridge/Generated
```

⚠️ **Important**: Output MUST go to `Sources/DevTunnelsBridge/Generated/`, not the parent directory. Putting files in both locations causes duplicate symbol build errors.

### Run tests

```bash
swift test    # Runs 10 tests
```

### Build the XCFramework (for iOS distribution)

```bash
cd scripts
./build-xcframework.sh
```

### Use in AHPClient

In Xcode, add `DevTunnelsBridge` as a local package dependency:
File → Add Package → Add Local → select this directory.

## Reference

- **el (TUI client)**: `/Users/penlv/Code/Personal/el` — Node.js AHP client with Dev Tunnels
  - `src/protocol/tunnel-transport.ts` — Relay connection flow (SSH-over-WS, the "ideal" approach)
  - `src/tunnel/discovery.ts` — Tunnel listing/resolution
  - `src/auth/tunnel-auth.ts` — GitHub device code auth + disk caching
- **Dev Tunnels SDK**: `microsoft/dev-tunnels` repo
  - `rs/` — Rust SDK (management + host relay only, no client relay)
  - `ts/src/connections/tunnelRelayTunnelClient.ts` — Reference client relay (563 lines)
  - `rs/src/connections/ws.rs` — Reusable WebSocket utilities
  - `rs/src/contracts/tunnel_access_scopes.rs` — Token scope constants (CONNECT = "connect")
- **UniFFI docs**: https://mozilla.github.io/uniffi-rs/

## Code Conventions

- Use Swift naming conventions (camelCase methods, PascalCase types)
- Keep FFI boundary thin — complex logic in Rust, ergonomic wrappers in Swift
- No external Swift dependencies (the package should be self-contained except for the Rust lib)
- Rust structs exposed via UniFFI use `#[derive(uniffi::Record)]` for value types and `#[derive(uniffi::Enum)]` for enums
- All Rust FFI functions are synchronous (block on a tokio runtime internally) because UniFFI async support is limited

## Implementation Status

1. ✅ **Scaffold** — Swift Package + Rust crate structure
2. ✅ **Management API** — List tunnels, get tunnel detail with connect token
3. ✅ **Device code auth** — Full GitHub device code flow with polling
4. ✅ **Connect token** — Request `tokenScopes=["connect"]`, extract JWT from `access_tokens`
5. ✅ **Integration** — Wired into AHPClient with one-tap connect, server switcher, auto-token-refresh
6. ⬚ **AHP server auth** — Send `?tkn=SHA256(tunnelId)` for AHP server behind tunnel
7. ⬚ **Relay client** — SSH-over-WS relay (only if private tunnel support is needed)
