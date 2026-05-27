# Changelog — `AgentHostProtocol` Swift Package

All notable changes to the Swift package (`AgentHostProtocol` +
`AgentHostProtocolClient` products) are documented here. See
[`../../CHANGELOG.md`](../../CHANGELOG.md) for the protocol spec changelog
and [`release-metadata.json`](release-metadata.json) for the machine-readable
mapping between the current source tree and protocol versions.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the package follows [SemVer](https://semver.org).

SwiftPM resolves packages by matching plain `vX.Y.Z` git tags at the repo
root, so Swift releases use the bare semver tag namespace (no `swift/`
prefix). The `publish-swift.yml` workflow refuses to publish a `vX.Y.Z` tag
whose matching `## [X.Y.Z]` heading is missing from this file, and verifies
the tag matches the version pinned in [`VERSION`](VERSION).

## [Unreleased]

Implements AHP `0.2.0`.

[`VERSION`](VERSION) currently pins `0.2.0`; the first tag-driven Swift
release will use the bare `v0.2.0` tag.

## [0.2.0] — Unreleased

Implements AHP `0.2.0`.

First versioned Swift Package Manager release. Includes:

- `AgentHostProtocol` product — generated wire types, actions, commands,
  notifications, errors, reducers (`AHPRootReducer`, `AHPSessionReducer`,
  `AHPTerminalReducer`, `AHPChangesetReducer`, `NativeReducer`).
- `AgentHostProtocolClient` product — single-host `AHPClient`, multi-host
  `MultiHostClient`, `AHPStateMirror` / `MultiHostStateMirror`, transports
  (`URLSessionWebSocketTransport`, `NWConnectionWebSocketTransport`,
  `InMemoryTransport`), and persistent client-ID stores.
- Generated `PROTOCOL_VERSION` and `SUPPORTED_PROTOCOL_VERSIONS` constants
  on the `AgentHostProtocol` module.
