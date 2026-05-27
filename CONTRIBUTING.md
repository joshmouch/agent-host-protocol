# Contributing to the Agent Host Protocol

Thanks for your interest in contributing to AHP. This document covers the
mechanics of working in this repository — for the protocol design rationale,
see the [specification](docs/specification/) and the
[versioning policy](docs/specification/versioning.md).

> **Code of conduct:** participation is governed by the
> [Microsoft Open Source Code of Conduct](CODE_OF_CONDUCT.md).

## Repository layout

This is a polyglot repo. The TypeScript types under `types/` are the canonical
source of truth; everything else is generated from them or hand-maintained
against them.

| Path | What lives here |
| --- | --- |
| `types/` | Canonical TypeScript protocol types, reducers, version registry. |
| `schema/` | JSON Schema files generated from `types/`. |
| `docs/` | VitePress documentation source. |
| `scripts/` | TypeScript code-gen scripts (one per target language + shared helpers). |
| `clients/rust/` | `ahp-types`, `ahp`, `ahp-ws` Cargo workspace. |
| `clients/kotlin/` | Kotlin/JVM library (`com.microsoft.agenthostprotocol:agent-host-protocol`). |
| `clients/swift/` | Swift package (consumed by SwiftPM at the repo root). |
| `clients/typescript/` | npm package `@microsoft/agent-host-protocol`. |
| `.github/workflows/` | CI and per-artifact publish pipelines. |

## Local dev loop

```bash
npm install                      # install root tooling
npm run generate                 # regenerate every client + schemas
npm test                         # typecheck + lint + verify:release-metadata + reducer tests
```

Per-client builds (run only what's relevant to your change):

```bash
cd clients/typescript && npm ci && npm test && npm run build
cd clients/rust && cargo test --workspace
cd clients/kotlin && ./gradlew build
swift build && swift test        # Swift uses the root Package.swift
```

## Release model

Every shippable artifact in this repo is versioned independently using its
ecosystem's native SemVer. The protocol specification is a fifth artifact
with its own release cadence. Each client release advertises which protocol
versions it supports via a generated `SUPPORTED_PROTOCOL_VERSIONS` constant
and a checked-in `clients/<lang>/release-metadata.json`.

See [`docs/specification/versioning.md`](docs/specification/versioning.md) for
the design rationale; this section covers the mechanics.

### Tag conventions

| Artifact   | Tag pattern         | Workflow                          | Registry / discovery |
| ---------- | ------------------- | --------------------------------- | -------------------- |
| Spec       | `spec/vX.Y.Z`       | `.github/workflows/publish-spec.yml` | GitHub Release with schema assets. |
| Rust       | `rust/vX.Y.Z`       | `.github/workflows/publish-rust.yml` | crates.io (`ahp-types`, `ahp`, `ahp-ws`). |
| Kotlin     | `kotlin/vX.Y.Z`     | `.github/workflows/publish-kotlin.yml` | Maven Central (`com.microsoft.agenthostprotocol:agent-host-protocol`). |
| TypeScript | `typescript/vX.Y.Z` | `.github/workflows/publish-typescript.yml` (added in [#156](https://github.com/microsoft/agent-host-protocol/pull/156)). | npm (`@microsoft/agent-host-protocol`). |
| Swift      | `vX.Y.Z` (bare)     | `.github/workflows/publish-swift.yml` | SwiftPM resolves the tag directly. |

> **Why Swift gets the bare semver tag namespace:** SwiftPM only resolves
> packages by matching plain `X.Y.Z` / `vX.Y.Z` git tags at the manifest's
> repo root. Path-prefixed tags like `swift/v0.2.0` are invisible to it. Bare
> semver tags at this repo's root are therefore reserved for Swift releases.

### Per-client release flow

#### Rust (`rust/vX.Y.Z`)

1. Bump `[workspace.package].version` in `clients/rust/Cargo.toml` (this
   bumps all three crates — `ahp-types`, `ahp`, `ahp-ws` — together; the
   workspace is intentionally version-locked).
2. Update cross-crate `version = "0.X.Y"` pins in
   `[workspace.dependencies]` and any per-crate dependency declarations.
3. Run `npm run generate:metadata` and commit the regenerated
   `clients/rust/release-metadata.json`.
4. Rotate `clients/rust/CHANGELOG.md`: move the `## [Unreleased]` section to
   `## [X.Y.Z] — YYYY-MM-DD` with an `Implements AHP <version>` line.
5. Merge to `main`.
6. Tag: `git tag rust/v0.X.Y && git push origin rust/v0.X.Y`.
7. `publish-rust.yml` validates, then publishes `ahp-types`, `ahp`, and
   `ahp-ws` to crates.io in dependency order.

#### Kotlin (`kotlin/vX.Y.Z`)

1. Bump `VERSION_NAME` in `clients/kotlin/gradle.properties`. Drop any
   `-SNAPSHOT` suffix (the publish workflow rejects snapshot tags).
2. Run `npm run generate:metadata` and commit the regenerated
   `clients/kotlin/release-metadata.json`.
3. Rotate `clients/kotlin/CHANGELOG.md`.
4. Merge to `main`.
5. Tag: `git tag kotlin/v0.X.Y && git push origin kotlin/v0.X.Y`.
6. `publish-kotlin.yml` validates, then publishes to Maven Central via the
   Vanniktech `publishAndReleaseToMavenCentral` task. No manual Sonatype UI
   interaction is required.
7. Bump `VERSION_NAME` back to the next `-SNAPSHOT` for ongoing development.

#### TypeScript (`typescript/vX.Y.Z`)

> The TypeScript publish workflow lands in
> [#156](https://github.com/microsoft/agent-host-protocol/pull/156). Until
> that merges, the steps below describe the intended flow.

1. Bump `version` in `clients/typescript/package.json`.
2. `cd clients/typescript && npm install` to refresh the lockfile.
3. Run `npm run generate:metadata` from the repo root and commit the
   regenerated `clients/typescript/release-metadata.json`.
4. Rotate `clients/typescript/CHANGELOG.md`.
5. Merge to `main`.
6. Tag: `git tag typescript/v0.X.Y && git push origin typescript/v0.X.Y`.
7. `publish-typescript.yml` validates, then publishes to npm with
   provenance.

> **Cross-cutting addition for #156:** the typescript publish workflow
> should also run `npm run verify:release-metadata` and a CHANGELOG-entry
> check, mirroring the equivalent steps already wired into
> `publish-rust.yml` and `publish-kotlin.yml`. Add these in a follow-up to
> #156 (or in a rebase if it lands after this PR).

#### Swift (`vX.Y.Z`, bare)

1. Update `clients/swift/VERSION` to the new bare semver string (no
   leading `v`, no `-SNAPSHOT`).
2. Run `npm run generate:metadata` and commit the regenerated
   `clients/swift/release-metadata.json`.
3. Rotate `clients/swift/CHANGELOG.md`.
4. Merge to `main`.
5. Tag: `git tag v0.X.Y && git push origin v0.X.Y`. **Note the absence of
   any prefix** — this is the one place in the repo where bare semver tags
   are correct.
6. `publish-swift.yml` validates the tag against `clients/swift/VERSION`,
   builds and tests the Swift package on macOS, and publishes a GitHub
   Release. SwiftPM consumers resolve the tag directly; no registry push
   happens.

#### Spec (`spec/vX.Y.Z`)

1. Bump `PROTOCOL_VERSION` in `types/version/registry.ts` (and, if the
   release is also adding the version to the supported list,
   `SUPPORTED_PROTOCOL_VERSIONS`).
2. Update `ACTION_INTRODUCED_IN` / `NOTIFICATION_INTRODUCED_IN` for any
   new symbols.
3. Run `npm run generate` to refresh schemas, generated client sources,
   and metadata.
4. Rotate the root `CHANGELOG.md`.
5. Merge to `main`.
6. Tag: `git tag spec/v0.X.Y && git push origin spec/v0.X.Y`.
7. `publish-spec.yml` validates, regenerates the JSON schemas from the
   tagged commit, captures a `registry-snapshot.json` of the introduced-in
   maps, and creates a GitHub Release with all of the above as assets.

### What CI guards against

| Drift caught by | How |
| --- | --- |
| `Version.generated.{rs,kt,swift}` ↔ `types/version/registry.ts` | Per-language CI job re-runs `npm run generate:<lang>` and fails on diff. |
| `release-metadata.json` ↔ native manifest + registry | `npm run verify:release-metadata` (also gated on every publish workflow). |
| Tag ↔ manifest version | Every publish workflow's "Verify tag matches" step. |
| Missing CHANGELOG entry at publish time | Every publish workflow's `grep -qE '^## \[<version>\]' CHANGELOG.md` step. |

### Required infrastructure (one-time setup)

These environments and secrets are required for the publish workflows to
function. They are set per-repo by a maintainer with admin access.

| Environment / secret | Used by | Purpose |
| --- | --- | --- |
| `crates-io` environment, `CARGO_REGISTRY_TOKEN` secret | `publish-rust.yml` | Authenticates `cargo publish` for `ahp-types` / `ahp` / `ahp-ws`. |
| `maven-central` environment, `MAVEN_CENTRAL_USERNAME` / `MAVEN_CENTRAL_PASSWORD` / `SIGNING_IN_MEMORY_KEY` / `SIGNING_IN_MEMORY_KEY_PASSWORD` | `publish-kotlin.yml` | Sonatype Central Portal credentials + PGP key for signed artifact publish. |
| `npm` environment, `NPM_TOKEN` secret | `publish-typescript.yml` (PR #156) | Authenticates `npm publish --provenance` for `@microsoft/agent-host-protocol`. |
| (none required) | `publish-swift.yml`, `publish-spec.yml` | Both use the default `GITHUB_TOKEN` to create GitHub Releases. No external registry credentials needed. |

## Code-style and review

Editor / lint / typecheck configuration lives in this repo's `eslint.config.mjs`,
`tsconfig.json`, and (per-client) the equivalent files. Run `npm test` before
opening a PR; CI runs the same checks plus per-language builds.

When iterating on the protocol surface in `types/`, see
[`.github/instructions/general-instructions.instructions.md`](.github/instructions/general-instructions.instructions.md)
for the project's editorial rules on type changes.

For language-specific code-gen conventions, see the `AGENTS.md` file in each
client directory (`clients/kotlin/AGENTS.md`, `clients/swift/AGENTS.md`).
