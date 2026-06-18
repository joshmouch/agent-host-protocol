# JSON serialization engine

- **Status:** Accepted
- **Scope:** `clients/dotnet` (the `Microsoft.AgentHostProtocol*` packages)
- **Audience:** maintainers of the .NET client. Repo-only; not shipped in any
  NuGet package.

## Context

The client has to turn AHP wire messages (JSON-RPC framing wrapping protocol
state, actions, commands, notifications) into typed objects and back. The
reducers operate on fully-typed state, and the protocol uses string-keyed
discriminated unions, a `string | { markdown }` scalar, and bitset enums — so
the serializer has to support custom converters.

Two questions had to be answered: **which engine**, and **how coupled** the
rest of the client is to it. The engine is pluggable behind the
`IAhpSerializer` seam (`Encode`/`Decode`/`DecodeMessage`); this ADR records why
the default is System.Text.Json and what the seam does and does not decouple.

## Dimensions considered

1. **Throughput** — serialize/deserialize speed on the hot path (every event).
2. **Memory / allocations** — GC pressure per message.
3. **Eager vs lazy (late binding)** — materialize the whole POCO graph every
   time, or bind fields on demand from a DOM.
4. **Validation** — can inbound frames be checked against the JSON Schema the
   repo already generates under `schema/`?
5. **Dependency footprint** — does it add a NuGet dependency, or is it in-box?
6. **AOT / trimming** — reflection-based vs source-generated; Native AOT and
   trimming friendliness.
7. **Strictness / standards** — strict-by-default (good for a wire protocol)
   vs lenient.
8. **Polymorphism / custom converters** — support for the protocol's
   discriminated unions, `StringOrMarkdown`, and bitset enums.
9. **Ecosystem familiarity / migration cost.**
10. **Cross-client consistency** — how the other AHP clients bind their wire
    types.

## Options considered

### Engines

| Option | Throughput | Allocations | Eager/Lazy | Deps | AOT | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| **System.Text.Json (POCO)** ✅ | Highest | Lowest (`Span<T>`/UTF-8) | Eager | **In-box** (net8) | Source-gen capable | Strict by default; Microsoft's greenfield recommendation. |
| System.Text.Json + source generation | Highest (+startup, +AOT) | Lowest | Eager | In-box | **Best** | An AOT/trimming enhancement for later — but **not** a drop-in `[JsonSerializable]` context: see "Deferred, on purpose" — the runtime-`Type`-keyed union converters do not compose with the source generator, so it requires reshaping the generated unions, not just adding a context. |
| Newtonsoft.Json (Json.NET) | ~20–35% slower; ~3× more allocations on .NET 10 | High (reflection, no `Span`) | Eager or `JObject` (lazy, mutable) | **+dependency** | Reflection (AOT-hostile) | Lenient by default; ubiquitous, but a dependency and slower. |
| Lazy DOM — `JsonNode` / `JsonElement` (STJ) or `JObject` (Newtonsoft) | n/a (no bind) | Low for partial reads | **Lazy** | In-box (STJ) | ok | A *different consumption model*: expose untyped views instead of typed state. Reducers can't run on it without materializing. |
| Utf8Json / Jil / other high-perf | Very high | Very low | Eager | +dependency | varies | Effectively unmaintained; not worth the dependency/risk for a JSON wire protocol. |
| MessagePack / binary | Very high | Very low | Eager | +dependency | ok | Not JSON — the AHP wire format is JSON-RPC, so out of scope. |

### Validation libraries (for a future "validated" mode)

| Option | License | Notes |
| --- | --- | --- |
| **JsonSchema.Net (json-everything)** | MIT | Spec-compliant JSON Schema validator; the natural fit to validate against the repo's generated `schema/*.schema.json`. |
| NJsonSchema | MIT | Validation + code-gen; heavier surface than we need. |
| Newtonsoft.Json.Schema | **Commercial (paid above a free-use threshold)** | Disqualifying for an in-box, permissively-licensed library. |

## Decision

**Default engine: System.Text.Json, eager POCO binding, behind the
`IAhpSerializer` seam.**

Rationale, against the dimensions:

- **Throughput + memory:** STJ is the fastest, lowest-allocation option — it is
  built on `Span<T>`/`ReadOnlySpan<byte>` and is ~20–35% faster with ~3× fewer
  allocations than Newtonsoft on modern .NET. This matters on the per-event hot
  path.
- **Dependencies:** STJ is **in the shared framework** for net8 — the
  packages stay at **zero NuGet dependencies**, which is a hard goal for this
  library.
- **AOT / trimming:** STJ supports source generation as a path to
  Native-AOT/trimming friendliness later. Note this is **not** a free,
  drop-in step for this client: the discriminated unions dispatch on a
  runtime `Type`, which the source generator does not support, so the
  migration is a typed-variant rewrite of the generated unions (see
  "Deferred, on purpose"). Until then the shipping libraries declare the
  reflection unsafety via `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`
  on the serializer seam so trimmed/AOT consumers are warned at build time.
- **Strictness:** strict-by-default is correct for a wire protocol — a
  malformed or unexpected frame should fail loudly, not be silently coerced.
- **Custom shapes:** the protocol's discriminated unions, `StringOrMarkdown`,
  and bitset enums are handled by hand-written converters
  (`UnionConverter<T>`, `WireEnumConverter<T>`, `StringOrMarkdownConverter`) —
  which any engine would require, and which STJ supports cleanly.
- **Cross-client consistency:** every other AHP client bakes serialization into
  its generated wire types (Go `json` tags, Rust `serde`, Kotlin
  `@Serializable`, Swift `Codable`, TS native). The generated C# types are
  likewise STJ-attributed — consistent with the family.

### What the `IAhpSerializer` seam does and does not decouple

- It **does** make the *transport/client* engine swappable and lets a
  **validating decorator** wrap the default serializer (see below).
- It **does not** make the *generated types* serializer-agnostic: they carry
  STJ attributes by design (mirroring how every other client bakes its
  serializer into its types). A true engine swap (e.g. to Newtonsoft) would
  mean re-emitting the types for that engine — tractable since they're
  generated, but STJ stays the one default.

### Deferred, on purpose

- **Validation ("validated vs not"):** a future opt-in
  `Microsoft.AgentHostProtocol.Validation` package will decorate
  `IAhpSerializer` and validate inbound frames against the repo's generated
  `schema/*.schema.json` using **JsonSchema.Net (json-everything, MIT)**. Kept
  out of the core so the default path stays zero-dependency and fast.
- **Lazy / late-binding surface:** if a consumer needs to inspect frames
  without materializing typed state (a proxy/pass-through), that is a separate
  read-only `JsonNode`/`JsonElement` surface — not a drop-in serializer swap,
  because the reducers require typed state.
- **Source generation:** add source-gen for AOT/trimming and a further perf
  bump when there is a concrete AOT consumer. This is **not** merely "add a
  `[JsonSerializable]` `JsonSerializerContext`." The union machinery is
  fundamentally reflection-polymorphic: `UnionConverter<T>.Read` resolves the
  payload type at runtime from a `Dictionary<string, Type>` and calls
  `root.Deserialize(variantType, options)`, and `Write` serializes via
  `inner.GetType()`. The STJ source generator only emits metadata for
  compile-time-known closed types and does **not** support custom converters
  that dispatch on a runtime `Type`. A real source-gen migration therefore
  requires reshaping every discriminated union away from the `object?`-valued
  `AhpUnion` + runtime-`Type` dispatch toward a closed, statically-known variant
  representation (e.g. STJ's `[JsonPolymorphic]`/`[JsonDerivedType]`, or
  per-variant typed properties) — a redesign of the generated wire types plus
  the codegen, not a drop-in. In the meantime the libraries opt into the
  trim/AOT analyzers and annotate the reflection entry points with
  `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]` so the limitation is
  declared at build time rather than discovered at runtime.

## Consequences

- The default path is fast, allocation-light, and dependency-free.
- A different engine or a validating layer can be added behind `IAhpSerializer`
  without touching the client or transport.
- Consumers who want JSON-Schema validation opt into a separate package; the
  core never pays for it.

## References

- [Migrate from Newtonsoft.Json to System.Text.Json — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/migrate-from-newtonsoft)
- [Benchmarking System.Text.Json vs Newtonsoft.Json on .NET 10 — jkrussell.dev](https://jkrussell.dev/blog/system-text-json-vs-newtonsoft-json-benchmark/)
- [Newtonsoft.Json.Schema licensing (commercial)](https://www.newtonsoft.com/jsonschema)
