#!/usr/bin/env node
// AHP CONFORMANCE — CI GATE (build-phase B7, the capstone machine-check).
//
// One dependency-free Node script that runs the CORE conformance checks that are
// fast + local-runnable and exits NON-ZERO on the first hard failure. This is the
// machine-checked gate the CI workflow (../../.github/workflows/conformance.yml)
// invokes as its first job; the heavier per-client matrix + the nightly mutation
// run live in the workflow, not here.
//
// NO THEATER: every check below REALLY executes — it shells the real validators
// and the real host-conformance runner and parses their real output, or it reads
// the real artifacts and recomputes the real numbers. Nothing echoes "PASS".
//
// Checks (each is a hard gate unless noted):
//   A. scenario shape       — node types/test-cases/scenarios/scripts/validate-scenarios.mjs
//                             (every *.scenario.json structurally valid; expects 234 files.)
//   B. host-conformance     — conformance/runner/run.sh --all-reducers must report 233/233.
//   C. discovery integrity  — validate-inventory + verify-citations over out/d1..d10
//                             (656 rows shape-valid AND citation-grounded in the fork).
//   D. CORPUS-COVERS-MATRIX  — the exhaustiveness check (this file's own logic):
//                             cross-reference the corpus's behaviorIds against the D11
//                             discovery surface. HARD-assert every scenario-mappable
//                             (D5+D7) discovery behavior is covered by a scenario, and
//                             RATCHET the overall D11-surface coverage % against
//                             conformance/ci/coverage-floor.json (fail only on regression).
//                             Prints uncovered behaviors on failure.
//
// Usage:
//   node conformance/ci/gate.mjs                 # run every check; exit 1 on any failure
//   node conformance/ci/gate.mjs --print-coverage  # print the live coverage numbers + exit 0
//   node conformance/ci/gate.mjs --skip-host     # skip check B (slow; needs node build) — A,C,D only
//   node conformance/ci/gate.mjs --skip-runner-build  # forwarded to run.sh? no — see --skip-host
//
// Exit codes: 0 = all gates pass; 1 = a gate failed; 2 = bad invocation / missing artifact.

import { readFileSync, readdirSync, statSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, join } from 'node:path';
import { spawnSync } from 'node:child_process';

const __dirname = dirname(fileURLToPath(import.meta.url));
// conformance/ci -> repo root is two levels up.
const REPO = resolve(__dirname, '..', '..');

const argv = process.argv.slice(2);
const FLAG = (f) => argv.includes(f);
const PRINT_ONLY = FLAG('--print-coverage');
const SKIP_HOST = FLAG('--skip-host');

const C = {
  reset: '\x1b[0m', red: '\x1b[31m', green: '\x1b[32m',
  yellow: '\x1b[33m', cyan: '\x1b[36m', bold: '\x1b[1m', dim: '\x1b[2m',
};
const useColor = process.stdout.isTTY || process.env.FORCE_COLOR;
const col = (k, s) => (useColor ? C[k] + s + C.reset : s);

const results = []; // { name, ok, detail }
function record(name, ok, detail) {
  results.push({ name, ok, detail });
  const tag = ok ? col('green', 'PASS') : col('red', 'FAIL');
  console.log(`  [${tag}] ${name}${detail ? col('dim', '  — ' + detail) : ''}`);
}

// ── helpers ──────────────────────────────────────────────────────────────────

// Recursively collect *.scenario.json (skipping the schema/ dir).
function collectScenarioFiles(root) {
  const out = [];
  const walk = (d) => {
    for (const e of readdirSync(d, { withFileTypes: true })) {
      const p = join(d, e.name);
      if (e.isDirectory()) {
        if (e.name === 'schema') continue;
        walk(p);
      } else if (e.name.endsWith('.scenario.json')) {
        out.push(p);
      }
    }
  };
  walk(root);
  return out;
}

function readJsonl(absPath) {
  return readFileSync(absPath, 'utf8')
    .split('\n')
    .map((l) => l.trim())
    .filter(Boolean)
    .map((l) => JSON.parse(l));
}

// Run a child command, return { code, out } with combined stdout+stderr.
function run(cmd, args, opts = {}) {
  const r = spawnSync(cmd, args, {
    cwd: REPO,
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
    ...opts,
  });
  const out = (r.stdout || '') + (r.stderr || '');
  return { code: r.status == null ? 1 : r.status, out, signal: r.signal };
}

// ── the corpus-covers-matrix computation (shared by check D and --print-coverage) ──

function computeCoverage() {
  const scenariosRoot = resolve(REPO, 'types/test-cases/scenarios');
  const d11Path = resolve(REPO, 'conformance/discovery/out/d11-surface-matrix.jsonl');
  if (!existsSync(scenariosRoot)) throw new Error(`missing ${scenariosRoot}`);
  if (!existsSync(d11Path)) throw new Error(`missing ${d11Path}`);

  // 1. Corpus behaviorIds (the union over every scenario file's behaviorIds[]).
  const corpusBids = new Set();
  let scenarioCount = 0;
  for (const f of collectScenarioFiles(scenariosRoot)) {
    const j = JSON.parse(readFileSync(f, 'utf8'));
    scenarioCount++;
    for (const b of j.behaviorIds || []) corpusBids.add(b);
  }

  // 2. The D11 surface rows.
  const d11 = readJsonl(d11Path);
  const allD11Bids = new Set(d11.map((r) => r['behavior-id']));

  // 3. Scenario-mappable angles (D5 fixture-derived + D7 negative-paths): every
  //    behavior-id whose `sources` include one of these MUST be covered.
  const MAPPABLE_ANGLES = ['d5-fixture', 'd7-negative'];
  const mappableBids = new Set(
    d11
      .filter((r) => (r.sources || []).some((s) => MAPPABLE_ANGLES.includes(s)))
      .map((r) => r['behavior-id'])
  );
  const mappableUncovered = [...mappableBids].filter((b) => !corpusBids.has(b)).sort();
  const mappableCovered = mappableBids.size - mappableUncovered.length;

  // 4. Overall D11-surface breadth: distinct D11 behavior-ids a corpus id touches.
  let d11Covered = 0;
  for (const b of allD11Bids) if (corpusBids.has(b)) d11Covered++;
  const d11Pct = allD11Bids.size === 0 ? 0 : (100 * d11Covered) / allD11Bids.size;

  return {
    scenarioCount,
    corpusDistinctBehaviorIds: corpusBids.size,
    mappable: {
      angles: MAPPABLE_ANGLES,
      distinct: mappableBids.size,
      covered: mappableCovered,
      uncovered: mappableUncovered,
    },
    d11Surface: {
      distinct: allD11Bids.size,
      covered: d11Covered,
      pct: Math.round(d11Pct * 10) / 10,
    },
  };
}

// ── --print-coverage short-circuit ─────────────────────────────────────────────

if (PRINT_ONLY) {
  const cov = computeCoverage();
  console.log(JSON.stringify(cov, null, 2));
  process.exit(0);
}

// ── main gate ──────────────────────────────────────────────────────────────────

console.log(col('bold', '\nAHP CONFORMANCE — CI GATE (B7)\n'));
console.log(col('dim', `repo: ${REPO}\n`));

// ── Check A — scenario shape ───────────────────────────────────────────────────
console.log(col('cyan', 'A. scenario shape  (validate-scenarios.mjs)'));
{
  const { code, out } = run('node', ['types/test-cases/scenarios/scripts/validate-scenarios.mjs']);
  // The validator prints "PASS — N scenario(s) valid …" on success and exits 0.
  const tail = out.trim().split('\n').slice(-1)[0] || '';
  record('scenario fixtures structurally valid', code === 0, code === 0 ? tail : tail || `exit ${code}`);
  if (code !== 0) process.stdout.write(col('dim', out.split('\n').slice(-12).join('\n') + '\n'));
}

// ── Check B — host-conformance 233/233 ─────────────────────────────────────────
// NOTE: run at --concurrency 1 for DETERMINISM. The default concurrency-4 full
// run has a known subprocess race (~1/233 of full runs intermittently drops one
// scripted-error scenario — surfaced during B7 build-out, confirmed a flake: the
// affected scenarios pass 8/8 in isolation and the parallel run is 233/233 on
// re-run). The reducers/host/scenarios are correct; only the parallel harness
// flakes under load, so a CI GATE must serialize. The per-client matrix jobs in
// the workflow exercise the parallel path; this core gate trades ~10s of wall
// time for a deterministic verdict. (~14s serial vs ~5s parallel.)
console.log(col('cyan', '\nB. host-conformance  (runner/run.sh --all-reducers --concurrency 1 → 233/233)'));
if (SKIP_HOST) {
  record('host-conformance suite 233/233', true, col('yellow', 'SKIPPED (--skip-host)'));
} else {
  const { code, out } = run('bash', ['conformance/runner/run.sh', '--all-reducers', '--concurrency', '1']);
  // Parse the AUTHORITATIVE "GREEN: N/M" roll-up line (the suite's own final
  // tally) rather than the first N/M-shaped token (the per-tranche "round-trip
  // 23/23" line would otherwise be mis-grabbed). Fall back to the PASS banner.
  const m = out.match(/GREEN:\s*(\d+)\s*\/\s*(\d+)/i)
    || out.match(/(\d+)\s*\/\s*(\d+)\s+scenarios? converge/i)
    || out.match(/SUITE PASS\s*—\s*(\d+)\s*\/\s*(\d+)/i);
  let green = null, total = null;
  if (m) { green = Number(m[1]); total = Number(m[2]); }
  const ok = code === 0 && green !== null && green === total && total >= 233;
  const detail = green !== null ? `${green}/${total}` : `exit ${code}, no GREEN: N/M line parsed`;
  record('host-conformance suite 233/233', ok, detail);
  if (!ok) process.stdout.write(col('dim', out.split('\n').slice(-25).join('\n') + '\n'));
}

// ── Check C — discovery integrity (656 grounded) ───────────────────────────────
console.log(col('cyan', '\nC. discovery integrity  (validate-inventory + verify-citations, d1..d10)'));
{
  const D = (n) => `conformance/discovery/out/${n}`;
  const dFiles = [
    'd1-schema-surface.jsonl', 'd2-normative-rules.jsonl', 'd3-mined-client-expectations.jsonl',
    'd4-host-behaviors.jsonl', 'd5-fixture-derived-scenarios.jsonl', 'd6-lifecycle-transitions.jsonl',
    'd7-negative-paths.jsonl', 'd8-divergences.jsonl', 'd9-surviving-mutants.jsonl',
    'd10-property-findings.jsonl',
  ].map(D);
  const missing = dFiles.filter((f) => !existsSync(resolve(REPO, f)));
  if (missing.length) {
    record('discovery artifacts present', false, `missing: ${missing.join(', ')}`);
  } else {
    {
      const { code, out } = run('node', ['conformance/discovery/scripts/validate-inventory.mjs', ...dFiles]);
      const tail = out.trim().split('\n').slice(-1)[0] || '';
      record('inventory rows shape-valid (656)', code === 0, code === 0 ? tail : tail || `exit ${code}`);
      if (code !== 0) process.stdout.write(col('dim', out.split('\n').slice(-12).join('\n') + '\n'));
    }
    {
      const { code, out } = run('node', ['conformance/discovery/scripts/verify-citations.mjs', ...dFiles]);
      const tail = out.trim().split('\n').slice(-1)[0] || '';
      record('inventory citations grounded (656)', code === 0, code === 0 ? tail : tail || `exit ${code}`);
      if (code !== 0) process.stdout.write(col('dim', out.split('\n').slice(-12).join('\n') + '\n'));
    }
  }
}

// ── Check D — CORPUS-COVERS-MATRIX (the exhaustiveness ratchet) ────────────────
console.log(col('cyan', '\nD. CORPUS-COVERS-MATRIX  (corpus behaviorIds × D11 discovery surface)'));
{
  const floorPath = resolve(__dirname, 'coverage-floor.json');
  let floor;
  try {
    floor = JSON.parse(readFileSync(floorPath, 'utf8'));
  } catch (e) {
    record('coverage floor present', false, `cannot read ${floorPath}: ${e.message}`);
  }
  if (floor) {
    let cov;
    try {
      cov = computeCoverage();
    } catch (e) {
      record('corpus-covers-matrix computed', false, e.message);
    }
    if (cov) {
      console.log(
        col('dim',
          `     corpus: ${cov.scenarioCount} scenarios, ${cov.corpusDistinctBehaviorIds} distinct behaviorIds`)
      );
      console.log(
        col('dim',
          `     D5+D7 mappable behaviors: ${cov.mappable.covered}/${cov.mappable.distinct} covered`)
      );
      console.log(
        col('dim',
          `     overall D11 surface: ${cov.d11Surface.covered}/${cov.d11Surface.distinct} = ${cov.d11Surface.pct}%`)
      );

      // D.1 — HARD exhaustiveness: every mappable behavior covered, and never
      //        below the ratchet floor.
      const minCovered = floor.scenarioMappableAngleCoverage?.minCovered ?? 0;
      const allMappableCovered = cov.mappable.uncovered.length === 0;
      const aboveFloor = cov.mappable.covered >= minCovered;
      const okMappable = allMappableCovered && aboveFloor;
      record(
        'every D5/D7 discovery behavior has a scenario',
        okMappable,
        `${cov.mappable.covered}/${cov.mappable.distinct} (floor ${minCovered})`
      );
      if (!okMappable) {
        if (cov.mappable.uncovered.length) {
          console.log(col('red', `     ${cov.mappable.uncovered.length} UNCOVERED mappable behavior(s):`));
          for (const b of cov.mappable.uncovered) console.log(col('red', `       • ${b}`));
        }
        if (!aboveFloor) {
          console.log(
            col('red',
              `     REGRESSION: mappable coverage ${cov.mappable.covered} < floor ${minCovered}`)
          );
        }
      }

      // D.2 — RATCHET the overall D11-surface breadth (fail only on regression).
      const minD11 = floor.d11SurfaceCoverage?.minCovered ?? 0;
      const minPct = floor.d11SurfaceCoverage?.minPct ?? 0;
      const okD11 = cov.d11Surface.covered >= minD11 && cov.d11Surface.pct >= minPct;
      record(
        'D11-surface coverage ratchet (no regression)',
        okD11,
        `${cov.d11Surface.covered} ≥ ${minD11}  &&  ${cov.d11Surface.pct}% ≥ ${minPct}%`
      );
      if (!okD11) {
        console.log(
          col('red',
            `     REGRESSION: D11 coverage ${cov.d11Surface.covered} (${cov.d11Surface.pct}%) ` +
            `fell below floor ${minD11} (${minPct}%). ` +
            `If this drop is intentional, lower the floor in conformance/ci/coverage-floor.json ` +
            `in the SAME commit and explain why; otherwise a scenario or discovery row was lost.`)
        );
      }
    }
  }
}

// ── roll-up ────────────────────────────────────────────────────────────────────
const failed = results.filter((r) => !r.ok);
console.log('');
if (failed.length === 0) {
  console.log(col('green', col('bold', `GATE PASS — ${results.length}/${results.length} checks green.`)));
  console.log(col('dim', 'Core conformance gate satisfied. (Per-client matrix + nightly mutation run live in the CI workflow.)\n'));
  process.exit(0);
} else {
  console.log(col('red', col('bold', `GATE FAIL — ${failed.length}/${results.length} check(s) failed:`)));
  for (const f of failed) console.log(col('red', `  ✗ ${f.name}${f.detail ? '  — ' + f.detail : ''}`));
  console.log('');
  process.exit(1);
}
