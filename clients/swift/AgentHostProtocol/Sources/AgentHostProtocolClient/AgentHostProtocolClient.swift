// AgentHostProtocolClient — single-host JSON-RPC client for the Agent Host Protocol.
//
// This module ships:
//   - `AHPTransport`: a pluggable async transport protocol (text/binary/parsed framing).
//   - `URLSessionWebSocketTransport`: default WebSocket implementation.
//   - `InMemoryTransport`: paired in-memory transport for tests.
//   - `AHPClient`: an actor that owns request/response correlation, subscription
//     fan-out, a top-level `events` tap, and connection-state changes.
//   - `AHPStateMirror`: a thin reducer façade for keeping an in-memory state copy.
//
// Multi-host abstractions, reconnect policy with backoff, generation-checked
// handles, observable façade, and `ClientIdStore` are deliberately *not* in
// this module — they belong on a higher layer (planned as a follow-up
// `MultiHostClient`).

import Foundation

/// Well-known resource URI for root-state subscriptions and snapshots.
///
/// The protocol-level value referenced throughout the spec, docs, and the
/// example app's initial subscriptions. Defined here as a constant so the
/// JSON-RPC client doesn't string-match the URI inline.
///
/// TODO(codegen): Source this from `AgentHostProtocol` once codegen exposes a
/// shared constant (TypeScript/Rust/Swift would all benefit).
public let RootResourceURI: String = "agenthost:/root"
