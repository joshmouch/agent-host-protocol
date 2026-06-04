/**
 * Comments Channel State Types — Per-session inline file-comment state
 * exposed on the `ahp-session:/<uuid>/comments` channel.
 *
 * Each session owns at most one comments channel. The channel URI is
 * derived from the session URI by appending `/comments` and is also
 * surfaced explicitly on {@link CommentsSummary.resource} for badge UI.
 *
 * @module channels-comments/state
 */

import type { URI, StringOrMarkdown, TextRange } from '../common/state.js';

// ─── Comments Summary ────────────────────────────────────────────────────────

/**
 * Lightweight per-session summary of the comments channel, surfaced on
 * {@link SessionSummary.comments} so badge UI can render thread / comment
 * counts without subscribing to the channel itself.
 *
 * @category Comments
 */
export interface CommentsSummary {
  /**
   * The subscribable comments channel URI for the owning session
   * (typically `ahp-session:/<uuid>/comments`). Surfaced explicitly even
   * though it is derivable from the session URI so badge UI does not need
   * to know the derivation rule.
   */
  resource: URI;
  /** Total number of {@link CommentThread} entries in the channel. */
  threadCount: number;
  /** Total number of {@link Comment} entries across every thread. */
  commentCount: number;
}

// ─── Comments State ──────────────────────────────────────────────────────────

/**
 * Full state for a session's comments channel, returned when a client
 * subscribes to an `ahp-session:/<uuid>/comments` URI.
 *
 * @category Comments
 */
export interface CommentsState {
  /** Comment threads in this channel, keyed by {@link CommentThread.id}. */
  threads: CommentThread[];
}

// ─── Comment Thread ──────────────────────────────────────────────────────────

/**
 * A conversation anchored to a specific range in a specific file produced
 * by a specific turn.
 *
 * {@link turnId} anchors the thread to the file versions that turn
 * produced, so a later turn that rewrites the same file does not silently
 * invalidate the comment's anchor — clients can resolve {@link resource}
 * and {@link range} against the turn's changeset.
 *
 * Every thread MUST contain at least one {@link Comment}. The server
 * enforces this invariant: {@link CreateCommentThreadParams |
 * `createCommentThread`} requires an initial comment, and deleting the
 * last remaining comment collapses the thread into a
 * {@link CommentsThreadRemovedAction} rather than leaving an empty thread
 * behind.
 *
 * @category Comments
 */
export interface CommentThread {
  /** Stable identifier within the comments channel. Server-assigned. */
  id: string;
  /**
   * Turn that produced the file versions this thread is anchored to.
   * Matches a {@link Turn.id} on the owning session.
   */
  turnId: string;
  /** The file the thread is anchored to. */
  resource: URI;
  /** Range within {@link resource} the thread is anchored to. */
  range: TextRange;
  /**
   * Comments in this thread, in dispatch order (oldest first). MUST
   * contain at least one entry.
   */
  comments: Comment[];
  /**
   * Server-defined opaque metadata, surfaced to tooling but not
   * interpreted by the protocol.
   */
  _meta?: Record<string, unknown>;
}

// ─── Comment ─────────────────────────────────────────────────────────────────

/**
 * A single comment within a {@link CommentThread}.
 *
 * @category Comments
 */
export interface Comment {
  /** Stable identifier within the enclosing thread. Server-assigned. */
  id: string;
  /**
   * Comment body. A bare `string` is rendered as plain text; pass
   * `{ markdown: "…" }` to opt into Markdown rendering. See
   * {@link StringOrMarkdown}.
   */
  text: StringOrMarkdown;
  /**
   * Server-defined opaque metadata, surfaced to tooling but not
   * interpreted by the protocol.
   */
  _meta?: Record<string, unknown>;
}

// ─── New Comment ─────────────────────────────────────────────────────────────

/**
 * Input shape passed to {@link CreateCommentThreadParams | `createCommentThread`}
 * and {@link AddCommentParams | `addComment`}. The server assigns the
 * resulting {@link Comment.id}.
 *
 * @category Comments
 */
export interface NewComment {
  /** Comment body. See {@link Comment.text}. */
  text: StringOrMarkdown;
  /**
   * Server-defined opaque metadata, forwarded onto the resulting
   * {@link Comment._meta}.
   */
  _meta?: Record<string, unknown>;
}
