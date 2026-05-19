// Generated from types/*.ts — do not edit.
//
// Regenerate with: npm run generate:rust

#![allow(missing_docs)]

#[allow(unused_imports)]
use crate::common::{AnyValue, JsonObject, StringOrMarkdown, Uri};
#[allow(unused_imports)]
use serde::{Deserialize, Serialize};
#[allow(unused_imports)]
use serde_repr::{Deserialize_repr, Serialize_repr};

#[allow(unused_imports)]
use crate::state::{
    ChangesetSummary, FileEdit, ModelSelection, ProjectInfo, SessionStatus, SessionSummary,
};

// ─── Enums ────────────────────────────────────────────────────────────

/// Reason why authentication is required.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum AuthRequiredReason {
    /// The client has not yet authenticated for the resource
    #[serde(rename = "required")]
    Required,
    /// A previously valid token has expired or been revoked
    #[serde(rename = "expired")]
    Expired,
}

// ─── Notification Payloads ────────────────────────────────────────────

/// Broadcast to all clients subscribed to the root channel when a new session
/// is created.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionAddedParams {
    /// Channel URI this notification belongs to (the root channel)
    pub channel: Uri,
    /// Summary of the new session
    pub summary: SessionSummary,
}

/// Broadcast to all clients subscribed to the root channel when a session is
/// disposed.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionRemovedParams {
    /// Channel URI this notification belongs to (the root channel)
    pub channel: Uri,
    /// URI of the removed session
    pub session: Uri,
}

/// Broadcast to all clients subscribed to the root channel when an existing
/// session's summary changes (title, status, `modifiedAt`, model, working
/// directory, read/done state, or diff statistics).
///
/// This notification lets clients that maintain a cached session list — for
/// example, the result of a previous `listSessions()` call — stay in sync with
/// in-flight sessions without having to subscribe to every session URI
/// individually. It is complementary to, not a replacement for,
/// `root/sessionAdded` and `root/sessionRemoved`: those signal lifecycle
/// (creation/disposal), while this signals summary-level mutations on an
/// already-known session.
///
/// Semantics:
///
/// - Only fields present in `changes` have new values; omitted fields are
///   unchanged on the client's cached summary.
/// - Identity fields (`resource`, `provider`, `createdAt`) never change and
///   are not carried.
/// - Like all protocol notifications, this is ephemeral: it is **not**
///   replayed on reconnect. On reconnect, clients should re-fetch the full
///   catalog via `listSessions()` as usual.
/// - The server SHOULD emit this notification whenever any mutable field on
///   {@link SessionSummary | `SessionSummary`} changes for a session the
///   server has surfaced via `listSessions()` or `root/sessionAdded`.
///   Servers MAY coalesce or debounce updates for noisy fields (for example,
///   `modifiedAt` bumps while a turn is streaming, or rapidly changing
///   `changesets`) at their discretion.
/// - Clients that have no cached entry for `session` MAY ignore the
///   notification; it is not a substitute for `root/sessionAdded`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionSummaryChangedParams {
    /// Channel URI this notification belongs to (the root channel)
    pub channel: Uri,
    /// URI of the session whose summary changed
    pub session: Uri,
    /// Mutable summary fields that changed; omitted fields are unchanged.
    ///
    /// Identity fields (`resource`, `provider`, `createdAt`) never change and
    /// MUST be omitted by senders; receivers SHOULD ignore them if present.
    pub changes: PartialSessionSummary,
}

/// Sent by the server when a protected resource requires (re-)authentication.
///
/// This notification MAY be associated with any channel — for example, an
/// agent advertised on the root channel, or a per-session resource. The
/// `channel` field identifies the subscription the auth requirement belongs
/// to; the `resource` field carries the OAuth-protected resource identifier
/// (per RFC 9728).
///
/// Clients should obtain a fresh token and push it via the `authenticate`
/// command.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AuthRequiredParams {
    /// Channel URI this notification belongs to
    pub channel: Uri,
    /// The protected resource identifier that requires authentication
    pub resource: String,
    /// Why authentication is required
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reason: Option<AuthRequiredReason>,
}

/// Carries one segment of a JSON-RPC message that the sender has split to
/// stay below a transport frame ceiling. Reassembled by the receiver's
/// framing layer before normal JSON-RPC dispatch.
///
/// `ahp/messageSegment` is a **control** notification: it belongs to the
/// framing layer, not to any subscribable resource, and is the only AHP
/// notification that does **not** carry a top-level `channel: URI`. It is
/// registered in {@link ControlNotificationMap} rather than the client- or
/// server-direction notification maps; it MAY be sent in either direction.
///
/// Chunking is opt-in per direction: a sender MUST NOT emit
/// `ahp/messageSegment` unless the receiver has advertised
/// {@link ChunkingCapability} during `initialize` (or `reconnect`). See
/// [Chunking](/specification/chunking) for the full lifecycle, sender and
/// receiver behaviour, validation rules, and DoS limits.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MessageSegmentParams {
    /// Opaque sender-chosen identifier that scopes one in-flight reassembly.
    ///
    /// MUST be a non-empty string of at most 128 UTF-8 bytes. Receivers MUST
    /// NOT interpret the value. A `groupId` MUST be unique among the sender's
    /// currently in-flight reassemblies on a single connection; it MAY be
    /// reused once the receiver has either fully reassembled or discarded
    /// that group. `groupId` and JSON-RPC `id` are independent namespaces.
    pub group_id: String,
    /// 0-based position of this segment within the group. MUST be a
    /// non-negative integer strictly less than `total`. Receivers MUST reject
    /// duplicate or out-of-order indices as a protocol error.
    pub index: i64,
    /// Total number of segments in this group. MUST be at least 1 and less
    /// than 65 536. MUST be identical across every segment of the group; a
    /// subsequent segment with a different `total` is a protocol error.
    pub total: i64,
    /// Base64-encoded slice of the UTF-8 bytes of the original JSON-RPC
    /// message being reassembled. Uses the standard base64 alphabet
    /// ([RFC 4648 §4](https://www.rfc-editor.org/rfc/rfc4648#section-4)) with
    /// padding. Concatenating the decoded bytes of segments `0..total-1` in
    /// order MUST produce a UTF-8 byte sequence that parses as exactly one
    /// JSON-RPC request, response, or notification — which MUST NOT itself
    /// be an `ahp/messageSegment` (no recursion).
    pub data: String,
}

// ─── Partial Summaries ────────────────────────────────────────────────

/// Partial equivalent of SessionSummary — every field is optional for delta updates.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct PartialSessionSummary {
    /// Session URI
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub resource: Option<Uri>,
    /// Agent provider ID
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub provider: Option<String>,
    /// Session title
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub title: Option<String>,
    /// Current session status
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub status: Option<u32>,
    /// Human-readable description of what the session is currently doing
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub activity: Option<String>,
    /// Creation timestamp
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub created_at: Option<i64>,
    /// Last modification timestamp
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub modified_at: Option<i64>,
    /// Server-owned project for this session
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub project: Option<ProjectInfo>,
    /// Currently selected model
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub model: Option<ModelSelection>,
    /// The working directory URI for this session
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub working_directory: Option<Uri>,
    /// Catalogue of changesets the server can produce for this session. Each
    /// entry advertises a subscribable view of file changes (uncommitted,
    /// session-wide, per-turn, etc.) and the URI template the client expands
    /// before subscribing. See {@link ChangesetSummary} for the full shape and
    /// {@link /guide/changesets | Changesets} for an overview of the model.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub changesets: Option<Vec<ChangesetSummary>>,
}
