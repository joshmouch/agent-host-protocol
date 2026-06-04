# ADR-RECONNECT — Reconnect / retry strategy

- **Status:** Accepted
- **Scope:** `clients/dotnet` — the multi-host reconnect supervisor
  (`Hosts/MultiHostClient.cs`, `ReconnectPolicy`).
- **Audience:** maintainers of the .NET client. Repo-only; not shipped in any
  NuGet package.

## Context

When a host's transport drops unexpectedly, `MultiHostClient` supervises a
reconnect with exponential backoff (`ReconnectPolicy`: initial/max backoff,
multiplier, max attempts, reset-on-success), emitting `Reconnecting` / `Failed`
host-state events and preserving the client id across attempts.

.NET has a first-class retry/resilience stack now —
**Polly v8** and **`Microsoft.Extensions.Resilience`** /
**`Microsoft.Extensions.Http.Resilience`** (the latter's
`AddStandardResilienceHandler()` gives an HttpClient a pre-built pipeline of
retry-with-jitter + circuit-breaker + timeout in one line). The question is
whether the client should depend on that stack or keep its small hand-rolled
loop.

## Dimensions considered

1. **Dependency footprint** — the libraries currently have **zero** NuGet
   dependencies.
2. **Fit** — is this an HttpClient call, or something else?
3. **Cross-client parity** — how the other AHP clients reconnect.
4. **Features** — retry, jitter, circuit breaker, timeout, telemetry.
5. **Consumer extensibility** — can a consumer who *does* use Polly add their
   own resilience without the client forcing it?

## Options

| Option | Deps | Fit | Notes |
| --- | --- | --- | --- |
| **Hand-rolled exponential backoff** (current) | none | exact | Lives inside the supervisor alongside host-state transitions and client-id persistence. Matches the Go/Rust/TS/Swift/Kotlin clients, which all hand-roll reconnect. |
| `Microsoft.Extensions.Http.Resilience` (`AddStandardResilienceHandler`) | +Polly +Extensions | **poor** | Built for `HttpClient` message pipelines. The AHP transport is a WebSocket/abstract `ITransport`, not an `HttpClient` call — this doesn't apply. |
| `Microsoft.Extensions.Resilience` / Polly v8 `ResiliencePipeline` | +Polly +Extensions | partial | `ResiliencePipeline` can wrap an arbitrary delegate, so it *could* drive the reconnect. But it adds dependencies, and the reconnect is intertwined with host-state events, client-id persistence, and supervisor lifetime that a generic retry pipeline doesn't model cleanly. |

## Decision

**Keep the hand-rolled exponential backoff in the core**, and adopt the one
best-practice the resilience libraries embody — **exponential backoff with
jitter** — as a dependency-free, opt-in `ReconnectPolicy.Jitter`.

- **Zero dependencies** stays a hard goal for the libraries; pulling in Polly +
  `Microsoft.Extensions.*` for a ~30-line backoff loop is a bad trade.
- **Parity:** every other AHP client hand-rolls reconnect with the same
  policy shape; matching them keeps behavior consistent across the family.
- **Fit:** the reconnect is a transport-reconnect state machine, not an
  HttpClient call — the standard HTTP resilience handler does not apply.
- **Jitter** (`ReconnectPolicy.Jitter`, a 0–1 fraction, default **0** for
  parity) randomizes each backoff by ±that fraction to avoid reconnect storms
  when many hosts drop at once. `0.2` is a reasonable production value. This
  captures the resilience libraries' headline recommendation without their
  dependency.

### Consumer seam

A consumer who already standardizes on Polly / `Microsoft.Extensions.Resilience`
is **not** blocked: `HostConfig.TransportFactory` is the delegate that opens a
transport, so they can wrap their own resilience pipeline (retry, circuit
breaker, timeout) around transport creation. The client doesn't bake a policy
in; it provides the seam.

## Consequences

- Core libraries stay dependency-free.
- Jitter is available immediately and tested (`ReconnectPolicyTests`).
- Advanced strategies (circuit breaker, per-attempt timeout, telemetry) are a
  documented future option — most naturally as an *optional* resilience-
  integration package (analogous to the planned validation package in
  [ADR-SERIALIZATION](ADR-SERIALIZATION.md)), not a core dependency.

## References

- [Build resilient HTTP apps: key patterns — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience)
- [Retry resilience strategy — Polly](https://www.pollydocs.org/strategies/retry.html)
