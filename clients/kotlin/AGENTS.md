# Kotlin Client — Agent Guide

## Overview

This directory contains the **Kotlin/JVM** client library for the Agent Host Protocol (AHP), distributed via Maven Central as `com.microsoft.agenthostprotocol:agent-host-protocol`.

The library targets pure Kotlin/JVM (Java 8 bytecode, JDK 17 toolchain) so it works for Android consumers, server-side JVM consumers, and KMP/JVM target consumers without any Android-specific dependencies. Only `kotlinx-serialization-json` is on the classpath at runtime.

## Code Generation

Types in `src/main/kotlin/com/microsoft/agenthostprotocol/generated/` are **auto-generated** from the TypeScript definitions in `types/`. Do not edit these files directly. Generated files are committed to source control so the package is consumable via Maven Central without a code-generation toolchain.

To regenerate after protocol changes:

```bash
npm run generate:kotlin    # runs: tsx scripts/generate.ts --kotlin
```

Generated files: `State`, `Commands`, `Actions`, `Errors`, `Messages`, `Notifications` — all suffixed `.generated.kt`.

CI verifies the committed generated files match the output of `npm run generate:kotlin` and fails on drift.

## Library structure

- `src/main/kotlin/com/microsoft/agenthostprotocol/Ahp.kt` — Hand-maintained entry point. Exposes the configured `kotlinx.serialization.json.Json` instance (`Ahp.json`) that consumers MUST use to encode/decode protocol messages. The custom `KSerializer`s for discriminated unions require a JSON-aware encoder/decoder, so generic `Json` instances may not work.
- `src/main/kotlin/com/microsoft/agenthostprotocol/generated/` — Auto-generated wire types.
- `build.gradle.kts` — Gradle build config. Sets `jvmTarget = JVM_1_8` (Android-friendly) with a JDK 17 toolchain. Configures the Vanniktech `maven-publish` plugin for Sonatype Central Portal publishing.
- `gradle.properties` — Source of truth for the artifact's Maven coordinates (`GROUP`, `VERSION_NAME`) and POM metadata.
- `gradle/libs.versions.toml` — Version catalog (Kotlin, kotlinx.serialization, JUnit, Vanniktech plugin).

## Type mapping (TS → Kotlin)

| TypeScript                    | Kotlin                                                            |
| ----------------------------- | ----------------------------------------------------------------- |
| `string`                      | `String`                                                          |
| `number`                      | `Long` (TS contract: 64-bit ints)                                 |
| `number` w/ `@format float`   | `Double`                                                          |
| `boolean`                     | `Boolean`                                                         |
| `unknown` / `object`          | `kotlinx.serialization.json.JsonElement`                          |
| `T \| null`                   | `T?`                                                              |
| `T?` field / `T \| undefined` | `T? = null`                                                       |
| `T[]` / `Array<T>`            | `List<T>`                                                         |
| `Record<string, T>`           | `Map<String, T>`                                                  |
| `Partial<T>`                  | `PartialT` data class with all fields nullable                    |
| `enum E { A = "a" }`          | `@Serializable enum class E { @SerialName("a") A }`               |
| Bitset enum (JSDoc "Bitset")  | `@JvmInline value class` over `Int` w/ companion-object constants |
| Interface struct              | `@Serializable data class`                                        |
| Discriminated union           | sealed interface + custom `KSerializer` (mirrors Swift)           |
| `URI`                         | `typealias URI = String`                                          |
| `StringOrMarkdown`            | sealed interface w/ custom serializer                             |
| Recursive struct              | data class (heap-allocated by default)                            |
| `_meta` field                 | Kotlin `meta` + `@SerialName("_meta")`                            |
| `snake_case` wire field       | camelCase + `@SerialName("snake_case")`                           |

### Why custom union serializers (and not `@JsonClassDiscriminator`)

`@JsonClassDiscriminator` is the idiomatic kotlinx-serialization way to model discriminated unions, but it forbids the discriminator field from existing on the variant data class. Since our TS variant interfaces include their discriminator (e.g. `MarkdownResponsePart.kind = 'markdown'`), generating `@JsonClassDiscriminator`-based unions would require cross-cutting field filtering everywhere those interfaces appear. Mirroring Swift's manual sealed-union serializer keeps the variant data classes 1:1 with their TS counterparts.

A consequence: **always use `Ahp.json` (or a `Json` instance with `classDiscriminator` set to a sentinel value)** when encoding/decoding. The default kotlinx `"type"` discriminator collides with real `type` fields in our schema.

### Notifications are routed by JSON-RPC method, not by an embedded discriminator

Since the v0.2 channels reorg, server → client notifications are dispatched on the JSON-RPC `method` name (e.g. `root/sessionAdded`, `auth/required`, `otlp/exportLogs`) rather than on a `type` discriminator field. The generator therefore emits each notification payload as a plain `*Params` data class (no sealed-union wrapper). Consumers extract `method` from the JSON-RPC envelope themselves and decode the matching params type. The `action` notification is special-cased: its params are always `ActionEnvelope`.

### Multi-value discriminators

`SessionInputQuestion` is the one union where two wire `kind` values map to the same Kotlin data class:

- `kind: "number"` → `SessionInputQuestionNumber(SessionInputNumberQuestion(kind = NUMBER, ...))`
- `kind: "integer"` → `SessionInputQuestionNumber(SessionInputNumberQuestion(kind = INTEGER, ...))`

The custom serializer handles both wire values during decode; encode preserves whichever discriminator was set on the data class. Tests in `DiscriminatedUnionTest.kt` cover this case.

### Hand-rolled `ChangesetOperationTarget` union

The TS source models `ChangesetOperationTarget` as a discriminated union over two inline variant shapes that aren't exported as their own interfaces. The generator emits the whole subgraph — the sealed `ChangesetOperationTarget`, the two variant data classes (`ChangesetOperationResourceTarget` and `ChangesetOperationRangeTarget`), the `ChangesetOperationTargetRange` helper, and the custom serializer — by hand from `generateChangesetOperationTargetKotlin()` so the Kotlin wire surface stays aligned with the Swift and Rust clients.

### Bitset enums

`SessionStatus` is currently the only bitset enum in the protocol. It's emitted as a `@JvmInline value class` over `Int` so that **unknown future flags survive a decode/encode round-trip** without being silently dropped. Use `or`/`and`/`in` for combinator/containment ops:

```kotlin
val combined = SessionStatus.IDLE or SessionStatus.IS_READ
SessionStatus.IDLE in combined   // true
```

## Distribution

Artifacts are published to Maven Central (Sonatype Central Portal) via [`gradle-maven-publish-plugin`](https://github.com/vanniktech/gradle-maven-publish-plugin) v0.36+ on `kotlin/v*` git tags.

The release pipeline (`.github/workflows/publish-kotlin.yml`):

1. **`validate` job** — re-runs `npm run generate:kotlin` (fails on diff) and `./gradlew check`.
2. **`publish` job** — verifies the git tag matches `gradle.properties` `VERSION_NAME` (read via `./gradlew properties -q`), then runs `./gradlew publishAndReleaseToMavenCentral`.

### Required repository secrets (`maven-central` GitHub environment)

| Secret                          | Purpose                                                                              |
| ------------------------------- | ------------------------------------------------------------------------------------ |
| `MAVEN_CENTRAL_USERNAME`        | Sonatype Central Portal user-token username.                                         |
| `MAVEN_CENTRAL_PASSWORD`        | Sonatype Central Portal user-token password.                                         |
| `SIGNING_IN_MEMORY_KEY`         | ASCII-armored PGP private key (entire `-----BEGIN PGP PRIVATE KEY BLOCK-----` body). |
| `SIGNING_IN_MEMORY_KEY_PASSWORD`| Passphrase for the PGP key (omit secret if the key is unprotected).                  |

The Vanniktech plugin reads these from `ORG_GRADLE_PROJECT_*`-prefixed env vars at publish time, which the workflow sets from the secrets above.

### Cutting a release

See [`CONTRIBUTING.md`](../../CONTRIBUTING.md) for the full release flow.
Summary, scoped to Kotlin:

1. Bump `VERSION_NAME` in `clients/kotlin/gradle.properties` (drop `-SNAPSHOT` for a public release; the version should match the `PROTOCOL_VERSION` in `types/version/registry.ts` when shipping a protocol-aligned drop, e.g. `0.2.0`).
2. Run `npm run generate:metadata` and commit the regenerated `clients/kotlin/release-metadata.json`.
3. Rotate the `## [Unreleased]` section of `clients/kotlin/CHANGELOG.md` to `## [X.Y.Z] — YYYY-MM-DD` with an `Implements AHP <version>` line. The publish workflow fails if no `## [X.Y.Z]` heading exists for the tag version.
4. Commit, merge to `main`.
5. Tag the merge commit using `kotlin/v` + the same version (e.g. `git tag kotlin/v0.2.0 && git push origin kotlin/v0.2.0`). The publish workflow rejects any mismatch between the tag and `VERSION_NAME`, and refuses `*-SNAPSHOT` tags outright.
6. The publish workflow runs and pushes to Maven Central. With `automaticRelease = true` set in `mavenPublishing { ... }`, no manual Sonatype UI interaction is required.
7. Bump `VERSION_NAME` back to the next `-SNAPSHOT` for ongoing development.

## Building and testing locally

```bash
cd clients/kotlin
./gradlew build           # compile + tests + assemble
./gradlew test            # tests only
./gradlew publishToMavenLocal   # smoke-test publishing (skips signing if no key configured)
```

Requires a JDK 17+ on `JAVA_HOME`. Gradle wrapper handles everything else.

## Out of scope (intentional)

This package currently ships **wire types only**. The following are deferred to follow-up PRs:

- Reducer logic (analog of Swift's `AHPRootReducer` / `AHPSessionReducer`)
- Example Android app (analog of Swift's `AHPClient`)
- WebSocket transport / async client
- Kotlin Multiplatform (KMP) build — JVM target is sufficient for current Android consumers
