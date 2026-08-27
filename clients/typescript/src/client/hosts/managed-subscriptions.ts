/** Long-lived managed subscriptions spanning host transport generations. */

import type { ActionEnvelope } from '../../types/common/actions.js';
import type { SubscribeResult } from '../../types/common/commands.js';
import type { URI } from '../../types/common/state.js';
import { AsyncBroadcastQueue } from '../async-queue.js';
import type { SubscribeOptions } from '../client.js';
import type { SubscriptionEvent } from '../events.js';
import { HostMultiError, type HostId } from './types.js';

export type ManagedHostSubscriptionStatus =
  | 'pending'
  | 'active'
  | 'reconnecting'
  | 'missing'
  | 'failed'
  | 'closed';

export class HostSubscriptionMissingError extends HostMultiError {
  readonly hostId: HostId;
  readonly uri: URI;

  constructor(hostId: HostId, uri: URI) {
    super(`subscription "${uri}" is missing after reconnecting host "${hostId}"`);
    this.name = 'HostSubscriptionMissingError';
    this.hostId = hostId;
    this.uri = uri;
  }
}

export interface ManagedHostSubscriptionUpdate {
  readonly status: ManagedHostSubscriptionStatus;
  readonly generation: number;
  readonly result: SubscribeResult | undefined;
  readonly error: Error | undefined;
}

export interface ManagedHostSubscription {
  readonly hostId: HostId;
  readonly uri: URI;
  readonly ready: Promise<SubscribeResult>;
  readonly status: ManagedHostSubscriptionStatus;
  readonly generation: number;
  readonly result: SubscribeResult | undefined;
  readonly error: Error | undefined;
}

export interface ManagedHostSubscriptionLease {
  readonly subscription: ManagedHostSubscription;
  readonly events: AsyncIterableIterator<SubscriptionEvent>;
  readonly updates: AsyncIterableIterator<ManagedHostSubscriptionUpdate>;
  release(): void;
}

export interface ManagedHostSubscriptionHolder {
  readonly owner: string;
  readonly count: number;
}

export interface ManagedHostSubscriptionInfo extends ManagedHostSubscriptionUpdate {
  readonly hostId: HostId;
  readonly uri: URI;
  readonly refCount: number;
  readonly holders: readonly ManagedHostSubscriptionHolder[];
}

interface Entry {
  readonly uri: URI;
  readonly options: SubscribeOptions;
  readonly optionsKey: string;
  readonly subscription: ManagedHostSubscriptionState;
  readonly events: AsyncBroadcastQueue<SubscriptionEvent>;
  readonly updates: AsyncBroadcastQueue<ManagedHostSubscriptionUpdate>;
  readonly holders: Map<number, string>;
}

class ManagedHostSubscriptionState implements ManagedHostSubscription {
  readonly hostId: HostId;
  readonly uri: URI;
  readonly ready: Promise<SubscribeResult>;
  status: ManagedHostSubscriptionStatus = 'pending';
  generation = 0;
  result: SubscribeResult | undefined;
  error: Error | undefined;
  private readonly resolveReady: (result: SubscribeResult) => void;
  private readonly rejectReady: (error: Error) => void;
  private readySettled = false;

  constructor(hostId: HostId, uri: URI) {
    this.hostId = hostId;
    this.uri = uri;
    let resolveReady!: (result: SubscribeResult) => void;
    let rejectReady!: (error: Error) => void;
    this.ready = new Promise<SubscribeResult>((resolve, reject) => {
      resolveReady = resolve;
      rejectReady = reject;
    });
    void this.ready.catch(() => undefined);
    this.resolveReady = resolveReady;
    this.rejectReady = rejectReady;
  }

  transition(update: ManagedHostSubscriptionUpdate): void {
    this.status = update.status;
    this.generation = update.generation;
    this.result = update.result;
    this.error = update.error;
    if (!this.readySettled && update.status === 'active') {
      this.readySettled = true;
      this.resolveReady(update.result ?? {});
    } else if (!this.readySettled && (update.status === 'missing' || update.status === 'failed' || update.status === 'closed')) {
      this.readySettled = true;
      this.rejectReady(update.error ?? new Error(`subscription ${update.status}`));
    }
  }
}

export interface ManagedHostSubscriptionBinding {
  readonly generation: number;
  subscribe(uri: URI, options: SubscribeOptions): Promise<SubscribeResult>;
  unsubscribe(uri: URI): Promise<void>;
}

export interface ManagedHostSubscriptionRestoration {
  readonly generation: number;
  readonly restored: ReadonlySet<URI>;
  readonly snapshots: ReadonlyMap<URI, SubscribeResult>;
  readonly missing: ReadonlySet<URI>;
  readonly replay: readonly ActionEnvelope[];
}

/** @internal */
export class ManagedHostSubscriptionRegistry {
  private readonly hostId: HostId;
  private readonly track: (uri: URI) => void;
  private readonly untrack: (uri: URI) => void;
  private readonly buffer: number;
  private readonly entries = new Map<URI, Entry>();
  private binding: ManagedHostSubscriptionBinding | null = null;
  private highestGeneration = 0;
  private nextHolderId = 1;
  private closed = false;

  constructor(
    hostId: HostId,
    track: (uri: URI) => void,
    untrack: (uri: URI) => void,
    buffer = 1024,
  ) {
    this.hostId = hostId;
    this.track = track;
    this.untrack = untrack;
    this.buffer = buffer >= 1 ? Math.floor(buffer) : 1;
  }

  acquire(uri: URI, owner: string, options: SubscribeOptions = {}): ManagedHostSubscriptionLease {
    if (this.closed) throw new HostMultiError(`subscription registry for host "${this.hostId}" is closed`);
    const optionsKey = optionKey(options);
    let entry = this.entries.get(uri);
    if (entry && (entry.subscription.status === 'missing' || entry.subscription.status === 'failed')) {
      this.entries.delete(uri);
      this.closeEntry(entry);
      entry = undefined;
    }
    if (entry && entry.optionsKey !== optionsKey) {
      throw new TypeError(`subscription options for "${uri}" differ from the active subscription`);
    }
    if (!entry) {
      entry = {
        uri,
        options,
        optionsKey,
        subscription: new ManagedHostSubscriptionState(this.hostId, uri),
        events: new AsyncBroadcastQueue<SubscriptionEvent>(this.buffer),
        updates: new AsyncBroadcastQueue<ManagedHostSubscriptionUpdate>(this.buffer),
        holders: new Map(),
      };
      this.entries.set(uri, entry);
      this.track(uri);
    }
    const lease = this.makeLease(entry, owner);
    if (entry.holders.size === 1 && this.binding) void this.start(entry, this.binding);
    return lease;
  }

  activeSubscriptions(): ManagedHostSubscriptionInfo[] {
    return [...this.entries.values()]
      .sort((a, b) => a.uri.localeCompare(b.uri))
      .map(entry => ({
        hostId: this.hostId,
        uri: entry.uri,
        status: entry.subscription.status,
        generation: entry.subscription.generation,
        result: entry.subscription.result,
        error: entry.subscription.error,
        refCount: entry.holders.size,
        holders: summarize(entry.holders),
      }));
  }

  has(uri: URI): boolean {
    return this.entries.has(uri);
  }

  result(uri: URI): SubscribeResult | undefined {
    return this.entries.get(uri)?.subscription.result;
  }

  restore(binding: ManagedHostSubscriptionBinding, restoration: ManagedHostSubscriptionRestoration): void {
    if (restoration.generation !== binding.generation) {
      throw new Error('managed subscription restoration generation does not match its binding');
    }
    if (binding.generation <= this.highestGeneration) return;
    this.highestGeneration = binding.generation;
    this.binding = binding;
    for (const entry of this.entries.values()) {
      if (restoration.missing.has(entry.uri)) {
        this.untrack(entry.uri);
        this.transition(entry, {
          status: 'missing',
          generation: binding.generation,
          result: entry.subscription.result,
          error: new HostSubscriptionMissingError(this.hostId, entry.uri),
        });
        continue;
      }
      if (!restoration.restored.has(entry.uri)) {
        void this.start(entry, binding);
        continue;
      }
      this.transition(entry, {
        status: 'active',
        generation: binding.generation,
        result: restoration.snapshots.get(entry.uri) ?? entry.subscription.result ?? {},
        error: undefined,
      });
    }
    for (const envelope of restoration.replay) {
      const entry = this.entries.get(envelope.channel);
      if (entry?.subscription.status === 'active') {
        entry.events.publish({ type: 'action', params: envelope });
      }
    }
  }

  suspend(generation: number): void {
    if (this.binding?.generation !== generation) return;
    this.binding = null;
    for (const entry of this.entries.values()) {
      if (entry.subscription.status === 'active' || entry.subscription.status === 'pending') {
        this.transition(entry, {
          status: 'reconnecting',
          generation,
          result: entry.subscription.result,
          error: undefined,
        });
      }
    }
  }

  receive(event: { channel: URI; event: SubscriptionEvent }, generation: number): void {
    if (this.binding?.generation !== generation) return;
    const entry = this.entries.get(event.channel);
    if (entry?.subscription.status === 'active' && entry.subscription.generation === generation) {
      entry.events.publish(event.event);
    }
  }

  close(): void {
    if (this.closed) return;
    this.closed = true;
    const entries = [...this.entries.values()];
    this.entries.clear();
    for (const entry of entries) this.closeEntry(entry);
    this.binding = null;
  }

  private makeLease(entry: Entry, owner: string): ManagedHostSubscriptionLease {
    const holderId = this.nextHolderId++;
    entry.holders.set(holderId, owner);
    const events = entry.events.reader();
    const updates = entry.updates.reader();
    let released = false;
    return {
      subscription: entry.subscription,
      events,
      updates,
      release: () => {
        if (released) return;
        released = true;
        void events.return?.();
        void updates.return?.();
        entry.holders.delete(holderId);
        if (entry.holders.size === 0 && this.entries.get(entry.uri) === entry) {
          this.entries.delete(entry.uri);
          this.untrack(entry.uri);
          const binding = this.binding;
          if (binding) void binding.unsubscribe(entry.uri).catch(() => undefined);
          this.closeEntry(entry);
        }
      },
    };
  }

  private async start(entry: Entry, binding: ManagedHostSubscriptionBinding): Promise<void> {
    try {
      const result = await binding.subscribe(entry.uri, entry.options);
      if (this.binding !== binding || this.entries.get(entry.uri) !== entry) return;
      this.transition(entry, {
        status: 'active',
        generation: binding.generation,
        result,
        error: undefined,
      });
    } catch (cause) {
      if (this.binding !== binding || this.entries.get(entry.uri) !== entry) return;
      this.untrack(entry.uri);
      this.transition(entry, {
        status: 'failed',
        generation: binding.generation,
        result: entry.subscription.result,
        error: cause instanceof Error ? cause : new Error(String(cause)),
      });
    }
  }

  private transition(entry: Entry, update: ManagedHostSubscriptionUpdate): void {
    entry.subscription.transition(update);
    entry.updates.publish(update);
  }

  private closeEntry(entry: Entry): void {
    const error = entry.subscription.error ?? new HostMultiError(`subscription "${entry.uri}" closed`);
    entry.subscription.transition({
      status: 'closed',
      generation: entry.subscription.generation,
      result: entry.subscription.result,
      error,
    });
    entry.events.close();
    entry.updates.close();
  }
}

function optionKey(options: SubscribeOptions): string {
  return JSON.stringify({
    maxLatencyMs: options.delivery?.maxLatencyMs ?? null,
    turns: options.view?.turns ?? null,
  });
}

function summarize(holders: ReadonlyMap<number, string>): ManagedHostSubscriptionHolder[] {
  const counts = new Map<string, number>();
  for (const owner of holders.values()) counts.set(owner, (counts.get(owner) ?? 0) + 1);
  return [...counts.entries()]
    .map(([owner, count]) => ({ owner, count }))
    .sort((a, b) => b.count - a.count || a.owner.localeCompare(b.owner));
}
