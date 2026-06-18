# Client Self-Instrumentation

Self-instrumentation is how an AHP **client** reports on *its own* operation —
the spans, metrics, and attributes it emits about the requests it sends, the
frames it receives, the reconnects it performs, and the events it drops.

It is distinct from the [telemetry channel](./telemetry-channel.md), which
carries OpenTelemetry data the **host** emits *to* clients (server → client
OTLP delivery). This page is about the client observing itself, not the host
observing the agent.

> Self-instrumentation is **optional** and **non-normative** for interop: a
> client that emits no telemetry is fully conformant. What *is* normative when a
> client chooses to instrument is the **names** it uses — see below.

## The names are the contract

Every client's self-instrumentation flows into **OpenTelemetry**:

- The **.NET** client uses `System.Diagnostics.ActivitySource` + `Meter`, which
  the OpenTelemetry .NET SDK consumes natively — no shim.
- The **Rust** client emits through the [`metrics`](https://docs.rs/metrics)
  facade (and `tracing` for diagnostics), both of which convert to OTel through
  the standard exporters (`tracing-opentelemetry`, an OTel metrics exporter).
- Other clients adopt the same names when they add instrumentation
  (OTel-Go / swift-otel / OTel-Java / OTel-JS).

In OpenTelemetry the **span / metric / attribute names *are* the contract**: the
dashboards, alerts, and queries an operator builds are keyed off those names. If
the .NET client emits `ahp.client.request.duration` while another client emits a
differently-spelled name for the same thing, a single operator query can no
longer span both clients and cross-client observability silently breaks.

So for the OTel contract to stay consistent across clients, these names **must be
identical by construction**, not by convention.

## How the names stay identical: one generated source

The names live in a single TypeScript source — `types/telemetry/registry.ts` —
as **string enums** (`TelemetrySpan`, `TelemetryMetric`, `TelemetryAttribute`,
`TelemetryOutcome`, …), the same shape as the protocol enums (e.g.
`ChangesetOperationStatus`). Each client SDK gets an idiomatic, **generated**
holder compiled from them:

| Client | Generated holder |
|---|---|
| .NET | `AhpTelemetryNames` (static class) |
| Rust | `ahp_types::telemetry` (`pub const &str`) |
| Swift | `AhpTelemetryNames` (caseless `enum`) |
| Kotlin | `AhpTelemetryNames` (`object`) |
| Go | `telemetry.generated.go` (package consts) |
| TypeScript | the telemetry enums re-exported from the package root |

One edit to `types/telemetry/registry.ts` propagates to every client via
`npm run generate`; a divergent hand-typed name is impossible. (The generated
holders are flat constant holders, not language enums — telemetry names are
consumed as raw strings by `Meter` / `metrics`.)

Each name's description is a JSDoc comment on its enum member, extracted by the
generators with `member.getJsDocs()` — exactly the way descriptions are
extracted for every other protocol enum — so the description authored once,
right next to the name, surfaces as a doc comment in every SDK.

Only the **names** are shared. The instrumentation *logic* — `ActivitySource` /
`Meter` wiring, `HasListeners()`-style gating, the `metrics` facade calls — stays
hand-written and idiomatic per language.

## Names

The current contract (see `types/telemetry/registry.ts` for the authoritative
list and per-name descriptions):

- **Scope:** `Microsoft.AgentHostProtocol`
- **Span:** `ahp.request`
- **Metrics:** `ahp.client.messages.sent` / `.messages.received` /
  `.request.duration` / `.requests.in_flight` / `.subscriptions.active` /
  `.reconnects` / `.events.dropped` / `.frames.malformed`
- **Attributes:** `rpc.system`, `rpc.method`, `ahp.request.id`, `ahp.outcome`,
  `ahp.message.kind`, `ahp.stream`
- **Attribute values:** `ahp.outcome` ∈ `{ok, error, cancelled, timeout}`;
  `ahp.message.kind` ∈ `{request, notification}`; `ahp.stream` ∈
  `{subscription, event, state, host-event, host-subscription, host-resource,
  host-snapshot, host-summaries}`; `rpc.system` = `jsonrpc`.

`rpc.*` attributes follow the OpenTelemetry RPC semantic conventions. The
`host-*` `ahp.stream` values name the per-stream dropped-event channels a
multi-host client (e.g. the .NET client) fans the host's own notifications
across; they are enumerated attribute *values*, not OTel instrument names, so
the hyphenated spelling is intentional.

## Consuming the telemetry

The SDKs emit through each platform's native OpenTelemetry primitives, so any
standard exporter picks the signals up. Two reference examples wire a real OTel
exporter to the generated names end-to-end:

- **Rust:** `clients/rust/crates/ahp/examples/otel_export.rs` — installs a
  `metrics` recorder and exports the `ahp.client.*` metrics.
- **.NET:** the `OtelExport` example under `clients/dotnet/examples/` — subscribes
  an OTel `MeterListener` / `ActivitySource` to `Microsoft.AgentHostProtocol`
  and exports the spans + metrics.

In both, the instrumentation is zero-cost until a recorder / listener is
installed, so importing the SDK does not force a telemetry dependency on hosts
that don't want one.
