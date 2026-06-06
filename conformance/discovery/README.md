# `conformance/discovery/` — the AHP conformance-surface discovery pipeline

This directory is the **discovery half (Part 1)** of the AHP conformance-suite
effort. Its job is to attack the conformance surface from many independent,
overlapping angles so no behavior hides in a single blind spot, and to emit the
result as a **machine-readable, re-runnable inventory** rather than a prose
document.

Plan of record:
`OpenAgency/docs/plans/active/2026-06-06-0642-ahp-conformance-suite/plan.md`
(Part 1, phases D0–D11).

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
