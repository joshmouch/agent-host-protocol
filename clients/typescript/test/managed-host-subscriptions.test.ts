import test from 'node:test';
import assert from 'node:assert/strict';

import type { ActionEnvelope, StateAction } from '../src/types/common/actions.js';
import { ActionType } from '../src/types/common/actions.js';
import {
  HostSubscriptionMissingError,
  ManagedHostSubscriptionRegistry,
  type ManagedHostSubscriptionBinding,
} from '../src/client/hosts/managed-subscriptions.js';

const URI = 'ahp-session:/persistent';

function action(serverSeq: number): ActionEnvelope {
  return {
    channel: URI,
    serverSeq,
    action: {
      type: ActionType.SessionTitleChanged,
      title: `generation-${serverSeq}`,
    } as unknown as StateAction,
    origin: null,
  };
}

function binding(
  generation: number,
  subscribe: ManagedHostSubscriptionBinding['subscribe'] = async () => ({}),
  unsubscribe: ManagedHostSubscriptionBinding['unsubscribe'] = async () => undefined,
): ManagedHostSubscriptionBinding {
  return { generation, subscribe, unsubscribe };
}

test('one logical lease survives replay restoration and fences stale events', async () => {
  const tracked = new Set<string>();
  const registry = new ManagedHostSubscriptionRegistry(
    'local',
    uri => tracked.add(uri),
    uri => tracked.delete(uri),
  );
  const lease = registry.acquire(URI, 'SessionEditor');

  registry.restore(binding(1), {
    generation: 1,
    restored: new Set([URI]),
    snapshots: new Map([[URI, { snapshot: { resource: URI, state: {}, fromSeq: 3 } }]]),
    missing: new Set(),
    replay: [],
  });
  assert.equal((await lease.subscription.ready).snapshot?.fromSeq, 3);
  assert.equal(lease.subscription.generation, 1);
  assert.equal((await lease.updates.next()).value?.status, 'active');

  registry.suspend(1);
  assert.equal(lease.subscription.status, 'reconnecting');
  assert.equal((await lease.updates.next()).value?.status, 'reconnecting');
  registry.receive({ channel: URI, event: { type: 'action', params: action(4) } }, 1);

  registry.restore(binding(2), {
    generation: 2,
    restored: new Set([URI]),
    snapshots: new Map(),
    missing: new Set(),
    replay: [action(5)],
  });
  assert.equal(lease.subscription.status, 'active');
  assert.equal(lease.subscription.generation, 2);
  assert.equal((await lease.updates.next()).value?.generation, 2);
  const replayed = await lease.events.next();
  assert.equal(replayed.value?.type, 'action');
  if (replayed.value?.type === 'action') assert.equal(replayed.value.params.serverSeq, 5);
  assert.ok(tracked.has(URI));

  lease.release();
  assert.ok(!tracked.has(URI));
});

test('missing restoration reports a typed failure and reacquires cleanly', async () => {
  let subscribeCalls = 0;
  const registry = new ManagedHostSubscriptionRegistry('remote', () => undefined, () => undefined);
  const first = registry.acquire(URI, 'View');
  registry.restore(binding(1), {
    generation: 1,
    restored: new Set([URI]),
    snapshots: new Map(),
    missing: new Set(),
    replay: [],
  });
  await first.subscription.ready;
  registry.suspend(1);
  registry.restore(binding(2, async () => {
    subscribeCalls += 1;
    return { snapshot: { resource: URI, state: {}, fromSeq: 9 } };
  }), {
    generation: 2,
    restored: new Set(),
    snapshots: new Map(),
    missing: new Set([URI]),
    replay: [],
  });

  assert.equal(first.subscription.status, 'missing');
  assert.ok(first.subscription.error instanceof HostSubscriptionMissingError);

  const retry = registry.acquire(URI, 'View');
  assert.notEqual(retry.subscription, first.subscription);
  assert.equal((await retry.subscription.ready).snapshot?.fromSeq, 9);
  assert.equal(subscribeCalls, 1);

  first.release();
  assert.equal(registry.activeSubscriptions()[0]?.status, 'active');
  retry.release();
});

test('a late generation result cannot replace current restoration state', async () => {
  let resolveOld!: (value: {}) => void;
  const oldResult = new Promise<{}>(resolve => { resolveOld = resolve; });
  const registry = new ManagedHostSubscriptionRegistry('race', () => undefined, () => undefined);

  registry.restore(binding(1, () => oldResult), {
    generation: 1,
    restored: new Set(),
    snapshots: new Map(),
    missing: new Set(),
    replay: [],
  });
  const lease = registry.acquire(URI, 'Race');
  registry.suspend(1);
  registry.restore(binding(2, async () => ({
    snapshot: { resource: URI, state: {}, fromSeq: 2 },
  })), {
    generation: 2,
    restored: new Set(),
    snapshots: new Map(),
    missing: new Set(),
    replay: [],
  });

  resolveOld({});
  assert.equal((await lease.subscription.ready).snapshot?.fromSeq, 2);
  assert.equal(lease.subscription.generation, 2);
  lease.release();
});

test('last release targets only the current transport generation', async () => {
  const unsubscribed: Array<{ generation: number; uri: string }> = [];
  const registry = new ManagedHostSubscriptionRegistry('fenced', () => undefined, () => undefined);
  const lease = registry.acquire(URI, 'Owner');
  registry.restore(binding(1, undefined, async uri => {
    unsubscribed.push({ generation: 1, uri });
  }), {
    generation: 1,
    restored: new Set([URI]),
    snapshots: new Map(),
    missing: new Set(),
    replay: [],
  });
  await lease.subscription.ready;
  registry.suspend(1);
  registry.restore(binding(2, undefined, async uri => {
    unsubscribed.push({ generation: 2, uri });
  }), {
    generation: 2,
    restored: new Set([URI]),
    snapshots: new Map(),
    missing: new Set(),
    replay: [],
  });
  lease.release();
  await Promise.resolve();
  assert.deepEqual(unsubscribed, [{ generation: 2, uri: URI }]);
});
