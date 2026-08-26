/**
 * Tests for {@link WebSocketTransport} close semantics.
 *
 * Uses a minimal in-process WebSocket mock so the test never touches a
 * real network. The mock implements the subset of the browser
 * `WebSocket` interface that `WebSocketTransport` consumes.
 */

import test from 'node:test';
import assert from 'node:assert/strict';

import { TransportError } from '../src/client/index.js';
import { WebSocketTransport } from '../src/ws/index.js';

type Listener = (ev: unknown) => void;

class MockWebSocket {
  static CONNECTING = 0 as const;
  static OPEN = 1 as const;
  static CLOSING = 2 as const;
  static CLOSED = 3 as const;

  readyState: number = MockWebSocket.OPEN;
  binaryType: 'arraybuffer' | 'blob' = 'blob';
  bufferedAmount = 0;

  readonly OPEN = MockWebSocket.OPEN;
  readonly CONNECTING = MockWebSocket.CONNECTING;
  readonly CLOSING = MockWebSocket.CLOSING;
  readonly CLOSED = MockWebSocket.CLOSED;

  private listeners = new Map<string, Listener[]>();

  send(_data: string | ArrayBufferLike): void { void _data; }
  close(): void { this.readyState = MockWebSocket.CLOSED; }

  addEventListener(event: string, listener: Listener): void {
    const arr = this.listeners.get(event) ?? [];
    arr.push(listener);
    this.listeners.set(event, arr);
  }
  removeEventListener(event: string, listener: Listener): void {
    const arr = this.listeners.get(event);
    if (!arr) return;
    const idx = arr.indexOf(listener);
    if (idx >= 0) arr.splice(idx, 1);
  }

  /** @internal helper for tests to fire events at the mock. */
  fire(event: string, payload: unknown): void {
    for (const l of this.listeners.get(event) ?? []) l(payload);
  }
}

class OpeningMockWebSocket extends MockWebSocket {
  static instances: OpeningMockWebSocket[] = [];

  override readyState: number = MockWebSocket.CONNECTING;
  closeCount = 0;

  constructor(_url: string | URL, _protocols?: string | string[]) {
    super();
    OpeningMockWebSocket.instances.push(this);
  }

  override close(): void {
    this.closeCount++;
    this.readyState = MockWebSocket.CLOSED;
  }
}

async function withOpeningMock<T>(run: () => Promise<T>): Promise<T> {
  const target = globalThis as { WebSocket?: typeof WebSocket };
  const original = target.WebSocket;
  OpeningMockWebSocket.instances = [];
  target.WebSocket = OpeningMockWebSocket as unknown as typeof WebSocket;
  try {
    return await run();
  } finally {
    target.WebSocket = original;
  }
}

async function makeTransport(): Promise<{ ws: MockWebSocket; transport: import('../src/ws/index.js').WebSocketTransport }> {
  const ws = new MockWebSocket();
  return { ws, transport: WebSocketTransport.fromSocket(ws as unknown as WebSocket) };
}

test('clean close drains every pending recv() waiter with null', async () => {
  const { ws, transport } = await makeTransport();

  // Park multiple concurrent recv() calls.
  const a = transport.recv();
  const b = transport.recv();
  const c = transport.recv();

  // Clean close.
  ws.fire('close', { code: 1000, reason: '', wasClean: true });

  const results = await Promise.all([a, b, c]);
  for (const r of results) assert.equal(r, null);
});

test('abnormal close rejects every pending recv() with TransportError', async () => {
  const { ws, transport } = await makeTransport();

  const a = transport.recv();
  const b = transport.recv();

  // Abnormal close (wasClean=false).
  ws.fire('close', { code: 1006, reason: 'abnormal', wasClean: false });

  await assert.rejects(a, (err: unknown) => err instanceof TransportError && (err as TransportError).kind === 'closed');
  await assert.rejects(b, (err: unknown) => err instanceof TransportError && (err as TransportError).kind === 'closed');

  // Subsequent recv() also rejects with the same error class (sticky).
  await assert.rejects(transport.recv(), (err: unknown) => err instanceof TransportError);
});

test('lastClose reflects the wasClean flag from the close event', async () => {
  const { ws, transport } = await makeTransport();
  ws.fire('close', { code: 1006, reason: 'gone', wasClean: false });
  const info = transport.lastClose;
  assert.ok(info);
  assert.equal(info.code, 1006);
  assert.equal(info.wasClean, false);
  assert.equal(info.reason, 'gone');
});

test('connect does not construct a socket when its signal is already aborted', async () => {
  await withOpeningMock(async () => {
    const controller = new AbortController();
    controller.abort(new Error('stop'));

    await assert.rejects(
      WebSocketTransport.connect('ws://example.invalid', { signal: controller.signal }),
      (err: unknown) => err instanceof TransportError && err.message.includes('cancelled'),
    );
    assert.equal(OpeningMockWebSocket.instances.length, 0);
  });
});

test('connect cancellation closes a socket that is still opening', async () => {
  await withOpeningMock(async () => {
    const controller = new AbortController();
    const connecting = WebSocketTransport.connect('ws://example.invalid', { signal: controller.signal });
    const socket = OpeningMockWebSocket.instances[0];
    assert.ok(socket);

    controller.abort();

    await assert.rejects(
      connecting,
      (err: unknown) => err instanceof TransportError && err.message.includes('cancelled'),
    );
    assert.equal(socket.closeCount, 1);
  });
});

test('connect timeout closes a socket that never opens', async () => {
  await withOpeningMock(async () => {
    const connecting = WebSocketTransport.connect('ws://example.invalid', { timeoutMs: 5 });
    const socket = OpeningMockWebSocket.instances[0];
    assert.ok(socket);

    await assert.rejects(
      connecting,
      (err: unknown) => err instanceof TransportError && err.message.includes('timed out after 5ms'),
    );
    assert.equal(socket.closeCount, 1);
  });
});

test('connect resolves an opened socket before its timeout', async () => {
  await withOpeningMock(async () => {
    const connecting = WebSocketTransport.connect('ws://example.invalid', { timeoutMs: 50 });
    const socket = OpeningMockWebSocket.instances[0];
    assert.ok(socket);

    socket.readyState = MockWebSocket.OPEN;
    socket.fire('open', {});

    const transport = await connecting;
    assert.equal(transport.bufferedAmount, 0);
    assert.equal(socket.closeCount, 0);
  });
});
