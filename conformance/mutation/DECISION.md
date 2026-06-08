# Mutation Testing for the AHP Conformance Suite — KEEP/DROP Decision

Decision gate: does mutation testing earn its place in this
suite? Grounded in a real StrykerJS run over the canonical TypeScript reducers,
killed by the conformance corpus.

## TL;DR — **KEEP** (CI-gated, nightly, with a ratchet floor)

Mutation testing **proved the conformance corpus has teeth**: a real Stryker run
injected **813 bugs** into the canonical reducers and the corpus caught **713 of
them (87.7%)** in **48 seconds**, zero setup friction beyond one isolated
dev-only package. The 100 survivors are not noise — they cluster into **one
coherent, actionable gap** (defensive early-return / not-found / empty-list guard
branches that no scenario exercises) plus **a band of equivalent mutants** in two
deliberately-no-op functions. That is exactly the signal mutation testing exists
to produce: a measured, file:line-precise gap list (the empirical version of D9),
not a vibe. KEEP it, gate it nightly, and ratchet the floor.

---

## The real run

| | |
|---|---|
| Tool | StrykerJS 8.7.1 (`@stryker-mutator/core`), `command` test runner |
| Mutated | The 5 canonical channel reducers + `common/reducer-helpers.ts` under `clients/typescript/src/types/` |
| Kill-signal | `replay-corpus.mjs` — in-process replay of **all 233** conformance scenarios through the **real `src` reducers** (imported via tsx, no build), same reduction routing + assertion semantics as the host-conformance runner |
| Wall time | **48 seconds** (full reducer set, concurrency 4) |
| Baseline replay | 233/233 PASS in **~0.24s** |

### Numbers (from `reports/mutation.json`, mirrored in `mutation-summary.json`)

```
Total mutants:   813
Killed:          713   (87.70%)
Survived:        100
Timed-out:         0
Errors:            0
```

| File | Killed | Survived | Kill-rate |
|---|---:|---:|---:|
| `channels-terminal/reducer.ts` | 73 | 4 | **94.81%** |
| `channels-session/reducer.ts` | 565 | 67 | **89.40%** |
| `channels-root/reducer.ts` | 15 | 2 | **88.24%** |
| `channels-changeset/reducer.ts` | 60 | 16 | **78.95%** |
| `channels-resource-watch/reducer.ts` | 0 | 7 | **0.00%** |
| `common/reducer-helpers.ts` | 0 | 4 | **0.00%** |

The kill-signal is independently proven: a hand-injected `activeSessions + 1`
mutation in the root reducer flipped exactly one scenario to FAIL (232/233) and
the harness exited non-zero — the corpus catches a real injected bug.

---

## What the 100 survivors reveal (the measured corpus gaps = D9)

Survivors by mutator kind: **47 ConditionalExpression, 26 BlockStatement**, 7
LogicalOperator, 5 EqualityOperator, 3 ArrayDeclaration, 3 MethodExpression, the
rest singletons. The Conditional + Block survivors are overwhelmingly the **same
shape**: removing or short-circuiting a guard so its early-return branch
disappears. They sort into three buckets.

### Bucket 1 — REAL gaps: defensive / not-found / empty-list guard branches (actionable)

These are genuine behaviors no scenario asserts. Every scenario drives the
**happy path** (the guard's "proceed" arm); none drives the **defensive arm**.
Examples (file:line — what mutated — the uncovered behavior):

- `channels-session/reducer.ts:713–714` — `findIndex`→`every`, optional-chain
  removal, `idx < 0`→`false`, `!existing && idx<0` — **`SessionInputAnswerChanged`
  for a `requestId` that is NOT in `inputRequests`** (the "answer a request that
  doesn't exist → no-op" branch).
- `channels-session/reducer.ts:738` — `some`→`every`, optional-chain removal —
  **`SessionInputCompleted` for an absent `requestId`** (same not-found no-op).
- `channels-root/reducer.ts:30` — `if (!state.config)`→`if (false)` / `{}` —
  **`root/configChanged` when `state.config` is undefined** (the "config update
  with no config object → no-op" guard).
- `channels-changeset/reducer.ts:39` — `idx <= 0` boundary, `:58/:66/:70/:77/:89`
  conditional/block removals — **changeset operation-target lookups that miss /
  status transitions on the boundary** (e.g. operation index 0, `status !== Error`
  branch).
- `channels-session/reducer.ts` L100/L201/L210/L250/L582/L625/L629/L650/L662/
  L666/L674/L745/L781/L792 — a long tail of `if (notFound) return state` /
  `if (!present) {…}` turn-lifecycle and tool-call guards whose "skip" arm is
  never hit.

**Action (if the suite wants ~95%+):** add scenarios that fire each lifecycle
action against a **missing / already-consumed / empty** target — answer a
non-existent input request, complete an already-completed one, configChange with
no config, changeset op on a missing target. ~10–15 targeted negative/edge
scenarios would convert most of bucket 1.

### Bucket 2 — EQUIVALENT mutants: deliberately no-op functions (NOT a gap)

`channels-resource-watch/reducer.ts` is **intentionally identity-on-state** — its
own doc comment says watches are event-pass-through and "the reducer keeps no
history … never mutates." Both arms of its only `if` `return state`. So mutating
`action.type === X` → `true`/`false`/`!==` produces **byte-identical output**;
these mutants are *equivalent by construction* and **cannot** be killed by any
state assertion. Its 0% is a property of the function, not a hole in the corpus.

### Bucket 3 — WARNING-LOG mutants: no observable state effect (NOT a gap)

`common/reducer-helpers.ts` (`softAssertNever`) and the `(log ?? console.warn)`
tail of resource-watch: mutating the log call (`log && console.warn`), the
warning string (→ ` `` `), or emptying the block changes **only a console
warning**, never reduced state. The corpus asserts state, so it correctly does
not (and arguably should not) fail on these. `reducer-helpers.ts:44`
(`isClientDispatchable`) is a server-side dispatch-validation guard the
reducer-convergence corpus does not exercise at all — a separate surface, not a
reducer gap.

**Net:** of the 100 survivors, ~**11** (resource-watch 7 + helpers 4) are
equivalent/out-of-scope mutants that should be **excluded** (or accepted) rather
than chased; the remaining ~**89** are real "defensive-branch" coverage that a
focused batch of edge scenarios would close. The *effective* reducer-behavior
kill-rate, excluding the equivalent-mutant files, is **713 / (813−11) ≈ 88.9%**.

---

## Cost / friction (the other half of the gate)

| Dimension | Reality |
|---|---|
| Setup | One isolated `conformance/mutation/package.json` (Stryker + tsx as **devDeps**). **Zero** new deps in the shipped `@microsoft/agent-host-protocol` client. |
| Runtime dep risk | **None** — Stryker never enters any published package's dependency graph (verified: `grep stryker clients/typescript/package.json` → 0). |
| Interactive run | 48s for the full reducer set; the kill-signal replay alone is 0.24s. |
| Determinism | Clock-pinned per scenario (same contract as the host/runner); the run is reproducible (713/100 split stable across runs). |
| Maintenance | The kill-signal reuses the corpus + the runner's own assertion code verbatim; nothing bespoke to keep in sync beyond the reducer file globs. |

The dominant risk mutation testing usually carries — *slow, flaky, drags CI* —
**does not apply here** because the reducers are pure and the corpus replays
in-process in sub-second time. 48s is cheaper than most unit suites.

---

## Decision: **KEEP**

**Rationale.** Mutation testing did its job on the first real run: it converted
"we think the corpus is good" into "the corpus kills 87.7% of injected reducer
bugs, here are the exact 89 behaviors it misses, here are the 11 that are
unkillable by construction." That is high-value, low-cost signal. The survivor
list is *directly actionable* (it names the missing edge scenarios) and the run
is fast and deterministic. Nothing about the cost argues for DROP.

### How it should run (for the CI gate to wire)

1. **Cadence — nightly + on-demand, NOT per-PR-blocking initially.** 48s is
   cheap, but mutation scores are most useful as a *trend/ratchet*, not a
   per-commit gate while the floor is still being raised. Run it:
   - **Nightly** in CI (scheduled workflow) over the full reducer set.
   - **On-demand** via `npm --prefix conformance/mutation run mutate` (from repo
     root) for anyone touching a reducer.
2. **Ratchet floor.** Set `thresholds.break` to a floor that only goes **up**:
   - Start the break floor at **87** (just under today's 87.70 — fails CI only on
     a *regression* below the current bar).
   - After closing bucket-1 edge scenarios, ratchet to **90 → 92 → 95**.
   - Keep `thresholds.high: 90`, `low: 70` for the report's colour bands.
3. **Exclude the equivalent-mutant files from the score** so the ratchet tracks
   real behavior, not unkillable noise. Either add
   `channels-resource-watch/reducer.ts` + `common/reducer-helpers.ts` to a
   Stryker `mutate` exclusion, **or** keep them in but document their floor at 0%.
   (This config currently mutates them so the gap is *visible*; the CI gate should
   decide visible-but-excluded vs removed. Recommended: keep visible, exclude from the
   `break` calculation, so a future real `resourceWatch/*` mutating action would
   resurface them.)
4. **The full subprocess runner stays the stricter cross-check.** The in-process
   replay is the fast per-mutant signal; the `conformance-suite.mjs
   --all-reducers` (233 scenarios end-to-end over real WebSocket, ~3.5s) remains
   the canonical green proof and a *second*, transport-inclusive kill-signal if a
   future mutation ever escapes the in-process harness (it cannot for pure
   reducers, but the runner guards the host protocol layer a future change may also mutate).

### What KEEP does NOT mean

- Do **not** chase 100%. The equivalent mutants in resource-watch/helpers are
  unkillable by construction; a 100% target would invite fake assertions
  (asserting on console warnings) — exactly the coverage-theater this repo bans.
- Do **not** add scenarios *only* to kill a mutant. Add them because the
  not-found / empty / boundary behavior is a real contract worth pinning; the
  mutant just told you which one was unguarded.

---

## Reproduce

```bash
# From the repo root (Stryker sandbox needs both the mutated src AND the corpus):
cd /path/to/agent-host-protocol

# one-time, dev-only, isolated — does NOT touch any shipped package:
npm --prefix conformance/mutation install

# fast kill-signal sanity check (233 scenarios through src reducers, ~0.24s):
node --import tsx conformance/mutation/replay-corpus.mjs

# FULL mutation run over the canonical reducers (~48s):
conformance/mutation/node_modules/.bin/stryker run conformance/mutation/stryker.conf.json
#   reports → conformance/mutation/reports/{mutation.html,mutation.json}
#   headline → conformance/mutation/mutation-summary.json
```

**CI full-corpus command (for the CI gate), exact:**

```bash
# kill-signal = full conformance corpus (in-process, fast):
conformance/mutation/node_modules/.bin/stryker run conformance/mutation/stryker.conf.json

# stricter transport-inclusive cross-check (optional second gate, ~3.5s):
node conformance/runner/conformance-suite.mjs --all-reducers --concurrency 8
```

Stryker is dev-only and isolated to `conformance/mutation/`; it is **not** a
dependency of `@microsoft/agent-host-protocol` or any shipped client.
