# Contributing to the Agent Host Protocol

Thanks for your interest in contributing to AHP. This document covers the
mechanics of working in this repository as a contributor — for the
protocol design rationale see the [specification](docs/specification/) and
the [versioning policy](docs/specification/versioning.md), and for the
mechanics of cutting a release see [`RELEASING.md`](RELEASING.md).

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

## Releases

Release mechanics — tag conventions, per-client publish flows, CI guards,
and the one-time admin setup for each environment — live in
[`RELEASING.md`](RELEASING.md). For the protocol-level versioning policy,
see [`docs/specification/versioning.md`](docs/specification/versioning.md).

## Updating CHANGELOGs

This repo ships five independently-versioned artifacts (spec + four clients),
each with its own `CHANGELOG.md` in [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
format. The publish workflows refuse to release a tag whose matching
`## [X.Y.Z]` heading is missing, so every user-visible change should land its
CHANGELOG bullet in the same PR as the code.

**Add a one-line bullet under `## [Unreleased]`** when your change is
user-visible: a new / removed / renamed / behaviourally-changed action,
command, state field, error, notification, version constant, or public client
API; an observable bug fix; or anything security-relevant. **Skip the
CHANGELOG** for generated code (`**/generated/**`), docs, tests, CI, lint
config, formatting, or internal refactors with no observable effect.

Path → changelog map:

| Source path touched | CHANGELOG(s) that need an entry |
| --- | --- |
| `types/**` (protocol surface) | Root `CHANGELOG.md` **and** every `clients/<lang>/CHANGELOG.md` (a spec change ripples to every client). |
| `clients/<lang>/**` (non-generated) | That client's `CHANGELOG.md` only. |
| `schema/**` | Root `CHANGELOG.md` (the schema is a spec output). |
| `scripts/generate*.ts` that changes any client's generated output | Every affected client's `CHANGELOG.md`. |

Use the standard Keep-a-Changelog subsection headers — `Added`, `Changed`,
`Deprecated`, `Removed`, `Fixed`, `Security` — under `## [Unreleased]`. One
bullet per change. Don't invent a `## [X.Y.Z]` heading — that's reserved for
release time per [`RELEASING.md`](RELEASING.md).

This rule is also encoded in [`AGENTS.md`](AGENTS.md) so AI coding agents
working in the repo follow the same convention.

## Code-style and review

Editor / lint / typecheck configuration lives in this repo's `eslint.config.mjs`,
`tsconfig.json`, and (per-client) the equivalent files. Run `npm test` before
opening a PR; CI runs the same checks plus per-language builds.

When iterating on the protocol surface in `types/`, see
[`.github/instructions/general-instructions.instructions.md`](.github/instructions/general-instructions.instructions.md)
for the project's editorial rules on type changes.

For language-specific code-gen conventions, see the `AGENTS.md` file in each
client directory (`clients/kotlin/AGENTS.md`, `clients/swift/AGENTS.md`).
