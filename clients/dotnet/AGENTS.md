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
