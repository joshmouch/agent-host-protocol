#!/usr/bin/env node
// AHP Conformance Discovery — D11 reconciliation (canonical-key coverage).
//
// The per-angle behavior-ids do NOT converge across independent angles (the
// same `initialize` happy path is named ~18 different ways), so a merge keyed on
// the raw behavior-id under-collapses (656 rows -> 652) and a denominator-
// coverage check keyed on the raw `concept` string under-counts coverage (exact
// match: 18/123). Both failures share ONE root cause: no canonical taxonomy.
//
// This reconciler imposes a deterministic canonical key:
//   canon(s) = strip leading "StateAction:"/"error:" -> drop non-alphanumerics
//              -> lowercase
// so "StateAction:SessionTitleChanged", "session/titleChanged" and
// "session-titleChanged" all collapse to "sessiontitlechanged". It then answers
// the real Part-1 exit question: of the D1 schema-surface elements (the
// denominator) + the D2 strong-normative clauses, how many are actually TOUCHED
// by a scenario-producing angle (D3-D10), vs present only in their own
// denominator file.
//
// Reproducible + falsifiable: reads only the real out/*.jsonl angle files;
// rebuilding from the same inputs yields the same coverage.json. No LLM
// judgment, no network, no hidden state.
//
// Run from anywhere:
//   node conformance/discovery/scripts/reconcile.mjs
// Writes: out/d11-coverage.json, out/d11-reconciliation.md

import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const OUT = resolve(__dirname, '..', 'out');

const ANGLE_FILES = {
  'd1-schema': 'd1-schema-surface.jsonl',
  'd2-spec': 'd2-normative-rules.jsonl',
  'd3-mined-client': 'd3-mined-client-expectations.jsonl',
  'd4-host': 'd4-host-behaviors.jsonl',
  'd5-fixture': 'd5-fixture-derived-scenarios.jsonl',
  'd6-lifecycle': 'd6-lifecycle-transitions.jsonl',
  'd7-negative': 'd7-negative-paths.jsonl',
  'd8-differential': 'd8-divergences.jsonl',
  'd9-mutation': 'd9-surviving-mutants.jsonl',
  'd10-property': 'd10-property-findings.jsonl',
};

// Angles that produce a runnable check (the "numerator" — proof a behavior is
// exercised). D1/D2 are the denominator (schema + prose), not scenarios.
const SCENARIO_ANGLES = new Set([
  'd3-mined-client', 'd4-host', 'd5-fixture', 'd6-lifecycle',
  'd7-negative', 'd8-differential', 'd9-mutation', 'd10-property',
]);

const STRONG_NORMATIVE = new Set(['MUST', 'MUST_NOT', 'REQUIRED', 'SHALL']);

const canon = (s) =>
  s == null
    ? ''
    : String(s)
        .replace(/^StateAction:/i, '')
        .replace(/^error:/i, '')
        .replace(/[^A-Za-z0-9]/g, '')
        .toLowerCase();

function readRows(file) {
  const path = resolve(OUT, file);
  if (!existsSync(path)) return [];
  return readFileSync(path, 'utf8')
    .split('\n')
    .map((l) => l.trim())
    .filter(Boolean)
    .map((l) => JSON.parse(l));
}

// Load every angle's rows.
const rows = {};
for (const [source, file] of Object.entries(ANGLE_FILES)) rows[source] = readRows(file);

// Canonical keys touched by each angle (concept OR method, both normalized).
const keysByAngle = {};
for (const [source, rs] of Object.entries(rows)) {
  const set = new Set();
  for (const r of rs) {
    const ck = canon(r.concept);
    if (ck) set.add(ck);
    const mk = canon(r.method);
    if (mk) set.add(mk);
  }
  keysByAngle[source] = set;
}

// Union of canonical keys across scenario-producing angles.
const scenarioKeys = new Set();
for (const a of SCENARIO_ANGLES) for (const k of keysByAngle[a]) scenarioKeys.add(k);

// --- D1 denominator coverage ---
const d1ByKey = new Map(); // canonKey -> display concept (first seen)
for (const r of rows['d1-schema']) {
  const ck = canon(r.concept);
  if (ck && !d1ByKey.has(ck)) d1ByKey.set(ck, r.concept);
}
const d1Total = d1ByKey.size;
const d1Covered = [];
const d1Uncovered = [];
for (const [ck, display] of d1ByKey) {
  (scenarioKeys.has(ck) ? d1Covered : d1Uncovered).push(display);
}

// --- D2 strong-normative coverage ---
// D2 rows are concept-keyed (method usually null). A MUST clause is "touched"
// if a scenario angle exercises the same canonical concept.
const d2Strong = rows['d2-spec'].filter((r) => STRONG_NORMATIVE.has(r['normative-level']));
const d2StrongTotal = d2Strong.length;
const d2Covered = [];
const d2Uncovered = [];
for (const r of d2Strong) {
  const ck = canon(r.concept);
  const touched = scenarioKeys.has(ck) || scenarioKeys.has(canon(r.method));
  (touched ? d2Covered : d2Uncovered).push({ concept: r.concept, level: r['normative-level'], 'behavior-id': r['behavior-id'] });
}

// --- distinct canonical concepts across ALL angles ---
const allConceptKeys = new Set();
for (const rs of Object.values(rows)) for (const r of rs) { const c = canon(r.concept); if (c) allConceptKeys.add(c); }

const totalRows = Object.values(rows).reduce((n, rs) => n + rs.length, 0);

const coverage = {
  generatedBy: 'conformance/discovery/scripts/reconcile.mjs',
  totalAngleRows: totalRows,
  distinctCanonicalConcepts: allConceptKeys.size,
  behaviorIdMergeNote:
    'D11 matrix has 652 lines but only collapsed 4 of 656 rows because behavior-ids diverge across angles. Canonical-concept normalization shows the true distinct-concept count is ' +
    allConceptKeys.size + '.',
  d1SchemaCoverage: {
    total: d1Total,
    touchedByScenarioAngle: d1Covered.length,
    untouched: d1Uncovered.length,
    untouchedList: d1Uncovered.sort(),
  },
  d2StrongNormativeCoverage: {
    total: d2StrongTotal,
    touchedByScenarioAngle: d2Covered.length,
    untouched: d2Uncovered.length,
    untouchedList: d2Uncovered,
  },
  rowsByAngle: Object.fromEntries(Object.entries(rows).map(([s, rs]) => [s, rs.length])),
};

writeFileSync(resolve(OUT, 'd11-coverage.json'), JSON.stringify(coverage, null, 2) + '\n');

const pct = (n, d) => (d === 0 ? '0' : ((100 * n) / d).toFixed(1));
const md = `# D11 — Reconciliation: canonical-key coverage (corrects the raw matrix)

> Generated by \`conformance/discovery/scripts/reconcile.mjs\` (deterministic, no
> LLM judgment). The raw \`d11-surface-matrix.{jsonl,md}\` merges by literal
> \`behavior-id\` and so under-collapses: independent angles named the same
> behavior differently, so only **4 of 656** rows merged and the exit-criterion
> "PASS" there is *circular* (every D1 element is "mapped" only because D1 itself
> emitted it). This file imposes a canonical key —
> \`strip StateAction:/error: → drop non-alphanumerics → lowercase\` — and
> measures the REAL coverage question.

## Headline (honest) numbers

| Metric | Value |
|---|---:|
| Total angle rows (D1–D10) | ${totalRows} |
| Raw \`behavior-id\` matrix lines | 652 |
| **Distinct canonical concepts** | **${allConceptKeys.size}** |
| D1 schema elements (denominator) | ${d1Total} |
| — touched by a scenario-producing angle | **${d1Covered.length} (${pct(d1Covered.length, d1Total)}%)** |
| — NOT touched by any scenario angle | ${d1Uncovered.length} |
| D2 strong-normative clauses (MUST/MUST_NOT/REQUIRED/SHALL) | ${d2StrongTotal} |
| — touched by a scenario-producing angle | **${d2Covered.length} (${pct(d2Covered.length, d2StrongTotal)}%)** |
| — NOT touched by any scenario angle | ${d2Uncovered.length} |

"Touched by a scenario-producing angle" = the same canonical concept appears in
at least one of D3/D4/D5/D6/D7/D8/D9/D10 (the angles that turn into runnable
checks), not just in its own denominator file.

## What this means for Part 1's exit criterion

The plan's exit bar is: *every D1 surface element and every D2 MUST/REQUIRED
clause is mapped to a planned scenario or explicitly out-of-scope-with-reason.*
The **honest** status:

- **Enumeration is complete and verified** — ${totalRows} behaviors, every one
  shape-valid and citation-grounded (both gates re-run independently).
- **Reconciliation is partial** — ${d1Covered.length}/${d1Total} schema elements
  and ${d2Covered.length}/${d2StrongTotal} strong-normative clauses are currently
  corroborated by a scenario-producing angle. The remaining
  ${d1Uncovered.length} schema elements + ${d2Uncovered.length} MUST-class
  clauses are enumerated but **not yet matched to a scenario angle** — they are
  the precise Part-2 authoring backlog (and the honest gap the raw matrix hid).

## D1 schema elements NOT yet touched by a scenario angle (${d1Uncovered.length})

These are the schema-typed surfaces no D3–D10 angle exercised. Most are
\`StateAction\` variants and channel notifications a Part-2 fixture/scenario must
drive:

${d1Uncovered.length ? d1Uncovered.sort().map((c) => `- \`${c}\``).join('\n') : '_(none — full coverage)_'}

## D2 strong-normative clauses NOT yet touched by a scenario angle (${d2Uncovered.length})

${d2Uncovered.length ? d2Uncovered.map((r) => `- **${r.level}** \`${r.concept}\` — ${r['behavior-id']}`).join('\n') : '_(none — full coverage)_'}

## Reproduce

\`\`\`bash
node conformance/discovery/scripts/reconcile.mjs
cat conformance/discovery/out/d11-coverage.json
\`\`\`
`;
writeFileSync(resolve(OUT, 'd11-reconciliation.md'), md);

console.log(`distinct canonical concepts: ${allConceptKeys.size} (raw matrix claimed 652 unique)`);
console.log(`D1 coverage: ${d1Covered.length}/${d1Total} schema elements touched by a scenario angle (${d1Uncovered.length} not)`);
console.log(`D2 MUST-class coverage: ${d2Covered.length}/${d2StrongTotal} clauses touched (${d2Uncovered.length} not)`);
console.log(`wrote out/d11-coverage.json + out/d11-reconciliation.md`);
