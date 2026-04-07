# DevTunnelsBridge — Agent Guide

> This file is the single source of truth for any AI agent working on this package.
> Read this **first** before making changes.

## What is DevTunnelsBridge?

DevTunnelsBridge is a Swift package that provides Dev Tunnels connectivity for iOS/macOS apps. It wraps the Microsoft Dev Tunnels **Rust SDK** via UniFFI to expose tunnel discovery, authentication, and port forwarding to Swift.

The primary consumer is the **AHPClient** iOS app (in `../AHPClient/`), which uses this to connect to AHP servers running behind Dev Tunnels.

## Architecture

```
Swift Layer (DevTunnelsBridge)
  ├─ TunnelClient         — High-level async Swift API
  ├─ TunnelInfo           — Tunnel metadata (name, online status, ports)
  ├─ TunnelAuth           — GitHub device code auth flow
  └─ TunnelStream         — Bidirectional stream to forwarded port
       ↓
FFI Layer (UniFFI-generated)
  └─ DevTunnelsFFI.xcframework
       ↓
Rust Layer (rust/)
  ├─ lib.rs               — UniFFI exports
  ├─ tunnel_ops.rs        — Tunnel list, connect, auth operations
  └─ Cargo.toml           — depends on dev-tunnels crate
```

## How It Works

The Dev Tunnels protocol stack is: **WebSocket → SSH → port forwarding**. The Rust SDK handles all of this internally. From Swift's perspective:

1. **Authenticate** — GitHub device code flow → access token
2. **List tunnels** — Query Dev Tunnels management API → tunnel metadata
3. **Connect** — Relay client connects via SSH over WebSocket (internal to SDK)
4. **Forward port** — Get a bidirectional stream to the forwarded port
5. **WebSocket** — The AHPClient opens its own WebSocket over this stream

## Project Structure

```
DevTunnelsBridge/
├── Package.swift                          # Swift Package manifest
├── AGENTS.md                              # This file
├── Sources/DevTunnelsBridge/              # Swift wrapper code
│   └── DevTunnelsBridge.swift             # Public API placeholder
├── Tests/DevTunnelsBridgeTests/           # Swift tests
│   └── DevTunnelsBridgeTests.swift        # Test placeholder
├── rust/                                  # Rust source
│   ├── Cargo.toml                         # Rust dependencies
│   ├── src/lib.rs                         # FFI exports via UniFFI
│   └── uniffi.toml                        # UniFFI configuration
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

### Build the Rust XCFramework

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
- **Dev Tunnels Rust SDK**: `@aspect-build/rules_rust` or `dev-tunnels` crate
- **UniFFI docs**: https://mozilla.github.io/uniffi-rs/

## Code Conventions

- Use Swift naming conventions (camelCase methods, PascalCase types)
- All public APIs must be `async` (tunnel operations are inherently async)
- Errors use typed Swift `Error` enums, not generic throws
- Keep FFI boundary thin — complex logic in Rust, ergonomic wrappers in Swift
- No external Swift dependencies (the package should be self-contained except for the XCFramework)

## Phased Implementation

1. **Spike** — Validate Rust crate works, UniFFI generates callable Swift
2. **Core ops** — List tunnels, connect to tunnel, device code auth
3. **Swift API** — High-level `TunnelClient` with async/await
4. **Integration** — Wire into AHPClient as `TunnelTransport`
