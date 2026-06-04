/**
 * Comments Channel Reducer — Pure reducer for `CommentsState`.
 *
 * @module channels-comments/reducer
 */

import { ActionType } from '../common/actions.js';
import type { Comment, CommentThread, CommentsState } from './state.js';
import type { CommentsAction } from '../action-origin.generated.js';
import { softAssertNever } from '../common/reducer-helpers.js';

/**
 * Pure reducer for comments state. Handles every {@link CommentsAction}
 * variant.
 *
 * Per the spec, every comments action is server-only. The reducer
 * preserves the dispatch order of threads (and of comments within a
 * thread): new entries are appended; `*Set` actions with a matching id
 * replace in place, while actions whose target id is unknown are no-ops
 * (mirroring `changeset/fileRemoved` semantics). The single-comment
 * minimum invariant is enforced by the server, not the reducer — a
 * malformed server that removes a thread's last comment via
 * {@link CommentsCommentRemovedAction} would leave an empty thread,
 * which is observable but not catastrophic.
 */
export function commentsReducer(state: CommentsState, action: CommentsAction, log?: (msg: string) => void): CommentsState {
  switch (action.type) {
    case ActionType.CommentsThreadSet: {
      const idx = state.threads.findIndex(t => t.id === action.thread.id);
      if (idx < 0) {
        return { ...state, threads: [...state.threads, action.thread] };
      }
      const next: CommentThread[] = [...state.threads];
      next[idx] = action.thread;
      return { ...state, threads: next };
    }

    case ActionType.CommentsThreadRemoved: {
      const idx = state.threads.findIndex(t => t.id === action.threadId);
      if (idx < 0) {
        return state;
      }
      const next: CommentThread[] = [...state.threads];
      next.splice(idx, 1);
      return { ...state, threads: next };
    }

    case ActionType.CommentsCommentSet: {
      const tIdx = state.threads.findIndex(t => t.id === action.threadId);
      if (tIdx < 0) {
        return state;
      }
      const thread = state.threads[tIdx];
      const cIdx = thread.comments.findIndex(c => c.id === action.comment.id);
      let nextComments: Comment[];
      if (cIdx < 0) {
        nextComments = [...thread.comments, action.comment];
      } else {
        nextComments = [...thread.comments];
        nextComments[cIdx] = action.comment;
      }
      const nextThreads: CommentThread[] = [...state.threads];
      nextThreads[tIdx] = { ...thread, comments: nextComments };
      return { ...state, threads: nextThreads };
    }

    case ActionType.CommentsCommentRemoved: {
      const tIdx = state.threads.findIndex(t => t.id === action.threadId);
      if (tIdx < 0) {
        return state;
      }
      const thread = state.threads[tIdx];
      const cIdx = thread.comments.findIndex(c => c.id === action.commentId);
      if (cIdx < 0) {
        return state;
      }
      const nextComments: Comment[] = [...thread.comments];
      nextComments.splice(cIdx, 1);
      const nextThreads: CommentThread[] = [...state.threads];
      nextThreads[tIdx] = { ...thread, comments: nextComments };
      return { ...state, threads: nextThreads };
    }

    case ActionType.CommentsCleared:
      if (state.threads.length === 0) {
        return state;
      }
      return { ...state, threads: [] };

    default:
      softAssertNever(action, log);
      return state;
  }
}
