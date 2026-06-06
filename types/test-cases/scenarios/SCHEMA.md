# AHP Conformance — Scenario-Fixture schema (Part 2, build-phase B1)

This is the **shared, language-neutral vocabulary** for the AHP conformance
*scenario* corpus — the format that all **six client runners** (Go, Rust,
Kotlin, Swift, TypeScript, .NET) **and the host runner** load and execute
identically. Where the existing `reducers/` and `round-trips/` corpora each check
**one** implementation in isolation against a single canonical input, a
**scenario** is an ordered conversation: it drives a client's reducer with a
sequence of server messages and asserts the resulting client-reduced state,
events, and errors. It is the runnable counterpart of the discovery inventory's
`behavior-id`s.

- `schema/scenario.schema.json` is the machine-checkable form (JSON Schema
  2020-12).
- This file is the prose contract: every step op, its fields, examples, and the
  convergence-assertion model.
- `scripts/validate-scenarios.mjs` is the **dependency-free** gate every scenario
  must pass.

> **Declarative, not scripted.** A scenario is *data*: an ordered list of steps,
> each a single discriminated `op`. There is no scripting DSL — no loops, no
> branches, no variables, no expressions. A runner walks the steps in order and
> dispatches on `op`. This is deliberate: a no-logic format is the only thing six
> independent SDKs in six languages can load with byte-identical semantics. If a
> behavior needs control flow, it is two scenarios, not one scripted scenario.

## Discoverability discipline (mirrors the round-trip + discovery corpora)

Three properties, all enforced by the validator, keep scenarios discoverable
from every direction — the same three-pointer discipline the round-trip corpus
and the discovery inventory follow:

1. **Neutral discriminator.** Every step carries an `op` string from a fixed,
   language-agnostic set (below). A runner decodes a step by `op` alone — never
   by a language-specific type tag. This mirrors the round-trip corpus's neutral
   `type` discriminator and the reducer corpus's neutral `reducer` discriminator.
2. **id ⇄ filename.** A scenario's `id` follows the discovery **behavior-id
   grammar** (`<domain>.<concept>.<scenario-class>[.<discriminator>]`, 3–5 dot
   segments of `[A-Za-z0-9-]`), and the file is named `<id>.scenario.json`. The
   validator fails any file whose basename ≠ its `id`, and any `id` reused across
   the corpus. So you can find a scenario from its id and an id from its file.
3. **behaviorIds back-link.** Every scenario lists the discovery `behaviorIds[]`
   it covers (cited from `conformance/discovery/out/*.jsonl`). This is the bridge
   between Part 1 (the coverage matrix) and Part 2 (the runnable checks): the
   matrix says *what behaviors exist*; a scenario's `behaviorIds[]` says *this
   file exercises these ones*. Reconciliation tools union the two on these ids,
   exactly as D11 unions discovery sources on `behavior-id`.

## Scenario metadata

| field | type | required | meaning |
|---|---|:--:|---|
| `id` | string | ✅ | Stable scenario id; behavior-id grammar; `<id>.scenario.json` is the filename. The corpus-wide merge key. |
| `behaviorIds` | string[] | ✅ | ≥1 discovery behavior-id this scenario covers (cite from `conformance/discovery/out/*.jsonl`). |
| `description` | string | ✅ | One-liner: what protocol behavior this scenario proves. |
| `protocolVersion` | string | ✅ | AHP protocol version the scenario targets (e.g. `"0.3.0"`). |
| `pinClock` | integer \| null | optional | Pin the impure clock (epoch-ms) **before step 0** so impure reducer fields (e.g. `summary.modifiedAt`) are deterministic. See *Determinism* below. |
| `notes` | string | optional | Authoring notes, cross-refs, per-client known-skip reasons. |
| `steps` | step[] | ✅ | Ordered list (≥1). Executed strictly in array order. Each element is exactly one `op`. |

## The step ops

Eight ops, three families: **drive** (`client.request`, `server.response`,
`server.notify`, `client.reconnect`), **assert** (`client.assert.state`,
`client.assert.event`, `client.assert.error`), and **control**
(`pin.clock`). Every step object has `op` plus that op's fields and an optional
free-text `label` for diagnostics; **unknown fields are rejected**
(`additionalProperties:false` per op), so a typo'd field is a hard failure rather
than a silent no-op.

### `client.request` — client sends a JSON-RPC request

The client drives the protocol with a request that expects a reply.

| field | type | required | meaning |
|---|---|:--:|---|
| `method` | string | ✅ | JSON-RPC method, e.g. `"initialize"`, `"subscribe"`. |
| `params` | any | optional | JSON-RPC params (any JSON value, or absent). |
| `id` | string \| integer | ✅ | Request id; a later `server.response.forId` must equal it. |

```jsonc
{ "op": "client.request", "method": "initialize", "params": {}, "id": 1 }
```

### `server.response` — server replies to an earlier request

Replies to the `client.request` whose `id` equals `forId`. **Exactly one** of
`result` / `error` is present (JSON-RPC success vs error response — the validator
rejects both-or-neither). For snapshot-bearing results (`initialize`,
`subscribe`), the client seeds its reduced state from the snapshot in the result.

| field | type | required | meaning |
|---|---|:--:|---|
| `forId` | string \| integer | ✅ | The `id` of the request this answers. |
| `result` | any | one-of | JSON-RPC success result. Mutually exclusive with `error`. |
| `error` | object | one-of | JSON-RPC error `{ code, message, data? }`. Mutually exclusive with `result`. |

```jsonc
{
  "op": "server.response", "forId": 1,
  "result": { "protocolVersion": "0.3.0", "serverSeq": 0,
    "snapshots": [ { "resource": "ahp-session:/compliant", "fromSeq": 0, "state": { /* … */ } } ] }
}
```

### `server.notify` — server emits a notification

A JSON-RPC notification (method + params, **no id**) — e.g. an `action` envelope
the host emits. The client routes the payload through its reducer. This is the
live action stream that drives convergence.

| field | type | required | meaning |
|---|---|:--:|---|
| `method` | string | ✅ | Notification method, e.g. `"action"`. |
| `params` | any | optional | Payload; for `"action"`, the `ActionEnvelope { channel, action, serverSeq, origin }`. |

```jsonc
{
  "op": "server.notify", "method": "action",
  "params": { "channel": "ahp-session:/compliant",
    "action": { "type": "session/titleChanged", "title": "Live handshake two" },
    "serverSeq": 4, "origin": null }
}
```

### `client.assert.state` — assert the reduced state

Assert the client-reduced state at a dotted `path` deep-equals `equals`. The
`path` uses the **same dotted-selector convention as the round-trip corpus's
`expect` keys** (`"summary.title"`, `"lifecycle"`); numeric segments index arrays
(`"turns.0.id"`). **Absent / empty `path`** asserts the **whole reduced state**
equals `equals` — that is the byte-for-byte **convergence** assertion.

| field | type | required | meaning |
|---|---|:--:|---|
| `path` | string | optional | Dotted selector into reduced state. Empty/absent = whole-state (convergence). |
| `equals` | any | ✅ | Expected value at `path` (deep-equal); any JSON value, incl. `null`. |
| `channel` | string | optional | Scope to one reduced channel when the runner tracks several (e.g. `"ahp-session:/compliant"`). Absent = the scenario's primary channel. |

```jsonc
{ "op": "client.assert.state", "path": "summary.title", "equals": "Live handshake two" }
{ "op": "client.assert.state", "equals": { /* whole expected state — convergence */ } }
```

### `client.assert.event` — assert an observed event/action

Assert the client observed an event/action **deep-containing** the partial
`matches` shape. Only the named fields must match; unnamed fields are ignored —
the **same partial-`expect` discipline** the round-trip corpus uses for its
`expect` maps.

| field | type | required | meaning |
|---|---|:--:|---|
| `matches` | object | ✅ | Partial event shape that must be deep-contained in ≥1 observed event. |

```jsonc
{ "op": "client.assert.event", "matches": { "type": "session/titleChanged", "title": "Live handshake two" } }
```

### `client.assert.error` — assert a surfaced protocol error

Assert the client surfaced a protocol error with JSON-RPC `code` (and optionally
a `message` substring). For negative / error-path scenarios.

| field | type | required | meaning |
|---|---|:--:|---|
| `code` | integer | ✅ | Expected JSON-RPC error code, e.g. `-32601`. |
| `message` | string | optional | A substring the surfaced message must contain. |

```jsonc
{ "op": "client.assert.error", "code": -32601, "message": "method not found" }
```

### `client.reconnect` — client reconnects / resubscribes

Models reconnect-replay. The client reconnects and resubscribes, optionally
declaring `lastSeenServerSeq` (the highest `serverSeq` it had applied) so the
server can replay-from-seq or resnapshot. The **following** `server.*` steps
supply the replay (a `server.notify` stream from `lastSeenServerSeq+1`) or the
resnapshot (a `server.response` carrying a fresh snapshot).

| field | type | required | meaning |
|---|---|:--:|---|
| `lastSeenServerSeq` | integer \| null | optional | Highest applied `serverSeq` before disconnect. `null`/absent = no cursor; expect a full resnapshot. |
| `channel` | string | optional | Channel being resubscribed. Absent = primary channel. |

```jsonc
{ "op": "client.reconnect", "channel": "ahp-session:/compliant", "lastSeenServerSeq": 3 }
```

### `pin.clock` — pin the impure clock (mid-stream)

Pin the impure clock to a fixed epoch-ms `value` from this step forward, so
impure reducer fields (e.g. `summary.modifiedAt`) are deterministic across
implementations. The scenario-level `pinClock` is the **before-step-0** form;
this op is the **mid-stream** form (re-pin between steps).

| field | type | required | meaning |
|---|---|:--:|---|
| `value` | integer | ✅ | Epoch-ms the clock returns from now on, e.g. `9999`. |

```jsonc
{ "op": "pin.clock", "value": 9999 }
```

## The convergence-assertion model

A scenario is the runnable form of the live conformance handshake
(`conformance/host/host.mjs` + `conformance/README.md`). The model is
**snapshot-and-stream**:

1. **Seed.** A `server.response` to an `initialize`/`subscribe` request carries a
   `snapshot` (`{ resource, fromSeq, state }`). The runner seeds its per-channel
   reduced state from `state` and records `fromSeq` as the channel's high-water
   `serverSeq`.
2. **Stream.** Each `server.notify { method: "action", params: ActionEnvelope }`
   is fed through the client's **canonical reducer**; the channel's reduced state
   advances and its high-water `serverSeq` becomes the envelope's `serverSeq`.
3. **Converge.** A whole-state `client.assert.state` (empty `path`) asserts the
   reduced state equals the host's authoritative final state **byte-for-byte**.
   Two independent implementations that each apply the same actions through the
   same reducer logic must land on the same bytes — that equality *is*
   conformance. (See `conformance/host/final.json` for the reference host's
   authoritative final state, which the example scenario asserts verbatim.)

**Field vs whole-state.** Use dotted-`path` `client.assert.state` for targeted
field checks (`summary.title`), and the empty-`path` form for the full
convergence assertion. A scenario typically ends with the whole-state assertion
and may include field assertions along the way as readable checkpoints.

**Determinism.** The real AHP wire carries **no host-authoritative meta**, so
impure reducer fields like `summary.modifiedAt`
(microsoft/agent-host-protocol#186) are derived independently on each side from
the same clock. A scenario that asserts such a field MUST pin the clock
(`pinClock` at the top, or a `pin.clock` step) so client and host derive the same
value — exactly what `conformance/host/host.mjs` does with `Date.now = 9999` and
the client does with its now-provider hook.

**Reconnect.** `client.reconnect` plus the `server.*` steps that follow it model
replay-from-seq (declare `lastSeenServerSeq`, then stream deltas) or resnapshot
(omit the cursor, then send a fresh snapshot in a `server.response`). The
convergence assertion after replay must match the no-disconnect path — a reconnect
must not change the destination state.

## How a runner consumes a scenario (informative)

Each of the six client runners + the host runner loads `*.scenario.json` and
walks `steps` in order, dispatching on `op`:

- `client.request` → encode + record the pending id (a real runner may send it on
  the wire to a live host, or hand it to an in-process server stub built from the
  scenario's own `server.response`).
- `server.response` / `server.notify` → decode the payload; seed/advance the
  per-channel reduced state via the canonical reducer.
- `client.assert.*` → evaluate against the reduced state / observed events /
  surfaced error; a mismatch fails the scenario with the step `label`.
- `client.reconnect` → reset the channel cursor per `lastSeenServerSeq`.
- `pin.clock` → set the impure-clock provider.

The runner is *I/O + reducer + deep-equal* — no per-scenario logic, because the
scenario carries none.

## Validate

```bash
cd types/test-cases/scenarios
# all **/*.scenario.json under this tree:
node scripts/validate-scenarios.mjs
# one file:
node scripts/validate-scenarios.mjs examples/lifecycle.initialize.happy.snapshot-then-action-stream.scenario.json
```

PASS prints `PASS — N scenario file(s) valid (M unique id(s)).` (exit 0). Any
malformed scenario prints a per-file, per-error report and exits non-zero. The
validator is **dependency-free** (pure Node, no `npm install`) — same discipline
as `conformance/discovery/scripts/validate-inventory.mjs` — so every client
runner can gate its scenario inputs without a toolchain.

It rejects: bad/`id`-mismatched filenames, malformed `id`s, duplicate ids across
the corpus, empty `behaviorIds`, unknown step ops, missing required step fields,
unknown step/scenario properties (`additionalProperties:false`), and a
`server.response` with both or neither of `result`/`error`.

## Worked example

[`examples/lifecycle.initialize.happy.snapshot-then-action-stream.scenario.json`](./examples/lifecycle.initialize.happy.snapshot-then-action-stream.scenario.json)
is the initialize handshake, lifted verbatim from `conformance/host/host.mjs`
(its 5-action `plan`) and `conformance/host/final.json` (its authoritative final
state): initialize → snapshot response → five `action` notifications → a whole-state
convergence assertion against `final.json`. Its `behaviorIds[]` cite three real
discovery rows: `rpc.initialize.happy.snapshot-in-result`,
`rpc.initialize.happy.action-stream-after-response`, and
`action.session-titleChanged.happy.session-titlechanged-updates-title`.
