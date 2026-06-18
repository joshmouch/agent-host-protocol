/**
 * Telemetry Registry — the single source of truth for the self-instrumentation
 * span / metric / attribute names THIS .NET client emits about its own
 * operation, so the names live in one place and the generated
 * `AhpTelemetryNames` holder cannot drift from them.
 *
 * This registry is .NET-client-private (it lives under the client tree, not in
 * `types/`): AHP has not specced a cross-client self-instrumentation names
 * contract, and a maintainer declined to take one for now (issue #239, kept on
 * the backlog). The shape deliberately mirrors the protocol enums (e.g.
 * `ChangesetOperationStatus`): each name is a **string enum member**, so its
 * value and its `/** ... *\/` description live together and the generator
 * extracts the description the same way it does for every other enum
 * (`enumDecl.getMembers()` + `member.getJsDocs()`). If AHP ever specs a shared
 * cross-client contract, this file moves back to `types/telemetry/registry.ts`
 * and the other-language generators wire to it; only the NAMES would be
 * shared, the instrumentation LOGIC staying hand-written per language.
 *
 * This is client SELF-instrumentation, distinct from the protocol's
 * "OpenTelemetry over AHP" channel (server -> client OTLP delivery).
 *
 * @module telemetry/registry
 */

/** Instrumentation-scope name used for every AHP self-instrumentation span and metric. */
export const TELEMETRY_SOURCE = 'Microsoft.AgentHostProtocol';

/** Span names. One span per JSON-RPC request, named `${request} {method}`. */
export enum TelemetrySpan {
  /** Span covering a single JSON-RPC request, from send until it settles. */
  Request = 'ahp.request',
}

/**
 * Metric instrument names (lowercase-dotted per OTel convention). Units are
 * carried separately in {@link TELEMETRY_METRIC_UNITS}.
 */
export enum TelemetryMetric {
  /** Messages sent to the host, tagged by ahp.message.kind (request|notification). */
  MessagesSent = 'ahp.client.messages.sent',
  /** Messages received from the host. */
  MessagesReceived = 'ahp.client.messages.received',
  /** Round-trip duration of a JSON-RPC request, tagged by rpc.method and ahp.outcome (ok|error|cancelled|timeout). */
  RequestDuration = 'ahp.client.request.duration',
  /** Requests awaiting a response. */
  RequestsInFlight = 'ahp.client.requests.in_flight',
  /** Subscriptions registered with the client (decremented on unsubscribe or shutdown). */
  SubscriptionsActive = 'ahp.client.subscriptions.active',
  /** Reconnect operations, tagged by outcome. */
  Reconnects = 'ahp.client.reconnects',
  /** Buffered events evicted under back-pressure (drop-oldest), tagged by stream. */
  EventsDropped = 'ahp.client.events.dropped',
  /** Inbound frames that failed to decode and were skipped (protocol resync is the host’s responsibility). */
  FramesMalformed = 'ahp.client.frames.malformed',
}

/** OTel unit annotation for each metric. Trivial metadata, keyed by metric (no doc needed). */
export const TELEMETRY_METRIC_UNITS: Record<TelemetryMetric, string> = {
  [TelemetryMetric.MessagesSent]: '{message}',
  [TelemetryMetric.MessagesReceived]: '{message}',
  [TelemetryMetric.RequestDuration]: 'ms',
  [TelemetryMetric.RequestsInFlight]: '{request}',
  [TelemetryMetric.SubscriptionsActive]: '{subscription}',
  [TelemetryMetric.Reconnects]: '{reconnect}',
  [TelemetryMetric.EventsDropped]: '{event}',
  [TelemetryMetric.FramesMalformed]: '{frame}',
};

/** Attribute (tag) keys. `rpc.*` follow the OTel RPC semantic conventions. */
export enum TelemetryAttribute {
  /** RPC system identifier (OTel rpc.system); always "jsonrpc" for AHP. */
  RpcSystem = 'rpc.system',
  /** JSON-RPC method name the span/metric is scoped to (OTel rpc.method). */
  RpcMethod = 'rpc.method',
  /** Client-assigned JSON-RPC request id. */
  RequestId = 'ahp.request.id',
  /** Terminal outcome of a request or reconnect (ok|error|cancelled|timeout). */
  Outcome = 'ahp.outcome',
  /** Whether a sent message was a request or a notification. */
  MessageKind = 'ahp.message.kind',
  /** Which event stream a dropped or observed event belongs to. */
  Stream = 'ahp.stream',
}

/** `rpc.system` values. */
export enum TelemetryRpcSystem {
  /** JSON-RPC — the only RPC system AHP uses. */
  Jsonrpc = 'jsonrpc',
}

/**
 * `ahp.outcome` values. NOTE: this is the SINGLE outcome vocabulary — requests
 * AND reconnects both use it. The protocol models a reconnect as
 * success-or-rejected (a `ReconnectResult` or a rejected JSON-RPC request), the
 * same ok/error dichotomy as a request, so a reconnect tags `ok`/`error`, not a
 * separate `success`/`failure` set.
 */
export enum TelemetryOutcome {
  /** The request or reconnect completed successfully. */
  Ok = 'ok',
  /** The request or reconnect failed with an error response. */
  Error = 'error',
  /** The request was cancelled before it settled. */
  Cancelled = 'cancelled',
  /** The request exceeded its configured timeout. */
  Timeout = 'timeout',
}

/** `ahp.message.kind` values. */
export enum TelemetryMessageKind {
  /** A JSON-RPC request (expects a response). */
  Request = 'request',
  /** A JSON-RPC notification (fire-and-forget). */
  Notification = 'notification',
}

/**
 * `ahp.stream` values. The `host-*` members identify the per-stream
 * dropped-event channels a multi-host client (e.g. the .NET client) fans the
 * host's own notifications across; they are enumerated attribute VALUES, not
 * OTel instrument names, so the hyphenated spelling is intentional and
 * idiomatic (cf. OTel attribute values like `http.request.method=GET`).
 */
export enum TelemetryStream {
  /** A per-resource subscription stream. */
  Subscription = 'subscription',
  /** The client-wide event stream. */
  Event = 'event',
  /** A state-snapshot stream. */
  State = 'state',
  /** A multi-host client's host-event delivery stream. */
  HostEvent = 'host-event',
  /** A multi-host client's host-subscription delivery stream. */
  HostSubscription = 'host-subscription',
  /** A multi-host client's host-resource delivery stream. */
  HostResource = 'host-resource',
  /** A multi-host client's host-snapshot delivery stream. */
  HostSnapshot = 'host-snapshot',
  /** A multi-host client's host-summaries delivery stream. */
  HostSummaries = 'host-summaries',
}
