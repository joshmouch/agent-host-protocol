# DevTunnelsBridge — Agent Guide

> This file is the single source of truth for any AI agent working on this package.
> Read this **first** before making changes.

## What is DevTunnelsBridge?

DevTunnelsBridge is a Swift package that provides Dev Tunnels connectivity for iOS/macOS apps. It wraps the Microsoft Dev Tunnels **Rust SDK** via UniFFI to expose tunnel discovery, authentication, and relay connections to Swift.

The primary consumer is the **AHPClient** iOS app (in `../AHPClient/`), which uses this to connect to AHP servers running behind Dev Tunnels.

## Critical Finding: Rust SDK Gap

**The `tunnels` Rust crate (at `microsoft/dev-tunnels/rs/`) only has HOST-side relay support.**

The `connections` module exports only `relay_tunnel_host.rs` — there is no `TunnelRelayTunnelClient` equivalent. The CLIENT-side relay exists in C#, Java, and TypeScript SDKs but NOT in Rust.

**What works in Rust:**
- ✅ Management API — list, get, create, delete tunnels (`TunnelManagementClient`)
- ✅ WebSocket utilities — `connect_directly`, `connect_via_proxy`, `build_websocket_request` (in `connections/ws.rs`)
- ✅ Contract types — `Tunnel`, `TunnelEndpoint`, `TunnelRelayTunnelEndpoint`, etc.

**What's missing in Rust:**
- ❌ `TunnelRelayTunnelClient` — SSH client session over WebSocket relay
- ❌ `connectToForwardedPort` — port forwarding channel over SSH
- ❌ V2 protocol support (`tunnel-relay-client-v2-dev`)

**Options for relay connection (to be decided):**
1. **Implement client relay in Rust** — Use `russh` (already a dep) to build an SSH client session over the relay WebSocket. ~500 lines based on TS reference implementation. This is the most self-contained approach.
2. **Port the Go SDK** — Go has `TunnelRelayTunnelClient` too. Could port it to Rust.
3. **Direct WebSocket** — If the AHP server exposes a public WebSocket endpoint (no tunnel), skip relay entirely. Only works for some deployment scenarios.

## Architecture

```
Swift Layer (DevTunnelsBridge)
  ├─ TunnelClient         — High-level async Swift API
  ├─ TunnelInfo           — Tunnel metadata (name, cluster, endpoints)
  ├─ TunnelAuth           — GitHub device code auth flow
  └─ TunnelStream         — Bidirectional stream to forwarded port
       ↓
FFI Layer (UniFFI-generated)
  └─ DevTunnelsFFI.xcframework
       ↓
Rust Layer (rust/)
  ├─ lib.rs               — UniFFI exports + management API calls
  ├─ relay_client.rs      — (TODO) Client-side SSH relay using russh
  └─ Cargo.toml           — depends on tunnels crate (local vendor)
```

## How It Works

The Dev Tunnels protocol stack is: **WebSocket → SSH → port forwarding**.

For the **management** layer (list tunnels, auth):
1. **Authenticate** — GitHub device code flow → access token
2. **List tunnels** — `TunnelManagementClient.list_all_tunnels()` → tunnel metadata

For the **relay connection** layer (connect to a tunnel's port):
1. **Get relay URI** — From `TunnelRelayTunnelEndpoint.clientRelayUri`
2. **WebSocket connect** — Open WebSocket to relay URI with `tunnel-relay-client` subprotocol
3. **SSH session** — Start SSH client session over WebSocket stream (using `russh`)
4. **Port forwarding** — Open SSH channel for the target port
5. **AHP WebSocket** — Open AHP WebSocket over the forwarded port stream

## Project Structure

```
DevTunnelsBridge/
├── Package.swift                          # Swift Package manifest
├── AGENTS.md                              # This file
├── .gitignore                             # Ignores vendor/, .build/, target/, Frameworks/, Generated/
├── Sources/DevTunnelsBridge/              # Swift wrapper code
│   └── DevTunnelsBridge.swift             # Public API placeholder
├── Tests/DevTunnelsBridgeTests/           # Swift tests
│   └── DevTunnelsBridgeTests.swift        # Test placeholder
├── rust/                                  # Rust source
│   ├── Cargo.toml                         # Rust dependencies (tunnels via local vendor)
│   ├── src/lib.rs                         # FFI exports via UniFFI
│   └── uniffi.toml                        # UniFFI configuration
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
- UniFFI bindgen:
  ```bash
  cargo install uniffi-bindgen-swift
  ```

### Setup vendored dependencies

```bash
git clone --depth 1 https://github.com/microsoft/dev-tunnels.git vendor/dev-tunnels
```

### Build Rust (native, for development)

```bash
cd rust
cargo build
cargo test
```

### Build the Rust XCFramework (for iOS)

```bash
cd scripts
./build-xcframework.sh
```

This compiles the Rust code for ARM64 device and ARM64 simulator, generates Swift bindings via UniFFI, and packages everything as an XCFramework in `Frameworks/`.

### Use in AHPClient

In Xcode, add `DevTunnelsBridge` as a local package dependency:
File → Add Package → Add Local → select this directory.

## Reference

- **el (TUI client)**: `/Users/penlv/Code/Personal/el` — Node.js Dev Tunnels implementation to match
  - `src/protocol/tunnel-transport.ts` — Connection flow reference
  - `src/tunnel/discovery.ts` — Tunnel listing/resolution reference
  - `src/auth/tunnel-auth.ts` — Auth flow reference
- **Dev Tunnels SDK**: `microsoft/dev-tunnels` repo
  - `rs/` — Rust SDK (management + host relay)
  - `ts/src/connections/tunnelRelayTunnelClient.ts` — Reference client relay implementation (563 lines)
  - `rs/src/connections/ws.rs` — Reusable WebSocket utilities for Rust client implementation
- **UniFFI docs**: https://mozilla.github.io/uniffi-rs/

## Code Conventions

- Use Swift naming conventions (camelCase methods, PascalCase types)
- All public APIs must be `async` (tunnel operations are inherently async)
- Errors use typed Swift `Error` enums, not generic throws
- Keep FFI boundary thin — complex logic in Rust, ergonomic wrappers in Swift
- No external Swift dependencies (the package should be self-contained except for the XCFramework)

## Phased Implementation

1. ✅ **Scaffold** — Swift Package + Rust crate structure
2. ✅ **Rust spike** — Validated: management API works, compiles against `tunnels` crate
3. **UniFFI spike** — Generate Swift bindings, verify callable from Swift
4. **Core ops** — List tunnels via FFI, device code auth
5. **Relay client** — Implement `TunnelRelayTunnelClient` in Rust (SSH over WebSocket)
6. **Swift API** — High-level `TunnelClient` with async/await
7. **Integration** — Wire into AHPClient as `TunnelTransport`
