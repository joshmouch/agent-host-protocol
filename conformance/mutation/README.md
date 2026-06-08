# conformance/mutation — mutation testing for the AHP canonical reducers

Proves the conformance corpus has *teeth*: it injects bugs
into the canonical TypeScript reducers and measures how many the corpus catches.

- **Decision + full rationale:** [`DECISION.md`](./DECISION.md) — KEEP/DROP gate,
  survivor analysis, CI cadence, ratchet floor.
- **Headline numbers (committed, small):** [`mutation-summary.json`](./mutation-summary.json)
  — totals, per-file kill-rate, and the full 100-survivor list (file:line +
  mutator + replacement).
- **Full HTML/JSON reports:** `reports/` (git-ignored; regenerate with the run
  command below).

## Latest real run

| | |
|---|---|
| Tool | StrykerJS 8.7.1 (dev-only, isolated to this package) |
| Mutated | the 5 channel reducers + `common/reducer-helpers.ts` under `clients/typescript/src/types/` |
| Total / Killed / Survived | **813 / 713 / 100** |
| Kill-rate | **87.70%** |
| Wall time | ~50s (full reducer set, concurrency 4) |

## Kill-signal

`replay-corpus.mjs` replays **all 233** conformance scenarios
(`types/test-cases/scenarios/{reducers,round-trips,negatives}`) **in-process**
through the **real `src` reducers** (imported via `tsx`, no build), using the
exact reduction routing + assertion semantics of the host-conformance runner
(`conformance/runner/run-conformance.mjs`). A mutant that survives here survives
the runner too (same reducers, same assertions); the harness exits non-zero the
moment any scenario's assertion fails, which is how Stryker scores a kill. The
full subprocess runner (`conformance-suite.mjs --all-reducers`) remains the
stricter, transport-inclusive cross-check.

## Run it

```bash
# from the REPO ROOT (the Stryker sandbox needs both the mutated src AND the corpus):

# one-time dev-only install — does NOT touch any shipped package:
npm --prefix conformance/mutation install

# fast kill-signal sanity check (233 scenarios, ~0.24s):
node --import tsx conformance/mutation/replay-corpus.mjs

# FULL mutation run (~50s) — reports land in conformance/mutation/reports/:
conformance/mutation/node_modules/.bin/stryker run conformance/mutation/stryker.conf.json
```

## Isolation guarantee

Stryker + tsx are `devDependencies` of **this** package only
(`conformance/mutation/package.json`). They are **not** dependencies of
`@microsoft/agent-host-protocol` or any shipped client. Verify:

```bash
grep -c stryker clients/typescript/package.json   # → 0
```

## Notes for CI wiring

- `stryker.conf.json` sets `thresholds.break: 87` — a **ratchet floor** just
  under today's 87.70%. Raise it toward 90/92/95 as the bucket-1 edge scenarios
  (see `DECISION.md`) close. Never lower it.
- `channels-resource-watch/reducer.ts` and `common/reducer-helpers.ts` hold
  **equivalent / out-of-scope mutants** (deliberate no-op state + warning-log-only
  code). Their 0% is structural, not a gap — exclude them from the `break`
  calculation so the floor tracks real reducer behavior.
- `ignorePatterns` excludes the cross-language build dirs (`.build`, `target`,
  `build`, `bin`, `obj`, …) — required, because Stryker copies the project into a
  sandbox and `.build/debug` is a unix socket that throws `ENOTSUP` if copied.
