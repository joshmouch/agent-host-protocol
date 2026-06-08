> **STATUS — RESOLVED (2026-06-05).** Every gap below was FOUND by wiring this shared round-trip corpus into all six clients, and FIXED at the source:
> - **Swift (4 bugs)** — unknown `StateAction` empty-encode; `Customization` throws on unknown; `ChangesetOperationTarget` drops `kind` (x2): fixed in `scripts/generate-swift.ts` + regenerated (`ffb4a7d`). `swift test` 97/97.
> - **.NET conformance** — `SessionAddedParams.summary` modeled nullable though schema-required: schema-required nested objects now emit C# `required`; fixture 019 repaired to a valid payload (`e9d1a2d`). 315/315 both TFMs.
> - **Rust** — `SessionStatus` bitset was a closed enum that LOST unknown bits: fixed (`2635980`). `cargo test` green.
> - **Kotlin** — `SessionStatus` uint32 backed by a SIGNED Int (truncation): fixed (`8b4beab`). `gradlew check` 240/240.
> - **Go** — clean (corpus round-trips 22/23; 019 the only skip; no Go bug).
> - **TypeScript** — no runtime decoder (compile-time types only); one representational gap (017 unknown-wire-keys) recorded with a drift tripwire.
>
> The corpus surfaced + fixed real fidelity bugs in **4 of 6** reference clients. Historical analysis retained below for the upstream PR narrative.

---

