/**
 * Pluggable persistence for stable per-host `clientId`s.
 *
 * The AHP `reconnect` flow uses `clientId` to identify a logical client
 * across reconnects. Apps that need cross-launch identity (i.e. resume
 * an in-progress turn after the user kills the app) must persist the
 * `clientId` somewhere durable and surface it through a {@link ClientIdStore}.
 *
 * The default in-memory store ({@link InMemoryClientIdStore}) keeps ids
 * stable within a single process but resets on restart — fine for
 * tests and ephemeral CLIs. Production apps wrap their platform's
 * secure storage (Keychain, `localStorage`, IndexedDB, Node `fs`,
 * Electron `safeStorage`, …) in a custom {@link ClientIdStore}.
 *
 * @module client/hosts/client-id-store
 */

import type { HostId } from './types.js';

/**
 * Persistence hook for stable `clientId`s per host.
 *
 * Implementations must be safe to call concurrently for different host
 * ids; the supervisor calls `load` once per `addHost` and `store` once
 * per resolved id. The optional `signal` is aborted when the host is
 * being removed or the multi-host client is shutting down; long-running
 * stores SHOULD honour it where practical.
 *
 * Errors surface as {@link ClientIdStoreError} from
 * {@link MultiHostClient.addHost} so persistent stores fail loudly
 * instead of silently dropping ids.
 */
export interface ClientIdStore {
  /** Look up the previously stored `clientId` for `hostId`, if any. */
  load(hostId: HostId, signal?: AbortSignal): Promise<string | null>;
  /**
   * Persist `clientId` for `hostId`. Implementations must overwrite any
   * previous value.
   */
  store(hostId: HostId, clientId: string, signal?: AbortSignal): Promise<void>;
}

/**
 * In-process {@link ClientIdStore} backed by a `Map`.
 *
 * Survives reconnects within the same process but not restarts. Fine
 * for tests, ephemeral CLIs, and as a starting point.
 */
export class InMemoryClientIdStore implements ClientIdStore {
  private readonly inner = new Map<HostId, string>();

  load(hostId: HostId): Promise<string | null> {
    return Promise.resolve(this.inner.get(hostId) ?? null);
  }

  store(hostId: HostId, clientId: string): Promise<void> {
    this.inner.set(hostId, clientId);
    return Promise.resolve();
  }
}
