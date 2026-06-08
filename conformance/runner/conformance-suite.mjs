// AHP HOST-CONFORMANCE SUITE DRIVER — the tranche green proof.
//
// Runs the host-conformance runner (run-conformance.mjs) over a TRANCHE of
// the scenario corpus, each scenario replayed by a real client against the
// real scenario-driven host over a real WebSocket, and rolls up GREEN/TOTAL.
//
// Tranche:
//   • ALL round-trip scenarios          (types/test-cases/scenarios/round-trips)
//   • a representative sample of reducer scenarios — at least 30, sampled
//     deterministically across the alphabetised set so every reducer family
//     (root / session / terminal / changeset / resource-watch) is covered
//   • ALL negative scenarios            (types/test-cases/scenarios/negatives)
//
// Usage:
//   node conformance-suite.mjs                  # default tranche, summary only
//   node conformance-suite.mjs --reducer-sample 60
//   node conformance-suite.mjs --all-reducers   # run every reducer scenario
//   node conformance-suite.mjs --verbose        # per-assertion detail
//   node conformance-suite.mjs --concurrency 6  # parallel host processes
//
// Exit 0 = every scenario in the tranche PASSED; 1 = one or more FAILED/ERRORED.

import { readdirSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, resolve, join } from 'node:path'
import { runScenario } from './run-conformance.mjs'

const __dirname = dirname(fileURLToPath(import.meta.url))
const SCENARIOS_ROOT = resolve(__dirname, '..', '..', 'types', 'test-cases', 'scenarios')
const REDUCERS_DIR = join(SCENARIOS_ROOT, 'reducers')
const ROUND_TRIPS_DIR = join(SCENARIOS_ROOT, 'round-trips')
const NEGATIVES_DIR = join(SCENARIOS_ROOT, 'negatives')

// ── args ───────────────────────────────────────────────────────────────────
const args = process.argv.slice(2)
const has = (f) => args.includes(f)
const val = (f, d) => { const i = args.indexOf(f); return i >= 0 ? args[i + 1] : d }
const VERBOSE = has('--verbose')
const ALL_REDUCERS = has('--all-reducers')
const REDUCER_SAMPLE = Number(val('--reducer-sample', '30'))
const CONCURRENCY = Math.max(1, Number(val('--concurrency', '4')))

// ── scenario selection ───────────────────────────────────────────────────────
function listScenarios(dir) {
  return readdirSync(dir)
    .filter((f) => f.endsWith('.scenario.json'))
    .sort()
    .map((f) => join(dir, f))
}

// Deterministic, family-spanning sample: walk the alphabetised list with an
// even stride so the N picks are spread across the whole corpus (which is
// grouped by action family, so a spread sample covers every reducer family).
function sample(list, n) {
  if (n >= list.length) return list.slice()
  const out = []
  const stride = list.length / n
  for (let i = 0; i < n; i++) out.push(list[Math.floor(i * stride)])
  // de-dup (floor collisions) and top up from the front if needed
  const seen = new Set(out)
  for (const item of list) {
    if (out.length >= n) break
    if (!seen.has(item)) { out.push(item); seen.add(item) }
  }
  return out.slice(0, n).sort()
}

const roundTrips = listScenarios(ROUND_TRIPS_DIR)
const negatives = listScenarios(NEGATIVES_DIR)
const allReducers = listScenarios(REDUCERS_DIR)
const reducers = ALL_REDUCERS ? allReducers : sample(allReducers, REDUCER_SAMPLE)

const tranche = [
  ...roundTrips.map((p) => ({ p, tranche: 'round-trip' })),
  ...reducers.map((p) => ({ p, tranche: 'reducer' })),
  ...negatives.map((p) => ({ p, tranche: 'negative' })),
]

// ── bounded-concurrency runner ───────────────────────────────────────────────
async function runAll(items, concurrency) {
  const results = new Array(items.length)
  let next = 0
  async function worker() {
    while (true) {
      const i = next++
      if (i >= items.length) return
      const { p, tranche } = items[i]
      try {
        const r = await runScenario(p, { verbose: false })
        results[i] = { ...r, trancheName: tranche }
      } catch (e) {
        results[i] = { id: p, scenarioPath: p, status: 'ERROR', reason: e.message, asserts: [], trancheName: tranche }
      }
    }
  }
  await Promise.all(Array.from({ length: concurrency }, worker))
  return results
}

// ── main ─────────────────────────────────────────────────────────────────────
console.log('AHP HOST-CONFORMANCE SUITE')
console.log('Real client ↔ real scenario-driven host ↔ real WebSocket ↔ canonical reducers. No mocks.')
console.log('')
console.log(`Tranche:`)
console.log(`  round-trips: ${roundTrips.length} (ALL)`)
console.log(`  reducers:    ${reducers.length}${ALL_REDUCERS ? ' (ALL)' : ` (sample of ${allReducers.length})`}`)
console.log(`  negatives:   ${negatives.length} (ALL)`)
console.log(`  TOTAL:       ${tranche.length} scenarios   (concurrency ${CONCURRENCY})`)
console.log('')

// Use performance.now() (monotonic, NOT affected by the clock pin that
// runScenario applies to Date.now for impure-field determinism).
const t0 = performance.now()
const results = await runAll(tranche, CONCURRENCY)
const elapsed = ((performance.now() - t0) / 1000).toFixed(1)

// ── roll-up ──────────────────────────────────────────────────────────────────
const byStatus = { PASS: [], FAIL: [], ERROR: [] }
for (const r of results) byStatus[r.status]?.push(r)

const totalAsserts = results.reduce((n, r) => n + (r.asserts?.length ?? 0), 0)
const green = byStatus.PASS.length
const total = results.length

// Per-tranche breakdown.
const perTranche = {}
for (const r of results) {
  const t = (perTranche[r.trancheName] ??= { pass: 0, fail: 0, error: 0, total: 0 })
  t.total++
  if (r.status === 'PASS') t.pass++
  else if (r.status === 'FAIL') t.fail++
  else t.error++
}

if (byStatus.FAIL.length || byStatus.ERROR.length) {
  console.log('── FAILURES / ERRORS ──────────────────────────────────────────')
  for (const r of [...byStatus.FAIL, ...byStatus.ERROR]) {
    console.log(`\n  ✗ [${r.trancheName}] ${r.id}  (${r.status})`)
    if (r.reason) console.log(`      reason: ${r.reason}`)
    if (r.hostStderr) console.log(`      host stderr: ${r.hostStderr.trim().split('\n').join('\n        ')}`)
    for (const a of (r.asserts ?? []).filter((x) => !x.ok)) {
      console.log(`      ✗ ${a.op}  ${a.label}`)
      console.log(`        → ${a.detail}`)
    }
  }
  console.log('')
}

if (VERBOSE) {
  console.log('── PASSED ─────────────────────────────────────────────────────')
  for (const r of byStatus.PASS) console.log(`  ✓ [${r.trancheName}] ${r.id}  (${r.asserts.length} assertion(s))`)
  console.log('')
}

console.log('── SUMMARY ────────────────────────────────────────────────────')
for (const [name, t] of Object.entries(perTranche)) {
  console.log(`  ${name.padEnd(11)} ${t.pass}/${t.total} green${t.fail ? `  (${t.fail} fail)` : ''}${t.error ? `  (${t.error} error)` : ''}`)
}
console.log('')
console.log(`  GREEN: ${green}/${total}   (${totalAsserts} assertions across the tranche, ${elapsed}s)`)
console.log('')

if (green === total) {
  console.log(`HOST-CONFORMANCE SUITE PASS — ${green}/${total} scenarios converge against the real host over real WebSocket via the canonical reducers`)
  process.exit(0)
} else {
  console.log(`HOST-CONFORMANCE SUITE FAIL — ${total - green}/${total} scenario(s) did not pass`)
  process.exit(1)
}
