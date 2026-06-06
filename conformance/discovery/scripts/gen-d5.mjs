#!/usr/bin/env node
// AHP Conformance Discovery — D5 fixture-derived scenario generator.
//
// Reads every reducer fixture (types/test-cases/reducers/*.json) and every
// round-trip fixture (types/test-cases/round-trips/*.json), extracts REAL
// snippets from those files for citations, and emits one inventory row per
// fixture to stdout (JSONL). Pipe to out/d5-fixture-derived-scenarios.jsonl.
//
// Usage:
//   node conformance/discovery/scripts/gen-d5.mjs \
//     > conformance/discovery/out/d5-fixture-derived-scenarios.jsonl
//
// The grounding gate (verify-citations.mjs) will open each cited file and
// confirm the excerpt actually appears there — so every excerpt here is read
// verbatim from the real file, not invented.

import { readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, join, relative } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const FORK_ROOT = resolve(__dirname, '..', '..', '..');

const REDUCER_DIR = join(FORK_ROOT, 'types', 'test-cases', 'reducers');
const ROUND_TRIP_DIR = join(FORK_ROOT, 'types', 'test-cases', 'round-trips');

// ── helpers ──────────────────────────────────────────────────────────────────

function toRelPath(absPath) {
  return relative(FORK_ROOT, absPath).replace(/\\/g, '/');
}

/**
 * Slugify an action type like "root/agentsChanged" → "root-agentsChanged"
 * and strip slashes / special chars to make it safe for a behavior-id segment.
 */
function slugifyType(type) {
  return type
    .replace(/\//g, '-')
    .replace(/[^A-Za-z0-9-]/g, '-')
    .replace(/-+/g, '-')
    .replace(/^-|-$/g, '');
}

/**
 * Convert a filename slug like "001-root-agentschanged" to a discriminator.
 * Strips the leading numeric prefix.
 */
function fileSlug(filename) {
  return filename
    .replace(/\.json$/, '')
    .replace(/^\d+-/, '');
}

/**
 * Determine a scenario-class from fixture name + content.
 * Heuristics:
 *  - "no-op", "noop", "wrong", "unknown", "mismatch", "missing", "error",
 *    "fail", "invalid", "absent", "preserved", "ignored", "skips", "gaps",
 *    "without", "force-cancel" → edge
 *  - names containing "reconnect" or "replay" → reconnect
 *  - otherwise → happy
 */
function scenarioClass(name, description) {
  const s = (name + ' ' + (description || '')).toLowerCase();
  if (/reconnect|replay/.test(s)) return 'reconnect';
  if (
    /no.?op|noop|wrong|unknown|mismatch|missing|error|fail|invalid|absent|preserved|ignored|skips|gap|without|force.?cancel|no.result|not.found|unclassified|pending/.test(
      s,
    )
  )
    return 'edge';
  return 'happy';
}

// ── REDUCER fixtures ──────────────────────────────────────────────────────────

const reducerFiles = readdirSync(REDUCER_DIR)
  .filter((f) => f.endsWith('.json'))
  .sort();

const rows = [];

for (const filename of reducerFiles) {
  const absPath = join(REDUCER_DIR, filename);
  const relPath = toRelPath(absPath);
  const raw = readFileSync(absPath, 'utf8');
  let fixture;
  try {
    fixture = JSON.parse(raw);
  } catch (e) {
    process.stderr.write(`SKIP ${filename}: JSON parse error: ${e.message}\n`);
    continue;
  }

  const description = fixture.description || '';
  const reducer = fixture.reducer || 'unknown';

  // The first action's "type" field is the most grounded citation anchor —
  // we know it physically appears in the file.
  const firstAction = Array.isArray(fixture.actions) && fixture.actions[0];
  const actionType = firstAction && firstAction.type ? firstAction.type : null;

  // Build the concept string
  const concept = actionType
    ? `StateAction:${actionType}`
    : `reducer:${reducer}`;

  // Build a stable behavior-id
  // Domain: "action" when we have an action type; "state" for reducer-only
  const domain = actionType ? 'action' : 'state';
  const conceptSlug = actionType ? slugifyType(actionType) : slugifyType(reducer);
  const cls = scenarioClass(filename, description);
  const discriminator = fileSlug(filename);
  const behaviorId = `${domain}.${conceptSlug}.${cls}.${discriminator}`;

  // Citation: the "description" field is the most stable verbatim anchor
  // (it always exists in these fixtures). Use line null so the verifier
  // scans the whole file.
  const excerpt = description
    ? `"description": "${description.replace(/"/g, '\\"')}"`
    : (actionType ? `"type": "${actionType}"` : `"reducer": "${reducer}"`);

  const row = {
    'behavior-id': behaviorId,
    source: 'd5-fixture',
    method: 'session/subscribe',
    concept: concept,
    'scenario-class': cls,
    'normative-level': 'NONE',
    citation: {
      file: relPath,
      line: null,
      excerpt: excerpt,
    },
    coverage: 'unknown',
    assertion: `Host emits action(s) from ${relPath} as real notifications; client reducer converges to the fixture's expected state.`,
    notes: `Reducer: ${reducer}. Fixture file: ${filename}.`,
  };

  rows.push(row);
}

// ── ROUND-TRIP fixtures ──────────────────────────────────────────────────────

const roundTripFiles = readdirSync(ROUND_TRIP_DIR)
  .filter((f) => f.endsWith('.json'))
  .sort();

for (const filename of roundTripFiles) {
  const absPath = join(ROUND_TRIP_DIR, filename);
  const relPath = toRelPath(absPath);
  const raw = readFileSync(absPath, 'utf8');
  let fixture;
  try {
    fixture = JSON.parse(raw);
  } catch (e) {
    process.stderr.write(`SKIP ${filename}: JSON parse error: ${e.message}\n`);
    continue;
  }

  const name = fixture.name || fileSlug(filename);
  const description = fixture.description || '';
  const wireType = fixture.type || 'unknown';

  // Build concept
  const concept = `roundtrip:${wireType}`;

  // scenario-class: round-trips are usually about edge cases (preservation,
  // unknown variants, optional fields, bitflags). Only a handful are happy.
  const cls = scenarioClass(name, description);

  // behavior-id: use "roundtrip" domain
  const discriminator = fileSlug(filename);
  // Safe segment: use wireType slug + discriminator
  const typeSlug = slugifyType(wireType);
  const behaviorId = `roundtrip.${typeSlug}.${cls}.${discriminator}`;

  // Citation: "name" or "description" field — whichever is more grounding
  let excerpt;
  if (fixture.name) {
    excerpt = `"name": "${fixture.name.replace(/"/g, '\\"')}"`;
  } else if (description) {
    excerpt = `"description": "${description.slice(0, 80).replace(/"/g, '\\"')}"`;
  } else {
    excerpt = `"type": "${wireType}"`;
  }

  const row = {
    'behavior-id': behaviorId,
    source: 'd5-fixture',
    method: null,
    concept: concept,
    'scenario-class': cls,
    'normative-level': 'NONE',
    citation: {
      file: relPath,
      line: null,
      excerpt: excerpt,
    },
    coverage: 'unknown',
    assertion: `Wire message from ${relPath} must survive encode→decode→re-encode unchanged; all known fields present, unknown keys handled per fixture expectations.`,
    notes: `Wire type: ${wireType}. Fixture file: ${filename}.`,
  };

  rows.push(row);
}

// ── emit ─────────────────────────────────────────────────────────────────────

for (const row of rows) {
  process.stdout.write(JSON.stringify(row) + '\n');
}

process.stderr.write(`gen-d5: emitted ${rows.length} rows (${reducerFiles.length} reducers + ${roundTripFiles.length} round-trips)\n`);
