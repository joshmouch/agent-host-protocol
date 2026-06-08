# Agent Guide — Agent Host Protocol Repo

Cross-cutting rules for AI coding agents working in this repository. Per-client
codegen conventions are in `clients/kotlin/AGENTS.md`,
`clients/swift/AGENTS.md`, `clients/go/AGENTS.md`, and
`clients/dotnet/AGENTS.md`. Editorial rules
for protocol types are in
`.github/instructions/general-instructions.instructions.md`. Release mechanics
are in [`RELEASING.md`](RELEASING.md).

## Updating CHANGELOGs

This repo ships seven independently-versioned artifacts (the spec plus
the Rust / Kotlin / Swift / TypeScript / Go / .NET clients), each with its
own `CHANGELOG.md` in Keep-a-Changelog format. The publish workflows
refuse to release a tag whose matching `## [X.Y.Z]` heading is
missing, so every user-visible change should land its CHANGELOG bullet
in the same PR as the code.

### When to add an entry

Add a one-line bullet under `## [Unreleased]` whenever your change is
**user-visible**:

- A new, removed, renamed, or behaviourally-changed action, command, state
  field, error, notification, or version constant in `types/`.
- A new, removed, or behaviourally-changed public API in one of the
  `clients/<lang>/` source trees (constructor signatures, exported
  functions/types, transport options, reducer outputs, etc.).
- A bug fix that changes observable behaviour for a consumer of the spec or
  any client.
- A security-relevant change (always also add a `### Security` subsection).

**Skip the CHANGELOG** when the change is purely:

- Edits under `**/generated/**` (those mirror a `types/` change that should
  have its own entry).
- Docs in `docs/`, `README.md`, comments, AGENTS.md, CONTRIBUTING.md.
- Tests, CI, lint config, formatting, internal refactors with no observable
  effect.

### Which CHANGELOG(s) to update

Map source paths to changelogs:

| Source path touched | CHANGELOG(s) that need an entry |
| --- | --- |
| `types/**` (protocol surface) | Root `CHANGELOG.md` **and** every `clients/<lang>/CHANGELOG.md` (a spec change ripples to every client). |
| `clients/rust/**` (non-generated) | `clients/rust/CHANGELOG.md` only. |
| `clients/kotlin/**` (non-generated) | `clients/kotlin/CHANGELOG.md` only. |
| `clients/swift/**` (non-generated) | `clients/swift/CHANGELOG.md` only. |
| `clients/typescript/**` (non-generated) | `clients/typescript/CHANGELOG.md` only. |
| `clients/go/**` (non-generated) | `clients/go/CHANGELOG.md` only. |
| `clients/dotnet/**` (non-generated) | `clients/dotnet/CHANGELOG.md` only. |
| `schema/**` | Root `CHANGELOG.md` (the schema is a spec output). |
| `scripts/generate*.ts` that changes any client's generated output | Every affected client's `CHANGELOG.md`. |

### Format

Use the standard Keep-a-Changelog subsection headers — `Added`, `Changed`,
`Deprecated`, `Removed`, `Fixed`, `Security` — under `## [Unreleased]`.
Create the subsection if it doesn't already exist. One bullet per change.

```markdown
## [Unreleased]

### Added
- `session/cancelTurn` action for client-initiated turn cancellation.

### Changed
- `AhpClient.connect` now rejects with `AhpProtocolError` (not
  `Error`) on negotiation failure.
```

Do **not** invent a `## [X.Y.Z]` heading — that's reserved for release time
and is added by the maintainer cutting the release per
[`RELEASING.md`](RELEASING.md).
