# Agent Guide — .NET client

Conventions for AI coding agents working on the .NET client. Cross-cutting
repo rules are in the root [`AGENTS.md`](../../AGENTS.md); release mechanics
are in [`RELEASING.md`](../../RELEASING.md).

## Layout

| Path | Contents |
| --- | --- |
| `src/AgentHostProtocol.Abstractions/Generated/*.generated.cs` | **Generated** wire types. Do not edit. |
| `src/AgentHostProtocol.Abstractions/Json/`, `Transport/` | Hand-written serialization support (`AhpUnion`, `UnionConverter`, `WireEnumConverter`, `StringOrMarkdown`) and the `ITransport` / `IAhpSerializer` seams. |
| `src/AgentHostProtocol/` | `AhpClient`, the reducers, the default `SystemTextJsonAhpSerializer`, subscriptions, and the `Hosts/` multi-host runtime. |
| `src/AgentHostProtocol.WebSockets/` | `ClientWebSocket`-based transport. |
| `tests/AgentHostProtocol.Tests/` | xUnit tests, including the shared reducer-fixture conformance suite. |
| `examples/` | Runnable console samples. |

## Code generation

Generated files are produced by `scripts/generate-csharp.ts` (run from the repo
root via `npm run generate:dotnet`) from the TypeScript definitions in
`types/`. The generator is modeled on `scripts/generate-go.ts` and shares its
curated struct / enum / union lists — they are protocol-driven, not
language-specific. After changing anything under `types/`, regenerate and
commit; CI fails on any diff between the committed sources and a fresh run.

## Type mapping (TS → C#)

- `number` → `long` (or `double` when the property carries `@format float`).
- `unknown` / `object` → `System.Text.Json.JsonElement`;
  `Record<string, unknown>` → `Dictionary<string, JsonElement>`.
- Optional (`?` / `| undefined` / `| null`) fields → nullable + `[JsonIgnore(
  Condition = JsonIgnoreCondition.WhenWritingNull)]`. Required fields serialize
  their value (a required reference left null serializes as `null`, mirroring
  Go's `nil`-slice semantics).
- String enums → C# `enum` with `[WireValue("…")]` per member, (de)serialized
  by `WireEnumConverter<T>`. Bitset enums → `[Flags] enum : uint`, serialized
  as their numeric value so unknown future bits round-trip.
- Discriminated unions → a sealed wrapper deriving from `AhpUnion` (carrying
  `object? Value`) plus a generated `UnionConverter<T>`. Unknown discriminator
  values are preserved verbatim as a raw `JsonElement`.

## Reducers

The reducers are a faithful port of the Go client's `reducers.go` and mirror
the canonical TypeScript reducers. They mutate state in place. The shared
fixtures under `types/test-cases/reducers/*.json` are the cross-language parity
gate — run them with `dotnet test`. The `changeset` and `resourceWatch`
reducers are intentional stubs (parity with the Rust and Go clients).

## Testing

Run by `dotnet test` (against both target frameworks, `net8.0` and `net9.0`).
The suite is **282 tests, all green on both TFMs** (0 skipped):

1. **Shared reducer conformance** — `FixtureDrivenReducerTests` replays the 163
   cross-language reducer fixtures (`types/test-cases/reducers/*.json`). The
   whole set counts as a single `[Theory]`.
2. **Shared wire round-trip corpus** — `TypesRoundTripFixtures` data-drives the
   language-agnostic round-trip corpus under `types/test-cases/round-trips/*.json`
   through the REAL serializer, asserting decode → re-encode is a byte-exact
   fixed point. A `[Theory]` iterates every fixture; named `[Fact]` wrappers in
   `TypesRoundTripTests` carry the cross-language matrix method names.
3. **Native unit tests** — `ClientTests` (full `AhpClient` over an in-memory
   `MemTransport`, the port of Go's `client_test.go`), `HostsTests`,
   `MultiHostClientTests`, `MultiHostStateMirrorTests`, `NativeReducerTests`,
   `TypesRoundTripTests`, `ReconnectPolicyTests`, `ClientIdStoreTests`,
   `FileClientIdStoreTests`, `TransportTests`, `WebSocketTransportTests`.
4. **Cross-implementation convergence** — `CrossImplementationConvergenceTests`
   replays a session trace captured from an INDEPENDENT host (OpenAgency's
   `AhpWsHost` on the canonical TS `sessionReducer`) and asserts byte-identical
   convergence (`serverSeq` + host-authoritative `modifiedAt`).

Beyond CI, the **full `AhpClient` has been validated LIVE over a real WebSocket**
against a spec-faithful AHP host built on the canonical `sessionReducer`: the
real `initialize` request/response handshake, the snapshot in `InitializeResult`,
and the live `action` notification stream all converge with the host. (No
client in any language ships a real-socket integration test — they are all
mock-transport-based; this validation is run out-of-band rather than committed,
since it needs a Node host + the published package.)

### Test-parity gate

Two layers enforce **manifest parity** — the machine-checked cross-language
matrix subset (OpenAgency plan `2026-06-04-0137-ahp-dotnet-client-test-parity`).
Both run [`scripts/check-test-parity.sh`](scripts/check-test-parity.sh) against
[`tests/parity-manifest.txt`](tests/parity-manifest.txt) — the expected parity
test methods in executable form — plus the count floor in
[`tests/MIN_TEST_COUNT`](tests/MIN_TEST_COUNT).

The manifest currently enumerates **70 method names** and all 70 are present
(70/70). Read that precisely: it is the cross-language matrix *subset* that has
been transcribed into the manifest, **not** a literal mirror of the entire Swift
suite. Some Swift behaviors — notably a number of §H `MultiHostClient`
edge-cases and several sub-cases — are not yet enumerated in the manifest, so
"70/70 manifest parity" is a green gate, not a claim of complete Swift parity.
When you add tests that close one of those gaps, add the method name to the
manifest (and `--bump` the floor) so the matrix subset grows with the suite.

- **CI (blocking):** `.github/workflows/ci.yml` runs the gate in COMPLETE mode —
  it **fails the build while any manifest test is missing**, and the error
  enumerates exactly which test methods to add (grouped by phase) and references
  the plan. Green only when every *manifest* method is present.
- **Local pre-push (ratchet):** the hook runs `--ratchet`, which blocks a push
  only if the discrete `[Fact]`/`[Theory]` count drops below the floor (catches
  deletions). It never blocks in-progress work, so the incremental commits that
  climb toward parity push fine.

Commands:

- **Activate the local hook** (per-clone; git hooks are never shared — run once
  from the repo root): `git config core.hooksPath scripts/git-hooks`
- **See what's still missing:** `clients/dotnet/scripts/check-test-parity.sh --list`
- **Raise the count floor after adding tests:**
  `clients/dotnet/scripts/check-test-parity.sh --bump`

Neither layer runs `dotnet test`; test *correctness* is enforced by the
`dotnet test` step in CI. The 163 shared reducer fixtures count as one `[Theory]`,
so they do not inflate the floor.

## Architecture decisions

- [`docs/decisions/sync.md`](docs/decisions/sync.md)
  — the full menu of .NET synchronization primitives, the distinct concurrency
  use cases in the client, which primitive each gets (`ConcurrentDictionary`
  for the collections, `lock` for the `HostEntry` field-bundle, `SemaphoreSlim`
  only for the WebSocket send path, `Channels`/`Interlocked`/`volatile`
  elsewhere), and why the libraries multi-target `net8.0;net9.0` to use
  `System.Threading.Lock` on .NET 9.
- [`docs/decisions/serialization.md`](docs/decisions/serialization.md)
  — System.Text.Json (default, in-box, fastest) behind the `IAhpSerializer`
  seam, versus Newtonsoft / lazy-DOM / validating options, across speed,
  memory, lazy-vs-eager, validation, dependencies, and AOT.
- [`docs/decisions/reconnect.md`](docs/decisions/reconnect.md)
  — hand-rolled exponential backoff (with opt-in jitter) versus
  Polly / `Microsoft.Extensions.Resilience`, and why the core stays
  dependency-free.

These decision records live under `docs/decisions/` and are repo-only — they are not packed into any NuGet
package (only `README.md` is).

## Releasing

Sub-package releases use the `dotnet/vX.Y.Z` tag namespace; `publish-dotnet.yml`
validates the tag against `VERSION` + `CHANGELOG.md`, packs every packable
project, and pushes to NuGet.org.

## Out of scope

JSON-Schema validation (a `Microsoft.AgentHostProtocol.Validation` decorator
over `IAhpSerializer`) and DI/extension helpers
(`Microsoft.AgentHostProtocol.Extensions`) are planned follow-ups, not part of
this client yet.
