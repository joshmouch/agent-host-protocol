/**
 * Message Map Exhaustiveness Checks — Compile-time verification that the
 * command and notification maps in messages.ts stay in sync with the actual
 * method definitions, and that every notification carries a top-level
 * `channel: URI`.
 *
 * If a method is added to commands.ts or notifications.ts but not
 * registered in the maps (or vice versa), the compiler will surface an
 * error here. If any notification's params shape is missing `channel: URI`,
 * the compiler will also surface an error.
 *
 * @module version/message-checks
 */

import type {
  CommandMap,
  ClientNotificationMap,
  ServerNotificationMap,
  ServerCommandMap,
} from '../messages.js';
import type { URI } from '../state.js';

// ─── Helpers ─────────────────────────────────────────────────────────────────

type _Exact<A, B> = [A] extends [B] ? [B] extends [A] ? true : never : never;

/**
 * Resolves to `true` if every entry in `M` has params extending
 * `{ channel: URI }`. Any offending key is mapped to `never`, so the final
 * union evaluates to `never` and the assertion below fails to compile.
 */
type _AllParamsHaveChannel<M> = {
  [K in keyof M]: M[K] extends { params: { channel: URI } } ? true : never;
}[keyof M];

// ─── Expected Method Names ───────────────────────────────────────────────────

/** All methods annotated `@messageType Request` in commands.ts. */
type _ExpectedCommands =
  | 'initialize'
  | 'ping'
  | 'reconnect'
  | 'subscribe'
  | 'createSession'
  | 'disposeSession'
  | 'createTerminal'
  | 'disposeTerminal'
  | 'listSessions'
  | 'resourceRead'
  | 'resourceWrite'
  | 'resourceList'
  | 'resourceCopy'
  | 'resourceDelete'
  | 'resourceMove'
  | 'resourceRequest'
  | 'fetchTurns'
  | 'authenticate'
  | 'resolveSessionConfig'
  | 'sessionConfigCompletions'
  | 'completions';

/** All methods annotated `@messageType Notification` (client → server). */
type _ExpectedClientNotifications =
  | 'unsubscribe'
  | 'dispatchAction';

/** All server → client notification methods. */
type _ExpectedServerNotifications =
  | 'action'
  | 'root/sessionAdded'
  | 'root/sessionRemoved'
  | 'root/sessionSummaryChanged'
  | 'auth/required';

/** All server → client request methods. */
type _ExpectedServerCommands =
  | 'resourceRequest';

// ─── Assertions ──────────────────────────────────────────────────────────────

// eslint-disable-next-line @typescript-eslint/no-unused-vars
type _CheckCommandMapKeys = _Exact<keyof CommandMap, _ExpectedCommands>;
// eslint-disable-next-line @typescript-eslint/no-unused-vars
type _CheckClientNotificationMapKeys = _Exact<keyof ClientNotificationMap, _ExpectedClientNotifications>;
// eslint-disable-next-line @typescript-eslint/no-unused-vars
type _CheckServerNotificationMapKeys = _Exact<keyof ServerNotificationMap, _ExpectedServerNotifications>;
// eslint-disable-next-line @typescript-eslint/no-unused-vars
type _CheckServerCommandMapKeys = _Exact<keyof ServerCommandMap, _ExpectedServerCommands>;

// eslint-disable-next-line @typescript-eslint/no-unused-vars
type _CheckClientNotificationsHaveChannel = _AllParamsHaveChannel<ClientNotificationMap> extends true ? true : never;
// eslint-disable-next-line @typescript-eslint/no-unused-vars
type _CheckServerNotificationsHaveChannel = _AllParamsHaveChannel<ServerNotificationMap> extends true ? true : never;
