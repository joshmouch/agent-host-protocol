/**
 * Common Notification Types — `auth/required` (connection-level protocol
 * notification) and `ahp/messageSegment` (control notification used by the
 * framing layer for message chunking).
 *
 * @module common/notifications
 */

import type { URI } from './state.js';

/**
 * Reason why authentication is required.
 *
 * @category Protocol Notifications
 */
export const enum AuthRequiredReason {
  /** The client has not yet authenticated for the resource */
  Required = 'required',
  /** A previously valid token has expired or been revoked */
  Expired = 'expired',
}

// ─── auth/required ───────────────────────────────────────────────────────────

/**
 * Sent by the server when a protected resource requires (re-)authentication.
 *
 * This notification MAY be associated with any channel — for example, an
 * agent advertised on the root channel, or a per-session resource. The
 * `channel` field identifies the subscription the auth requirement belongs
 * to; the `resource` field carries the OAuth-protected resource identifier
 * (per RFC 9728).
 *
 * Clients should obtain a fresh token and push it via the `authenticate`
 * command.
 *
 * @category Protocol Notifications
 * @method auth/required
 * @direction Server → Client
 * @messageType Notification
 * @version 1
 * @see {@link /specification/authentication | Authentication}
 * @example
 * ```json
 * {
 *   "jsonrpc": "2.0",
 *   "method": "auth/required",
 *   "params": {
 *     "channel": "ahp-root://",
 *     "resource": "https://api.github.com",
 *     "reason": "expired"
 *   }
 * }
 * ```
 */
export interface AuthRequiredParams {
  /** Channel URI this notification belongs to */
  channel: URI;
  /** The protected resource identifier that requires authentication */
  resource: string;
  /** Why authentication is required */
  reason?: AuthRequiredReason;
}

// ─── ahp/messageSegment ──────────────────────────────────────────────────────

/**
 * Carries one segment of a JSON-RPC message that the sender has split to
 * stay below a transport frame ceiling. Reassembled by the receiver's
 * framing layer before normal JSON-RPC dispatch.
 *
 * `ahp/messageSegment` is a **control** notification: it belongs to the
 * framing layer, not to any subscribable resource, and is the only AHP
 * notification that does **not** carry a top-level `channel: URI`. It is
 * registered in {@link ControlNotificationMap} rather than the client- or
 * server-direction notification maps; it MAY be sent in either direction.
 *
 * Chunking is opt-in per direction: a sender MUST NOT emit
 * `ahp/messageSegment` unless the receiver has advertised
 * {@link ChunkingCapability} during `initialize` (or `reconnect`). See
 * [Chunking](/specification/chunking) for the full lifecycle, sender and
 * receiver behaviour, validation rules, and DoS limits.
 *
 * @category Protocol Notifications
 * @method ahp/messageSegment
 * @direction Either
 * @messageType Notification
 * @version 0.3.0
 * @see {@link /specification/chunking | Chunking}
 * @example
 * ```json
 * {
 *   "jsonrpc": "2.0",
 *   "method": "ahp/messageSegment",
 *   "params": {
 *     "groupId": "b9f1c6c2-3f4d-4f8e-9a2c-7d4c0e9a8e15",
 *     "index": 0,
 *     "total": 3,
 *     "data": "eyJqc29ucnBjIjoiMi4wIiwibWV0aG9kIjoiYWN0aW9uIiwic..."
 *   }
 * }
 * ```
 */
export interface MessageSegmentParams {
  /**
   * Opaque sender-chosen identifier that scopes one in-flight reassembly.
   *
   * MUST be a non-empty string of at most 128 UTF-8 bytes. Receivers MUST
   * NOT interpret the value. A `groupId` MUST be unique among the sender's
   * currently in-flight reassemblies on a single connection; it MAY be
   * reused once the receiver has either fully reassembled or discarded
   * that group. `groupId` and JSON-RPC `id` are independent namespaces.
   */
  groupId: string;
  /**
   * 0-based position of this segment within the group. MUST be a
   * non-negative integer strictly less than `total`. Receivers MUST reject
   * duplicate or out-of-order indices as a protocol error.
   *
   * @minimum 0
   */
  index: number;
  /**
   * Total number of segments in this group. MUST be at least 1 and less
   * than 65 536. MUST be identical across every segment of the group; a
   * subsequent segment with a different `total` is a protocol error.
   *
   * @minimum 1
   * @maximum 65535
   */
  total: number;
  /**
   * Base64-encoded slice of the UTF-8 bytes of the original JSON-RPC
   * message being reassembled. Uses the standard base64 alphabet
   * ([RFC 4648 §4](https://www.rfc-editor.org/rfc/rfc4648#section-4)) with
   * padding. Concatenating the decoded bytes of segments `0..total-1` in
   * order MUST produce a UTF-8 byte sequence that parses as exactly one
   * JSON-RPC request, response, or notification — which MUST NOT itself
   * be an `ahp/messageSegment` (no recursion).
   */
  data: string;
}
