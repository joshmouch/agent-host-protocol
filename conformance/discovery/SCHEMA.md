# AHP Conformance Discovery — inventory-row schema (Phase D0)

This is the **shared vocabulary** for the conformance-suite discovery campaign.
Every discovery angle (D1–D11) emits one JSONL file under `out/` where each line
is one **inventory row** describing a single conformance *behavior*. This document
is the prose contract; `schema/inventory-row.schema.json` is the machine-checkable
form; `scripts/validate-inventory.mjs` is the gate that every angle's output must
pass.

> **Discovery rule (all phases):** sources never edit each other's rows — they
> only *add*. Two angles that emit the **same `behavior-id`** are describing the
> **same behavior**; D11 reconciles them by **union of `sources[]`**, never by
> editing. No angle may silently subsume another (that is how exhaustiveness
> leaks).

## `behavior-id` grammar

```
behavior-id := <domain> "." <concept> "." <scenario-class> [ "." <discriminator> ]
```

- 3–5 dot-separated segments; each segment is `[A-Za-z0-9-]+`.
- **Stable + collision-resistant + human-readable.** The id IS the merge key:
  pick it so an *independent* angle that finds the same behavior naturally lands
  on the same id.
- `<domain>` — the top-level area. Suggested controlled set (extend only when a
  behavior genuinely has no home): `rpc`, `session`, `subscription`, `reconnect`,
  `action`, `error`, `lifecycle`, `transport`, `versioning`, `auth`, `channel`,
  `state`.
- `<concept>` — the specific method / notification / action / error / concept
  (e.g. `initialize`, `SessionTitleChanged`, `MethodNotFound`, `session-channel`).
  PascalCase is allowed here for action/type names.
- `<scenario-class>` — one of the `scenario-class` enum values (below), OR a
  descriptive class like `roundtrip` when the row is about wire-survival rather
  than a runtime scenario. (The strict enum lives in the `scenario-class` *field*;
  the id segment is the human label.)
- `<discriminator>` — optional kebab tail disambiguating sibling behaviors
  (e.g. `version-mismatch`, `creating-to-active`, `unknown-variant-preserved`).

**Examples**
- `session.lifecycle.happy.creating-to-active`
- `rpc.initialize.error.version-mismatch`
- `action.SessionTitleChanged.roundtrip.unknown-variant-preserved`
- `reconnect.replay.edge.gap-detected-resnapshot`
- `error.MethodNotFound.error.unknown-method`

## Row fields

| field | type | required | meaning / allowed values |
|---|---|:--:|---|
| `behavior-id` | string | ✅ | The stable id above. 3–5 dot segments of `[A-Za-z0-9-]`. |
| `source` | enum | ✅ | Which angle emitted it: `d1-schema` \| `d2-spec` \| `d3-mined-client` \| `d4-host` \| `d5-fixture` \| `d6-lifecycle` \| `d7-negative` \| `d8-differential` \| `d9-mutation` \| `d10-property` \| `d11-synthesis`. |
| `method` | string \| null | ✅ | The RPC method / notification / channel (e.g. `initialize`, `session/subscribe`), or `null` if not method-scoped. |
| `concept` | string | ✅ | The protocol concept (e.g. `reconnect-replay`, `StateAction:SessionTitleChanged`, `error:MethodNotFound`). Non-empty. |
| `scenario-class` | enum | ✅ | `happy` \| `error` \| `edge` \| `reconnect` \| `version` \| `concurrency`. |
| `normative-level` | enum | ✅ | RFC-2119 level: `MUST` \| `MUST_NOT` \| `SHOULD` \| `SHOULD_NOT` \| `REQUIRED` \| `SHALL` \| `MAY` \| `NONE`. Use `NONE` for rows not derived from spec prose. |
| `citation` | object | ✅ | `{ file, line, excerpt }` — a **real** grounding (see below). |
| `coverage` | enum | ✅ | `unknown` \| `planned` \| `covered` \| `out-of-scope`. **D1–D10 always emit `unknown`**; D11 reconciles. |
| `notes` | string | optional | Triage, cross-refs, divergence detail, out-of-scope reason. |
| `assertion` | string | optional | The spec-correct observable outcome a scenario would check. |
| `params-shape-ref` | string | optional | Pointer to the params/result schema shape, e.g. `commands.schema.json#/$defs/InitializeParams`. |

### `citation` (mandatory, non-negotiable)

```jsonc
"citation": {
  "file": "schema/commands.schema.json",   // path relative to the fork root
  "line": 42,                               // 1-based, or null if whole-file
  "excerpt": "\"method\": { \"const\": \"initialize\" }"  // verbatim from file:line
}
```

Every behavior **must** be grounded in a real `file:line` you actually opened and
read. `file:line` must exist and really contain `excerpt`. **No fabricated rows,
no invented paths, no guessed line numbers** — this is a *conformance* suite;
fabricated evidence poisons the entire surface inventory (see the no-mock policy
in the campaign briefs).

## Output convention

- Each angle writes exactly one disjoint file: `out/<phase>.jsonl`, where
  `<phase>` matches the canonical names below (kept in lockstep with the `source`
  enum):

  | source | output file |
  |---|---|
  | `d1-schema` | `out/d1-schema-surface.jsonl` |
  | `d2-spec` | `out/d2-normative-rules.jsonl` |
  | `d3-mined-client` | `out/d3-mined-client-expectations.jsonl` |
  | `d4-host` | `out/d4-host-behaviors.jsonl` |
  | `d5-fixture` | `out/d5-fixture-derived-scenarios.jsonl` |
  | `d6-lifecycle` | `out/d6-lifecycle-transitions.jsonl` |
  | `d7-negative` | `out/d7-negative-paths.jsonl` |
  | `d8-differential` | `out/d8-divergences.jsonl` |
  | `d9-mutation` | `out/d9-surviving-mutants.jsonl` |
  | `d10-property` | `out/d10-property-findings.jsonl` |
  | `d11-synthesis` | `out/d11-surface-matrix.jsonl` (+ `out/d11-surface-matrix.md`) |

- One JSON object per line (JSONL). Blank lines are tolerated by the validator.
- Within one file every `behavior-id` must be **unique** (the validator enforces
  this). Across files, repeats are *expected* and are how D11 unions sources.

## Validate (two gates)

```bash
cd conformance/discovery
node scripts/validate-inventory.mjs out/d1-schema-surface.jsonl   # shape
node scripts/verify-citations.mjs  out/d1-schema-surface.jsonl    # grounding
npm run gate:all                                                  # both, all files
```

- **Shape** (`validate-inventory.mjs`): each row matches this schema. PASS prints
  `PASS — N row(s) valid …` (exit 0); any malformed row prints `file:line:
  <reason>` (exit non-zero).
- **Grounding** (`verify-citations.mjs`): each `citation.file:line` really exists
  in the fork and really contains `citation.excerpt`. This is the
  anti-fabrication gate — a shape-valid row with an invented citation passes the
  shape gate but **fails** grounding. GROUNDED (exit 0) / FAIL (exit non-zero).

Both are **dependency-free** — no `npm install` required.
