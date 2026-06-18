// Generated from types/*.ts — do not edit.
//
// Regenerate with: npm run generate:go

package ahptypes

// Cross-client telemetry NAMES — the self-instrumentation contract shared by every
// AHP client, generated from types/telemetry/registry.ts. Only the names are shared;
// the tracer / meter wiring is hand-written per language.

const (
	// Instrumentation-scope name used for every AHP self-instrumentation span and metric.
	TelemetrySource = "Microsoft.AgentHostProtocol"

	// Span names.
	// Span covering a single JSON-RPC request, from send until it settles.
	RequestSpan = "ahp.request"

	// Metric names.
	// Messages sent to the host, tagged by ahp.message.kind (request|notification).
	MessagesSentMetric = "ahp.client.messages.sent"
	// Messages received from the host.
	MessagesReceivedMetric = "ahp.client.messages.received"
	// Round-trip duration of a JSON-RPC request, tagged by rpc.method and ahp.outcome (ok|error|cancelled|timeout).
	RequestDurationMetric = "ahp.client.request.duration"
	// Requests awaiting a response.
	RequestsInFlightMetric = "ahp.client.requests.in_flight"
	// Subscriptions registered with the client (decremented on unsubscribe or shutdown).
	SubscriptionsActiveMetric = "ahp.client.subscriptions.active"
	// Reconnect operations, tagged by outcome.
	ReconnectsMetric = "ahp.client.reconnects"
	// Buffered events evicted under back-pressure (drop-oldest), tagged by stream.
	EventsDroppedMetric = "ahp.client.events.dropped"
	// Inbound frames that failed to decode and were skipped (protocol resync is the host’s responsibility).
	FramesMalformedMetric = "ahp.client.frames.malformed"

	// Metric units.
	// Unit for the ahp.client.messages.sent metric.
	MessagesSentUnit = "{message}"
	// Unit for the ahp.client.messages.received metric.
	MessagesReceivedUnit = "{message}"
	// Unit for the ahp.client.request.duration metric.
	RequestDurationUnit = "ms"
	// Unit for the ahp.client.requests.in_flight metric.
	RequestsInFlightUnit = "{request}"
	// Unit for the ahp.client.subscriptions.active metric.
	SubscriptionsActiveUnit = "{subscription}"
	// Unit for the ahp.client.reconnects metric.
	ReconnectsUnit = "{reconnect}"
	// Unit for the ahp.client.events.dropped metric.
	EventsDroppedUnit = "{event}"
	// Unit for the ahp.client.frames.malformed metric.
	FramesMalformedUnit = "{frame}"

	// Attribute keys.
	// RPC system identifier (OTel rpc.system); always "jsonrpc" for AHP.
	AttrRpcSystem = "rpc.system"
	// JSON-RPC method name the span/metric is scoped to (OTel rpc.method).
	AttrRpcMethod = "rpc.method"
	// Client-assigned JSON-RPC request id.
	AttrRequestId = "ahp.request.id"
	// Terminal outcome of a request or reconnect (ok|error|cancelled|timeout).
	AttrOutcome = "ahp.outcome"
	// Whether a sent message was a request or a notification.
	AttrMessageKind = "ahp.message.kind"
	// Which event stream a dropped or observed event belongs to.
	AttrStream = "ahp.stream"

	// Attribute values.
	// JSON-RPC — the only RPC system AHP uses.
	RpcSystemJsonrpc = "jsonrpc"
	// The request or reconnect completed successfully.
	OutcomeOk = "ok"
	// The request or reconnect failed with an error response.
	OutcomeError = "error"
	// The request was cancelled before it settled.
	OutcomeCancelled = "cancelled"
	// The request exceeded its configured timeout.
	OutcomeTimeout = "timeout"
	// A JSON-RPC request (expects a response).
	MessageKindRequest = "request"
	// A JSON-RPC notification (fire-and-forget).
	MessageKindNotification = "notification"
	// A per-resource subscription stream.
	StreamSubscription = "subscription"
	// The client-wide event stream.
	StreamEvent = "event"
	// A state-snapshot stream.
	StreamState = "state"
	// A multi-host client's host-event delivery stream.
	StreamHostEvent = "host-event"
	// A multi-host client's host-subscription delivery stream.
	StreamHostSubscription = "host-subscription"
	// A multi-host client's host-resource delivery stream.
	StreamHostResource = "host-resource"
	// A multi-host client's host-snapshot delivery stream.
	StreamHostSnapshot = "host-snapshot"
	// A multi-host client's host-summaries delivery stream.
	StreamHostSummaries = "host-summaries"
)
