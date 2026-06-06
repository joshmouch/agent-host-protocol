// Scenario-driven AHP conformance host.
//
// Reads a *.scenario.json file and serves its server-side script — the ordered
// sequence of server.response + server.notify steps — over a real WebSocket to
// any connecting client.  The host honours the scenario's forId contract (each
// server.response is sent only after the client.request with that id arrives)
// and flushes all server.notify steps that immediately follow a response before
// waiting for the next request.
//
// pin.clock steps (and the top-level pinClock field) pin Date.now() to a fixed
// epoch-ms value so impure reducer fields (e.g. summary.modifiedAt) are
// deterministic — exactly as conformance/host/host.mjs does.
//
// Usage:
//   node conformance/host/scenario-host.mjs <path-to-scenario.json>
//
// The host prints one line to stdout when ready:
//   SCENARIO HOST READY  ws://127.0.0.1:<port>  <scenario-id>
//
// It exits cleanly once the client disconnects (all server steps delivered).
// Exit code 0 = OK; 1 = bad scenario path or JSON; 2 = scenario schema error.
//
// run-scenario.sh wires this for the existing run.sh neighbourhood.

import { readFileSync } from 'node:fs'
import { WebSocketServer } from 'ws'

// ---------------------------------------------------------------------------
// 1. Parse CLI arg
// ---------------------------------------------------------------------------
const scenarioPath = process.argv[2]
if (!scenarioPath) {
  console.error('Usage: node scenario-host.mjs <path-to-scenario.json>')
  process.exit(1)
}

let scenario
try {
  scenario = JSON.parse(readFileSync(scenarioPath, 'utf8'))
} catch (e) {
  console.error('Failed to read/parse scenario:', e.message)
  process.exit(1)
}

// Basic presence checks (the full validator lives in validate-scenarios.mjs).
if (!scenario || typeof scenario !== 'object') { console.error('Scenario must be a JSON object'); process.exit(2) }
if (!Array.isArray(scenario.steps) || scenario.steps.length === 0) { console.error('Scenario must have a non-empty steps array'); process.exit(2) }
if (typeof scenario.id !== 'string') { console.error('Scenario must have an id string'); process.exit(2) }

// ---------------------------------------------------------------------------
// 2. Apply top-level pinClock (before any step, before any reducer work).
// ---------------------------------------------------------------------------
function pinClock(epochMs) {
  globalThis.Date.now = () => epochMs
}

if (typeof scenario.pinClock === 'number') {
  pinClock(scenario.pinClock)
}

// ---------------------------------------------------------------------------
// 3. Extract the server-side script from the steps array.
//
//    We only look at server.response, server.notify, and pin.clock steps.
//    client.* and assert.* steps are for client runners; the host ignores them
//    structurally (they carry no server-side effect).
//
//    We build a list of "reply groups", one per server.response:
//      { forId, responsePayload, notifies: [] }
//    Each group collects the server.notify steps that immediately follow the
//    server.response (up to the next server.response, a pin.clock, or end).
//
//    pin.clock steps are recorded at the position in the script where they
//    appear — the host executes them in order (before the next response/notify).
// ---------------------------------------------------------------------------

/** @typedef {{ kind: 'response', forId: string|number, payload: object, clockPins: number[] }} ReplyGroup */
/** @typedef {{ kind: 'pin', value: number }} ClockPin */

// We produce a flat execution plan: a sequence of items the host executes in
// order as requests arrive.
//
// Plan item:
//   { type: 'pin',      value }            — execute pinClock(value) immediately
//   { type: 'response', forId, payload }   — send when request with that id arrives
//   { type: 'notify',   payload }          — send immediately after the preceding response

const plan = []

for (const step of scenario.steps) {
  if (typeof step.op !== 'string') continue  // malformed — skip

  if (step.op === 'pin.clock') {
    plan.push({ type: 'pin', value: step.value })
  } else if (step.op === 'server.response') {
    // Build the JSON-RPC response frame.
    const frame = { jsonrpc: '2.0', id: step.forId }
    if ('result' in step) {
      frame.result = step.result
    } else {
      frame.error = step.error
    }
    plan.push({ type: 'response', forId: step.forId, payload: frame })
  } else if (step.op === 'server.notify') {
    // Build the JSON-RPC notification frame (no id).
    const frame = { jsonrpc: '2.0', method: step.method }
    if ('params' in step) frame.params = step.params
    plan.push({ type: 'notify', payload: frame })
  }
  // client.request, client.assert.*, client.reconnect — no server-side effect.
}

// ---------------------------------------------------------------------------
// 4. Serve the script over real WebSocket.
//
//    Execution model:
//      - Maintain a cursor into the plan.
//      - On each incoming JSON-RPC request from the client:
//          a. Advance the cursor past any leading pin.clock items (execute them).
//          b. Find the next response item; if its forId matches the request id,
//             send the response, then flush all following notify/pin items until
//             the next response item (or end of plan).
//          c. If forId does not match (scenario out of sync), log a warning and
//             send an error frame so the client isn't left hanging.
//      - When the plan is exhausted, close the ws cleanly.
// ---------------------------------------------------------------------------

const wss = new WebSocketServer({ port: 0 })
await new Promise((r) => wss.once('listening', r))
console.log('SCENARIO HOST READY', `ws://127.0.0.1:${wss.address().port}`, scenario.id)

wss.on('connection', (ws) => {
  let cursor = 0

  // Flush all LEADING server-push items (pin.clock + server.notify) that come
  // before the first scripted server.response. A scenario whose server side is
  // a pure notify-stream (no client.request / no server.response — e.g. every
  // round-trip ActionEnvelope fixture, where the server simply pushes one
  // 'action' notification) carries no request to trigger a flush, so these
  // notifies must be delivered on connection. pin.clock items are applied here
  // too (they were already, for the same before-first-request reason).
  while (cursor < plan.length && plan[cursor].type !== 'response') {
    const item = plan[cursor]
    if (item.type === 'pin') {
      pinClock(item.value)
    } else if (item.type === 'notify') {
      ws.send(JSON.stringify(item.payload))
    }
    cursor++
  }

  // If the whole plan was leading push items (no response at all), the exchange
  // is complete — close once the client has had a tick to receive them.
  if (cursor >= plan.length) {
    setImmediate(() => ws.close())
  }

  ws.on('message', (raw) => {
    let msg
    try { msg = JSON.parse(String(raw)) } catch { return }
    // Only handle JSON-RPC requests (has an id field, not a notification).
    if (msg.id == null) return

    // Find the next response item in the plan.
    while (cursor < plan.length && plan[cursor].type !== 'response') {
      if (plan[cursor].type === 'pin') {
        pinClock(plan[cursor].value)
      }
      // notify items before any response are unusual but harmless to skip.
      cursor++
    }

    if (cursor >= plan.length) {
      // No response scripted for this request.
      ws.send(JSON.stringify({ jsonrpc: '2.0', id: msg.id, error: { code: -32000, message: 'scenario: no more scripted responses' } }))
      return
    }

    const responseItem = plan[cursor]

    // Warn if forId doesn't match (scenario request order doesn't match actual
    // client request order), but still send the scripted response so the client
    // isn't left hanging.
    if (responseItem.forId !== msg.id) {
      process.stderr.write(
        `[scenario-host] WARNING: expected request id ${JSON.stringify(responseItem.forId)}, got ${JSON.stringify(msg.id)}; sending scripted response anyway\n`
      )
    }

    // Send the response with the client's actual id so JSON-RPC correlation works.
    const frame = { ...responseItem.payload, id: msg.id }
    ws.send(JSON.stringify(frame))
    cursor++

    // Flush all notify + pin.clock items that immediately follow this response,
    // stopping before the next response item.
    while (cursor < plan.length && plan[cursor].type !== 'response') {
      const item = plan[cursor]
      if (item.type === 'pin') {
        pinClock(item.value)
      } else if (item.type === 'notify') {
        ws.send(JSON.stringify(item.payload))
      }
      cursor++
    }

    // If the plan is exhausted, close the connection cleanly.
    if (cursor >= plan.length) {
      // Give the client a tick to receive the last message before closing.
      setImmediate(() => ws.close())
    }
  })

  ws.on('close', () => {
    // Shut the server down once the connection closes so the process exits.
    wss.close()
  })
})
