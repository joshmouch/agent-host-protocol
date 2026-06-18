// Generated from types/telemetry/registry.ts — do not edit

package com.microsoft.agenthostprotocol.generated

/**
 * Cross-client telemetry NAMES — the self-instrumentation contract shared by every
 * AHP client, generated from `types/telemetry/registry.ts`. Only the names are shared;
 * the OpenTelemetry tracer / meter wiring is hand-written per language.
 */
object AhpTelemetryNames {

    // ── Instrumentation scope ──
    /** Instrumentation-scope name used for every AHP self-instrumentation span and metric. */
    const val SOURCE: String = "Microsoft.AgentHostProtocol"

    // ── Span names ──
    /** Span covering a single JSON-RPC request, from send until it settles. */
    const val REQUEST_SPAN: String = "ahp.request"

    // ── Metric names ──
    /** Messages sent to the host, tagged by ahp.message.kind (request|notification). */
    const val MESSAGES_SENT: String = "ahp.client.messages.sent"
    /** Messages received from the host. */
    const val MESSAGES_RECEIVED: String = "ahp.client.messages.received"
    /** Round-trip duration of a JSON-RPC request, tagged by rpc.method and ahp.outcome (ok|error|cancelled|timeout). */
    const val REQUEST_DURATION: String = "ahp.client.request.duration"
    /** Requests awaiting a response. */
    const val REQUESTS_IN_FLIGHT: String = "ahp.client.requests.in_flight"
    /** Subscriptions registered with the client (decremented on unsubscribe or shutdown). */
    const val SUBSCRIPTIONS_ACTIVE: String = "ahp.client.subscriptions.active"
    /** Reconnect operations, tagged by outcome. */
    const val RECONNECTS: String = "ahp.client.reconnects"
    /** Buffered events evicted under back-pressure (drop-oldest), tagged by stream. */
    const val EVENTS_DROPPED: String = "ahp.client.events.dropped"
    /** Inbound frames that failed to decode and were skipped (protocol resync is the host’s responsibility). */
    const val FRAMES_MALFORMED: String = "ahp.client.frames.malformed"

    // ── Metric units ──
    /** Unit for the `ahp.client.messages.sent` metric. */
    const val MESSAGES_SENT_UNIT: String = "{message}"
    /** Unit for the `ahp.client.messages.received` metric. */
    const val MESSAGES_RECEIVED_UNIT: String = "{message}"
    /** Unit for the `ahp.client.request.duration` metric. */
    const val REQUEST_DURATION_UNIT: String = "ms"
    /** Unit for the `ahp.client.requests.in_flight` metric. */
    const val REQUESTS_IN_FLIGHT_UNIT: String = "{request}"
    /** Unit for the `ahp.client.subscriptions.active` metric. */
    const val SUBSCRIPTIONS_ACTIVE_UNIT: String = "{subscription}"
    /** Unit for the `ahp.client.reconnects` metric. */
    const val RECONNECTS_UNIT: String = "{reconnect}"
    /** Unit for the `ahp.client.events.dropped` metric. */
    const val EVENTS_DROPPED_UNIT: String = "{event}"
    /** Unit for the `ahp.client.frames.malformed` metric. */
    const val FRAMES_MALFORMED_UNIT: String = "{frame}"

    // ── Attribute keys ──
    /** RPC system identifier (OTel rpc.system); always "jsonrpc" for AHP. */
    const val ATTR_RPC_SYSTEM: String = "rpc.system"
    /** JSON-RPC method name the span/metric is scoped to (OTel rpc.method). */
    const val ATTR_RPC_METHOD: String = "rpc.method"
    /** Client-assigned JSON-RPC request id. */
    const val ATTR_REQUEST_ID: String = "ahp.request.id"
    /** Terminal outcome of a request or reconnect (ok|error|cancelled|timeout). */
    const val ATTR_OUTCOME: String = "ahp.outcome"
    /** Whether a sent message was a request or a notification. */
    const val ATTR_MESSAGE_KIND: String = "ahp.message.kind"
    /** Which event stream a dropped or observed event belongs to. */
    const val ATTR_STREAM: String = "ahp.stream"

    // ── Attribute values ──
    /** JSON-RPC — the only RPC system AHP uses. */
    const val RPC_SYSTEM_JSONRPC: String = "jsonrpc"
    /** The request or reconnect completed successfully. */
    const val OUTCOME_OK: String = "ok"
    /** The request or reconnect failed with an error response. */
    const val OUTCOME_ERROR: String = "error"
    /** The request was cancelled before it settled. */
    const val OUTCOME_CANCELLED: String = "cancelled"
    /** The request exceeded its configured timeout. */
    const val OUTCOME_TIMEOUT: String = "timeout"
    /** A JSON-RPC request (expects a response). */
    const val MESSAGE_KIND_REQUEST: String = "request"
    /** A JSON-RPC notification (fire-and-forget). */
    const val MESSAGE_KIND_NOTIFICATION: String = "notification"
    /** A per-resource subscription stream. */
    const val STREAM_SUBSCRIPTION: String = "subscription"
    /** The client-wide event stream. */
    const val STREAM_EVENT: String = "event"
    /** A state-snapshot stream. */
    const val STREAM_STATE: String = "state"
    /** A multi-host client's host-event delivery stream. */
    const val STREAM_HOST_EVENT: String = "host-event"
    /** A multi-host client's host-subscription delivery stream. */
    const val STREAM_HOST_SUBSCRIPTION: String = "host-subscription"
    /** A multi-host client's host-resource delivery stream. */
    const val STREAM_HOST_RESOURCE: String = "host-resource"
    /** A multi-host client's host-snapshot delivery stream. */
    const val STREAM_HOST_SNAPSHOT: String = "host-snapshot"
    /** A multi-host client's host-summaries delivery stream. */
    const val STREAM_HOST_SUMMARIES: String = "host-summaries"
}
