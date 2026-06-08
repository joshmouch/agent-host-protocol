# `conformance/discovery/` — the AHP conformance-surface discovery pipeline

This directory is the **discovery half (Part 1)** of the AHP conformance-suite
effort. Its job is to attack the conformance surface from many independent,
overlapping angles so no behavior hides in a single blind spot, and to emit the
result as a **machine-readable, re-runnable inventory** rather than a prose
document. For the suite as a whole — the scenario corpus, the six green client
runners, mutation, and how the pieces fit — see the conformance suite report,
[`../REPORT.md`](../REPORT.md).

## How it works

```
D0  defines + gates the row schema   ──►  schema/inventory-row.schema.json
                                          scripts/validate-inventory.mjs
                                          SCHEMA.md   (the prose contract)

D1 … D10  each emit ONE disjoint file ─►  out/<phase>.jsonl   (one behavior/line)
          (sources never edit each other's rows — they only add)

D11  merges D1–D10 by behavior-id     ─►  out/d11-surface-matrix.jsonl
     (union of sources[], coverage)        out/d11-surface-matrix.md
```

Each line of an `out/*.jsonl` file is one **inventory row**: a single conformance
*behavior* with a stable `behavior-id`, the angle that found it (`source`), the
protocol `concept` / `method` / `scenario-class` / `normative-level`, a **real
`file:line` citation** that grounds it, and a `coverage` status. The full field
contract is in [`SCHEMA.md`](./SCHEMA.md).

## The angles (Part 1)

| phase | angle | output |
|---|---|---|
| D0 | discovery harness + the row schema (this scaffold) | — |
| D1 | schema-derived surface (every method/action/error/cap) | `out/d1-schema-surface.jsonl` |
| D2 | spec-prose RFC-2119 normative inventory | `out/d2-normative-rules.jsonl` |
| D3 | mine the 6 client test suites' fake-host scripts | `out/d3-mined-client-expectations.jsonl` |
| D4 | reference-host + real-host behavior inventory | `out/d4-host-behaviors.jsonl` |
| D5 | lift the 163 reducer + 23 round-trip fixtures into wire scenarios | `out/d5-fixture-derived-scenarios.jsonl` |
| D6 | state-machine / lifecycle / reconnect-replay enumeration | `out/d6-lifecycle-transitions.jsonl` |
| D7 | error / negative / protocol-violation enumeration | `out/d7-negative-paths.jsonl` |
| D8 | differential / cross-implementation divergence | `out/d8-divergences.jsonl` |
| D9 | mutation-driven gap discovery (dev-only tool) | `out/d9-surviving-mutants.jsonl` |
| D10 | property-based / fuzz discovery | `out/d10-property-findings.jsonl` |
| D11 | synthesis: the conformance-surface matrix + coverage report | `out/d11-surface-matrix.{jsonl,md}` |

## Run the gates

Two orthogonal, **dependency-free** (pure Node, no `npm install`) gates:

```bash
cd conformance/discovery
# 1. SHAPE — every row matches schema/inventory-row.schema.json
node scripts/validate-inventory.mjs out/d1-schema-surface.jsonl
# 2. GROUNDING — every citation.file:line really exists and contains the excerpt
node scripts/verify-citations.mjs  out/d1-schema-surface.jsonl
# both, over everything:
npm run gate:all
```

- **`validate-inventory.mjs` (shape)** rejects missing required fields,
  out-of-enum values, type mismatches, malformed `behavior-id`s, bad `citation`
  shapes, and duplicate ids within one file.
- **`verify-citations.mjs` (grounding)** is the **anti-fabrication** gate: it
  opens each `citation.file` in the fork and confirms `citation.excerpt` really
  appears at/around `citation.line`. A well-formed row with an invented path,
  guessed line, or made-up excerpt passes the shape gate but **fails** here.

## Discipline (every angle)

- **Real citations only.** Every row's `citation.file:line` must really exist and
  really contain `citation.excerpt`. No fabricated rows, no invented paths.
- **One disjoint output file per angle**; never edit another angle's rows.
- **D1–D10 emit `coverage: "unknown"`**; only D11 reconciles coverage.
- Run the validator on your file before reporting done; paste the PASS line.

## Maintenance contract — which `out/*.jsonl` files are generated vs hand-authored

The angle files split into two kinds. **Know which kind you are editing before
you touch an `out/*.jsonl` file** — editing a generated file by hand is a
mistake (the next generator run overwrites it).

| Kind | Files | Source of truth | Regenerate with |
|---|---|---|---|
| **Generated** (derived from the protocol/fixtures by a script) | `d1`, `d2`, `d5`, `d7` | the corresponding `scripts/gen-d<n>.mjs` | `node scripts/gen-d<n>.mjs > out/<file>.jsonl` |
| **Hand-authored** (curated discovery rows) | `d3`, `d4`, `d6`, `d8`, `d9`, `d10` | the `out/*.jsonl` file itself | n/a — edit the file directly |
| **Reconciled** (synthesis of D1–D10) | `d11` | `scripts/reconcile.mjs` | `node scripts/reconcile.mjs` |

The four generators emit JSONL to **stdout**, redirected into `out/`:

- `gen-d1.mjs` — every method / action / error / capability in `schema/`.
- `gen-d2.mjs` — RFC-2119 (MUST/SHOULD/MAY) clauses mined from the spec prose.
- `gen-d5.mjs` — the reducer + round-trip fixtures lifted into wire scenarios.
- `gen-d7.mjs` — error / negative / protocol-violation paths.

### Adding rows when the protocol gains a new action / error / state field

1. **Identify the angle(s) the change belongs to.** A new wire surface
   (action / error / method / capability / state field) is **generated** — it
   appears in `d1` (schema surface) and, if it has a fixture or an error path,
   in `d5` / `d7`. A new *behavioral expectation* with no schema delta (a
   lifecycle transition, a cross-client divergence, a property) is
   **hand-authored** in `d4` / `d6` / `d8` / `d10`.

2. **For a generated angle:** update the upstream source the generator reads
   (the protocol `types/` + `schema/` for `d1`/`d2`, the
   `types/test-cases/**` fixtures for `d5`, the negative-path inputs for `d7`),
   then **re-run the generator** (`node scripts/gen-d<n>.mjs > out/<file>.jsonl`).
   Do **not** hand-edit the generated `out/*.jsonl`.

3. **For a hand-authored angle:** append the new row(s) directly to that angle's
   `out/*.jsonl`, with `coverage: "unknown"` and a **real `citation.file:line`**
   into the fork. Never edit another angle's file.

4. **Re-reconcile** so the surface matrix + coverage pick up the new rows:
   `node scripts/reconcile.mjs` (rewrites `out/d11-*` and
   `out/d11-coverage.json`).

5. **Re-run both gates** and paste the PASS lines:
   `node scripts/validate-inventory.mjs out/*.jsonl` (shape) and
   `node scripts/verify-citations.mjs out/*.jsonl` (grounding). If the new
   behavior is also covered by a corpus scenario, add the scenario's
   `behaviorIds` entry so the CI gate's corpus-covers-matrix ratchet
   ([`../ci/gate.mjs`](../ci/gate.mjs)) accounts for it.
