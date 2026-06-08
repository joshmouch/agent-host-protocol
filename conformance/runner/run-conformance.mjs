// AHP HOST-CONFORMANCE RUNNER — the scripted replay CLIENT.
//
// This is the end-to-end green proof that the whole conformance tranche works:
// a real client replays a scenario against the REAL scenario-driven host
// (conformance/host/scenario-host.mjs) over a REAL WebSocket, applies every
// server.notify action through the CANONICAL in-repo reducers, and checks every
// client.assert.* step. NO MOCKS — real files, real transport, real reducers,
// real assertions.
//
// ── Convergence model (snapshot-and-stream) ────────────────────────────────
//   • A `server.response` whose result carries `snapshots[]` SEEDS per-channel
//     reduced state from each snapshot's `state` (keyed by snapshot `resource`).
//     Its `protocolVersion` is captured into a synthetic top-level field.
//   • A `server.notify { method:"action", params: ActionEnvelope }` ROUTES the
//     action through the reducer chosen by the action's `type` PREFIX
//     (root/ session/ terminal/ changeset/ resource/), advancing that channel's
//     state and recording the envelope as an observed event.
//   • A `server.response` whose body is `error` SURFACES a JSON-RPC error.
//   • The impure clock (Date.now) is PINNED client-side to `scenario.pinClock`
//     BEFORE any reduction, exactly as the host pins it — so impure reducer
//     fields (summary.modifiedAt, microsoft/agent-host-protocol#186) converge.
//
// ── Reducer dispatch is by ACTION-TYPE PREFIX, not channel scheme ───────────
//   The corpus routes terminal-reducer fixtures onto an `ahp-session:/…`
//   channel (gen-scenarios has no terminal channel entry), but their state is a
//   TerminalState and their actions are `terminal/*`. Dispatching by channel
//   scheme would pick the wrong reducer; dispatching by the action `type` prefix
//   is correct. The channel string is only the state-bucket key.
//
// ── Assertions ─────────────────────────────────────────────────────────────
//   • client.assert.state  — deep-equal the reduced channel state (whole, when
//     `path` empty/absent → byte-for-byte convergence) or the value at a dotted
//     `path`. A path with no `channel` reads the synthetic top-level state
//     (protocolVersion / pingSeen).
//   • client.assert.event  — partial deep-CONTAINED match against any observed
//     event (the ActionEnvelope, or a decoded response/notification).
//   • client.assert.error  — a surfaced JSON-RPC error matches `code` (and, if
//     present, the `message` substring).
//
// Usage:
//   node run-conformance.mjs <scenario.json> [--host <path>] [--verbose]
// Exit 0 = scenario PASSED; 1 = scenario FAILED; 2 = harness/setup error.
//
// Programmatic: `import { runScenario } from './run-conformance.mjs'` returns a
// structured result the suite driver rolls up.

import { spawn } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, resolve, basename } from 'node:path'
import { WebSocket } from 'ws'
import {
  rootReducer,
  sessionReducer,
  terminalReducer,
  changesetReducer,
  resourceWatchReducer,
} from '@microsoft/agent-host-protocol'

const __dirname = dirname(fileURLToPath(import.meta.url))
const DEFAULT_HOST = resolve(__dirname, '..', 'host', 'scenario-host.mjs')

// ───────────────────────────────────────────────────────────────────────────
// Reducer dispatch — by action-type prefix (the reliable discriminator).
// ───────────────────────────────────────────────────────────────────────────
const REDUCER_BY_PREFIX = {
  root: rootReducer,
  session: sessionReducer,
  terminal: terminalReducer,
  changeset: changesetReducer,
  resource: resourceWatchReducer, // resource/watchChanged → resourceWatchReducer
}

function reducerForAction(action) {
  const type = action?.type
  if (typeof type !== 'string') return null
  const prefix = type.split('/')[0]
  return REDUCER_BY_PREFIX[prefix] ?? null
}

// ───────────────────────────────────────────────────────────────────────────
// Deep equality + deep-containment + dotted-path navigation.
// Pure, dependency-free; mirrors the round-trip corpus's expect discipline.
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

// `expected` is deep-CONTAINED in `actual`: every key in expected matches
// (recursively); extra keys in actual are ignored. Arrays compare element-wise
// with the same containment rule.
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

// Canonicalize a JSON value for CONVERGENCE EQUALITY: recursively drop
// null/undefined-valued object keys and sort keys. This is the SAME rule every
// cross-language harness uses (.NET FixtureDrivenReducerTests.Canon, and the
// Go/TS harnesses it cites): "an omitted optional field equals an explicit
// null." The canonical TS reducer represents absent optionals as `undefined`
// (e.g. `activeClient: action.activeClient ?? undefined`), which JSON-drops on
// the wire; the JSON fixtures spell the same absence as explicit `null`. Both
// normalize to the same canonical form here, so they converge.
function canonicalize(value) {
  if (Array.isArray(value)) return value.map(canonicalize)
  if (value !== null && typeof value === 'object') {
    const out = {}
    for (const k of Object.keys(value).sort()) {
      const v = value[k]
      if (v === null || v === undefined) continue // drop null/undefined-valued keys
      out[k] = canonicalize(v)
    }
    return out
  }
  return value
}

// Navigate a dotted path; numeric segments index arrays. Returns
// { found: boolean, value }. Empty / undefined path → the whole object.
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
// Clock pin — replicate the host's determinism contract on the client side.
// ───────────────────────────────────────────────────────────────────────────
function pinClock(epochMs) {
  if (typeof epochMs === 'number') globalThis.Date.now = () => epochMs
}

// ───────────────────────────────────────────────────────────────────────────
// Start the scenario-driven host for one scenario; resolve its ws:// URL.
// ───────────────────────────────────────────────────────────────────────────
function startHost(hostScript, scenarioPath, { timeoutMs = 10000 } = {}) {
  const child = spawn(process.execPath, [hostScript, scenarioPath], {
    stdio: ['ignore', 'pipe', 'pipe'],
  })
  let stderr = ''
  child.stderr.on('data', (c) => { stderr += c.toString() })

  // READY line: "SCENARIO HOST READY <wsUrl> <scenarioId> <connectNonce>".
  // The nonce is the WebSocket subprotocol the host will negotiate; the client
  // connects with it so it can detect landing on a foreign server (recycled
  // ephemeral port) and re-spawn on a fresh port.
  const ready = new Promise((resolveReady, rejectReady) => {
    let buf = ''
    const onData = (chunk) => {
      buf += chunk.toString()
      const m = buf.match(/SCENARIO HOST READY (ws:\/\/127\.0\.0\.1:\d+) \S+ (\S+)/)
      if (m) { child.stdout.off('data', onData); resolveReady({ url: m[1], nonce: m[2] }) }
    }
    child.stdout.on('data', onData)
    child.on('exit', (code) => {
      if (code !== 0) rejectReady(new Error(`host exited with code ${code} before READY. stderr:\n${stderr}`))
    })
    setTimeout(() => rejectReady(new Error(`host did not print READY within ${timeoutMs}ms. stderr:\n${stderr}`)), timeoutMs)
  })

  return { child, ready, getStderr: () => stderr }
}

// ───────────────────────────────────────────────────────────────────────────
// Drive the protocol: send each client.request in scenario order, collect
// every incoming frame, and reduce action notifications into per-channel state.
// Resolves when the host closes the socket (plan exhausted) or a timeout fires.
// ───────────────────────────────────────────────────────────────────────────
// Is this a TRANSIENT connect-phase websocket error (the upgrade hit a
// half-ready / just-recycled listener)? Such errors happen BEFORE the socket
// ever opens, so a bounded retry to the same host URL is safe and correct
// (per the repo's transient-failure-retry discipline). A post-open error is a
// real failure and is never retried.
function isTransientConnectError(message) {
  return (
    /Unexpected server response: \d+/.test(message) || // e.g. 400/503 before upgrade
    /ECONNREFUSED/.test(message) ||
    /ECONNRESET/.test(message) ||
    /socket hang up/.test(message) ||
    /EPIPE/.test(message)
  )
}

// Sentinel: a pre-open connection failure (or a connection that reached a
// FOREIGN server because the host's ephemeral port was recycled). The caller
// re-spawns the host on a fresh port rather than retrying the same — possibly
// stolen — port. Carries the underlying reason for diagnostics.
class RespawnNeeded extends Error {
  constructor(reason) { super(reason); this.name = 'RespawnNeeded'; this.respawn = true }
}

function driveProtocol(wsUrl, nonce, scenario, { timeoutMs = 10000 } = {}) {
  return new Promise((resolveDrive, rejectDrive) => {
    // The ordered client.request steps to replay (host correlates by id/position).
    const requests = scenario.steps.filter((s) => s.op === 'client.request')

    // One connection attempt. A PRE-OPEN failure (or a subprotocol mismatch,
    // meaning we reached a foreign server on a recycled port) rejects with
    // RespawnNeeded so the caller re-spawns on a fresh port. Once opened against
    // the CORRECT host (nonce subprotocol echoed), errors are real.
    {
      // Connect requesting the host's nonce as the WebSocket subprotocol. The
      // real scenario host negotiates it back; a foreign server will not.
      const ws = new WebSocket(wsUrl, [nonce])
      let opened = false

      // Per-channel reduced state, keyed by channel/resource URI.
      const channels = new Map()
      // Synthetic top-level runner state (protocolVersion, pingSeen, …).
      const synthetic = {}
      // Every observed action envelope / decoded message, in arrival order.
      const observedEvents = []
      // Every surfaced JSON-RPC error response.
      const surfacedErrors = []
      // Warnings (non-fatal) emitted while reducing.
      const warnings = []

      let requestCursor = 0

    function sendNextRequest() {
      if (requestCursor >= requests.length) return
      const step = requests[requestCursor++]
      const frame = { jsonrpc: '2.0', method: step.method, id: step.id }
      if ('params' in step) frame.params = step.params
      ws.send(JSON.stringify(frame))
    }

    function applyActionNotification(params) {
      // params is an ActionEnvelope: { channel, action, serverSeq, origin }.
      // ALWAYS record the observed event first — `client.assert.event` checks
      // that the envelope was decoded/observed, independent of whether it folds
      // into convergent state.
      observedEvents.push(params)
      const channel = params?.channel
      const action = params?.action
      const reducer = reducerForAction(action)
      if (!reducer) {
        // Non-reducible action shape (e.g. a wrapped wire value that is not a
        // real StateAction — round-trip "generic case" payloads). The event is
        // still observed above; there is nothing to reduce.
        return
      }
      const hadPrev = channels.has(channel)
      const prev = hadPrev ? channels.get(channel) : undefined
      try {
        // The reducer is pure; the 3rd arg is an optional log sink — omit it.
        const next = reducer(prev, action)
        channels.set(channel, next)
      } catch (e) {
        // A reducer that throws WITH seeded prior state is a real reducer bug —
        // surface it loudly (it will fail any assert.state and is recorded as a
        // warning). A reducer that throws with NO prior state is an event-only
        // round-trip scenario (no snapshot was seeded for this channel); the
        // envelope is already observed, so swallow the incidental fold error.
        if (hadPrev) {
          warnings.push(`reducer for ${action?.type} on channel ${channel} threw with seeded state: ${e.message}`)
        }
        // else: event-only scenario — observed event suffices, no state to fold.
      }
    }

    function seedFromSnapshots(result) {
      if (typeof result?.protocolVersion === 'string') {
        synthetic.protocolVersion = result.protocolVersion
      }
      const snaps = result?.snapshots
      if (Array.isArray(snaps)) {
        for (const snap of snaps) {
          if (snap && typeof snap.resource === 'string') {
            channels.set(snap.resource, snap.state)
          }
        }
      }
      // Reconnect snapshot/replay shapes also carry state — handle both.
      if (result?.snapshot && typeof result.snapshot.resource === 'string') {
        channels.set(result.snapshot.resource, result.snapshot.state)
      }
    }

      let settled = false
      const finish = () => {
        if (settled) return
        settled = true
        clearTimeout(softTimer)
        try { ws.close() } catch { /* already closing */ }
        resolveDrive({ channels, synthetic, observedEvents, surfacedErrors, warnings })
      }

      ws.on('open', () => {
        // Guard against a recycled-port collision that nonetheless completed a
        // WebSocket upgrade: only the real scenario host echoes our nonce as the
        // negotiated subprotocol. Anything else means we reached a foreign
        // server squatting on the recycled port — re-spawn on a fresh port.
        if (ws.protocol !== nonce) {
          if (settled) return
          settled = true
          clearTimeout(softTimer)
          try { ws.close() } catch { /* noop */ }
          rejectDrive(new RespawnNeeded(`connected to a server that did not echo the host nonce (got subprotocol '${ws.protocol}') — recycled ephemeral port`))
          return
        }
        opened = true
        // Kick off the first request. Subsequent requests are sent as each prior
        // response arrives (the host replies one response per request, in order).
        sendNextRequest()
        // If a scenario has zero client.request steps (pure notify stream), the
        // host still flushes leading notifies on connection; nothing to send.
      })

      ws.on('message', (raw) => {
        let msg
        try { msg = JSON.parse(String(raw)) } catch { return }

        if (msg.id != null && (msg.result !== undefined || msg.error !== undefined)) {
          // It's a response to one of our requests.
          if (msg.error !== undefined) {
            surfacedErrors.push(msg.error)
            synthetic.lastResponseOk = false
          } else {
            // A success (non-error) response. Expose a synthetic flag so success-
            // variant round-trips can assert the decode succeeded, and seed any
            // snapshot the result carries.
            synthetic.lastResponseOk = true
            seedFromSnapshots(msg.result)
          }
          // Drive the next request now that this one is answered.
          sendNextRequest()
        } else if (msg.method != null && msg.id == null) {
          // It's a server notification. Record the MESSAGE form { method, params }
          // as an observed event so message-level assertions (e.g.
          // matches:{ method:'action' }) can match. For 'action' notifications we
          // additionally reduce + record the ActionEnvelope (params) so
          // envelope/action-field assertions match and convergence folds.
          observedEvents.push({ method: msg.method, params: msg.params })
          if (msg.method === 'action') {
            applyActionNotification(msg.params)
          }
        }
        // Other frame shapes are ignored.
      })

      ws.on('close', (code, reason) => {
        // A close that arrives BEFORE we ever opened-and-validated is not a clean
        // end-of-plan — it means the upgrade was refused/torn down (e.g. a
        // foreign server on a recycled port closing with 1008/1006). Re-spawn on
        // a fresh port rather than asserting against empty state.
        if (!opened && !settled) {
          settled = true
          clearTimeout(softTimer)
          rejectDrive(new RespawnNeeded(`socket closed before open (code ${code}${reason && reason.length ? `, reason '${String(reason)}'` : ''}) — likely a recycled ephemeral port`))
          return
        }
        finish()
      })
      ws.on('error', (e) => {
        if (settled) return
        // A PRE-OPEN error (the upgrade hit a half-ready / recycled listener, or
        // a foreign server squatting on a recycled ephemeral port) re-spawns the
        // host on a fresh port — retrying the SAME, possibly-stolen, port is
        // futile. A POST-OPEN error is a real failure.
        if (!opened && isTransientConnectError(e.message)) {
          try { ws.close() } catch { /* noop */ }
          clearTimeout(softTimer)
          settled = true
          rejectDrive(new RespawnNeeded(`pre-open websocket error: ${e.message}`))
          return
        }
        settled = true
        clearTimeout(softTimer)
        rejectDrive(new Error(`websocket error: ${e.message}`))
      })

      const softTimer = setTimeout(() => {
        // Timeout is a soft finish: some scenarios (e.g. ping-edge) leave the
        // socket open after the last expected frame. We've collected everything;
        // proceed to assertions.
        finish()
      }, timeoutMs)
    }
  })
}

// ───────────────────────────────────────────────────────────────────────────
// Check one client.assert.* step against the collected client state.
// Returns { ok, detail }.
// ───────────────────────────────────────────────────────────────────────────
function checkAssertion(step, state) {
  const { channels, synthetic, observedEvents, surfacedErrors } = state

  if (step.op === 'client.assert.state') {
    // Choose the state bucket: an explicit channel, else (path-only) the
    // synthetic top-level state, else the scenario's single primary channel.
    let target
    let bucketLabel
    if (step.channel) {
      if (!channels.has(step.channel)) {
        return { ok: false, detail: `no reduced state for channel ${step.channel}; known channels: [${[...channels.keys()].join(', ')}]` }
      }
      target = channels.get(step.channel)
      bucketLabel = `channel ${step.channel}`
    } else if (step.path) {
      // Path with no channel → synthetic top-level state (protocolVersion / pingSeen).
      target = synthetic
      bucketLabel = 'synthetic top-level state'
    } else {
      // No channel and no path → whole-state convergence against the single channel.
      if (channels.size === 1) {
        target = [...channels.values()][0]
        bucketLabel = `the single channel (${[...channels.keys()][0]})`
      } else {
        return { ok: false, detail: `whole-state assertion needs exactly one channel, found ${channels.size}: [${[...channels.keys()].join(', ')}]` }
      }
    }

    const nav = navigate(target, step.path)
    // For synthetic top-level paths, an unset field resolves to undefined; the
    // scenario expects `null` for "never set" (e.g. pingSeen). Treat
    // undefined-at-synthetic ≈ the expected value when that expected value is
    // null. For real channel state, undefined≠null is preserved (no masking).
    let actual = nav.value
    if (!nav.found && bucketLabel === 'synthetic top-level state' && step.equals === null) {
      actual = null
    }
    // Convergence equality uses the canonical null-stripped/key-sorted form, the
    // SAME rule as the cross-language harnesses (omitted optional == explicit
    // null). Scalars pass through canonicalize unchanged.
    const actualCanon = canonicalize(actual)
    const expectedCanon = canonicalize(step.equals)
    if (deepEqual(actualCanon, expectedCanon)) return { ok: true }
    return {
      ok: false,
      detail: `assert.state @ ${bucketLabel}${step.path ? ` path '${step.path}'` : ' (whole state)'}: expected ${JSON.stringify(expectedCanon)}, got ${nav.found ? JSON.stringify(actualCanon) : '<path not found>'}`,
    }
  }

  if (step.op === 'client.assert.event') {
    // The corpus's `matches` shapes name fields at three different depths of an
    // observed event, so we try the event under several VIEWS and pass if the
    // partial deep-contains any of them:
    //   • the envelope itself        — { channel, serverSeq, action, origin }
    //   • the envelope's `.action`   — inner action fields named directly
    //     (e.g. { type, title }, { kind, id, … }, { markdown }, { resource… })
    //   • the envelope's `.params`   — when a non-action message was recorded
    //   • the message form           — { method, params } for non-action notifs
    // An empty `matches` ({}) deep-contains every object → always passes if any
    // event was observed.
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
      detail: `assert.event: no observed event (or its .action/.params view) deep-contains ${JSON.stringify(step.matches)}. observed ${observedEvents.length} event(s): ${JSON.stringify(observedEvents).slice(0, 600)}`,
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
// Run one scenario end-to-end. Returns a structured result.
// ───────────────────────────────────────────────────────────────────────────
export async function runScenario(scenarioPath, opts = {}) {
  const hostScript = opts.host ?? DEFAULT_HOST
  const verbose = !!opts.verbose
  const id = basename(scenarioPath).replace(/\.scenario\.json$/, '')

  let scenario
  try {
    scenario = JSON.parse(readFileSync(scenarioPath, 'utf8'))
  } catch (e) {
    return { id, scenarioPath, status: 'ERROR', reason: `failed to parse scenario: ${e.message}`, asserts: [] }
  }

  // Pin the client clock BEFORE any reduction so impure fields converge.
  pinClock(scenario.pinClock)

  // Spawn the host, connect, and drive the protocol. A pre-open failure or a
  // connection that reached a foreign server (because the host's OS-assigned
  // ephemeral port was recycled by another local process — common on a busy dev
  // machine running editors / model servers) raises RespawnNeeded; we then kill
  // this host and spawn a brand-new one, which binds a FRESH port. Retrying the
  // same — possibly stolen — port would be futile.
  const SPAWN_ATTEMPTS = 6
  let state
  let lastErr
  for (let spawnTry = 0; spawnTry < SPAWN_ATTEMPTS; spawnTry++) {
    const { child, ready, getStderr } = startHost(hostScript, scenarioPath)
    try {
      const { url: wsUrl, nonce } = await ready
      state = await driveProtocol(wsUrl, nonce, scenario)
      break // success
    } catch (e) {
      lastErr = e
      if (e && e.respawn && spawnTry < SPAWN_ATTEMPTS - 1) {
        // Brief backoff before re-spawning on a fresh port.
        await new Promise((r) => setTimeout(r, 60))
        continue
      }
      return { id, scenarioPath, status: 'ERROR', reason: e.message, hostStderr: getStderr(), asserts: [] }
    } finally {
      try { child.kill() } catch { /* noop */ }
    }
  }
  if (state === undefined) {
    return { id, scenarioPath, status: 'ERROR', reason: lastErr ? lastErr.message : 'host did not converge after respawns', asserts: [] }
  }

  // Run every assertion step against the collected state.
  const assertSteps = scenario.steps.filter((s) => s.op.startsWith('client.assert.'))
  const asserts = []
  let allOk = true
  for (const step of assertSteps) {
    const res = checkAssertion(step, state)
    asserts.push({ op: step.op, label: step.label ?? '', ok: res.ok, detail: res.detail })
    if (!res.ok) allOk = false
  }

  // A scenario with no assertion steps is meaningless — flag it.
  if (assertSteps.length === 0) {
    return { id, scenarioPath, status: 'ERROR', reason: 'scenario has no client.assert.* steps', asserts: [], warnings: state.warnings }
  }

  if (verbose) {
    for (const a of asserts) {
      console.log(`  ${a.ok ? 'PASS' : 'FAIL'}  ${a.op}  ${a.label}${a.ok ? '' : `\n        → ${a.detail}`}`)
    }
    for (const w of state.warnings) console.log(`  WARN  ${w}`)
  }

  return {
    id,
    scenarioPath,
    status: allOk ? 'PASS' : 'FAIL',
    asserts,
    warnings: state.warnings,
  }
}

// ───────────────────────────────────────────────────────────────────────────
// CLI entry: run a single scenario.
// ───────────────────────────────────────────────────────────────────────────
const isMain = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)
if (isMain) {
  const args = process.argv.slice(2)
  const scenarioPath = args.find((a) => !a.startsWith('--'))
  const verbose = args.includes('--verbose')
  const hostIdx = args.indexOf('--host')
  const host = hostIdx >= 0 ? args[hostIdx + 1] : undefined

  if (!scenarioPath) {
    console.error('Usage: node run-conformance.mjs <scenario.json> [--host <path>] [--verbose]')
    process.exit(2)
  }

  const result = await runScenario(resolve(scenarioPath), { verbose, host })
  if (result.status === 'PASS') {
    console.log(`PASS  ${result.id}  (${result.asserts.length} assertion(s))`)
    process.exit(0)
  } else if (result.status === 'FAIL') {
    console.error(`FAIL  ${result.id}`)
    for (const a of result.asserts.filter((x) => !x.ok)) {
      console.error(`  ✗ ${a.op}  ${a.label}\n    → ${a.detail}`)
    }
    process.exit(1)
  } else {
    console.error(`ERROR ${result.id}: ${result.reason}`)
    if (result.hostStderr) console.error(`  host stderr:\n${result.hostStderr}`)
    process.exit(2)
  }
}
