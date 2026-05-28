# Changelog — `@microsoft/agent-host-protocol` (TypeScript)

All notable changes to the TypeScript client package are documented here. See
[`../../CHANGELOG.md`](../../CHANGELOG.md) for the protocol spec changelog
and [`release-metadata.json`](release-metadata.json) for the machine-readable
mapping between the current source tree and protocol versions.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the package follows [SemVer](https://semver.org).

The `publish-typescript.yml` workflow refuses to publish a `typescript/vX.Y.Z`
tag whose matching `## [X.Y.Z]` heading is missing from this file. The
workflow validates the tag, runs `npm run verify:release-metadata` and
`npm run verify:changelog`, and only then triggers the Azure DevOps
pipeline at `pipeline.yml` (via the Pipelines REST API with
`publishPackage: true`) to perform the actual signed npm publish.

The ADO pipeline can also be triggered manually from the ADO UI as a
hotfix escape hatch.

## [Unreleased]

Implements AHP `0.2.0`.

- Wire types for the extended `resource*` family: `resourceResolve`,
  `resourceMkdir`, `createResourceWatch`, and the new `ahp-resource-watch:/`
  channel with the `resourceWatch/changed` action. `ResourceWriteParams`
  gains `mode` / `position` / `ifMatch`. New `Conflict` (`-32011`) error
  code. The whole content-bearing `resource*` family is now bidirectional
  (it appears in both `CommandMap` and `ServerCommandMap`).

## [0.2.0] — Unreleased

Implements AHP `0.2.0`.

Initial npm publish of `@microsoft/agent-host-protocol`. Includes:

- Default entry — wire types, actions, commands, reducers, version
  constants (`PROTOCOL_VERSION`, `SUPPORTED_PROTOCOL_VERSIONS`). Zero I/O.
- `/client` subpath — `AhpClient`, `Subscription`, `AhpStateMirror`,
  `AhpTransport` interface, `InMemoryTransport`, error taxonomy.
- `/ws` subpath — `WebSocketTransport` built on the global `WebSocket`.
