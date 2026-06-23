// Generated from types/*.ts — do not edit.
//
// Regenerate with: npm run generate:go

package ahptypes

import (
	"encoding/json"
)

// Reference the encoding/json import to keep gofmt -d from
// stripping it when a generated file has no struct that mentions
// json.RawMessage directly (rare but possible). Compiled out.
var _ = json.RawMessage(nil)

// ─── Enums ────────────────────────────────────────────────────────────

// Reason why authentication is required.
type AuthRequiredReason string

const (
	// The client has not yet authenticated for the resource
	AuthRequiredReasonRequired AuthRequiredReason = "required"
	// A previously valid token has expired or been revoked
	AuthRequiredReasonExpired AuthRequiredReason = "expired"
)

// Lifecycle phase of a single download.
type DownloadPhase string

const (
	// The download has begun; no bytes received yet.
	DownloadPhaseStarted DownloadPhase = "started"
	// A throttled progress sample with bytes received so far.
	DownloadPhaseProgress DownloadPhase = "progress"
	// Terminal success frame; the resource is fully downloaded.
	DownloadPhaseCompleted DownloadPhase = "completed"
	// Terminal failure frame; see {@link DownloadProgressParams.error}.
	DownloadPhaseFailed DownloadPhase = "failed"
)

// ─── Notification Payloads ────────────────────────────────────────────

// Broadcast to all clients subscribed to the root channel when a new session
// is created.
type SessionAddedParams struct {
	// Channel URI this notification belongs to (the root channel)
	Channel URI `json:"channel"`
	// Summary of the new session
	Summary SessionSummary `json:"summary"`
}

// Broadcast to all clients subscribed to the root channel when a session is
// disposed.
type SessionRemovedParams struct {
	// Channel URI this notification belongs to (the root channel)
	Channel URI `json:"channel"`
	// URI of the removed session
	Session URI `json:"session"`
}

// Broadcast to all clients subscribed to the root channel when an existing
// session's summary changes (title, status, `modifiedAt`, model, working
// directory, read/done state, or diff statistics).
//
// This notification lets clients that maintain a cached session list — for
// example, the result of a previous `listSessions()` call — stay in sync with
// in-flight sessions without having to subscribe to every session URI
// individually. It is complementary to, not a replacement for,
// `root/sessionAdded` and `root/sessionRemoved`: those signal lifecycle
// (creation/disposal), while this signals summary-level mutations on an
// already-known session.
//
// Semantics:
//
//   - Only fields present in `changes` have new values; omitted fields are
//     unchanged on the client's cached summary.
//   - Identity fields (`resource`, `provider`, `createdAt`) never change and
//     are not carried.
//   - Like all protocol notifications, this is ephemeral: it is **not**
//     replayed on reconnect. On reconnect, clients should re-fetch the full
//     catalog via `listSessions()` as usual.
//   - The server SHOULD emit this notification whenever any mutable field on
//     {@link SessionSummary | `SessionSummary`} changes for a session the
//     server has surfaced via `listSessions()` or `root/sessionAdded`.
//     Servers MAY coalesce or debounce updates for noisy fields (for example,
//     `modifiedAt` bumps while a turn is streaming) at their discretion.
//   - Clients that have no cached entry for `session` MAY ignore the
//     notification; it is not a substitute for `root/sessionAdded`.
type SessionSummaryChangedParams struct {
	// Channel URI this notification belongs to (the root channel)
	Channel URI `json:"channel"`
	// URI of the session whose summary changed
	Session URI `json:"session"`
	// Mutable summary fields that changed; omitted fields are unchanged.
	//
	// Identity fields (`resource`, `provider`, `createdAt`) never change and
	// MUST be omitted by senders; receivers SHOULD ignore them if present.
	Changes PartialSessionSummary `json:"changes"`
}

// Broadcast on the root channel while the host downloads a resource on the
// client's behalf — typically a multi-MB artifact fetched lazily the first time
// it is needed (today: an agent's native SDK/runtime, `kind: 'agent-sdk'`). Lets
// clients show a progress indicator instead of a silent multi-second hang.
//
// The notification is intentionally **resource-agnostic** so the same channel
// can report future downloads (additional agent runtimes, plugins, models, …)
// without a new method. The `kind` discriminant categorizes the resource and
// `resourceId` identifies it within that kind; clients that don't care can show
// a single generic indicator driven by `displayName` + the byte counts.
//
// This is **host-level**, not session state: the artifact is shared across every
// consumer and the host deduplicates concurrent fetches into one download (one
// `downloadId`). The optional `session` field names the session whose action
// triggered the fetch, purely as context — a client MAY attribute the progress
// to that session's row, or show a single global indicator and ignore it.
//
// Semantics:
//
//   - Frames for one download share a stable `downloadId`. The first frame a
//     client observes for a `downloadId` begins the indicator even if it is not
//     `phase: 'started'` (a client that connects mid-download may miss the
//     `started` frame).
//   - `receivedBytes` is monotonically non-decreasing within a `downloadId`.
//     `totalBytes` is present only when the host knows the size up front
//     (e.g. a `Content-Length`); when absent the client SHOULD show an
//     indeterminate indicator.
//   - Exactly one terminal frame (`phase: 'completed'` or `'failed'`) ends a
//     download. `error` carries a short, non-localized reason on failure.
//   - Like all notifications this is ephemeral and is **not** replayed on
//     reconnect. A client that never receives a terminal frame (the download
//     finished while it was disconnected) SHOULD expire the indicator after an
//     idle timeout.
//   - The brand noun is carried in `displayName`; clients own the surrounding
//     (localized) template, e.g. `"Downloading {displayName}… {pct}%"`.
type DownloadProgressParams struct {
	// Channel URI this notification belongs to (the root channel)
	Channel URI `json:"channel"`
	// Stable id for one download. Coalesces the frames of a single fetch and
	// distinguishes concurrent downloads (e.g. two resources at once).
	DownloadId string `json:"downloadId"`
	// Category of resource being downloaded. An open string (not a closed enum)
	// so new resource types can be reported without a protocol bump. Known
	// values today: `'agent-sdk'` (an agent's native SDK/runtime).
	Kind string `json:"kind"`
	// Id of the resource within its {@link kind}, e.g. the provider id `'claude'`
	// or `'codex'` for an `'agent-sdk'` download.
	ResourceId string `json:"resourceId"`
	// Human-readable brand name for display, e.g. `'Claude'`. The host supplies
	// the noun; the client owns the surrounding localized template.
	DisplayName string `json:"displayName"`
	// Lifecycle phase of this frame.
	Phase DownloadPhase `json:"phase"`
	// Bytes written so far. Monotonically non-decreasing within a `downloadId`.
	ReceivedBytes int64 `json:"receivedBytes"`
	// Total bytes when known (e.g. from `Content-Length`); omitted ⇒ indeterminate.
	TotalBytes *int64 `json:"totalBytes,omitempty"`
	// Session whose action triggered the fetch, if any. Informational only —
	// the download is host-level and shared across sessions.
	Session *URI `json:"session,omitempty"`
	// Short, non-localized failure reason; present only when `phase: 'failed'`.
	Error *string `json:"error,omitempty"`
}

// Sent by the server when a protected resource requires (re-)authentication.
//
// This notification MAY be associated with any channel — for example, an
// agent advertised on the root channel, or a per-session resource. The
// `channel` field identifies the subscription the auth requirement belongs
// to; the `resource` field carries the OAuth-protected resource identifier
// (per RFC 9728).
//
// Clients should obtain a fresh token and push it via the `authenticate`
// command.
type AuthRequiredParams struct {
	// Channel URI this notification belongs to
	Channel URI `json:"channel"`
	// The protected resource identifier that requires authentication
	Resource string `json:"resource"`
	// Why authentication is required
	Reason *AuthRequiredReason `json:"reason,omitempty"`
}

// Delivers a batch of OTLP log records to a client subscribed to the host's
// logs channel (advertised on `TelemetryCapabilities.logs`).
//
// The `payload` field is an OTLP/JSON `ExportLogsServiceRequest` value
// verbatim — i.e. an object of shape `{ resourceLogs: ResourceLogs[] }` as
// defined by [opentelemetry-proto](https://github.com/open-telemetry/opentelemetry-proto/blob/main/opentelemetry/proto/collector/logs/v1/logs_service.proto).
// AHP does not redeclare the OTLP type system; clients SHOULD use an
// OpenTelemetry SDK or schema to parse it.
//
// Like all stateless-channel notifications, this is ephemeral: it is not
// replayed on reconnect. Subscribers receive only batches emitted after
// their `subscribe` succeeds.
type OtlpExportLogsParams struct {
	// Channel URI this notification belongs to (an `ahp-otlp:` URI advertised on `TelemetryCapabilities.logs`).
	Channel URI `json:"channel"`
	// OTLP/JSON `ExportLogsServiceRequest` value. The top-level field is
	// `resourceLogs: ResourceLogs[]`; nested shapes are defined by
	// opentelemetry-proto and are not redeclared here.
	Payload map[string]json.RawMessage `json:"payload"`
}

// Delivers a batch of OTLP spans to a client subscribed to the host's
// traces channel (advertised on `TelemetryCapabilities.traces`).
//
// The `payload` field is an OTLP/JSON `ExportTraceServiceRequest` value
// verbatim — i.e. an object of shape `{ resourceSpans: ResourceSpans[] }`
// as defined by [opentelemetry-proto](https://github.com/open-telemetry/opentelemetry-proto/blob/main/opentelemetry/proto/collector/trace/v1/trace_service.proto).
type OtlpExportTracesParams struct {
	// Channel URI this notification belongs to (an `ahp-otlp:` URI advertised on `TelemetryCapabilities.traces`).
	Channel URI `json:"channel"`
	// OTLP/JSON `ExportTraceServiceRequest` value. The top-level field is
	// `resourceSpans: ResourceSpans[]`; nested shapes are defined by
	// opentelemetry-proto and are not redeclared here.
	Payload map[string]json.RawMessage `json:"payload"`
}

// Delivers a batch of OTLP metric data points to a client subscribed to
// the host's metrics channel (advertised on `TelemetryCapabilities.metrics`).
//
// The `payload` field is an OTLP/JSON `ExportMetricsServiceRequest` value
// verbatim — i.e. an object of shape `{ resourceMetrics: ResourceMetrics[] }`
// as defined by [opentelemetry-proto](https://github.com/open-telemetry/opentelemetry-proto/blob/main/opentelemetry/proto/collector/metrics/v1/metrics_service.proto).
type OtlpExportMetricsParams struct {
	// Channel URI this notification belongs to (an `ahp-otlp:` URI advertised on `TelemetryCapabilities.metrics`).
	Channel URI `json:"channel"`
	// OTLP/JSON `ExportMetricsServiceRequest` value. The top-level field is
	// `resourceMetrics: ResourceMetrics[]`; nested shapes are defined by
	// opentelemetry-proto and are not redeclared here.
	Payload map[string]json.RawMessage `json:"payload"`
}

// ─── Partial Summaries ────────────────────────────────────────────────

// PartialSessionSummary is the partial equivalent of SessionSummary — every field is optional for delta updates.
type PartialSessionSummary struct {
	// Session URI
	Resource *URI `json:"resource,omitempty"`
	// Agent provider ID
	Provider *string `json:"provider,omitempty"`
	// Session title
	Title *string `json:"title,omitempty"`
	// Current session status
	Status *SessionStatus `json:"status,omitempty"`
	// Human-readable description of what the session is currently doing
	Activity *string `json:"activity,omitempty"`
	// Creation timestamp
	CreatedAt *int64 `json:"createdAt,omitempty"`
	// Last modification timestamp
	ModifiedAt *int64 `json:"modifiedAt,omitempty"`
	// Server-owned project for this session
	Project *ProjectInfo `json:"project,omitempty"`
	// Currently selected model
	Model *ModelSelection `json:"model,omitempty"`
	// Currently selected custom agent.
	//
	// Absent (`undefined`) means no custom agent is selected for this session
	// — the session uses the provider's default behavior.
	Agent *AgentSelection `json:"agent,omitempty"`
	// The default working directory URI for this session. Individual chats
	// MAY override via {@link ChatSummary.workingDirectory | their own
	// `workingDirectory`}; this field acts as the fallback for any chat that
	// does not.
	WorkingDirectory *URI `json:"workingDirectory,omitempty"`
	// Aggregate summary of file changes associated with this session. Servers
	// may populate this to give clients a quick at-a-glance view of the
	// session's footprint (e.g., for list rendering) without requiring the
	// client to subscribe to a changeset.
	Changes *ChangesSummary `json:"changes,omitempty"`
	// Lightweight summary of this session's inline annotations channel
	// (`ahp-session:/<uuid>/annotations`). Surfaced so badge UI can render
	// annotation / entry counts without subscribing. Absent when the session
	// does not expose an annotations channel.
	Annotations *AnnotationsSummary `json:"annotations,omitempty"`
	// Lightweight server-defined metadata clients may use for the session
	// presentation. The protocol does not interpret these values; producers
	// SHOULD keep the payload small because summaries appear in session lists
	// and session notifications.
	Meta map[string]json.RawMessage `json:"_meta,omitempty"`
}
