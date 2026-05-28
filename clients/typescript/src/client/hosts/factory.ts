/**
 * Pluggable transport factory used by {@link MultiHostClient} to open a
 * fresh {@link AhpTransport} for a host on every connect attempt
 * (including reconnects).
 *
 * Consumers refresh tokens, rotate URLs, or pick different backends per
 * attempt by inspecting `hostId`. The supplied `AbortSignal` is aborted
 * when the host is being removed, manually reconnected, or shut down;
 * factories SHOULD propagate the signal into any underlying networking
 * primitives that accept one (e.g. `fetch`, `WebSocket` opens via a
 * helper that bails on abort) so a slow handshake doesn't block teardown.
 *
 * @module client/hosts/factory
 */

import type { AhpTransport } from '../transport.js';
import type { HostId } from './types.js';

/**
 * Factory that opens (or re-opens) a transport for a host.
 *
 * Errors are surfaced as the host's `lastError` and trigger the
 * reconnect schedule (or the `failed` state if reconnects are disabled
 * or attempts are exhausted).
 *
 * @example
 * ```ts
 * const factory: HostTransportFactory = async (hostId, signal) => {
 *   const url = lookupUrl(hostId);
 *   return WebSocketTransport.connect(url, { signal });
 * };
 * ```
 */
export type HostTransportFactory = (
  hostId: HostId,
  signal: AbortSignal,
) => Promise<AhpTransport>;
