// AHP MUTATION KILL-SIGNAL — build-phase B6, the in-process corpus replay.
//
// This is the FAST kill-signal Stryker runs once per mutant. It replays the
// FULL conformance scenario corpus (types/test-cases/scenarios/{reducers,
// round-trips,negatives}) directly against the CANONICAL TypeScript reducers
// imported from clients/typescript/src/** (NOT the built dist), so Stryker can
// mutate the reducer SOURCE and have the change take effect with zero build
// step (tsx transpiles the freshly-mutated src on the fly).
//
// ── Why in-process, and why this is the same kill-signal as the B4 runner ───
//   The B4 host-conformance runner (conformance/runner/run-conformance.mjs)
//   proves the WHOLE tranche end-to-end: real client ↔ real scenario host ↔
//   real WebSocket ↔ canonical reducers. For MUTATION testing the question is
//   narrower: "does the corpus KILL a bug injected into the reducer?" The
//   subprocess + WebSocket plumbing is host-PROTOCOL transport, orthogonal to
//   the reducer logic being mutated. This harness keeps the parts that decide
//   the kill — the SAME reducers, the SAME scenario fixtures, the SAME
//   reduction routing (by action-type prefix) and the SAME assertion semantics
//   (deepEqual / deepContains / canonicalize / dotted-path navigate, copied
//   verbatim from run-conformance.mjs) — and drops only the transport. A mutant
//   that survives here survives the runner too (same reducers, same asserts);
//   a mutant the corpus catches fails an assertion here and exits non-zero.
//
//   The full subprocess runner is ALSO a valid (stricter, slower) kill-signal
//   and is documented for CI in DECISION.md (the `--all-reducers` suite). This
//   harness exists so the interactive / per-mutant run is seconds, not minutes.
//
// ── Clock pin ───────────────────────────────────────────────────────────────
//   Date.now is pinned per-scenario to scenario.pinClock BEFORE any reduction,
//   exactly as the host + the B4 runner do, so impure reducer fields converge.
//
// Usage:
//   node replay-corpus.mjs                 # replay full corpus, exit 0/1
//   node replay-corpus.mjs --verbose       # per-failure detail
//   node replay-corpus.mjs --only reducers # one family (reducers|round-trips|negatives)
// Exit 0 = every scenario PASSED; 1 = one or more FAILED/ERRORED.
//
// Run with tsx so the .ts reducer source is loaded directly:
//   node --import tsx replay-corpus.mjs

import { readdirSync, readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, resolve, join, basename } from 'node:path'

// Canonical reducers, imported from SOURCE (clients/typescript/src/**) so
// Stryker's mutation of the .ts source takes effect under tsx with no build.
import {
  rootReducer,
  sessionReducer,
  terminalReducer,
  changesetReducer,
  resourceWatchReducer,
} from '../../clients/typescript/src/types/reducers.ts'

const __dirname = dirname(fileURLToPath(import.meta.url))
const SCENARIOS_ROOT = resolve(__dirname, '..', '..', 'types', 'test-cases', 'scenarios')

// ───────────────────────────────────────────────────────────────────────────
// Reducer dispatch — by action-type prefix (verbatim from run-conformance.mjs).
// ───────────────────────────────────────────────────────────────────────────
const REDUCER_BY_PREFIX = {
  root: rootReducer,
  session: sessionReducer,
  terminal: terminalReducer,
  changeset: changesetReducer,
  resource: resourceWatchReducer,
}

function reducerForAction(action) {
  const type = action?.type
  if (typeof type !== 'string') return null
  const prefix = type.split('/')[0]
  return REDUCER_BY_PREFIX[prefix] ?? null
}

// ───────────────────────────────────────────────────────────────────────────
// Deep equality + deep-containment + canonicalize + dotted-path navigation.
// VERBATIM from conformance/runner/run-conformance.mjs — same assert discipline.
// ───────────────────────────────────────────────────────────────────────────
function deepEqual(a, b) {
  if (a === b) return true
  if (a === null || b === null) return a === b
  if (typeof a !== typeof b) return false
  if (typeof a !== 'object') return a === b
  if (Array.isArray(a) !== Array.isArray(b)) return false
  if (Array.isArray(a)) {
    if (a.length !== b.length) return false
    for (let i = 0; i < a.length; i++) if (!deepEqual(a[i], b[i])) return false
    return true
  }
  const ka = Object.keys(a)
  const kb = Object.keys(b)
  if (ka.length !== kb.length) return false
  for (const k of ka) {
    if (!Object.prototype.hasOwnProperty.call(b, k)) return false
    if (!deepEqual(a[k], b[k])) return false
  }
  return true
}

function deepContains(actual, expected) {
  if (expected === null || typeof expected !== 'object') return deepEqual(actual, expected)
  if (actual === null || typeof actual !== 'object') return false
  if (Array.isArray(expected)) {
    if (!Array.isArray(actual)) return false
    if (actual.length !== expected.length) return false
    for (let i = 0; i < expected.length; i++) if (!deepContains(actual[i], expected[i])) return false
    return true
  }
  if (Array.isArray(actual)) return false
  for (const k of Object.keys(expected)) {
    if (!Object.prototype.hasOwnProperty.call(actual, k)) return false
    if (!deepContains(actual[k], expected[k])) return false
  }
  return true
}

function canonicalize(value) {
  if (Array.isArray(value)) return value.map(canonicalize)
  if (value !== null && typeof value === 'object') {
    const out = {}
    for (const k of Object.keys(value).sort()) {
      const v = value[k]
      if (v === null || v === undefined) continue
      out[k] = canonicalize(v)
    }
    return out
  }
  return value
}

function navigate(obj, path) {
  if (path == null || path === '') return { found: true, value: obj }
  let cur = obj
  for (const seg of path.split('.')) {
    if (cur == null || typeof cur !== 'object') return { found: false, value: undefined }
    const key = /^\d+$/.test(seg) ? Number(seg) : seg
    if (!Object.prototype.hasOwnProperty.call(cur, key)) return { found: false, value: undefined }
    cur = cur[key]
  }
  return { found: true, value: cur }
}

// ───────────────────────────────────────────────────────────────────────────
// Replay ONE scenario IN-PROCESS: walk the steps, seed snapshots, reduce action
// notifications by action-type prefix, then check every client.assert.* step.
// Mirrors the runner's frame handling, minus the WebSocket/subprocess hop.
// ───────────────────────────────────────────────────────────────────────────
function replayScenario(scenarioPath) {
  const id = basename(scenarioPath).replace(/\.scenario\.json$/, '')
  let scenario
  try {
    scenario = JSON.parse(readFileSync(scenarioPath, 'utf8'))
  } catch (e) {
    return { id, status: 'ERROR', reason: `failed to parse scenario: ${e.message}`, asserts: [] }
  }

  // Pin the clock BEFORE any reduction, exactly as the host + runner do.
  const savedNow = globalThis.Date.now
  if (typeof scenario.pinClock === 'number') globalThis.Date.now = () => scenario.pinClock

  // Per-channel reduced state + synthetic top-level + observed events + errors.
  const channels = new Map()
  const synthetic = {}
  const observedEvents = []
  const surfacedErrors = []
  const warnings = []

  function seedFromSnapshots(result) {
    if (typeof result?.protocolVersion === 'string') synthetic.protocolVersion = result.protocolVersion
    const snaps = result?.snapshots
    if (Array.isArray(snaps)) {
      for (const snap of snaps) {
        if (snap && typeof snap.resource === 'string') channels.set(snap.resource, snap.state)
      }
    }
    if (result?.snapshot && typeof result.snapshot.resource === 'string') {
      channels.set(result.snapshot.resource, result.snapshot.state)
    }
  }

  function applyActionNotification(params) {
    observedEvents.push(params)
    const channel = params?.channel
    const action = params?.action
    const reducer = reducerForAction(action)
    if (!reducer) return
    const hadPrev = channels.has(channel)
    const prev = hadPrev ? channels.get(channel) : undefined
    try {
      const next = reducer(prev, action)
      channels.set(channel, next)
    } catch (e) {
      if (hadPrev) {
        warnings.push(`reducer for ${action?.type} on channel ${channel} threw with seeded state: ${e.message}`)
      }
      // else: event-only round-trip scenario; observed event suffices.
    }
  }

  try {
    // The host replays steps in order. server.response with a result seeds
    // snapshots / surfaces errors; server.notify {method:'action'} reduces.
    for (const step of scenario.steps) {
      if (step.op === 'server.response') {
        if (step.error !== undefined) {
          surfacedErrors.push(step.error)
          synthetic.lastResponseOk = false
        } else if (step.result !== undefined) {
          synthetic.lastResponseOk = true
          seedFromSnapshots(step.result)
        }
      } else if (step.op === 'server.notify') {
        observedEvents.push({ method: step.method, params: step.params })
        if (step.method === 'action') applyActionNotification(step.params)
      }
      // client.request / client.assert.* steps are not driven here:
      //   - client.request has no client-side state effect in this corpus
      //     (the host's server.response carries the seed),
      //   - client.assert.* is evaluated after the walk, below.
    }

    // Evaluate every assertion step against the collected state.
    const assertSteps = scenario.steps.filter((s) => s.op.startsWith('client.assert.'))
    if (assertSteps.length === 0) {
      return { id, status: 'ERROR', reason: 'scenario has no client.assert.* steps', asserts: [], warnings }
    }
    const asserts = []
    let allOk = true
    for (const step of assertSteps) {
      const res = checkAssertion(step, { channels, synthetic, observedEvents, surfacedErrors })
      asserts.push({ op: step.op, label: step.label ?? '', ok: res.ok, detail: res.detail })
      if (!res.ok) allOk = false
    }
    return { id, status: allOk ? 'PASS' : 'FAIL', asserts, warnings }
  } finally {
    globalThis.Date.now = savedNow
  }
}

// VERBATIM assertion semantics from run-conformance.mjs.
function checkAssertion(step, state) {
  const { channels, synthetic, observedEvents, surfacedErrors } = state

  if (step.op === 'client.assert.state') {
    let target
    let bucketLabel
    if (step.channel) {
      if (!channels.has(step.channel)) {
        return { ok: false, detail: `no reduced state for channel ${step.channel}; known channels: [${[...channels.keys()].join(', ')}]` }
      }
      target = channels.get(step.channel)
      bucketLabel = `channel ${step.channel}`
    } else if (step.path) {
      target = synthetic
      bucketLabel = 'synthetic top-level state'
    } else {
      if (channels.size === 1) {
        target = [...channels.values()][0]
        bucketLabel = `the single channel (${[...channels.keys()][0]})`
      } else {
        return { ok: false, detail: `whole-state assertion needs exactly one channel, found ${channels.size}: [${[...channels.keys()].join(', ')}]` }
      }
    }

    const nav = navigate(target, step.path)
    let actual = nav.value
    if (!nav.found && bucketLabel === 'synthetic top-level state' && step.equals === null) {
      actual = null
    }
    const actualCanon = canonicalize(actual)
    const expectedCanon = canonicalize(step.equals)
    if (deepEqual(actualCanon, expectedCanon)) return { ok: true }
    return {
      ok: false,
      detail: `assert.state @ ${bucketLabel}${step.path ? ` path '${step.path}'` : ' (whole state)'}: expected ${JSON.stringify(expectedCanon)}, got ${nav.found ? JSON.stringify(actualCanon) : '<path not found>'}`,
    }
  }

  if (step.op === 'client.assert.event') {
    const views = (ev) => {
      const vs = [ev]
      if (ev && typeof ev === 'object') {
        if ('action' in ev && ev.action && typeof ev.action === 'object') vs.push(ev.action)
        if ('params' in ev && ev.params && typeof ev.params === 'object') vs.push(ev.params)
      }
      return vs
    }
    for (const ev of observedEvents) {
      for (const view of views(ev)) {
        if (deepContains(view, step.matches)) return { ok: true }
      }
    }
    return {
      ok: false,
      detail: `assert.event: no observed event deep-contains ${JSON.stringify(step.matches)}. observed ${observedEvents.length} event(s)`,
    }
  }

  if (step.op === 'client.assert.error') {
    for (const err of surfacedErrors) {
      if (err?.code !== step.code) continue
      if (step.message != null && !String(err?.message ?? '').includes(step.message)) continue
      return { ok: true }
    }
    return {
      ok: false,
      detail: `assert.error: no surfaced error with code ${step.code}${step.message != null ? ` + message substring '${step.message}'` : ''}. surfaced: ${JSON.stringify(surfacedErrors)}`,
    }
  }

  return { ok: false, detail: `unknown assertion op: ${step.op}` }
}

// ───────────────────────────────────────────────────────────────────────────
// Driver: replay the whole corpus, roll up, exit non-zero on any non-PASS.
// ───────────────────────────────────────────────────────────────────────────
function listScenarios(dir) {
  return readdirSync(dir)
    .filter((f) => f.endsWith('.scenario.json'))
    .sort()
    .map((f) => join(dir, f))
}

const args = process.argv.slice(2)
const VERBOSE = args.includes('--verbose')
const onlyIdx = args.indexOf('--only')
const ONLY = onlyIdx >= 0 ? args[onlyIdx + 1] : null

const families = ONLY ? [ONLY] : ['reducers', 'round-trips', 'negatives']
let scenarios = []
for (const fam of families) {
  scenarios = scenarios.concat(listScenarios(join(SCENARIOS_ROOT, fam)))
}

let pass = 0
let fail = 0
let error = 0
const failures = []
for (const p of scenarios) {
  const r = replayScenario(p)
  if (r.status === 'PASS') pass++
  else if (r.status === 'FAIL') { fail++; failures.push(r) }
  else { error++; failures.push(r) }
}

if (VERBOSE || fail || error) {
  for (const r of failures) {
    process.stderr.write(`\n  ✗ ${r.id}  (${r.status})${r.reason ? `  reason: ${r.reason}` : ''}\n`)
    for (const a of (r.asserts ?? []).filter((x) => !x.ok)) {
      process.stderr.write(`      ✗ ${a.op}  ${a.label}\n        → ${a.detail}\n`)
    }
  }
}

const total = scenarios.length
process.stdout.write(
  `\nCORPUS REPLAY (in-process, src reducers): ${pass}/${total} PASS` +
    `${fail ? `, ${fail} FAIL` : ''}${error ? `, ${error} ERROR` : ''}\n`,
)

// Exit non-zero if ANY scenario did not PASS — this is the Stryker kill-signal.
process.exit(fail || error ? 1 : 0)
