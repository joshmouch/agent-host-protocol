// Generated from types/*.ts — do not edit

import Foundation

// MARK: - Telemetry Names

/// Cross-client telemetry names — the self-instrumentation contract shared by every
/// AHP client, generated from `types/telemetry/registry.ts`. Only the names are shared;
/// the tracer / meter wiring is hand-written per platform.
public enum AhpTelemetryNames {

    // MARK: - Source
    /// Instrumentation-scope name used for every AHP self-instrumentation span and metric.
    public static let source = "Microsoft.AgentHostProtocol"

    // MARK: - Span Names
    /// Span covering a single JSON-RPC request, from send until it settles.
    public static let requestSpan = "ahp.request"

    // MARK: - Metric Names
    /// Messages sent to the host, tagged by ahp.message.kind (request|notification).
    public static let messagesSent = "ahp.client.messages.sent"
    /// Messages received from the host.
    public static let messagesReceived = "ahp.client.messages.received"
    /// Round-trip duration of a JSON-RPC request, tagged by rpc.method and ahp.outcome (ok|error|cancelled|timeout).
    public static let requestDuration = "ahp.client.request.duration"
    /// Requests awaiting a response.
    public static let requestsInFlight = "ahp.client.requests.in_flight"
    /// Subscriptions registered with the client (decremented on unsubscribe or shutdown).
    public static let subscriptionsActive = "ahp.client.subscriptions.active"
    /// Reconnect operations, tagged by outcome.
    public static let reconnects = "ahp.client.reconnects"
    /// Buffered events evicted under back-pressure (drop-oldest), tagged by stream.
    public static let eventsDropped = "ahp.client.events.dropped"
    /// Inbound frames that failed to decode and were skipped (protocol resync is the host’s responsibility).
    public static let framesMalformed = "ahp.client.frames.malformed"

    // MARK: - Metric Units
    /// Unit for the `ahp.client.messages.sent` metric.
    public static let messagesSentUnit = "{message}"
    /// Unit for the `ahp.client.messages.received` metric.
    public static let messagesReceivedUnit = "{message}"
    /// Unit for the `ahp.client.request.duration` metric.
    public static let requestDurationUnit = "ms"
    /// Unit for the `ahp.client.requests.in_flight` metric.
    public static let requestsInFlightUnit = "{request}"
    /// Unit for the `ahp.client.subscriptions.active` metric.
    public static let subscriptionsActiveUnit = "{subscription}"
    /// Unit for the `ahp.client.reconnects` metric.
    public static let reconnectsUnit = "{reconnect}"
    /// Unit for the `ahp.client.events.dropped` metric.
    public static let eventsDroppedUnit = "{event}"
    /// Unit for the `ahp.client.frames.malformed` metric.
    public static let framesMalformedUnit = "{frame}"

    // MARK: - Attribute Keys
    /// RPC system identifier (OTel rpc.system); always "jsonrpc" for AHP.
    public static let attrRpcSystem = "rpc.system"
    /// JSON-RPC method name the span/metric is scoped to (OTel rpc.method).
    public static let attrRpcMethod = "rpc.method"
    /// Client-assigned JSON-RPC request id.
    public static let attrRequestId = "ahp.request.id"
    /// Terminal outcome of a request or reconnect (ok|error|cancelled|timeout).
    public static let attrOutcome = "ahp.outcome"
    /// Whether a sent message was a request or a notification.
    public static let attrMessageKind = "ahp.message.kind"
    /// Which event stream a dropped or observed event belongs to.
    public static let attrStream = "ahp.stream"

    // MARK: - Attribute Values
    /// JSON-RPC — the only RPC system AHP uses.
    public static let rpcSystemJsonrpc = "jsonrpc"
    /// The request or reconnect completed successfully.
    public static let outcomeOk = "ok"
    /// The request or reconnect failed with an error response.
    public static let outcomeError = "error"
    /// The request was cancelled before it settled.
    public static let outcomeCancelled = "cancelled"
    /// The request exceeded its configured timeout.
    public static let outcomeTimeout = "timeout"
    /// A JSON-RPC request (expects a response).
    public static let messageKindRequest = "request"
    /// A JSON-RPC notification (fire-and-forget).
    public static let messageKindNotification = "notification"
    /// A per-resource subscription stream.
    public static let streamSubscription = "subscription"
    /// The client-wide event stream.
    public static let streamEvent = "event"
    /// A state-snapshot stream.
    public static let streamState = "state"
    /// A multi-host client's host-event delivery stream.
    public static let streamHostEvent = "host-event"
    /// A multi-host client's host-subscription delivery stream.
    public static let streamHostSubscription = "host-subscription"
    /// A multi-host client's host-resource delivery stream.
    public static let streamHostResource = "host-resource"
    /// A multi-host client's host-snapshot delivery stream.
    public static let streamHostSnapshot = "host-snapshot"
    /// A multi-host client's host-summaries delivery stream.
    public static let streamHostSummaries = "host-summaries"

}
