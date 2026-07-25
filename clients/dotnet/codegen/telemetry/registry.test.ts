/**
 * Tests for the telemetry registry — guards the invariants the per-language
 * generators rely on:
 *
 *  - `TELEMETRY_SOURCE` is non-empty.
 *  - Every span / metric / attribute NAME is lowercase-dotted (these become
 *    OTel instrument / attribute-key names, which OTel constrains to dotted
 *    lowercase).
 *  - Every enumerated attribute VALUE is a lowercase token, hyphens allowed —
 *    attribute values are NOT OTel instrument names (cf. OTel attribute values
 *    such as `http.request.method=GET`), so the multi-host `host-*` stream
 *    values are legitimately hyphenated.
 *  - Span / metric / attribute names are globally unique (no collisions).
 *  - `TELEMETRY_METRIC_UNITS` has a non-empty unit for every metric.
 */

import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  TELEMETRY_SOURCE,
  TELEMETRY_METRIC_UNITS,
  TelemetrySpan,
  TelemetryMetric,
  TelemetryAttribute,
  TelemetryRpcSystem,
  TelemetryOutcome,
  TelemetryMessageKind,
  TelemetryStream,
} from './registry.js';

/**
 * NAME shape — OTel instrument / attribute-key names are lowercase-dotted
 * (no hyphens). Spans, metrics, and attribute KEYS must match this.
 */
const DOTTED_RE = /^[a-z][a-z0-9_]*(\.[a-z0-9_]+)*$/;

/**
 * VALUE shape — enumerated attribute VALUES are free-form lowercase tokens.
 * Hyphens are permitted here (and only here): an attribute value is not an
 * OTel instrument name, so `host-event` is a legitimate value even though it
 * could never be a metric/span name. Still constrained to a tight charset so
 * a typo like an uppercase letter or whitespace is caught.
 */
const VALUE_RE = /^[a-z][a-z0-9_]*(-[a-z0-9_]+)*$/;

test('TELEMETRY_SOURCE is non-empty', () => {
  assert.ok(TELEMETRY_SOURCE.length > 0);
});

test('every span / metric / attribute NAME is lowercase-dotted', () => {
  for (const value of Object.values(TelemetrySpan)) {
    assert.match(value, DOTTED_RE, `span not dotted: ${value}`);
  }
  for (const value of Object.values(TelemetryMetric)) {
    assert.match(value, DOTTED_RE, `metric not dotted: ${value}`);
  }
  for (const value of Object.values(TelemetryAttribute)) {
    assert.match(value, DOTTED_RE, `attribute not dotted: ${value}`);
  }
});

test('every enumerated attribute VALUE is a lowercase token (hyphens allowed)', () => {
  const valueEnums = [
    TelemetryRpcSystem,
    TelemetryOutcome,
    TelemetryMessageKind,
    TelemetryStream,
  ];
  for (const valueEnum of valueEnums) {
    for (const value of Object.values(valueEnum)) {
      assert.match(value, VALUE_RE, `attribute value not a lowercase token: ${value}`);
    }
  }
});

test('span / metric / attribute names do not collide', () => {
  const names = [
    ...Object.values(TelemetrySpan),
    ...Object.values(TelemetryMetric),
    ...Object.values(TelemetryAttribute),
  ];
  assert.equal(new Set(names).size, names.length, 'duplicate telemetry name');
});

test('enumerated values within a group do not collide', () => {
  for (const valueEnum of [
    TelemetryRpcSystem,
    TelemetryOutcome,
    TelemetryMessageKind,
    TelemetryStream,
  ]) {
    const values = Object.values(valueEnum);
    assert.equal(new Set(values).size, values.length, 'duplicate telemetry value');
  }
});

test('every metric has a non-empty unit', () => {
  for (const metric of Object.values(TelemetryMetric)) {
    const unit = TELEMETRY_METRIC_UNITS[metric];
    assert.ok(unit !== undefined && unit.length > 0, `missing unit for metric: ${metric}`);
  }
});
