// Generated from types/*.ts — do not edit.
//
// Regenerate with: npm run generate:rust

#![allow(missing_docs)]

// ─── Instrumentation scope ───────────────────────────────────────────────

/// Instrumentation-scope name used for every AHP self-instrumentation span and metric.
pub const TELEMETRY_SOURCE: &str = "Microsoft.AgentHostProtocol";

// ─── Span names ──────────────────────────────────────────────────────────

/// Span covering a single JSON-RPC request, from send until it settles.
pub const REQUEST_SPAN: &str = "ahp.request";

// ─── Metric names ────────────────────────────────────────────────────────

/// Messages sent to the host, tagged by ahp.message.kind (request|notification).
pub const MESSAGES_SENT: &str = "ahp.client.messages.sent";
/// Messages received from the host.
pub const MESSAGES_RECEIVED: &str = "ahp.client.messages.received";
/// Round-trip duration of a JSON-RPC request, tagged by rpc.method and ahp.outcome (ok|error|cancelled|timeout).
pub const REQUEST_DURATION: &str = "ahp.client.request.duration";
/// Requests awaiting a response.
pub const REQUESTS_IN_FLIGHT: &str = "ahp.client.requests.in_flight";
/// Subscriptions registered with the client (decremented on unsubscribe or shutdown).
pub const SUBSCRIPTIONS_ACTIVE: &str = "ahp.client.subscriptions.active";
/// Reconnect operations, tagged by outcome.
pub const RECONNECTS: &str = "ahp.client.reconnects";
/// Buffered events evicted under back-pressure (drop-oldest), tagged by stream.
pub const EVENTS_DROPPED: &str = "ahp.client.events.dropped";
/// Inbound frames that failed to decode and were skipped (protocol resync is the host’s responsibility).
pub const FRAMES_MALFORMED: &str = "ahp.client.frames.malformed";

// ─── Metric units ────────────────────────────────────────────────────────

/// Unit for the `ahp.client.messages.sent` metric.
pub const MESSAGES_SENT_UNIT: &str = "{message}";
/// Unit for the `ahp.client.messages.received` metric.
pub const MESSAGES_RECEIVED_UNIT: &str = "{message}";
/// Unit for the `ahp.client.request.duration` metric.
pub const REQUEST_DURATION_UNIT: &str = "ms";
/// Unit for the `ahp.client.requests.in_flight` metric.
pub const REQUESTS_IN_FLIGHT_UNIT: &str = "{request}";
/// Unit for the `ahp.client.subscriptions.active` metric.
pub const SUBSCRIPTIONS_ACTIVE_UNIT: &str = "{subscription}";
/// Unit for the `ahp.client.reconnects` metric.
pub const RECONNECTS_UNIT: &str = "{reconnect}";
/// Unit for the `ahp.client.events.dropped` metric.
pub const EVENTS_DROPPED_UNIT: &str = "{event}";
/// Unit for the `ahp.client.frames.malformed` metric.
pub const FRAMES_MALFORMED_UNIT: &str = "{frame}";

// ─── Attribute keys ──────────────────────────────────────────────────────

/// RPC system identifier (OTel rpc.system); always "jsonrpc" for AHP.
pub const ATTR_RPC_SYSTEM: &str = "rpc.system";
/// JSON-RPC method name the span/metric is scoped to (OTel rpc.method).
pub const ATTR_RPC_METHOD: &str = "rpc.method";
/// Client-assigned JSON-RPC request id.
pub const ATTR_REQUEST_ID: &str = "ahp.request.id";
/// Terminal outcome of a request or reconnect (ok|error|cancelled|timeout).
pub const ATTR_OUTCOME: &str = "ahp.outcome";
/// Whether a sent message was a request or a notification.
pub const ATTR_MESSAGE_KIND: &str = "ahp.message.kind";
/// Which event stream a dropped or observed event belongs to.
pub const ATTR_STREAM: &str = "ahp.stream";

// ─── Attribute values ────────────────────────────────────────────────────

/// JSON-RPC — the only RPC system AHP uses.
pub const RPC_SYSTEM_JSONRPC: &str = "jsonrpc";
/// The request or reconnect completed successfully.
pub const OUTCOME_OK: &str = "ok";
/// The request or reconnect failed with an error response.
pub const OUTCOME_ERROR: &str = "error";
/// The request was cancelled before it settled.
pub const OUTCOME_CANCELLED: &str = "cancelled";
/// The request exceeded its configured timeout.
pub const OUTCOME_TIMEOUT: &str = "timeout";
/// A JSON-RPC request (expects a response).
pub const MESSAGE_KIND_REQUEST: &str = "request";
/// A JSON-RPC notification (fire-and-forget).
pub const MESSAGE_KIND_NOTIFICATION: &str = "notification";
/// A per-resource subscription stream.
pub const STREAM_SUBSCRIPTION: &str = "subscription";
/// The client-wide event stream.
pub const STREAM_EVENT: &str = "event";
/// A state-snapshot stream.
pub const STREAM_STATE: &str = "state";
/// A multi-host client's host-event delivery stream.
pub const STREAM_HOST_EVENT: &str = "host-event";
/// A multi-host client's host-subscription delivery stream.
pub const STREAM_HOST_SUBSCRIPTION: &str = "host-subscription";
/// A multi-host client's host-resource delivery stream.
pub const STREAM_HOST_RESOURCE: &str = "host-resource";
/// A multi-host client's host-snapshot delivery stream.
pub const STREAM_HOST_SNAPSHOT: &str = "host-snapshot";
/// A multi-host client's host-summaries delivery stream.
pub const STREAM_HOST_SUMMARIES: &str = "host-summaries";
