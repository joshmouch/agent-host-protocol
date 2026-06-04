/**
 * Comments Channel Actions — Mutations of an `ahp-session:/<uuid>/comments`
 * channel's state.
 *
 * Every comments action is server-only: clients drive mutations through
 * the {@link CreateCommentThreadParams | `createCommentThread`},
 * {@link AddCommentParams | `addComment`}, etc. commands, and the server
 * echoes the resulting state change as one of these actions. Mirrors the
 * shape of the `channeset/*` action family.
 *
 * @module channels-comments/actions
 */

import { ActionType } from '../common/actions.js';
import type { Comment, CommentThread } from './state.js';

// ─── Comments Actions ────────────────────────────────────────────────────────

/**
 * Upsert a {@link CommentThread} in the comments channel — adds a new
 * thread, or replaces an existing one identified by
 * {@link CommentThread.id}. When replacing, the full thread payload
 * (including its {@link CommentThread.comments | comments} list) is
 * substituted; producers SHOULD prefer {@link CommentsCommentSetAction}
 * for per-comment edits to keep wire updates small.
 *
 * @category Comments Actions
 * @version 3
 */
export interface CommentsThreadSetAction {
  type: ActionType.CommentsThreadSet;
  /** The new or replacement thread. MUST contain at least one comment. */
  thread: CommentThread;
}

/**
 * Remove a {@link CommentThread} from the channel by its id.
 *
 * The server emits this in two cases:
 * 1. The client explicitly invoked
 *    {@link DeleteCommentThreadParams | `deleteCommentThread`}.
 * 2. The client invoked {@link DeleteCommentParams | `deleteComment`} on
 *    the last remaining comment in the thread — the protocol collapses
 *    the thread rather than leaving an empty one behind.
 *
 * @category Comments Actions
 * @version 3
 */
export interface CommentsThreadRemovedAction {
  type: ActionType.CommentsThreadRemoved;
  /** The {@link CommentThread.id} of the thread to remove. */
  threadId: string;
}

/**
 * Upsert a {@link Comment} within an existing thread — adds a new
 * comment, or replaces one identified by {@link Comment.id}. If
 * {@link threadId} does not match any current thread the action is a
 * no-op.
 *
 * @category Comments Actions
 * @version 3
 */
export interface CommentsCommentSetAction {
  type: ActionType.CommentsCommentSet;
  /** The {@link CommentThread.id} the comment belongs to. */
  threadId: string;
  /** The new or replacement comment. */
  comment: Comment;
}

/**
 * Remove a single {@link Comment} from a thread without collapsing the
 * thread itself. Used when more than one comment remains — the server
 * MUST dispatch {@link CommentsThreadRemovedAction} instead when removing
 * the last comment would otherwise leave the thread empty.
 *
 * If either {@link threadId} or {@link commentId} does not match the
 * current state the action is a no-op.
 *
 * @category Comments Actions
 * @version 3
 */
export interface CommentsCommentRemovedAction {
  type: ActionType.CommentsCommentRemoved;
  /** The {@link CommentThread.id} the comment belongs to. */
  threadId: string;
  /** The {@link Comment.id} to remove. */
  commentId: string;
}

/**
 * Drop every thread from the comments channel.
 *
 * Dispatched when the owning session is going away and the channel is
 * about to become un-subscribable. Clients SHOULD release references on
 * receipt and react to the corresponding session-level lifecycle signal
 * (e.g. `root/sessionRemoved`) to fully tear down UI.
 *
 * @category Comments Actions
 * @version 3
 */
export interface CommentsClearedAction {
  type: ActionType.CommentsCleared;
}
