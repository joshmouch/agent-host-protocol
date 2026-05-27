# Changelog — `@microsoft/agent-host-protocol` (TypeScript)

All notable changes to the TypeScript client package are documented here. See
[`../../CHANGELOG.md`](../../CHANGELOG.md) for the protocol spec changelog
and [`release-metadata.json`](release-metadata.json) for the machine-readable
mapping between the current source tree and protocol versions.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the package follows [SemVer](https://semver.org).

The TypeScript client publishes through an Azure DevOps pipeline
(`pipeline.yml`) — not a `typescript/v*` git tag. The repo's
`npm run verify:changelog` script is wired into GitHub Actions CI and
into the ADO pipeline's `buildSteps` + `testSteps`, so a `package.json`
version that doesn't have a matching `## [X.Y.Z]` heading here will fail
both on PR review and at publish time.

## [Unreleased]

Implements AHP `0.2.0`.

## [0.2.0] — Unreleased

Implements AHP `0.2.0`.

Initial npm publish of `@microsoft/agent-host-protocol`. Includes:

- Default entry — wire types, actions, commands, reducers, version
  constants (`PROTOCOL_VERSION`, `SUPPORTED_PROTOCOL_VERSIONS`). Zero I/O.
- `/client` subpath — `AhpClient`, `Subscription`, `AhpStateMirror`,
  `AhpTransport` interface, `InMemoryTransport`, error taxonomy.
- `/ws` subpath — `WebSocketTransport` built on the global `WebSocket`.
