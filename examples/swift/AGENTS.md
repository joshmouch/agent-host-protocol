# Swift Examples — Agent Guide

## Overview

This directory contains two Swift packages for the Agent Host Protocol (AHP):

1. **AgentHostProtocol** — A pure Swift library (no external dependencies) providing auto-generated types, actions, and reducers for the protocol. Targets iOS 16+, macOS 13+, Swift 5.9+.
2. **AHPClient** — An example iOS app (Xcode project) demonstrating a full AHP client with WebSocket transport, state synchronization, reconnection, and a SwiftUI chat UI.

## Code Generation

Types in `AgentHostProtocol/Sources/AgentHostProtocol/Generated/` are **auto-generated** from the TypeScript definitions in `types/`. Do not edit these files directly.

To regenerate after protocol changes:

```bash
npm run generate:swift    # runs: tsx scripts/generate.ts --swift
```

Generated files: `State`, `Commands`, `Actions`, `Errors`, `Messages`, `Notifications` — all suffixed `.generated.swift`.

## AgentHostProtocol Library

### Key Types

- **`RootState`** — Top-level state: agents list + active sessions.
- **`SessionState`** — Per-session conversation state (turns, tool calls, lifecycle).
- **`AgentInfo` / `SessionModelInfo`** — Agent capabilities and available models.
- **`ActionEnvelope`** — Server-sent action with `serverSeq` (global monotonic counter) and resource URI.
- **Command params/results** — `InitializeParams`, `ReconnectParams`, `CreateSessionParams`, `SubscribeParams`, etc.

### Reducer Pattern

Inspired by Swift Composable Architecture (TCA). Key abstractions:

- **`Reducer` protocol** — Pure function: `reduce(into state: inout State, action: Action)`.
- **`AHPRootReducer`** — Handles root-level actions (agents changed, session added/removed).
- **`AHPSessionReducer`** — Handles per-session actions (deltas, tool calls, turn lifecycle).
- **Composable** via `CombinedReducer` and `AnyReducer` type erasure.

Reducers are pure functions — replaying actions in `serverSeq` order on any prior state snapshot produces identical results. This is critical for the reconnection protocol.

## AHPClient App

### Architecture

```
AHPClientApp (@main, scenePhase monitoring)
  └─ AppStore (@Observable, @MainActor)
       ├─ GitHubAuthManager (OAuth + Keychain)
       ├─ HostDiscoveryService (Codamente registry client)
       ├─ CodespaceService (GitHub Codespaces API)
       ├─ AHPConnection (actor — WebSocket JSON-RPC transport)
       ├─ RootState + [SessionState] (protocol state, mutated by reducers)
       ├─ ServerStorage (persisted server configs)
       └─ Views (SwiftUI)
            ├─ SignInView (GitHub OAuth sign-in)
            ├─ HostPickerView (host discovery + codespace provisioning)
            ├─ CodespaceSetupView (repo picker + provisioning progress)
            ├─ SidebarView, ChatView, etc. (existing chat UI)
```

### Connection Flow

The app supports three ways to connect to an agent host:

1. **Remote Hosts (Codamente Registry)** — Lists hosts registered by VS Code's Codamente
   extension. User picks a host → `POST /api/hosts/:id/connect` → gets `tunnelUrl` +
   `connectionToken` → connects via AHPConnection.

2. **New Codespace** — Provisions a GitHub Codespace for a chosen repository via the
   Codespaces REST API. The Codespace's `postStartCommand` starts the agent host and
   registers it with the Codamente registry. The app polls until the host appears, then
   connects.

3. **Manual Server** — Direct WebSocket connection to a local or remote AHP server
   (the original flow).

### Authentication

`GitHubAuthManager` handles GitHub OAuth via `ASWebAuthenticationSession`:
- Scopes: `read:user` (for registry auth) + `codespace` (for provisioning) + `repo`
- Token stored in iOS Keychain (persists across launches)
- Same token used for both the Codamente registry API and the GitHub Codespaces API
- Users can skip auth and use manual server connections only

### Host Discovery

`HostDiscoveryService` wraps calls to the Codamente host registry server:
- `listHosts(token:)` → `GET /api/hosts` → `[RemoteHost]`
- `getConnectInfo(hostId:token:)` → `POST /api/hosts/:id/connect` → `HostConnectInfo`
- Hosts are registered by the Codamente VS Code extension (desktop mode)

### Codespace Provisioning

`CodespaceService` manages the GitHub Codespaces API lifecycle:
- `searchRepositories(query:token:)` — Search GitHub repos
- `createCodespace(owner:repo:ref:token:)` — `POST /repos/{owner}/{repo}/codespaces`
- `waitForCodespace(name:token:)` — Poll until `state == "Available"`
- `stopCodespace(name:token:)` / `deleteCodespace(name:token:)` — Cleanup

### AppStore

Central state container. All mutations flow through reducer functions (`applySnapshot`, `handleAction`). Key responsibilities:

- **Authentication** — `authManager` for GitHub OAuth, `skipAuth()` for manual mode
- **Host discovery** — `hostDiscovery` service, `connectToRemoteHost()`
- **Codespace provisioning** — `codespaceService` for GitHub Codespaces API
- **Connection lifecycle** — `connect()`, `disconnect()`, `reconnect()`, `reconnectIfNeeded()`
- **Session management** — `createSession()`, `disposeSession()`, `selectSession()`
- **State sync** — Receives `ActionEnvelope`s from the connection, applies via reducers
- **Reconnection** — Tracks `lastSeenServerSeq` and subscribed URIs; sends `reconnect` command on resume. Server responds with either **replay** (missed actions) or **snapshot** (fresh state).

### AHPConnection

An `actor` wrapping `URLSessionWebSocketTask` for thread-safe JSON-RPC over WebSocket:

- Request/response correlation via sequential message IDs and `CheckedContinuation`
- `onAction` callback delivers `ActionEnvelope`s to AppStore on `@MainActor`
- `onUnexpectedDisconnect` fires when the receive loop breaks on error
- `canReconnect` — true when a prior connection established `serverSeq` + subscriptions

### Reconnection Flow

When the phone wakes from background or the WebSocket drops unexpectedly:

1. `scenePhase → .active` triggers `reconnectIfNeeded()` (or `onUnexpectedDisconnect`)
2. Client sends `reconnect(clientId, lastSeenServerSeq, subscriptions)` to server
3. Server responds with **Replay** (array of missed `ActionEnvelope`s) or **Snapshot** (full state per subscribed URI if gap is too large)
4. `AppStore` applies the result via reducers, bringing local state up to date
5. A floating progress bar shows "Reconnecting…" in the active chat view

`serverSeq` is a **global** monotonic counter across all sessions. The server filters replays to only the client's subscribed URIs.

### UI Structure

- **SignInView** — Full-screen GitHub OAuth sign-in. Users can skip to use manual connections only.
- **HostPickerView** — Home screen with three connection options: remote hosts from the Codamente registry, new Codespace provisioning, and manual server connections.
- **CodespaceSetupView** — Repository search/picker with provisioning progress (creating → waiting for codespace → waiting for agent host → connecting).
- **ContentView** — Routes between SignInView → HostPickerView → main chat experience based on auth and connection state.
- **SidebarView** — Session list with `.searchable()` (iOS 26 liquid glass bottom bar), grouped by time or working directory. `NewSessionButton` opens `AgentPicker` modal. Includes "Switch Host" menu option.
- **ChatView** — VStack-based message list with bottom-anchored scroll, scroll-to-bottom button (glass effect on iOS 26), reconnect progress bar, and unified input bar (send/stop).
- **AgentPicker** — Form with `Picker` controls for agent, model, and a working directory text field.
- **ResponsePartView** — Renders markdown (via `AttributedString`), tool call cards (tap → detail modal with inputs/outputs), reasoning blocks, and content references.

### iOS 26 Adaptations

The app uses `#available(iOS 26.0, *)` checks for:
- `.glassEffect()` on the scroll-to-bottom button
- `.searchable()` with `.toolbar` placement + `DefaultToolbarItem(kind: .search, placement: .bottomBar)`
- New Session button in `.bottomBar` toolbar
- Fallbacks to `.ultraThinMaterial` and `.navigationBarDrawer` on older iOS

### Testing

- **AHPClientTests** — Reconnection state tests: snapshot restore, replay ordering, empty replay, multi-resource snapshot, live action application.
- **AgentHostProtocol Tests** — Reducer unit tests (root reducer, session reducer, native reducer).

### Build & Run

Open `AHPClient/AHPClient.xcodeproj` in Xcode. The project references `AgentHostProtocol` as a local Swift package dependency. Code signing requires a `Signing.local.xcconfig` file (see `Config/Signing.local.xcconfig.example`).

For development, `AHPClient` uses a native `NWConnection` WebSocket transport instead of `URLSessionWebSocketTask`,  avoiding `URLSession` ATS enforcement for direct `ws://` development targets such as local LAN addresses or Tailscale tailnet IPs. Public or internet-exposed deployments should still prefer `wss://`.

#### GitHub OAuth Setup

To use host discovery and Codespace provisioning, create a GitHub OAuth App:
1. Go to GitHub Settings → Developer Settings → OAuth Apps → New OAuth App.
2. Set the callback URL to `ahpclient://oauth/callback`.
3. Replace `GITHUB_CLIENT_ID` in `GitHubAuthManager.swift` with your app's Client ID.
4. For production, implement the token exchange on a backend server that holds the client secret.
