# conformance — live client↔server full-handshake check

A **real client↔server conformance test**, run over a real WebSocket with no
mocks. It exercises the in-repo **.NET** AHP client (`Microsoft.AgentHostProtocol*`,
`clients/dotnet`) against a **spec-faithful AHP host** built on the in-repo
**TypeScript** client's canonical `sessionReducer` (`clients/typescript`), and
asserts the two converge **byte-for-byte**.

Unlike the per-client fixture/round-trip suites (which check one client in
isolation), this proves two independent implementations of the protocol agree
when actually talking to each other on the wire.

## What it exercises end-to-end

1. The full `AhpClient` real `initialize` JSON-RPC request/response handshake.
2. Seeding client state from the `snapshot` in `InitializeResult`.
3. The live `action` notification stream → `sessionReducer` → **byte-for-byte
   convergence** with the host's authoritative state.

The clock is pinned on both sides (`Date.now = 9999` on the host;
`Reducers.SetNowProvider(() => 9999)` on the client) so the impure
`summary.modifiedAt` (microsoft/agent-host-protocol#186) is deterministic — the
real wire protocol carries no host-authoritative meta, so both sides must derive
it identically from the same reducer logic.

## Self-contained

Every dependency resolves **inside this repo** — nothing published, nothing
machine-absolute:

- **Host** (`host/`) — depends on `@microsoft/agent-host-protocol` via a
  `file:../../clients/typescript` dependency (the in-repo TS client), plus `ws`.
- **.NET client** (`dotnet/FullHandshake`) — uses `<ProjectReference>` to the
  in-repo client csprojs (`clients/dotnet/src/AgentHostProtocol.Abstractions`,
  `AgentHostProtocol`, `AgentHostProtocol.WebSockets`), built from source.

## Prerequisites

`node` + `npm`, and the .NET SDK (`dotnet`). No network access to a package
registry is required beyond the first `npm install` of `ws` and the .NET SDK's
own restore.

## Run

```bash
./conformance/run.sh
```

`run.sh` is portable (resolves all paths relative to itself). On first run it
bootstraps the in-repo TypeScript client (generate wire types → `npm install` →
`npm run build`) so the host's `file:` dependency has a compiled `dist/`,
installs the host deps, builds the .NET client + handshake harness through the
`<ProjectReference>` chain, starts the host, captures its `ws://` URL, runs the
.NET client against it, and asserts the success line.

Expected tail:

```
FULL-HANDSHAKE LIVE PASS — initialize + snapshot + live action stream converge with the canonical reducer
```

## The conformance-host seed

This is the seed of a shared **conformance host**: a single spec-faithful host,
built on the canonical reducer, that *any* AHP client (Go, Rust, Kotlin, Swift,
TypeScript, …) can run the same handshake against to prove interoperability. The
host (`host/host.mjs`) is intentionally tiny and protocol-only — it speaks the
real `initialize` / `subscribe` / `action` wire messages and nothing
client-specific — so adding a second client is just a new harness under
`conformance/<lang>/` pointed at the same `ws://` URL and the same `final.json`
expected-state, with that language's clock-pinning hook.

## The full suite — corpus, 6 clients, mutation, discovery, and the CI gate

That seed has since grown into a full cross-implementation conformance suite. The
single-handshake check above is one slice of it; the whole picture is the
**[cross-implementation conformance report](REPORT.md)**:

- **233-scenario corpus** (`types/test-cases/scenarios/`: 164 reducer + 23
  round-trip + 46 negative) — a language-neutral fixture corpus the clients
  replay. Shape-validated by `validate-scenarios.mjs`.
- **6 green clients** (TS · Kotlin · Swift · .NET · Go · Rust = **233/233** each)
  — every client replays the corpus against the scenario-driven host
  (`host/scenario-host.mjs`) over a real WebSocket through its own native
  reducers. Runners live under `conformance/<lang>/`.
- **Mutation testing** (`conformance/mutation/`) — **87.70 %** kill-rate over the
  canonical TS reducers, **KEEP** (`mutation/DECISION.md`), `break:87` floor.
- **Part-1 discovery** (`conformance/discovery/`) — **656** behaviors across 10
  angles, all shape-valid + citation-grounded.

### The machine-checked CI gate

[`conformance/ci/gate.mjs`](ci/gate.mjs) is the dependency-free gate that runs the
core checks and exits non-zero on the first failure:

```bash
node conformance/ci/gate.mjs                 # GATE PASS — 6/6 checks green
node conformance/ci/gate.mjs --print-coverage  # the live corpus-covers-matrix numbers
```

It checks: (A) scenario shape, (B) host-conformance **233/233** (serialized for
determinism), (C) discovery integrity (**656** grounded), and (D) the
**CORPUS-COVERS-MATRIX** exhaustiveness ratchet — every D5/D7 discovery behavior
is covered by a scenario (**233/233**), and the overall D11-surface coverage
(**235/652 = 36.0 %**) is held against a ratchet floor in
[`ci/coverage-floor.json`](ci/coverage-floor.json) (fails only on regression).

CI wiring: [`.github/workflows/conformance.yml`](../.github/workflows/conformance.yml)
— the core gate + a per-client matrix on every push/PR, plus a nightly/on-demand
mutation job.
