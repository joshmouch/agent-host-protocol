/**
 * Tests for `read-telemetry.ts` — the shared ts-morph reader the per-language
 * generators use to extract the telemetry-name enums + their descriptions.
 *
 * These run over a small IN-MEMORY fixture registry (built with
 * `project.createSourceFile`) rather than the real `types/telemetry/registry.ts`
 * so the assertions pin the reader's behavior independently of the live
 * contract: unit resolution from the computed `[TelemetryMetric.X]` keys,
 * `getJsDocs()` member-description extraction (including the empty-doc case),
 * and the module-doc-vs-const-doc heuristic that picks the LAST leading JSDoc
 * for `TELEMETRY_SOURCE`.
 */

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { Project } from 'ts-morph';

import { readTelemetry } from './read-telemetry.js';

/**
 * Build an in-memory project whose only source file is a telemetry registry at
 * a path that ends in `/telemetry/registry.ts` (which is how `readTelemetry`
 * locates it). The fixture mirrors the real registry's SHAPE — a module-level
 * JSDoc, a `TELEMETRY_SOURCE` const with its own doc, a `TELEMETRY_METRIC_UNITS`
 * record keyed by computed `[TelemetryMetric.X]` members, and the span / metric
 * / attribute / value enums — but with trimmed, fixture-only contents.
 */
function fixtureProject(source: string): Project {
  const project = new Project({ useInMemoryFileSystem: true });
  project.createSourceFile('types/telemetry/registry.ts', source);
  return project;
}

const FIXTURE = `
/**
 * Module-level doc that must NOT be mistaken for the TELEMETRY_SOURCE doc.
 * @module telemetry/registry
 */

/** The instrumentation-scope name. */
export const TELEMETRY_SOURCE = 'Fixture.Scope';

export enum TelemetrySpan {
  /** A request span. */
  Request = 'ahp.request',
}

export enum TelemetryMetric {
  /** Messages sent. */
  MessagesSent = 'ahp.client.messages.sent',
  /** Request duration. */
  RequestDuration = 'ahp.client.request.duration',
}

export const TELEMETRY_METRIC_UNITS: Record<TelemetryMetric, string> = {
  [TelemetryMetric.MessagesSent]: '{message}',
  [TelemetryMetric.RequestDuration]: 'ms',
};

export enum TelemetryAttribute {
  /** rpc.method tag. */
  RpcMethod = 'rpc.method',
  // intentionally undocumented to exercise the empty-doc branch
  Stream = 'ahp.stream',
}

export enum TelemetryRpcSystem {
  /** jsonrpc. */
  Jsonrpc = 'jsonrpc',
}

export enum TelemetryOutcome {
  /** ok. */
  Ok = 'ok',
}

export enum TelemetryMessageKind {
  /** request. */
  Request = 'request',
}

export enum TelemetryStream {
  /** subscription stream. */
  Subscription = 'subscription',
  /** multi-host event stream. */
  HostEvent = 'host-event',
}
`;

test('reads source value and the CONST doc (not the module doc)', () => {
  const data = readTelemetry(fixtureProject(FIXTURE));
  assert.equal(data.source.value, 'Fixture.Scope');
  // The module-level JSDoc precedes the const's own JSDoc; the reader picks the
  // LAST leading doc, which is the const's own.
  assert.equal(data.source.doc, 'The instrumentation-scope name.');
  assert.doesNotMatch(data.source.doc, /Module-level doc/);
});

test('extracts span name + value + member JSDoc', () => {
  const data = readTelemetry(fixtureProject(FIXTURE));
  assert.deepEqual(data.spans, [
    { id: 'Request', value: 'ahp.request', doc: 'A request span.' },
  ]);
});

test('resolves each metric to its unit via the computed [TelemetryMetric.X] keys', () => {
  const data = readTelemetry(fixtureProject(FIXTURE));
  const byId = Object.fromEntries(data.metrics.map((m) => [m.id, m]));
  assert.equal(byId.MessagesSent.unit, '{message}');
  assert.equal(byId.MessagesSent.value, 'ahp.client.messages.sent');
  assert.equal(byId.RequestDuration.unit, 'ms');
});

test('an undocumented member yields an empty doc string', () => {
  const data = readTelemetry(fixtureProject(FIXTURE));
  const stream = data.attributes.find((a) => a.id === 'Stream');
  assert.ok(stream, 'expected a Stream attribute');
  assert.equal(stream.doc, '');
});

test('groups attribute VALUE enums (minus the Telemetry prefix), including hyphenated values', () => {
  const data = readTelemetry(fixtureProject(FIXTURE));
  const groupNames = data.values.map((g) => g.group);
  assert.deepEqual(groupNames, ['RpcSystem', 'Outcome', 'MessageKind', 'Stream']);

  const streamGroup = data.values.find((g) => g.group === 'Stream');
  assert.ok(streamGroup, 'expected a Stream value group');
  const hostEvent = streamGroup.members.find((m) => m.id === 'HostEvent');
  assert.ok(hostEvent, 'expected the HostEvent value');
  assert.equal(hostEvent.value, 'host-event');
});

test('throws loudly when a required enum is missing', () => {
  const broken = `
    /** doc */
    export const TELEMETRY_SOURCE = 'X';
    export const TELEMETRY_METRIC_UNITS: Record<string, string> = {};
    export enum TelemetrySpan { Request = 'ahp.request' }
  `;
  assert.throws(
    () => readTelemetry(fixtureProject(broken)),
    /enum TelemetryMetric not found/,
  );
});

test('throws when the registry source file is not in the project', () => {
  const project = new Project({ useInMemoryFileSystem: true });
  project.createSourceFile('types/unrelated.ts', 'export const x = 1;');
  assert.throws(() => readTelemetry(project), /could not locate a .*\/telemetry\/registry\.ts source file/);
});

test('throws when an enum member is not string-valued', () => {
  const numeric = `
    /** doc */
    export const TELEMETRY_SOURCE = 'X';
    export enum TelemetrySpan {
      /** numeric */
      Request = 1,
    }
    export enum TelemetryMetric { MessagesSent = 'ahp.client.messages.sent' }
    export const TELEMETRY_METRIC_UNITS: Record<TelemetryMetric, string> = {
      [TelemetryMetric.MessagesSent]: '{message}',
    };
    export enum TelemetryAttribute { RpcMethod = 'rpc.method' }
    export enum TelemetryRpcSystem { Jsonrpc = 'jsonrpc' }
    export enum TelemetryOutcome { Ok = 'ok' }
    export enum TelemetryMessageKind { Request = 'request' }
    export enum TelemetryStream { Subscription = 'subscription' }
  `;
  assert.throws(
    () => readTelemetry(fixtureProject(numeric)),
    /TelemetrySpan\.Request is not a string-valued enum member/,
  );
});
