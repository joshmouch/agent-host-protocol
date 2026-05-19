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

public struct MessageSegmentParams: Codable, Sendable {
    /// Opaque sender-chosen identifier that scopes one in-flight reassembly.
    /// 
    /// MUST be a non-empty string of at most 128 UTF-8 bytes. Receivers MUST
    /// NOT interpret the value. A `groupId` MUST be unique among the sender's
    /// currently in-flight reassemblies on a single connection; it MAY be
    /// reused once the receiver has either fully reassembled or discarded
    /// that group. `groupId` and JSON-RPC `id` are independent namespaces.
    public var groupId: String
    /// 0-based position of this segment within the group. MUST be a
    /// non-negative integer strictly less than `total`. Receivers MUST reject
    /// duplicate or out-of-order indices as a protocol error.
    public var index: Int
    /// Total number of segments in this group. MUST be at least 1 and less
    /// than 65 536. MUST be identical across every segment of the group; a
    /// subsequent segment with a different `total` is a protocol error.
    public var total: Int
    /// Base64-encoded slice of the UTF-8 bytes of the original JSON-RPC
    /// message being reassembled. Uses the standard base64 alphabet
    /// ([RFC 4648 §4](https://www.rfc-editor.org/rfc/rfc4648#section-4)) with
    /// padding. Concatenating the decoded bytes of segments `0..total-1` in
    /// order MUST produce a UTF-8 byte sequence that parses as exactly one
    /// JSON-RPC request, response, or notification — which MUST NOT itself
    /// be an `ahp/messageSegment` (no recursion).
    public var data: String

    public init(
        groupId: String,
        index: Int,
        total: Int,
        data: String
    ) {
        self.groupId = groupId
        self.index = index
        self.total = total
        self.data = data
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
    /// The working directory URI for this session
    public var workingDirectory: String?
    /// Catalogue of changesets the server can produce for this session. Each
    /// entry advertises a subscribable view of file changes (uncommitted,
    /// session-wide, per-turn, etc.) and the URI template the client expands
    /// before subscribing. See {@link ChangesetSummary} for the full shape and
    /// {@link /guide/changesets | Changesets} for an overview of the model.
    public var changesets: [ChangesetSummary]?

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
        workingDirectory: String? = nil,
        changesets: [ChangesetSummary]? = nil
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
        self.workingDirectory = workingDirectory
        self.changesets = changesets
    }
}
