// Generated from types/*.ts — do not edit

import Foundation

// MARK: - Notification Enums

/// Reason why authentication is required.
public enum AuthRequiredReason: String, Codable, Sendable {
    /// The client has not yet authenticated for the resource
    case required = "required"
    /// A previously valid token has expired or been revoked
    case expired = "expired"
}

/// Lifecycle phase of a single download.
public enum DownloadPhase: String, Codable, Sendable {
    /// The download has begun; no bytes received yet.
    case started = "started"
    /// A throttled progress sample with bytes received so far.
    case progress = "progress"
    /// Terminal success frame; the resource is fully downloaded.
    case completed = "completed"
    /// Terminal failure frame; see {@link DownloadProgressParams.error}.
    case failed = "failed"
}

// MARK: - Notification Types

public struct SessionAddedParams: Codable, Sendable {
    /// Channel URI this notification belongs to (the root channel)
    public var channel: String
    /// Summary of the new session
    public var summary: SessionSummary

    public init(
        channel: String,
        summary: SessionSummary
    ) {
        self.channel = channel
        self.summary = summary
    }
}

public struct SessionRemovedParams: Codable, Sendable {
    /// Channel URI this notification belongs to (the root channel)
    public var channel: String
    /// URI of the removed session
    public var session: String

    public init(
        channel: String,
        session: String
    ) {
        self.channel = channel
        self.session = session
    }
}

public struct SessionSummaryChangedParams: Codable, Sendable {
    /// Channel URI this notification belongs to (the root channel)
    public var channel: String
    /// URI of the session whose summary changed
    public var session: String
    /// Mutable summary fields that changed; omitted fields are unchanged.
    ///
    /// Identity fields (`resource`, `provider`, `createdAt`) never change and
    /// MUST be omitted by senders; receivers SHOULD ignore them if present.
    public var changes: PartialSessionSummary

    public init(
        channel: String,
        session: String,
        changes: PartialSessionSummary
    ) {
        self.channel = channel
        self.session = session
        self.changes = changes
    }
}

public struct DownloadProgressParams: Codable, Sendable {
    /// Channel URI this notification belongs to (the root channel)
    public var channel: String
    /// Stable id for one download. Coalesces the frames of a single fetch and
    /// distinguishes concurrent downloads (e.g. two resources at once).
    public var downloadId: String
    /// Category of resource being downloaded. An open string (not a closed enum)
    /// so new resource types can be reported without a protocol bump. Known
    /// values today: `'agent-sdk'` (an agent's native SDK/runtime).
    public var kind: String
    /// Id of the resource within its {@link kind}, e.g. the provider id `'claude'`
    /// or `'codex'` for an `'agent-sdk'` download.
    public var resourceId: String
    /// Human-readable brand name for display, e.g. `'Claude'`. The host supplies
    /// the noun; the client owns the surrounding localized template.
    public var displayName: String
    /// Lifecycle phase of this frame.
    public var phase: DownloadPhase
    /// Bytes written so far. Monotonically non-decreasing within a `downloadId`.
    public var receivedBytes: Int
    /// Total bytes when known (e.g. from `Content-Length`); omitted ⇒ indeterminate.
    public var totalBytes: Int?
    /// Session whose action triggered the fetch, if any. Informational only —
    /// the download is host-level and shared across sessions.
    public var session: String?
    /// Short, non-localized failure reason; present only when `phase: 'failed'`.
    public var error: String?

    public init(
        channel: String,
        downloadId: String,
        kind: String,
        resourceId: String,
        displayName: String,
        phase: DownloadPhase,
        receivedBytes: Int,
        totalBytes: Int? = nil,
        session: String? = nil,
        error: String? = nil
    ) {
        self.channel = channel
        self.downloadId = downloadId
        self.kind = kind
        self.resourceId = resourceId
        self.displayName = displayName
        self.phase = phase
        self.receivedBytes = receivedBytes
        self.totalBytes = totalBytes
        self.session = session
        self.error = error
    }
}

public struct AuthRequiredParams: Codable, Sendable {
    /// Channel URI this notification belongs to
    public var channel: String
    /// The protected resource identifier that requires authentication
    public var resource: String
    /// Why authentication is required
    public var reason: AuthRequiredReason?

    public init(
        channel: String,
        resource: String,
        reason: AuthRequiredReason? = nil
    ) {
        self.channel = channel
        self.resource = resource
        self.reason = reason
    }
}

public struct OtlpExportLogsParams: Codable, Sendable {
    /// Channel URI this notification belongs to (an `ahp-otlp:` URI advertised on `TelemetryCapabilities.logs`).
    public var channel: String
    /// OTLP/JSON `ExportLogsServiceRequest` value. The top-level field is
    /// `resourceLogs: ResourceLogs[]`; nested shapes are defined by
    /// opentelemetry-proto and are not redeclared here.
    public var payload: [String: AnyCodable]

    public init(
        channel: String,
        payload: [String: AnyCodable]
    ) {
        self.channel = channel
        self.payload = payload
    }
}

public struct OtlpExportTracesParams: Codable, Sendable {
    /// Channel URI this notification belongs to (an `ahp-otlp:` URI advertised on `TelemetryCapabilities.traces`).
    public var channel: String
    /// OTLP/JSON `ExportTraceServiceRequest` value. The top-level field is
    /// `resourceSpans: ResourceSpans[]`; nested shapes are defined by
    /// opentelemetry-proto and are not redeclared here.
    public var payload: [String: AnyCodable]

    public init(
        channel: String,
        payload: [String: AnyCodable]
    ) {
        self.channel = channel
        self.payload = payload
    }
}

public struct OtlpExportMetricsParams: Codable, Sendable {
    /// Channel URI this notification belongs to (an `ahp-otlp:` URI advertised on `TelemetryCapabilities.metrics`).
    public var channel: String
    /// OTLP/JSON `ExportMetricsServiceRequest` value. The top-level field is
    /// `resourceMetrics: ResourceMetrics[]`; nested shapes are defined by
    /// opentelemetry-proto and are not redeclared here.
    public var payload: [String: AnyCodable]

    public init(
        channel: String,
        payload: [String: AnyCodable]
    ) {
        self.channel = channel
        self.payload = payload
    }
}

// MARK: - Partial Summary Types

public struct PartialSessionSummary: Codable, Sendable {
    /// Session URI
    public var resource: String?
    /// Agent provider ID
    public var provider: String?
    /// Session title
    public var title: String?
    /// Current session status
    public var status: SessionStatus?
    /// Human-readable description of what the session is currently doing
    public var activity: String?
    /// Creation timestamp
    public var createdAt: Int?
    /// Last modification timestamp
    public var modifiedAt: Int?
    /// Server-owned project for this session
    public var project: ProjectInfo?
    /// Currently selected model
    public var model: ModelSelection?
    /// Currently selected custom agent.
    ///
    /// Absent (`undefined`) means no custom agent is selected for this session
    /// — the session uses the provider's default behavior.
    public var agent: AgentSelection?
    /// The default working directory URI for this session. Individual chats
    /// MAY override via {@link ChatSummary.workingDirectory | their own
    /// `workingDirectory`}; this field acts as the fallback for any chat that
    /// does not.
    public var workingDirectory: String?
    /// Aggregate summary of file changes associated with this session. Servers
    /// may populate this to give clients a quick at-a-glance view of the
    /// session's footprint (e.g., for list rendering) without requiring the
    /// client to subscribe to a changeset.
    public var changes: ChangesSummary?
    /// Lightweight summary of this session's inline annotations channel
    /// (`ahp-session:/<uuid>/annotations`). Surfaced so badge UI can render
    /// annotation / entry counts without subscribing. Absent when the session
    /// does not expose an annotations channel.
    public var annotations: AnnotationsSummary?
    /// Lightweight server-defined metadata clients may use for the session
    /// presentation. The protocol does not interpret these values; producers
    /// SHOULD keep the payload small because summaries appear in session lists
    /// and session notifications.
    public var meta: [String: AnyCodable]?

    enum CodingKeys: String, CodingKey {
        case resource
        case provider
        case title
        case status
        case activity
        case createdAt
        case modifiedAt
        case project
        case model
        case agent
        case workingDirectory
        case changes
        case annotations
        case meta = "_meta"
    }

    public init(
        resource: String? = nil,
        provider: String? = nil,
        title: String? = nil,
        status: SessionStatus? = nil,
        activity: String? = nil,
        createdAt: Int? = nil,
        modifiedAt: Int? = nil,
        project: ProjectInfo? = nil,
        model: ModelSelection? = nil,
        agent: AgentSelection? = nil,
        workingDirectory: String? = nil,
        changes: ChangesSummary? = nil,
        annotations: AnnotationsSummary? = nil,
        meta: [String: AnyCodable]? = nil
    ) {
        self.resource = resource
        self.provider = provider
        self.title = title
        self.status = status
        self.activity = activity
        self.createdAt = createdAt
        self.modifiedAt = modifiedAt
        self.project = project
        self.model = model
        self.agent = agent
        self.workingDirectory = workingDirectory
        self.changes = changes
        self.annotations = annotations
        self.meta = meta
    }
}
