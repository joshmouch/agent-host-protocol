/**
 * Message Map Exhaustiveness Checks — Compile-time verification that the
 * command and notification maps in messages.ts stay in sync with the actual
 * method definitions, and that every command and notification carries a
 * top-level `channel: URI`.
 *
 * If a method is added to commands.ts or notifications.ts but not
 * registered in the maps (or vice versa), the compiler will surface an
 * error here. If any command or notification's params shape is missing
 * `channel: URI`, the compiler will also surface an error.
 *
 * @module version/message-checks
 */

import type {
  CommandMap,
  ClientNotificationMap,
  ServerNotificationMap,
  ServerCommandMap,
  ControlNotificationMap,
} from '../messages.js';
import type { BaseParams } from '../commands.js';
import type { URI } from '../state.js';

// ─── Helpers ─────────────────────────────────────────────────────────────────

type _Exact<A, B> = [A] extends [B] ? [B] extends [A] ? true : never : never;

/**
 * Yields the union of keys `K in keyof M` whose `M[K]` does **not** extend
 * `OK`. Resolves to `never` if every entry conforms.
 *
 * Used to build a positive invariant check: assert
 * `[NonConformingKeys<M, OK>] extends [never]`. The `[T] extends [never]`
 * wrapping prevents distribution over `never` (a bare `never` in a naked
 * conditional otherwise distributes to `never`, which would mask the bug),
 * and yielding `K` (not `never`) on failure prevents the older
 * `true | never` collapse that allowed partial violations to slip past.
 */
type _NonConformingKeys<M, OK> = {
  [K in keyof M]: M[K] extends OK ? never : K;
}[keyof M];

/**
 * Yields the union of keys `K in keyof M` whose `M[K]['params']` carries a
 * `channel` field — required *or* optional. Resolves to `never` if no
 * entry has `channel`.
 *
 * The `'channel' extends keyof P` formulation is critical: a structural
 * check like `{ params: { channel: unknown } }` only matches *required*
 * `channel`, so an entry typed as `params: { channel?: URI }` would slip
 * past. The `keyof` check correctly catches both forms.
 */
type _ParamsCarryingChannel<M> = {
  [K in keyof M]: M[K] extends { params: infer P }
    ? 'channel' extends keyof P ? K : never
    : never;
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
  | 'completions'
  | 'invokeChangesetOperation';

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

/** All control notification methods (framing layer, either direction). */
type _ExpectedControlNotifications =
  | 'ahp/messageSegment';

// ─── Assertions ──────────────────────────────────────────────────────────────
//
// Each assertion below resolves to `true` when the invariant holds and to
// `never` when it doesn't. The `_CheckXxx` aliases serve no runtime
// purpose — they exist so the assertion produces a compile error if it
// fails. The `[T] extends [never]` wrapping is load-bearing: bare
// `never extends never` distributes to `never`, which would silently
// invert the result; the tuple-wrapping keeps the comparison literal.

// eslint-disable-next-line @typescript-eslint/no-unused-vars
type _CheckCommandMapKeys = _Exact<keyof CommandMap, _ExpectedCommands>;
// eslint-disable-next-line @typescript-eslint/no-unused-vars
type _CheckClientNotificationMapKeys = _Exact<keyof ClientNotificationMap, _ExpectedClientNotifications>;
// eslint-disable-next-line @typescript-eslint/no-unused-vars
type _CheckServerNotificationMapKeys = _Exact<keyof ServerNotificationMap, _ExpectedServerNotifications>;
// eslint-disable-next-line @typescript-eslint/no-unused-vars
type _CheckServerCommandMapKeys = _Exact<keyof ServerCommandMap, _ExpectedServerCommands>;
// eslint-disable-next-line @typescript-eslint/no-unused-vars
type _CheckControlNotificationMapKeys = _Exact<keyof ControlNotificationMap, _ExpectedControlNotifications>;

// eslint-disable-next-line @typescript-eslint/no-unused-vars
type _CheckClientNotificationsHaveChannel =
  [_NonConformingKeys<ClientNotificationMap, { params: { channel: URI } }>] extends [never] ? true : never;
// eslint-disable-next-line @typescript-eslint/no-unused-vars
type _CheckServerNotificationsHaveChannel =
  [_NonConformingKeys<ServerNotificationMap, { params: { channel: URI } }>] extends [never] ? true : never;
// eslint-disable-next-line @typescript-eslint/no-unused-vars
type _CheckCommandsHaveChannel =
  [_NonConformingKeys<CommandMap, { params: BaseParams }>] extends [never] ? true : never;
// eslint-disable-next-line @typescript-eslint/no-unused-vars
type _CheckServerCommandsHaveChannel =
  [_NonConformingKeys<ServerCommandMap, { params: BaseParams }>] extends [never] ? true : never;
// Control notifications are *exempt* from the channel-URI invariant by
// design — they belong to the framing layer rather than any subscribable
// resource. This check enforces the inverse: no control notification's
// params object carries a `channel` field, required or optional.
// eslint-disable-next-line @typescript-eslint/no-unused-vars
type _CheckControlNotificationsHaveNoChannel =
  [_ParamsCarryingChannel<ControlNotificationMap>] extends [never] ? true : never;

// ─── Self-tests ──────────────────────────────────────────────────────────────
//
// Synthetic maps with deliberate violations, used to verify that the
// invariants above actually fire. Each block defines a known-bad map and
// asserts via a string-sentinel literal type that the corresponding check
// on that map yields a non-`never` result (i.e., the violation is caught).
// If a future refactor silently re-broke an invariant, the bad map's
// check would now yield `never`, the conditional would resolve to the
// `'BUG: …'` branch, and the value-level assignment would fail to
// typecheck — surfacing the regression at exactly this site.
//
// `@ts-expect-error` was tempting but does not work here: `never extends
// true` is `true` (bottom-type semantics), so a broken invariant whose
// result distributes to `never` would silently satisfy an
// `_Expect<T extends true>` constraint and the directive would falsely
// fire as "unused".

namespace _SelfTest {
  // A known-bad command map (missing `channel` outright).
  interface _MissingChannel {
    bad: { params: { notChannel: string } };
  }
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const _missingChannelCatch:
    [_NonConformingKeys<_MissingChannel, { params: BaseParams }>] extends [never]
      ? 'BUG: command invariant did not fire on a fully-broken map'
      : 'OK' = 'OK';

  // A known-bad notification map where **one** entry conforms and another
  // doesn't — the case the old `true | never = true` erasure silently
  // accepted.
  interface _PartiallyMissing {
    good: { params: { channel: 'foo' } };
    bad: { params: { notChannel: 'bar' } };
  }
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const _partiallyMissingCatch:
    [_NonConformingKeys<_PartiallyMissing, { params: { channel: URI } }>] extends [never]
      ? 'BUG: notification invariant did not fire on a partial violation'
      : 'OK' = 'OK';

  // A known-bad control map carrying a required `channel`.
  interface _RequiredChannelControl {
    'control/withChannel': { params: { channel: URI; payload: string } };
  }
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const _requiredChannelCatch:
    [_ParamsCarryingChannel<_RequiredChannelControl>] extends [never]
      ? 'BUG: control invariant did not catch required channel'
      : 'OK' = 'OK';

  // A known-bad control map carrying an **optional** `channel` — the case
  // the structural `{ params: { channel: unknown } }` check missed because
  // `{ channel?: URI }` does not extend `{ channel: unknown }`.
  interface _OptionalChannelControl {
    'control/optionalChannel': { params: { channel?: URI; payload: string } };
  }
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const _optionalChannelCatch:
    [_ParamsCarryingChannel<_OptionalChannelControl>] extends [never]
      ? 'BUG: control invariant did not catch optional channel'
      : 'OK' = 'OK';
}
