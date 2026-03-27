# AHPApp — SwiftUI Agent Host Protocol Client

An iOS SwiftUI application that connects to an [Agent Host Protocol](../../../docs/) server
over WebSocket, providing a native chat interface for interacting with AI agents.

> **Platform:** iOS 17+. Requires Xcode 15+ to build.

## Features

- **Multi-session sidebar** — Create, select, and delete chat sessions
- **Real-time streaming** — Watch responses stream in as the agent generates them
- **Tool call approval** — Approve or deny tool invocations with full parameter visibility
- **Reasoning display** — Expandable reasoning sections showing the model's thought process
- **Multi-agent support** — Pick from available agents and models on the server
- **Automatic reconnection** — Tracks server sequence for reconnect/replay

## Architecture

```
┌──────────────────────────────────────────────┐
│  SwiftUI Views                               │
│  (ContentView, ChatView, SidebarView, …)     │
│                    │                         │
│                    ▼                         │
│  AppStore (@Observable, @MainActor)          │
│  ┌─────────────────────────────────────────┐ │
│  │ rootState: RootState                    │ │
│  │ sessions: [URI: SessionState]           │ │
│  │ selectedSessionURI: String?             │ │
│  └────────────────────┬────────────────────┘ │
│                       │ reduce via           │
│                       │ AHPSessionReducer /  │
│                       │ rootReducer          │
│                       ▼                      │
│  AHPConnection (actor)                       │
│  ┌─────────────────────────────────────────┐ │
│  │ WebSocket → JSON-RPC                    │ │
│  │ Request/Response correlation             │ │
│  │ Action & Notification dispatch           │ │
│  └─────────────────────────────────────────┘ │
└──────────────────────────────────────────────┘
                       │
                WebSocket (JSON-RPC 2.0)
                       │
              ┌────────▼────────┐
              │  AHP Server     │
              │  (Electron /    │
              │   standalone)   │
              └─────────────────┘
```

### Key Components

| File | Purpose |
|------|---------|
| `AHPApp.swift` | `@main` entry point, creates the `WindowGroup` |
| `Store/AppStore.swift` | `@Observable` state container; holds `RootState`, per-session `SessionState`, and dispatches actions via the protocol's pure reducers |
| `Store/AHPConnection.swift` | `actor`-based WebSocket transport handling JSON-RPC requests, responses, action notifications, and protocol notifications |
| `Views/ContentView.swift` | Root `NavigationSplitView` (sidebar + detail) |
| `Views/SidebarView.swift` | Session list, connection controls, new-chat button |
| `Views/ChatView.swift` | Message list, input bar, turn rendering |
| `Views/ResponsePartView.swift` | Renders markdown, reasoning, tool calls, and content refs |
| `Views/AgentPicker.swift` | Agent/model selection sheet |
| `Views/WelcomeView.swift` | Landing screen when no session is selected |
| `Views/SettingsView.swift` | Server URL configuration (presented as a sheet) |

## Building

```bash
# From this directory
cd examples/swift/AHPApp

# Build (requires macOS + Xcode with iOS SDK)
swift build

# Or open in Xcode
open Package.swift
```

The app depends on the sibling `AgentHostProtocol` Swift package (referenced via
`../AgentHostProtocol` in `Package.swift`), which provides:

- Generated protocol types (state, actions, commands, notifications)
- Pure state reducers (`rootReducer`, `sessionReducer`, `AHPSessionReducer`)
- JSON-RPC message helpers (`AHPCommands`, `AHPClientNotifications`)

## Running

1. Start an AHP server (e.g. the Electron utility process or a standalone WebSocket server)
2. Build and run the app in the iOS Simulator or on a device
3. Tap **Connect** in the sidebar
4. Tap **New Chat** to create a session
5. Start chatting!

The server URL can be changed via the ⚙️ settings button in the toolbar.
