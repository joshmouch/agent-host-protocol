// Real-execution smoke test for scenario-host.mjs.
//
// Starts the scenario-driven host for the example scenario, connects over a
// real WebSocket, sends the initialize request, and asserts:
//   - The response carries the scripted snapshot.
//   - All five scripted server.notify action notifications are received.
//   - The last message's action matches the final scripted notify.
//
// Exits 0 on PASS, 1 on any failure.
//
// Usage: node conformance/host/scenario-smoke.mjs
// (Run from the repo root; the host is started as a child process.)

import { spawn }  from 'node:child_process'
import { WebSocket } from 'ws'

const SCENARIO = new URL('../../types/test-cases/scenarios/examples/lifecycle.initialize.happy.snapshot-then-action-stream.scenario.json', import.meta.url).pathname
const HOST_SCRIPT = new URL('./scenario-host.mjs', import.meta.url).pathname

// ---------------------------------------------------------------------------
// 1. Start the scenario host as a child process.
// ---------------------------------------------------------------------------
const host = spawn(process.execPath, [HOST_SCRIPT, SCENARIO], { stdio: ['ignore', 'pipe', 'inherit'] })

let wsUrl = ''
const readyPromise = new Promise((resolve, reject) => {
  let buf = ''
  host.stdout.on('data', (chunk) => {
    buf += chunk.toString()
    const m = buf.match(/SCENARIO HOST READY (ws:\/\/127\.0\.0\.1:\d+)/)
    if (m) { wsUrl = m[1]; resolve(wsUrl) }
  })
  host.on('exit', (code) => {
    if (code !== 0 && !wsUrl) reject(new Error(`host exited with code ${code} before printing URL`))
  })
  setTimeout(() => reject(new Error('host did not print READY within 5s')), 5000)
})

try {
  await readyPromise
  console.log(`host: ${wsUrl}`)

  // -------------------------------------------------------------------------
  // 2. Connect over real WebSocket and drive the protocol.
  // -------------------------------------------------------------------------
  const messages = []
  await new Promise((resolve, reject) => {
    const ws = new WebSocket(wsUrl)

    ws.on('open', () => {
      // Send the initialize request (id 1), exactly as the scenario's client.request step.
      ws.send(JSON.stringify({ jsonrpc: '2.0', method: 'initialize', params: {}, id: 1 }))
    })

    ws.on('message', (raw) => {
      const msg = JSON.parse(String(raw))
      messages.push(msg)
    })

    ws.on('close', () => resolve())
    ws.on('error', reject)

    setTimeout(() => reject(new Error('client timed out after 5s')), 5000)
  })

  // -------------------------------------------------------------------------
  // 3. Assert results.
  // -------------------------------------------------------------------------
  const EXPECTED_NOTIFY_COUNT = 5  // matches the scenario's 5 server.notify steps
  const [response, ...notifies] = messages

  let pass = true
  const fail = (msg) => { console.error('FAIL:', msg); pass = false }

  // 3a. Response carries result (not error) for id 1.
  if (!response) { fail('no response received'); }
  else {
    if (response.id !== 1) fail(`response id: expected 1, got ${response.id}`)
    if (!response.result) fail('response.result is missing (expected initialize result with snapshot)')
    if (response.result?.snapshots?.[0]?.resource !== 'ahp-session:/compliant')
      fail(`snapshot resource: got ${response.result?.snapshots?.[0]?.resource}`)
    if (response.result?.protocolVersion !== '0.3.0')
      fail(`protocolVersion: got ${response.result?.protocolVersion}`)
    console.log('OK: initialize response carries snapshot')
  }

  // 3b. Notify count.
  if (notifies.length !== EXPECTED_NOTIFY_COUNT)
    fail(`expected ${EXPECTED_NOTIFY_COUNT} server.notify messages, got ${notifies.length}`)
  else
    console.log(`OK: received ${notifies.length} action notifications`)

  // 3c. Last notify is the isArchivedChanged action (step 5 in the scenario).
  const last = notifies[notifies.length - 1]
  if (last?.method !== 'action') fail(`last message method: expected 'action', got ${last?.method}`)
  if (last?.params?.action?.type !== 'session/isArchivedChanged')
    fail(`last action type: expected 'session/isArchivedChanged', got ${last?.params?.action?.type}`)
  if (last?.params?.action?.isArchived !== true)
    fail(`last action isArchived: expected true, got ${last?.params?.action?.isArchived}`)
  else
    console.log('OK: last notify is isArchivedChanged(true)')

  // 3d. Second-to-last notify is titleChanged 'Live handshake two'.
  const secondLast = notifies[notifies.length - 2]
  if (secondLast?.params?.action?.title !== 'Live handshake two')
    fail(`second-last title: expected 'Live handshake two', got ${secondLast?.params?.action?.title}`)
  else
    console.log("OK: second-last notify is titleChanged('Live handshake two')")

  // 3e. serverSeq numbers are 1..5 (the scenario assigns them).
  const seqs = notifies.map(n => n?.params?.serverSeq)
  const expectedSeqs = [1, 2, 3, 4, 5]
  if (JSON.stringify(seqs) !== JSON.stringify(expectedSeqs))
    fail(`serverSeq sequence: expected ${JSON.stringify(expectedSeqs)}, got ${JSON.stringify(seqs)}`)
  else
    console.log(`OK: serverSeq 1..5 in order`)

  if (pass) {
    console.log('\nSCENARIO SMOKE PASS — scenario-host plays script faithfully over real WebSocket')
    process.exitCode = 0
  } else {
    process.exitCode = 1
  }

} finally {
  host.kill()
}
