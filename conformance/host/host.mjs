// A minimal, spec-faithful AHP host on the CANONICAL sessionReducer, speaking
// the real AHP wire protocol (initialize / subscribe / action). Depends only on
// the IN-REPO @microsoft/agent-host-protocol TypeScript client (clients/typescript,
// wired as a `file:` dependency) + ws — no external host, no published package.
// Clock pinned so the impure modifiedAt (#186) is deterministic.
globalThis.Date.now = () => 9999

import { writeFileSync } from 'node:fs'
import { WebSocketServer } from 'ws'
import { sessionReducer } from '@microsoft/agent-host-protocol'

const channel = 'ahp-session:/compliant'
const freshInitial = () => ({
  summary: { resource: 'copilot:/compliant', provider: 'copilot', title: 'Initial title', status: 1, createdAt: 1000, modifiedAt: 1000 },
  lifecycle: 'creating', turns: [],
})
// Host-authoritative channel: reduce via the canonical reducer + assign serverSeq.
const makeChannel = (initial) => {
  let state = initial, seq = 0
  return {
    apply: (action) => { state = sessionReducer(state, action); return { channel, action, serverSeq: ++seq, origin: null } },
    snapshot: () => ({ state, serverSeq: seq }),
  }
}
const plan = [
  { type: 'session/titleChanged',      title: 'Live handshake one' },
  { type: 'session/isReadChanged',     isRead: true },
  { type: 'session/activityChanged',   activity: 'streaming' },
  { type: 'session/titleChanged',      title: 'Live handshake two' },
  { type: 'session/isArchivedChanged', isArchived: true },
]

const finalUrl = new URL('./final.json', import.meta.url)
const tmp = makeChannel(freshInitial()); for (const a of plan) tmp.apply(a)
writeFileSync(finalUrl, JSON.stringify({ final: tmp.snapshot().state, count: plan.length }, null, 2))

const ch = makeChannel(freshInitial())
const wss = new WebSocketServer({ port: 0 })
await new Promise((r) => wss.once('listening', r))
console.log('COMPLIANT HOST READY', `ws://127.0.0.1:${wss.address().port}`)
wss.on('connection', (ws) => ws.on('message', (raw) => {
  let msg; try { msg = JSON.parse(String(raw)) } catch { return }
  if (msg.method === 'initialize') {
    const s = ch.snapshot()
    ws.send(JSON.stringify({ jsonrpc: '2.0', id: msg.id, result: { protocolVersion: '0.3.0', serverSeq: s.serverSeq, snapshots: [{ resource: channel, state: s.state, fromSeq: s.serverSeq }] } }))
    for (const a of plan) ws.send(JSON.stringify({ jsonrpc: '2.0', method: 'action', params: ch.apply(a) }))
  } else if (msg.method === 'subscribe') {
    const s = ch.snapshot()
    ws.send(JSON.stringify({ jsonrpc: '2.0', id: msg.id, result: { snapshot: { resource: msg.params?.channel ?? channel, state: s.state, fromSeq: s.serverSeq } } }))
  }
}))
