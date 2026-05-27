/**
 * Unit tests for the internal AsyncBroadcastQueue used by AhpClient.
 */

import test from 'node:test';
import assert from 'node:assert/strict';
import { AsyncBroadcastQueue } from '../src/client/async-queue.js';

test('reader created after publish does not replay history', async () => {
  const q = new AsyncBroadcastQueue<number>();
  q.publish(1); // dropped — no readers
  const r = q.reader();
  q.publish(2);
  const a = await r.next();
  assert.equal(a.done, false);
  assert.equal(a.value, 2);
});

test('multiple readers each see every published value', async () => {
  const q = new AsyncBroadcastQueue<number>();
  const r1 = q.reader();
  const r2 = q.reader();
  q.publish(1);
  q.publish(2);
  assert.equal((await r1.next()).value, 1);
  assert.equal((await r2.next()).value, 1);
  assert.equal((await r1.next()).value, 2);
  assert.equal((await r2.next()).value, 2);
});

test('return() detaches a reader and trims unreachable entries', async () => {
  const q = new AsyncBroadcastQueue<number>();
  const r1 = q.reader();
  const r2 = q.reader();
  q.publish(1);
  // r1 consumes; r2 has not.
  assert.equal((await r1.next()).value, 1);
  // Detach r2 — r1 has already consumed and there's nobody else, so
  // the buffer can be trimmed.
  await r2.return!();
  // r1's next call should park, and a subsequent publish wakes it.
  const pending = r1.next();
  q.publish(2);
  assert.equal((await pending).value, 2);
});

test('close() terminates pending readers', async () => {
  const q = new AsyncBroadcastQueue<number>();
  const r = q.reader();
  const pending = r.next();
  q.close();
  const res = await pending;
  assert.equal(res.done, true);
});

test('next() after return() returns done immediately, even with unread buffer', async () => {
  const q = new AsyncBroadcastQueue<number>();
  const r = q.reader();
  q.publish(1);
  q.publish(2);
  // Detach before consuming the buffered values.
  await r.return!();
  // Even though there are unread values in the buffer, next() must
  // be terminal after return().
  const a = await r.next();
  assert.equal(a.done, true);
  const b = await r.next();
  assert.equal(b.done, true);
});

test('bounded buffer drops oldest and fast-forwards laggards', async () => {
  const q = new AsyncBroadcastQueue<number>(2);
  const r = q.reader();
  q.publish(1);
  q.publish(2);
  q.publish(3); // drops "1"; r's position should be fast-forwarded.
  q.publish(4); // drops "2"
  assert.equal((await r.next()).value, 3);
  assert.equal((await r.next()).value, 4);
});
