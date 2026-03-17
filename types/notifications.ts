/**
 * Notification Types — Source of truth for all AHP notification definitions.
 *
 * @module notifications
 * @description Notifications are ephemeral broadcasts that are **not** part of the
 * state tree. They are not processed by reducers and are not replayed on reconnection.
 */

import type { URI, ISessionSummary } from './state.js';

// ─── Protocol Notifications ──────────────────────────────────────────────────

/**
 * Discriminant values for all protocol notifications.
 *
 * @category Protocol Notifications
 */
export const enum NotificationType {
  SessionAdded = 'notify/sessionAdded',
  SessionRemoved = 'notify/sessionRemoved',
}

/**
 * Broadcast to all connected clients when a new session is created.
 *
 * @category Protocol Notifications
 * @version 1
 * @example
 * ```json
 * {
 *   "jsonrpc": "2.0",
 *   "method": "notification",
 *   "params": {
 *     "notification": {
 *       "type": "notify/sessionAdded",
 *       "summary": {
 *         "resource": "copilot:/<uuid>",
 *         "provider": "copilot",
 *         "title": "New Session",
 *         "status": "idle",
 *         "createdAt": 1710000000000,
 *         "modifiedAt": 1710000000000
 *       }
 *     }
 *   }
 * }
 * ```
 */
export interface ISessionAddedNotification {
  type: NotificationType.SessionAdded;
  /** Summary of the new session */
  summary: ISessionSummary;
}

/**
 * Broadcast to all connected clients when a session is disposed.
 *
 * @category Protocol Notifications
 * @version 1
 * @example
 * ```json
 * {
 *   "jsonrpc": "2.0",
 *   "method": "notification",
 *   "params": {
 *     "notification": {
 *       "type": "notify/sessionRemoved",
 *       "session": "copilot:/<uuid>"
 *     }
 *   }
 * }
 * ```
 */
export interface ISessionRemovedNotification {
  type: NotificationType.SessionRemoved;
  /** URI of the removed session */
  session: URI;
}

/**
 * Discriminated union of all protocol notifications.
 */
export type IProtocolNotification =
  | ISessionAddedNotification
  | ISessionRemovedNotification;
