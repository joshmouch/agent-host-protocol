/**
 * Comments Channel Commands — Client-driven mutations of an
 * `ahp-session:/<uuid>/comments` channel.
 *
 * The protocol forbids empty threads, so thread and first-comment
 * creation are fused into {@link CreateCommentThreadParams |
 * `createCommentThread`} and the server collapses a thread whose last
 * comment is deleted into a {@link CommentsThreadRemovedAction}. Every
 * accepted command echoes back through the normal `comments/*` action
 * stream on the channel.
 *
 * @module channels-comments/commands
 */

import type { URI, TextRange } from '../common/state.js';
import type { BaseParams } from '../common/commands.js';
import type { NewComment } from './state.js';

// ─── createCommentThread ─────────────────────────────────────────────────────

/**
 * Create a new {@link CommentThread} anchored to a file range from a
 * specific turn.
 *
 * The initial comment is required — the protocol forbids empty threads,
 * so thread creation and first-comment creation are fused into one
 * command. The server assigns both {@link CreateCommentThreadResult.threadId}
 * and {@link CreateCommentThreadResult.commentId}, then broadcasts a
 * {@link CommentsThreadSetAction} on the channel.
 *
 * @category Commands
 * @method createCommentThread
 * @direction Client → Server
 * @messageType Request
 * @version 3
 */
export interface CreateCommentThreadParams extends BaseParams {
  /** The comments channel URI, e.g. `ahp-session:/<uuid>/comments`. */
  channel: URI;
  /** Turn whose file versions {@link resource} + {@link range} address. */
  turnId: string;
  /** Anchored file URI. */
  resource: URI;
  /** Anchored range within {@link resource}. */
  range: TextRange;
  /** First comment in the thread. The server assigns its {@link Comment.id}. */
  comment: NewComment;
}

/**
 * Result of {@link CreateCommentThreadParams | `createCommentThread`}.
 *
 * @category Commands
 */
export interface CreateCommentThreadResult {
  /** Server-assigned {@link CommentThread.id}. */
  threadId: string;
  /** Server-assigned {@link Comment.id} of the initial comment. */
  commentId: string;
}

// ─── updateCommentThread ─────────────────────────────────────────────────────

/**
 * Re-anchor an existing {@link CommentThread} — typically used to re-pin
 * a thread to a different range or a newer turn after an edit. Comments
 * themselves are not modified by this command; use
 * {@link AddCommentParams | `addComment`},
 * {@link EditCommentParams | `editComment`}, or
 * {@link DeleteCommentParams | `deleteComment`} for that.
 *
 * Omitted optional fields preserve their current value. The server
 * echoes the resulting thread state as a {@link CommentsThreadSetAction}.
 *
 * @category Commands
 * @method updateCommentThread
 * @direction Client → Server
 * @messageType Request
 * @version 3
 */
export interface UpdateCommentThreadParams extends BaseParams {
  /** The comments channel URI. */
  channel: URI;
  /** The {@link CommentThread.id} to update. */
  threadId: string;
  /** New {@link CommentThread.turnId}, if changing. */
  turnId?: string;
  /** New anchored file URI, if changing. */
  resource?: URI;
  /** New anchored range, if changing. */
  range?: TextRange;
}

// ─── deleteCommentThread ─────────────────────────────────────────────────────

/**
 * Delete an entire comment thread (and every comment it contains). The
 * server echoes a {@link CommentsThreadRemovedAction} on the channel.
 *
 * @category Commands
 * @method deleteCommentThread
 * @direction Client → Server
 * @messageType Request
 * @version 3
 */
export interface DeleteCommentThreadParams extends BaseParams {
  /** The comments channel URI. */
  channel: URI;
  /** The {@link CommentThread.id} to delete. */
  threadId: string;
}

// ─── addComment ──────────────────────────────────────────────────────────────

/**
 * Append a new {@link Comment} to an existing thread. The server assigns
 * the resulting {@link Comment.id} and echoes a
 * {@link CommentsCommentSetAction}.
 *
 * @category Commands
 * @method addComment
 * @direction Client → Server
 * @messageType Request
 * @version 3
 */
export interface AddCommentParams extends BaseParams {
  /** The comments channel URI. */
  channel: URI;
  /** Thread that receives the new comment. */
  threadId: string;
  /** Comment payload — the server assigns the id. */
  comment: NewComment;
}

/**
 * Result of {@link AddCommentParams | `addComment`}.
 *
 * @category Commands
 */
export interface AddCommentResult {
  /** Server-assigned {@link Comment.id} of the new comment. */
  commentId: string;
}

// ─── editComment ─────────────────────────────────────────────────────────────

/**
 * Edit the body of an existing comment in place. The server echoes a
 * {@link CommentsCommentSetAction} carrying the updated comment.
 *
 * Only the body is mutable through this command; to change
 * {@link Comment.source} or {@link Comment._meta} delete and re-create
 * the comment.
 *
 * @category Commands
 * @method editComment
 * @direction Client → Server
 * @messageType Request
 * @version 3
 */
export interface EditCommentParams extends BaseParams {
  /** The comments channel URI. */
  channel: URI;
  /** Enclosing thread. */
  threadId: string;
  /** {@link Comment.id} to edit. */
  commentId: string;
  /** New comment body. */
  text: string;
}

// ─── deleteComment ───────────────────────────────────────────────────────────

/**
 * Remove a single comment from a thread.
 *
 * If the removal would leave the thread empty (i.e. the targeted comment
 * is the only one remaining), the server collapses the thread instead
 * — it dispatches a {@link CommentsThreadRemovedAction} and the thread
 * disappears from {@link CommentsState.threads}. Otherwise the server
 * echoes a {@link CommentsCommentRemovedAction}.
 *
 * @category Commands
 * @method deleteComment
 * @direction Client → Server
 * @messageType Request
 * @version 3
 */
export interface DeleteCommentParams extends BaseParams {
  /** The comments channel URI. */
  channel: URI;
  /** Enclosing thread. */
  threadId: string;
  /** {@link Comment.id} to remove. */
  commentId: string;
}
