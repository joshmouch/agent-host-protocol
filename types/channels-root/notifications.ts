/**
 * Root Channel Notifications — Session catalogue events delivered on the
 * `ahp-root://` channel.
 *
 * @module channels-root/notifications
 */

import type { URI } from '../common/state.js';
import type { SessionSummary } from '../channels-session/state.js';

// ─── root/sessionAdded ───────────────────────────────────────────────────────

/**
 * Broadcast to all clients subscribed to the root channel when a new session
 * is created.
 *
 * @category Protocol Notifications
 * @method root/sessionAdded
 * @direction Server → Client
 * @messageType Notification
 * @version 1
 * @example
 * ```json
 * {
 *   "jsonrpc": "2.0",
 *   "method": "root/sessionAdded",
 *   "params": {
 *     "channel": "ahp-root://",
 *     "summary": {
 *       "resource": "ahp-session:/<uuid>",
 *       "provider": "copilot",
 *       "title": "New Session",
 *       "status": 1,
 *       "createdAt": 1710000000000,
 *       "modifiedAt": 1710000000000
 *     }
 *   }
 * }
 * ```
 */
export interface SessionAddedParams {
  /** Channel URI this notification belongs to (the root channel) */
  channel: URI;
  /** Summary of the new session */
  summary: SessionSummary;
}

// ─── root/sessionRemoved ─────────────────────────────────────────────────────

/**
 * Broadcast to all clients subscribed to the root channel when a session is
 * disposed.
 *
 * @category Protocol Notifications
 * @method root/sessionRemoved
 * @direction Server → Client
 * @messageType Notification
 * @version 1
 * @example
 * ```json
 * {
 *   "jsonrpc": "2.0",
 *   "method": "root/sessionRemoved",
 *   "params": {
 *     "channel": "ahp-root://",
 *     "session": "ahp-session:/<uuid>"
 *   }
 * }
 * ```
 */
export interface SessionRemovedParams {
  /** Channel URI this notification belongs to (the root channel) */
  channel: URI;
  /** URI of the removed session */
  session: URI;
}

// ─── root/sessionSummaryChanged ──────────────────────────────────────────────

/**
 * Broadcast to all clients subscribed to the root channel when an existing
 * session's summary changes (title, status, `modifiedAt`, model, working
 * directory, read/done state, or diff statistics).
 *
 * This notification lets clients that maintain a cached session list — for
 * example, the result of a previous `listSessions()` call — stay in sync with
 * in-flight sessions without having to subscribe to every session URI
 * individually. It is complementary to, not a replacement for,
 * `root/sessionAdded` and `root/sessionRemoved`: those signal lifecycle
 * (creation/disposal), while this signals summary-level mutations on an
 * already-known session.
 *
 * Semantics:
 *
 * - Only fields present in `changes` have new values; omitted fields are
 *   unchanged on the client's cached summary.
 * - Identity fields (`resource`, `provider`, `createdAt`) never change and
 *   are not carried.
 * - Like all protocol notifications, this is ephemeral: it is **not**
 *   replayed on reconnect. On reconnect, clients should re-fetch the full
 *   catalog via `listSessions()` as usual.
 * - The server SHOULD emit this notification whenever any mutable field on
 *   {@link SessionSummary | `SessionSummary`} changes for a session the
 *   server has surfaced via `listSessions()` or `root/sessionAdded`.
 *   Servers MAY coalesce or debounce updates for noisy fields (for example,
 *   `modifiedAt` bumps while a turn is streaming) at their discretion.
 * - Clients that have no cached entry for `session` MAY ignore the
 *   notification; it is not a substitute for `root/sessionAdded`.
 *
 * @category Protocol Notifications
 * @method root/sessionSummaryChanged
 * @direction Server → Client
 * @messageType Notification
 * @version 1
 * @example
 * ```json
 * {
 *   "jsonrpc": "2.0",
 *   "method": "root/sessionSummaryChanged",
 *   "params": {
 *     "channel": "ahp-root://",
 *     "session": "ahp-session:/<uuid>",
 *     "changes": {
 *       "title": "Refactor auth middleware",
 *       "status": 8,
 *       "modifiedAt": 1710000123456
 *     }
 *   }
 * }
 * ```
 */
export interface SessionSummaryChangedParams {
  /** Channel URI this notification belongs to (the root channel) */
  channel: URI;
  /** URI of the session whose summary changed */
  session: URI;
  /**
   * Mutable summary fields that changed; omitted fields are unchanged.
   *
   * Identity fields (`resource`, `provider`, `createdAt`) never change and
   * MUST be omitted by senders; receivers SHOULD ignore them if present.
   */
  changes: Partial<SessionSummary>;
}

// ─── root/downloadProgress ───────────────────────────────────────────────────

/**
 * Lifecycle phase of a single download.
 *
 * @category Protocol Notifications
 */
export const enum DownloadPhase {
  /** The download has begun; no bytes received yet. */
  Started = 'started',
  /** A throttled progress sample with bytes received so far. */
  Progress = 'progress',
  /** Terminal success frame; the resource is fully downloaded. */
  Completed = 'completed',
  /** Terminal failure frame; see {@link DownloadProgressParams.error}. */
  Failed = 'failed',
}

/**
 * Broadcast on the root channel while the host downloads a resource on the
 * client's behalf — typically a multi-MB artifact fetched lazily the first time
 * it is needed (today: an agent's native SDK/runtime, `kind: 'agent-sdk'`). Lets
 * clients show a progress indicator instead of a silent multi-second hang.
 *
 * The notification is intentionally **resource-agnostic** so the same channel
 * can report future downloads (additional agent runtimes, plugins, models, …)
 * without a new method. The `kind` discriminant categorizes the resource and
 * `resourceId` identifies it within that kind; clients that don't care can show
 * a single generic indicator driven by `displayName` + the byte counts.
 *
 * This is **host-level**, not session state: the artifact is shared across every
 * consumer and the host deduplicates concurrent fetches into one download (one
 * `downloadId`). The optional `session` field names the session whose action
 * triggered the fetch, purely as context — a client MAY attribute the progress
 * to that session's row, or show a single global indicator and ignore it.
 *
 * Semantics:
 *
 * - Frames for one download share a stable `downloadId`. The first frame a
 *   client observes for a `downloadId` begins the indicator even if it is not
 *   `phase: 'started'` (a client that connects mid-download may miss the
 *   `started` frame).
 * - `receivedBytes` is monotonically non-decreasing within a `downloadId`.
 *   `totalBytes` is present only when the host knows the size up front
 *   (e.g. a `Content-Length`); when absent the client SHOULD show an
 *   indeterminate indicator.
 * - Exactly one terminal frame (`phase: 'completed'` or `'failed'`) ends a
 *   download. `error` carries a short, non-localized reason on failure.
 * - Like all notifications this is ephemeral and is **not** replayed on
 *   reconnect. A client that never receives a terminal frame (the download
 *   finished while it was disconnected) SHOULD expire the indicator after an
 *   idle timeout.
 * - The brand noun is carried in `displayName`; clients own the surrounding
 *   (localized) template, e.g. `"Downloading {displayName}… {pct}%"`.
 *
 * @category Protocol Notifications
 * @method root/downloadProgress
 * @direction Server → Client
 * @messageType Notification
 * @version 1
 * @example
 * ```json
 * {
 *   "jsonrpc": "2.0",
 *   "method": "root/downloadProgress",
 *   "params": {
 *     "channel": "ahp-root://",
 *     "downloadId": "d3f1c2",
 *     "kind": "agent-sdk",
 *     "resourceId": "claude",
 *     "displayName": "Claude",
 *     "phase": "progress",
 *     "receivedBytes": 18874368,
 *     "totalBytes": 41957498,
 *     "session": "ahp-session:/<uuid>"
 *   }
 * }
 * ```
 */
export interface DownloadProgressParams {
  /** Channel URI this notification belongs to (the root channel) */
  channel: URI;
  /**
   * Stable id for one download. Coalesces the frames of a single fetch and
   * distinguishes concurrent downloads (e.g. two resources at once).
   */
  downloadId: string;
  /**
   * Category of resource being downloaded. An open string (not a closed enum)
   * so new resource types can be reported without a protocol bump. Known
   * values today: `'agent-sdk'` (an agent's native SDK/runtime).
   */
  kind: string;
  /**
   * Id of the resource within its {@link kind}, e.g. the provider id `'claude'`
   * or `'codex'` for an `'agent-sdk'` download.
   */
  resourceId: string;
  /**
   * Human-readable brand name for display, e.g. `'Claude'`. The host supplies
   * the noun; the client owns the surrounding localized template.
   */
  displayName: string;
  /** Lifecycle phase of this frame. */
  phase: DownloadPhase;
  /** Bytes written so far. Monotonically non-decreasing within a `downloadId`. */
  receivedBytes: number;
  /** Total bytes when known (e.g. from `Content-Length`); omitted ⇒ indeterminate. */
  totalBytes?: number;
  /**
   * Session whose action triggered the fetch, if any. Informational only —
   * the download is host-level and shared across sessions.
   */
  session?: URI;
  /** Short, non-localized failure reason; present only when `phase: 'failed'`. */
  error?: string;
}
