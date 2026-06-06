# Swift Reference Client — Known Wire-Fidelity Gaps vs. the Shared Round-Trip Corpus

**Scope.** Independent confirmation of the five round-trip fixtures the Swift reference client cannot represent, against `types/test-cases/round-trips/*.json`. Every claim below is grounded in the actual Swift sources under `clients/swift/AgentHostProtocol/Sources/`, contrasted with the .NET client under `clients/dotnet/src/`, and cross-checked against the protocol spec (`docs/specification/versioning.md`) and JSON Schema (`schema/*.json`).

**Method.** Each fixture was read; the implicated Swift type's `init(from:)` / `encode(to:)` (or synthesized `Codable` surface) was read directly; the corresponding .NET converter / type was read; and the spec/schema basis was located. The Swift test harness (`clients/swift/.../TypesRoundTripFixtureTests.swift`) already encodes these five as `knownRepresentationalGaps` (lines 47–92) — this document independently re-derives them and adds a fairness assessment of bug-vs-deliberate-design for each, plus one **material correction** to the corpus's framing of fixture 019.

**Headline:** 4 of 5 gaps are genuine Swift-side encode-fidelity defects (002, 003, 012, 013). The 5th (019) is **not** a Swift bug — it is a corpus fixture that is itself schema-invalid, and where Swift's modeling is the spec-correct one and .NET's is the deviation.

---

## TL;DR table

| # | Fixture | Swift behavior | Verdict | .NET contrast |
|---|---------|----------------|---------|---------------|
| 002 | `state-action-unknown-variant-preserved` | Unknown `StateAction` payload dropped on decode; **encodes nothing** (empty object) | **Bug** (encode side); decode-drop is spec-tolerable | `allowUnknown: true` → preserved as `JsonElement`, re-emitted verbatim |
| 003 | `customization-unknown-type-preserved` | `Customization.init(from:)` **throws** on unknown `type` | **Gap** (codegen didn't honor `allowUnknown` for this union) | `allowUnknown: true` → preserved & re-emitted verbatim |
| 012 | `changeset-target-resource` | Decodes fine; **drops `kind`** on re-encode | **Bug** (`kind` is a non-encoded computed property) | `kind` is a real serialized field → re-emitted |
| 013 | `changeset-target-range` | Decodes fine; **drops `kind`** on re-encode | **Bug** (same root cause as 012) | same as 012 |
| 019 | `channel-scoped-notification-uri` | Decode **throws** `keyNotFound("summary")` | **NOT a Swift bug** — Swift matches the schema; the fixture + .NET deviate | `summary` modeled nullable (off-spec), so the degenerate payload decodes |

---

## Gap 1 — Fixture 002: unknown `StateAction` variant is lost on re-encode

**Fixture** (`types/test-cases/round-trips/002-state-action-unknown-variant-preserved.json`):
```json
"wireRaw": "{\"type\":\"future/newAction\",\"foo\":42}",
"expect": { "type": "future/newAction", "foo": 42 },
"reencodes": true
```
Contract: an unknown discriminator decodes to a raw passthrough and **re-encodes byte-for-byte** (the `foo: 42` payload survives).

**Swift source.** `clients/swift/AgentHostProtocol/Sources/AgentHostProtocol/Generated/Actions.generated.swift`:
- The unknown case carries **only the discriminant string**, no payload:
  - `Actions.generated.swift:1396` — `case unknown(type: String)`
- Decode discards every non-`type` field:
  - `Actions.generated.swift:1528-1529` — `default:` → `self = .unknown(type: type)`
- Encode emits **nothing at all**:
  - `Actions.generated.swift:1597` — `case .unknown: break`

**Observed behavior.** `{"type":"future/newAction","foo":42}` decodes to `.unknown(type: "future/newAction")` (loses `foo`), and re-encodes to an **empty JSON object `{}`** — even the `type` discriminant is gone, because `break` writes to no container. Both `expect.foo == 42` and `reencodes` (byte-exact) fail.

**.NET contrast.** `StateActionConverter` is constructed with `allowUnknown: true` (`clients/dotnet/src/AgentHostProtocol.Abstractions/Generated/Actions.generated.cs`, ctor at line 1760, flag confirmed in the `base(...)` call). The shared `UnionConverter<T>` preserves the unknown verbatim and re-emits it exactly:
- `clients/dotnet/.../Json/UnionConverter.cs:67-71` — `else if (_allowUnknown) { result.Value = root.Clone(); }`
- `UnionConverter.cs:91-95` — `if (inner is JsonElement raw) { raw.WriteTo(writer); }`
- The base class documents the intent at `clients/dotnet/.../Json/AhpUnion.cs:10-14` ("unknown discriminator values … are stored as a raw `JsonElement` so re-encoding round-trips faithfully").

The .NET corpus runner exercises this fixture with no skip: `clients/dotnet/tests/AgentHostProtocol.Tests/TypesRoundTripTests.cs:53` → `RunFixtureByName("002")`.

**Spec / forward-compat implication.** `docs/specification/versioning.md:55` states clients **SHOULD silently ignore** actions with unrecognized `type` values. Two distinct behaviors hide under that one rule:
1. *Decode-drop of the payload* — defensible. A `StateAction` is consumed by a reducer that no-ops on unknowns (the Swift case comment at `Actions.generated.swift:1395` says exactly this), so for the **reducer** path, dropping `foo` is within "silently ignore."
2. *Encoding an empty object* — **not** defensible. A client that decodes a `StateAction` and re-encodes it (relay, log, persist-and-replay, test round-trip) emits `{}`, which is not a valid action and has lost even its own discriminant. This is a genuine defect regardless of the lenient SHOULD.

**Fairness note.** The decode-side loss is arguably a deliberate "actions are no-op'd, so don't carry the payload" choice. The **encode-side `break`** is the indefensible part: at minimum the case should retain and re-emit the original object (or its `type`). A reference client that round-trips a value to `{}` is the strongest single argument that this is a bug, not a design stance.

**Recommended fix direction.** Make the unknown case payload-carrying (e.g. `case unknown(AnyCodable)` or `case unknown(type: String, raw: AnyCodable)`), decode the whole object into it, and encode by re-emitting the preserved object. `AnyCodable` already exists and round-trips arbitrary JSON faithfully (`clients/swift/.../AnyCodable.swift` — full `init(from:)`/`encode(to:)` over null/bool/int/double/string/array/object), so the building block is in place; only the `StateAction` union needs rewiring.

---

## Gap 2 — Fixture 003: unknown `Customization` type throws instead of passing through

**Fixture** (`003-customization-unknown-type-preserved.json`):
```json
"wireRaw": "{\"type\":\"future/unknownCustomization\",\"path\":\"/x\",\"extra\":7}",
"expect": { "type": "future/unknownCustomization", "path": "/x", "extra": 7 },
"reencodes": true
```
Contract: the `Customization` union **opts into `allowUnknown`** — an unrecognized `type` decodes to a raw passthrough (no throw) and re-encodes verbatim.

**Swift source.** `clients/swift/AgentHostProtocol/Sources/AgentHostProtocol/Generated/State.generated.swift`:
- `State.generated.swift:3729-3731` — `enum Customization` has exactly two cases (`.plugin`, `.directory`); **no unknown case**.
- `State.generated.swift:3745-3747` — `default:` → `throw DecodingError.dataCorruptedError(forKey: .discriminant, in: container, debugDescription: "Unknown Customization discriminant: \(discriminant)")`

**Observed behavior.** Decoding the unknown-`type` payload **throws** before any re-encode can happen. There is no passthrough path. (The sibling `ChildCustomization` union at `State.generated.swift:3786-3787` throws identically — same generated shape — so this is the codegen's default treatment of closed unions, not a one-off.)

**.NET contrast.** `CustomizationConverter` is built with `allowUnknown: true` — `clients/dotnet/src/AgentHostProtocol.Abstractions/Generated/State.generated.cs:4987`. Via the same `UnionConverter<T>` mechanism cited above, an unknown `Customization` decodes to a preserved `JsonElement` and re-encodes verbatim. So `path` and `extra` survive.

**Spec / forward-compat implication.** `allowUnknown` is a **per-union schema property** in the type model — some unions are closed (throw on unknown), some are open (preserve). `Customization` is declared open. The .NET generator honored that flag for this union; the Swift generator emits the same closed `default: throw` for **every** union regardless of the flag. So this is a **codegen-fidelity gap**, not merely a hand-authored design choice: the Swift type does not implement a schema-declared behavior that the .NET type does.

**Fairness note.** Unlike 002, there is a fair argument that *throwing on an unknown discriminant is a reasonable strictness default* for many unions — and indeed it is correct for closed unions like `ChangesetOperationTarget` (see Gap 3/4, where .NET also throws). The gap here is specifically that `Customization` is one of the unions the schema marks **open**, and Swift treats it as closed. The fix should be scoped to honoring the `allowUnknown` flag, **not** blanket-opening every union.

**Recommended fix direction.** Teach the Swift codegen to emit a passthrough case for unions whose schema sets `allowUnknown` (mirroring .NET's flag). Concretely: add an `unknown(AnyCodable)` case, change `default:` to decode the whole object into it instead of throwing, and re-emit it on encode. Leave closed unions (`allowUnknown: false`) throwing as they are.

---

## Gaps 3 & 4 — Fixtures 012 / 013: `ChangesetOperationTarget` drops the `kind` discriminant on re-encode

These two share one root cause, so they are documented together.

**Fixtures.**
- `012-changeset-target-resource.json`: `wireRaw {"kind":"resource","resource":"file:///a.txt"}`, `expect.kind == "resource"` (asserted against the **re-encoded** wire).
- `013-changeset-target-range.json`: `wireRaw {"kind":"range","resource":"file:///a.txt","range":{"start":2,"end":5}}`, `expect.kind == "range"`, plus `"roundTripStable": true`.

Contract: `ChangesetOperationTarget` dispatches on `kind`; the discriminator **and** the range survive re-encode. Note: both fixtures use **known** discriminants — this is **not** an unknown-variant test. The decode side is fine in both languages; the gap is purely on re-encode.

**Swift source.** `clients/swift/AgentHostProtocol/Sources/AgentHostProtocol/Generated/Commands.generated.swift`:
- The union decodes/dispatches correctly (closed union, throws only on unknown `kind`):
  - `Commands.generated.swift:1196-1223` — `enum ChangesetOperationTarget`, `discriminant = "kind"`, cases `.resource` / `.range`.
- The variant payload types declare `kind` as a **computed property** that is **excluded from `CodingKeys`**:
  - Resource: `Commands.generated.swift:1226` — `public var kind: String { "resource" }`; `CodingKeys` at `Commands.generated.swift:1235` — `case resource, side` (no `kind`).
  - Range: `Commands.generated.swift:1239` — `public var kind: String { "range" }`; `CodingKeys` at `Commands.generated.swift:1250` — `case resource, side, range` (no `kind`).

**Observed behavior.** A Swift `Codable` computed property with no backing storage that is **not listed in `CodingKeys`** is never written by the synthesized encoder. So `{"kind":"resource","resource":"file:///a.txt"}` re-encodes to `{"resource":"file:///a.txt"}` — **`kind` is gone**. `expect.kind` fails for both; 013's `roundTripStable` also fails because the second decode of the kind-less wire would throw (`kind` is required to dispatch the union) — the value is not a fixed point.

**.NET contrast.** The .NET payload types model `kind` as a **real, serialized property** with a default value:
- `clients/dotnet/src/AgentHostProtocol.Abstractions/Generated/Commands.generated.cs:1653-1654` — `[JsonPropertyName("kind")] public string Kind { get; set; } = "resource";`
- `Commands.generated.cs:1667-1668` — `[JsonPropertyName("kind")] public string Kind { get; set; } = "range";`

On encode, `UnionConverter<T>.Write` serializes by the runtime type so **every property including the discriminator is written** (`clients/dotnet/.../Json/UnionConverter.cs:97-100` — `JsonSerializer.Serialize(writer, inner, inner.GetType(), options)`). So `kind` round-trips.

Note for fairness: .NET's `ChangesetOperationTargetConverter` uses `allowUnknown: false` (`Commands.generated.cs:1702`) — i.e. .NET **also throws** on an unknown `kind`, identical to Swift. The two clients agree that this union is closed. The **only** divergence is `kind` re-emission on the known variants.

**Spec / forward-compat implication.** The wire contract for a discriminated union requires the discriminator on the wire — a `ChangesetOperationTarget` serialized without `kind` is not decodable by any conformant peer (it can't pick the variant). This is independent of forward-compat: it breaks **basic** round-trip and basic interop for a fully-known value. Of the five, these two are the least "design choice" and the most clearly latent bugs.

**Fairness note.** Modeling `kind` as a constant computed property is an understandable Swift idiom (the value is fixed per variant, so why store it). The bug is that the idiom silently opts the field **out of serialization**. There is no plausible reading under which omitting the discriminator from the wire is intended.

**Recommended fix direction.** Make `kind` participate in encoding for these variant structs — either (a) add `kind` to `CodingKeys` with a custom `encode(to:)` that writes the constant, or (b) drop the computed-property idiom and emit `kind` as a stored constant the synthesized encoder will include. The decode path already works and needs no change. Audit the Swift codegen for **other** variant types using the `var kind: String { "…" }` computed-discriminator pattern excluded from `CodingKeys` — any union member generated this way has the same latent drop. (`ChangesetOperationResourceTarget` and `...RangeTarget` are confirmed; a codegen-level fix would cover the whole class.)

---

## Gap 5 — Fixture 019: `SessionAddedParams` — **corpus framing is incorrect; this is not a Swift bug**

This fixture warrants the most care, because the task brief and the corpus describe it as an "unknown-key handling difference," and that framing is **misleading**. The actual mechanics point the other way.

**Fixture** (`019-channel-scoped-notification-uri.json`):
```json
"type": "SessionAddedParams",
"wire": { "channel": "ahp:/root", "session": "ahp-session:/s1" },
"expect": { "channel": "ahp:/root" },
"roundTripStable": true
```
The fixture's own description says it carries an extra `session` key (an "unknown key" that should drop) and asserts only `channel` survives.

**Swift source.** `clients/swift/AgentHostProtocol/Sources/AgentHostProtocol/Generated/Notifications.generated.swift:17-30`:
```swift
public struct SessionAddedParams: Codable, Sendable {
    public var channel: String
    public var summary: SessionSummary   // line 21 — NON-optional, required
    ...
}
```
There is no custom `init(from:)` — it uses Swift's synthesized `Codable`.

**Observed behavior.** The real failure is **not** about the unknown `session` key (Swift's synthesized decoder ignores unknown keys fine — see fixture 017, which passes for `SessionSummary`). The failure is that the wire payload has **no `summary`**, and Swift's `summary` is a **required, non-optional `SessionSummary`** (`Notifications.generated.swift:21`). So decode throws `DecodingError.keyNotFound("summary")` before any key-dropping or re-encode is reached.

**.NET contrast.** `clients/dotnet/src/AgentHostProtocol.Abstractions/Generated/Notifications.generated.cs:53` models `summary` as **nullable / non-validated**: `public SessionSummary Summary { get; set; } = null!;`. System.Text.Json does **not** enforce required members by default, so the missing `summary` simply leaves `Summary == null`, the unknown `session` key is ignored, and `channel` round-trips — making the fixture pass on .NET.

**Spec / schema basis — this is the decisive point.** `schema/notifications.schema.json:43-46` declares **both** `channel` and `summary` as **`required`** on `SessionAddedParams`. Therefore:
- The fixture's wire payload `{ channel, session }` (no `summary`) is **schema-invalid** — it omits a required field.
- **Swift's non-optional `summary` is the spec-faithful modeling.** Rejecting a payload that lacks a required field is correct conformance.
- **.NET's nullable `summary` is the deviation** — it silently accepts an off-spec payload (and would let a `null` summary propagate where the schema guarantees a value). The .NET `= null!` is a known System.Text.Json gap (no required-member enforcement), not a deliberate "be lenient" choice.

**Implication.** Listing 019 as a "Swift fidelity gap" alongside 002/003/012/013 conflates two opposite things. 002/003/012/013 are cases where Swift **loses data the spec wants preserved**. 019 is a case where Swift **correctly rejects a payload the spec forbids**, and the corpus + .NET are the lenient (off-spec) parties. The original `ChannelScopedNotification_CarriesUri` test that 019 descends from only ever asserted `channel`; it appears to have been ported onto a deliberately-degenerate `SessionAddedParams` payload (real `SessionAddedParams` always carries a `summary`), which is what trips the conformant client.

**Fairness note.** If the corpus's *intent* is purely "a channel-scoped notification's `channel` URI round-trips," the right vehicle is a payload that is **valid** for its declared `type` — i.e. include a (minimal) `summary` so the fixture exercises key-survival without depending on a missing-required-field path. As written, the fixture rewards the lenient implementation and penalizes the strict one, which inverts the spec.

**Recommended fix direction (two honest options — corpus-side, not Swift-side):**
1. **Preferred:** Fix the fixture, not the client. Give 019 a schema-valid `SessionAddedParams` (add a `summary` object) so it tests `channel` survival + unknown-key drop without requiring a missing required field. This keeps Swift's spec-correct strictness intact and removes 019 from the Swift gap list.
2. **Alternative (only if a "tolerate missing optional fields" semantics is actually desired protocol-wide):** that is a **spec change** — `summary` would have to be moved out of `required` in `schema/notifications.schema.json`, after which .NET's nullable becomes correct and Swift's type regenerates to optional. This is a protocol decision, not a client-fidelity fix, and should not be smuggled in via a client patch.

Do **not** "fix" this by making Swift's `summary` optional in isolation — that would make the reference client silently diverge from its own schema.

---

## Cross-cutting observations

1. **The infrastructure for the real fixes already exists in Swift.** `AnyCodable` (`clients/swift/.../AnyCodable.swift`) round-trips arbitrary JSON losslessly and is exactly the passthrough primitive 002 and 003 need; the Swift codegen simply doesn't wire it into union unknown-cases. This mirrors .NET's `JsonElement` passthrough (`AhpUnion`/`UnionConverter`). So 002 and 003 are "wire the existing primitive into the generator," not "invent a mechanism."

2. **`allowUnknown` is a schema-level open/closed flag the Swift generator does not consult.** .NET threads it through `UnionConverter(..., allowUnknown:)` per union (open: `StateAction`, `Customization`, `ResponsePart`, `ToolCallState`, …; closed: `ChangesetOperationTarget`, `ReconnectResult`). Swift emits `default: throw` for **every** union. Closing gaps 002 and 003 correctly means honoring this flag, not blanket-opening unions — `ChangesetOperationTarget` should stay closed (and .NET agrees, `allowUnknown: false`).

3. **The computed-discriminator-excluded-from-`CodingKeys` pattern (gaps 012/013) is a generator-class bug, not a two-struct bug.** Any Swift variant struct emitted as `var <disc>: String { "…" }` without that key in `CodingKeys` will drop its discriminator on encode. A codegen fix is higher-leverage than patching the two confirmed structs.

4. **The Swift harness already self-documents these as gaps and fails loudly if the set drifts.** `TypesRoundTripFixtureTests.swift:86-92` declares the gap set and `:161-164` asserts `gapHits == knownRepresentationalGaps`, so closing any gap forces the list to shrink (a green-on-fix tripwire). That is good hygiene — but note it currently treats **019 as a Swift gap**, which per Gap 5 above is a mischaracterization; the harness comment at `:70-74` correctly identifies the mechanism (non-optional `summary` → `keyNotFound`) but draws the wrong conclusion about which side is conformant.

---

## Suggested disposition for the corpus PR / issue

- **002, 003, 012, 013** → file as **Swift client wire-fidelity bugs** (encode side for 002/012/013; schema-flag-fidelity for 003). Keep them in the corpus as-is; they correctly catch real Swift defects. Reference this document's per-gap fix directions.
- **019** → **do not** file as a Swift bug. Either (a) repair the fixture to use a schema-valid `SessionAddedParams` (recommended), or (b) raise a separate **spec question** about whether `summary` should be required — and, separately, note that **.NET's nullable `summary` (`= null!`) is an independent .NET conformance gap** vs. `schema/notifications.schema.json` regardless of what Swift does.