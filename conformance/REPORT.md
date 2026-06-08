# AHP Cross-Implementation Conformance Report

> The capstone of the AHP conformance suite. This report rolls up the
> cross-implementation conformance picture: the scenario corpus, the six green
> client implementations, the mutation kill-rate, the Part-1 discovery coverage,
> the real findings the suite surfaced, and the honest non-goals.
>
> **Every number below is reproducible from a real artifact in this repo** — the
> commands that produce it are cited inline. Nothing here is hand-asserted.
> The machine-checked gate that enforces the core of this picture lives at
> [`conformance/ci/gate.mjs`](ci/gate.mjs); the CI wiring is
> [`.github/workflows/conformance.yml`](../.github/workflows/conformance.yml).

---

## 1. At a glance

| Dimension | Result | Source artifact |
|---|---|---|
| Scenario corpus | **233 scenarios** (164 reducer + 23 round-trip + 46 negative) | `types/test-cases/scenarios/` |
| Client implementations green | **6 / 6 = 233/233** (TS · Kotlin · Swift · .NET · Go · Rust) | per-client runners under `conformance/<lang>/` |
| Mutation kill-rate | **87.70 %** (713/813 killed) raw; **88.90 %** (713/802) excluding the 11 equivalent/out-of-scope mutants — **KEEP** | `conformance/mutation/mutation-summary.json` |
| Part-1 discovery surface | **384 distinct canonical concepts** (reconciled from **656** raw angle rows across 10 angles; the raw count double-counts behaviors named differently by independent angles), all shape-valid + citation-grounded | `conformance/discovery/out/d11-coverage.json` (`distinctCanonicalConcepts` / `totalAngleRows`) |
| Schema-element corroboration | **90 / 122** schema elements touched by a scenario angle | `conformance/discovery/out/d11-coverage.json` |
| Strong-normative corroboration | **11 / 30** MUST/MUST_NOT clauses touched by a scenario angle | `conformance/discovery/out/d11-coverage.json` |
| Corpus-covers-matrix | **233 / 233** D5+D7 mappable behaviors covered; **235 / 652 = 36.0 %** of the full D11 surface | `conformance/ci/gate.mjs --print-coverage` |
| CI gate | **6/6 checks green**, deterministic | `node conformance/ci/gate.mjs` |

---

## 2. The scenario corpus (Part 2 — 233 scenarios)

A language-neutral fixture corpus under
[`types/test-cases/scenarios/`](../types/test-cases/scenarios/). Each scenario is
a JSON script of `client.request` / `server.response` / `server.notify` /
`client.assert.*` steps with a stable `id` and a `behaviorIds[]` array that ties
it back to the Part-1 discovery surface. Every scenario file is structurally
validated by the dependency-free
[`validate-scenarios.mjs`](../types/test-cases/scenarios/scripts/validate-scenarios.mjs)
(JSON-Schema-2020-12 subset; duplicate-id detection; `server.response`
exactly-one-of result/error).

| Tranche | Count | What it checks |
|---|---:|---|
| Reducer fixtures (`reducers/`) | **164** | One action (or short action sequence) applied through a client's reducer must converge to the fixture's expected state. |
| Round-trip fixtures (`round-trips/`) | **23** | Decode -> re-encode fidelity for wire types, incl. unknown-variant forward-compat. |
| Negative / error paths (`negatives/`) | **46** | The client must surface the scripted JSON-RPC error code (e.g. `-32004 TurnInProgress`, `-32011 Conflict`, `-32601 MethodNotFound`). |
| **Corpus total** | **233** | |
| Example (`examples/`) | 1 | A documentation example; validated for shape (234th file) but outside the 233-scenario conformance count. |

> The shape validator counts **234 files** (the 233 corpus scenarios + 1 example);
> the **233** conformance figure is the corpus the clients replay. The gate's
> check A asserts all 234 are shape-valid; checks B and D operate on the 233.

---

## 3. Six green client implementations (233/233 each)

Every client replays the **same** corpus against the **same** real
scenario-driven host
([`conformance/host/scenario-host.mjs`](host/scenario-host.mjs)) over a **real
WebSocket**, applying every `server.notify` action through that language's
**native reducers** and checking every `client.assert.*` step. **No mocks** —
real files, real transport, real subprocess host, real reducers, real
assertions — verified by real execution, not mocks.

| Client | Result | Runner | Real run command |
|---|---|---|---|
| **TypeScript** | 233/233 | `conformance/runner/` (also the reference replay client) | `conformance/runner/run.sh --all-reducers` |
| **.NET** | 233/233 | `conformance/dotnet/CorpusRunner` | `conformance/run-corpus.sh` |
| **Go** | 233/233 | `conformance/go/` | `conformance/go/run.sh` |
| **Rust** | 233/233 | `conformance/rust/` | `cargo run --release -- --full` (in `conformance/rust`) |
| **Kotlin** | 233/233 | `conformance/kotlin/` | `conformance/kotlin/run.sh --full` |
| **Swift** | 233/233 | `conformance/swift/` | `swift run --package-path conformance/swift ConformanceRunner` |

The TypeScript client doubles as the host's canonical reducer (the host is built
on `clients/typescript`'s `sessionReducer` / `rootReducer` / etc. via a `file:`
dependency), so the host-conformance run is simultaneously the TS client's
green proof and the authoritative state every other client must converge to.

> Beyond the shared 233-scenario corpus, each client also carries its own native
> test suite. The .NET suite, for example, runs **315 test cases per target
> framework across `net8.0` + `net9.0`** (630 total executions) from **125
> distinct `[Fact]`/`[Theory]` methods** (`dotnet test`; the method floor is
> ratcheted by `clients/dotnet/tests/MIN_TEST_COUNT`). Quote the figure you mean:
> the **125 methods** are the authored tests; the 315/630 are framework-expanded
> executions, not 630 independent assertions.

### A real, reproducible flake — diagnosed and fixed at the root

The host-conformance suite runs each scenario in its own host subprocess and, by
default, with `--concurrency 4`. Under that parallelism a full run occasionally
failed a single, *random* scenario with empty/foreign state (e.g. `surfaced: []`
or `known channels: []`). The flake was a **harness/environment issue, not a
protocol bug** — every affected scenario passes in isolation and the run is
`233/233` on re-run; the reducers, host, and scenarios are all correct.

**Root cause (diagnosed):** each host binds an OS-assigned ephemeral port
(`port: 0`). On a busy machine (editors, model servers, other test runs), that
port can be **recycled by another local process** between the host printing its
`READY` line and the client connecting — so the client completes a WebSocket
upgrade against a *foreign* server that closes it (observed: HTTP `404`, or
`1008 Unauthorized` from an unrelated authenticated WS server) and the scenario
asserts against empty state. The original client blindly *retried the same —
now-stolen — port*, which is futile.

**Fix (at the root):** the host now emits a per-connection **nonce** as the
negotiated WebSocket subprotocol; the reference client requires the host to echo
it and, on any pre-open failure or nonce mismatch (i.e. it reached a foreign
server), **re-spawns the host on a fresh port** instead of retrying the stolen
one. Verified `233/233` across **50 consecutive `--concurrency 4` runs** with
zero flakes (the pre-fix rate was ~1 flake per ~8 full runs on the same
machine). The CI gate still runs the host-conformance check at `--concurrency 1`
for belt-and-suspenders determinism; the per-client matrix jobs exercise the
parallel path. (The nonce handshake is backward-compatible: clients that do not
offer it — the non-JS language runners — still connect, so their own blind-retry
path is unchanged.) See `conformance/host/scenario-host.mjs` /
`conformance/runner/run-conformance.mjs` for the implementation.

---

## 4. Mutation testing — 87.70 % kill-rate (KEEP)

Stryker ([`conformance/mutation/`](mutation/)) mutates the canonical TypeScript
reducers and re-runs the corpus as the kill-signal. It converts "we think the
corpus is good" into a measured number: **the corpus kills 87.70 % of injected
reducer mutants**.

| Metric | Value | Source |
|---|---:|---|
| Mutants generated | **813** | `mutation-summary.json` -> `totals.total` |
| Killed | **713** | `totals.killed` |
| Survived | **100** | `totals.survived` |
| **Kill-rate (raw)** | **87.70 %** (713/813) | `totals.killRatePct` |
| **Kill-rate (meaningful)** | **88.90 %** (713/802) | raw, minus the 11 equivalent/out-of-scope mutants (see below) |
| Break floor (CI) | **87** | `stryker.conf.json` -> `thresholds.break` |

Per-file kill-rate (the 6 mutated reducer files):

| Reducer file | Kill-rate | Killed/Total |
|---|---:|---:|
| `channels-terminal/reducer.ts` | 94.81 % | 73/77 |
| `channels-session/reducer.ts` | 89.40 % | 565/632 |
| `channels-root/reducer.ts` | 88.24 % | 15/17 |
| `channels-changeset/reducer.ts` | 78.95 % | 60/76 |
| `channels-resource-watch/reducer.ts` | 0 % | 0/7 |
| `common/reducer-helpers.ts` | 0 % | 0/4 |

The two 0 % files are **expected and documented**: `resource-watch` is an
event-pass-through reducer that keeps no derived state (its own doc-comment says
so), and `reducer-helpers` is trivia. Their **11 mutants are equivalent /
out-of-scope** (7 in `resource-watch` + 4 in `reducer-helpers`): a deliberate
no-op-state reducer and warning-log-only helpers produce mutants no
behavioral corpus can kill. They are kept visible in the report but excluded
from the *meaningful-kill* denominator — so the honest "how well does the corpus
kill mutants that are killable at all" figure is **88.90 % (713/802)**, not the
diluted **87.70 % (713/813)**. The CI `break:87` floor is set against the raw
813-denominator figure (conservative); the meaningful figure is the one that
reflects corpus strength. They are accounted-for, not silent gaps.

**Decision: KEEP** — CI-gated, nightly, with a ratchet floor. The kill-signal
reuses the corpus + the runner's own assertion code verbatim (nothing bespoke to
maintain), runs in ~48 s, and the **break floor is set at 87** (just under
today's 87.70) so CI fails only on a *regression*, never on the current state.
Full rationale: [`conformance/mutation/DECISION.md`](mutation/DECISION.md).

---

## 5. Part-1 discovery coverage (384 distinct concepts; 656 raw angle rows)

Before the corpus, a 10-angle discovery fan-out
([`conformance/discovery/`](discovery/)) enumerated the protocol's observable
behavior from independent vantage points. The angles emit **656 raw rows**
total; because independent angles name the same behavior differently, the
canonical-key reconciliation collapses these to **384 distinct canonical
concepts** ([`d11-coverage.json`](discovery/out/d11-coverage.json) →
`distinctCanonicalConcepts`). The **656** figure is rows-before-dedup; **384**
is the honest distinct-concept count. Every emitted row is **shape-valid**
(`validate-inventory.mjs`) **and citation-grounded** in a real fork file
(`verify-citations.mjs` — an anti-fabrication gate: each row's
`citation.file` must exist and `citation.excerpt` must really appear near
`citation.line`).

| Angle | Rows | What it mines |
|---|---:|---|
| D1 schema-surface | 131 | Every type/method/notification in `schema/`. |
| D2 normative-rules | 77 | MUST/SHOULD/MAY clauses from the spec prose. |
| D3 mined-client-expectations | 48 | Behaviors the reference clients assume. |
| D4 host-behaviors | 25 | What a spec-faithful host must do. |
| D5 fixture-derived | 187 | Behaviors backed by a reducer/round-trip fixture. |
| D6 lifecycle-transitions | 40 | Connection/session/turn state transitions. |
| D7 negative-paths | 46 | Error codes + the conditions that raise them. |
| D8 differential | 16 | Cross-client divergence candidates. |
| D9 surviving-mutants | 66 | Mutation survivors -> behaviors the corpus under-pins. |
| D10 property-findings | 20 | Invariants worth property-testing. |
| **Total (raw angle rows)** | **656** | All shape-valid + citation-grounded (both gates re-run clean). Reconciles to **384 distinct canonical concepts** (independent angles name the same behavior differently). |

**D11 reconciliation (the honest exit-criterion).** The raw D11 matrix merges by
literal `behavior-id` and so under-collapses (independent angles name the same
behavior differently). The deterministic
[`reconcile.mjs`](discovery/scripts/reconcile.mjs) imposes a canonical key and
measures the real coverage question
([`d11-coverage.json`](discovery/out/d11-coverage.json) +
[`d11-reconciliation.md`](discovery/out/d11-reconciliation.md)):

- **Enumeration is complete and verified** — 656 raw angle rows (384 distinct
  canonical concepts after reconciliation), all grounded.
- **Reconciliation is partial** — **90 / 122** schema elements and **11 / 30**
  strong-normative (MUST-class) clauses are corroborated by a scenario-producing
  angle. The remaining **32** schema elements + **19** MUST-class clauses are
  enumerated but not yet matched to a scenario angle — they are the precise
  Part-2 authoring backlog (and the honest gap the raw matrix hid).

> Note: the **90/122** and **11/30** figures are the *canonical-key reconciliation*
> over the whole 10-angle surface. The corpus-covers-matrix in section 6 measures
> a different, stricter thing: literal `behaviorIds` equality between corpus
> scenarios and the two scenario-mappable angles (D5+D7).

---

## 6. Corpus-covers-matrix (the exhaustiveness ratchet)

The CI gate's check D cross-references the corpus's `behaviorIds`
against the D11 discovery surface, computed live by
[`conformance/ci/gate.mjs`](ci/gate.mjs) and ratcheted against
[`conformance/ci/coverage-floor.json`](ci/coverage-floor.json).

```
$ node conformance/ci/gate.mjs --print-coverage
  corpus: 234 scenarios, 235 distinct behaviorIds
  D5+D7 mappable behaviors: 233/233 covered
  overall D11 surface: 235/652 = 36%
```

**Two coverage facts, both grounded in the artifacts:**

1. **Exhaustiveness over the scenario-mappable angles — 233 / 233 (100 %).** The
   D5 (fixture-derived) and D7 (negative-path) angles author their `behavior-id`s
   to map **1:1** onto runnable scenarios. The gate **hard-asserts** that *every*
   D5+D7 behavior is represented by a corpus scenario's `behaviorIds`. Today: all
   **233** distinct D5+D7 behaviors are covered (the 187 D5 fixture rows + 46 D7
   negative rows are exactly the 233 corpus scenarios). **Ratchet floor: 233** —
   the gate fails if this ever regresses, and prints the uncovered behaviors.

2. **Breadth over the whole D11 surface — 235 / 652 = 36.0 %.** Of the 652
   distinct `behavior-id`s across all 10 angles, a corpus `behaviorId` touches
   **235** (the 233 mappable + 2 that also surface under other angles' naming).
   The other angles (D1 schema, D2 spec, D9 mutation, ...) use a different naming
   scheme that does *not* map 1:1 onto a scenario id, so 36.0 % is the expected,
   honest breadth — the un-touched remainder is the enumerated Part-2 backlog
   that section 5's reconciliation already names. **Ratchet floor: 235 rows /
   36.0 %** — fails only on regression (a lost scenario or discovery row), never
   on the current state. To raise coverage, author scenarios for the backlog and
   raise the floor in the same commit.

The ratchet is the same discipline as the mutation `break:87` and the .NET parity
`MIN_TEST_COUNT`: **coverage may freely rise; it may never silently fall.** The
gate's failure path is verified real (tripping the floor exits non-zero with an
actionable regression message), not echoed.

---

## 7. Real findings the suite surfaced (and their disposition)

The suite is only worth its weight if it catches real bugs. It did — every item
below was **found** by wiring the shared corpus / discovery angles into the
implementations, and dispositioned:

### Fixed at the source

- **Swift `ResponsePart .unknown` decode bug — FIXED.** Swift's wire decoder
  lacked an unknown-variant fallback across 10 wire-decoded unions; a
  `ResponsePart` with an unrecognized `kind` failed to decode, taking Swift to
  **232/233**. Fixed (forward-compat `.unknown` across all 10 unions) ->
  **233/233** (commit `9333202`). Same class as the 4 Swift round-trip fidelity
  bugs the round-trip corpus caught and fixed
  (`types/test-cases/round-trips/KNOWN-FIDELITY-GAPS.md`).

- **Changeset reducer ported to .NET / Go / Rust — FIXED.** The `changeset/*`
  channel had no native reducer in the .NET, Go, and Rust clients, so 9–11
  changeset scenarios were skipped (Rust 222/233, Go 224/233 at first run). The
  changeset reducer was ported to all three -> **all six clients 233/233**
  (commit `3d4c33e`).

- **4 of 6 clients had round-trip fidelity bugs — FIXED.** Wiring the round-trip
  corpus in surfaced and fixed: Swift (x4: unknown-StateAction empty-encode,
  Customization throw-on-unknown, ChangesetOperationTarget dropping `kind` x2),
  Rust (`SessionStatus` closed-enum losing unknown bits), Kotlin (`SessionStatus`
  signed-Int truncation), .NET (`SessionAddedParams.summary` modeled nullable
  though schema-required). Go was clean; TypeScript has compile-time-only types.
  Full narrative: `types/test-cases/round-trips/KNOWN-FIDELITY-GAPS.md`.

### Spec-clarification candidates (reference-host / spec ambiguities, not client bugs)

- **`-32011 Conflict` schema / TS divergence.** The optimistic-concurrency error
  is defined at `types/common/errors.ts:97` (`Conflict: -32011`) and the schema
  mandates the server **MUST fail with `Conflict`** on an `ifMatch` etag mismatch
  (`schema/commands.schema.json`, `schema/errors.schema.json`). The exact
  surfacing/shape of the Conflict error across the schema and the TS surface is a
  divergence worth a spec clarification — captured as D7 rows
  `error.Conflict.error.ahp-code` + `error.Conflict.error.etag-mismatch`.
  **Disposition: spec-clarification candidate** (no client is "wrong"; the spec
  is under-specified).

- **Root-channel `terminalsChanged` notification-vs-action asymmetry.**
  `root/terminalsChanged` is modeled as a **StateAction**
  (`RootTerminalsChangedAction`, `schema/actions.schema.json:89`; applied by the
  root reducer at `channels-root/reducer.ts:26`), yet the command descriptions
  phrase it as something the server "**dispatches**" to update the root terminal
  list (`createTerminal` / `disposeTerminal` docs) — notification-shaped language
  for an action-shaped wire object. **Disposition: spec-clarification candidate**
  (the action/notification framing should be made consistent in the spec).

> Per the non-goals below, the spec-clarification candidates are **surfaced, not
> fixed here** — they are reference-host / spec questions for the upstream
> maintainers, recorded with grounded citations so the upstream PR can act on
> them.

---

## 8. Honest non-goals

- **This suite does not replace VS Code's internal tests.** It is an
  *independent, cross-implementation* conformance check built from the public
  protocol surface (schema + spec prose + the reference clients). It proves the
  six client implementations agree with a spec-faithful host on the wire; it does
  **not** exercise VS Code's private host implementation or its internal test
  matrix.

- **It finds reference-host bugs; it does not fix them.** The suite's job is to
  *surface* divergences and ambiguities with grounded evidence. Client bugs in
  *this* repo's six implementations were fixed (section 7); spec ambiguities and
  any reference-host behavior questions are **recorded as spec-clarification
  candidates**, not silently patched.

- **It is not a 100 %-of-the-protocol claim.** Sections 5 and 6 are explicit:
  90/122 schema elements and 11/30 MUST-class clauses are corroborated, and the
  corpus touches 36.0 % of the full enumerated D11 surface. The remainder is an
  *enumerated, named* backlog — the suite is honest about what it does and does
  not yet cover, rather than asserting completeness.

- **The mutation kill-rate measures the corpus, not the clients.** 87.70 % is how
  well the corpus kills mutants in the *canonical TS reducers* (the host's
  reducers). It is a proxy for corpus strength, not a per-client coverage number.

---

## 9. Reproduce everything

```bash
# The whole core gate (scenario shape + host 233/233 + discovery 656 + corpus-covers-matrix):
node conformance/ci/gate.mjs                 # -> GATE PASS — 6/6 checks green
node conformance/ci/gate.mjs --print-coverage  # -> the live coverage numbers (JSON)

# Per-client corpus replay (233/233 each):
conformance/runner/run.sh --all-reducers                       # TypeScript (+ the canonical host)
conformance/run-corpus.sh                                      # .NET
conformance/go/run.sh                                          # Go
( cd conformance/rust && cargo run --release -- --full )       # Rust
conformance/kotlin/run.sh --full                               # Kotlin
swift run --package-path conformance/swift ConformanceRunner   # Swift

# Discovery integrity (656 shape-valid + citation-grounded):
node conformance/discovery/scripts/validate-inventory.mjs conformance/discovery/out/d{1,2,3,4,5,6,7,8,9,10}-*.jsonl
node conformance/discovery/scripts/verify-citations.mjs   conformance/discovery/out/d{1,2,3,4,5,6,7,8,9,10}-*.jsonl
node conformance/discovery/scripts/reconcile.mjs && cat conformance/discovery/out/d11-coverage.json

# Mutation (87.70 %, break:87):
( cd conformance/mutation && npm ci && npm run mutate )
```

CI runs all of the above: [`.github/workflows/conformance.yml`](../.github/workflows/conformance.yml)
(core gate + per-client matrix on every push/PR; mutation nightly + on-demand).
