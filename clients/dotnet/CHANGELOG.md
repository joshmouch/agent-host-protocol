# Changelog

All notable changes to the .NET client (`Microsoft.AgentHostProtocol*`
NuGet packages) are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

This client tracks the Agent Host Protocol spec on its own version line; see
[`release-metadata.json`](release-metadata.json) for the protocol versions
this release negotiates.

## [Unreleased]

## [0.1.0]

Initial release of the .NET client. Implements AHP 0.3.0.

### Added

- **`Microsoft.AgentHostProtocol.Abstractions`** — the wire types generated
  from the canonical TypeScript protocol definitions (state, actions,
  commands, notifications, JSON-RPC messages, errors, and version
  constants), the `StringOrMarkdown` helper, the `AhpUnion` discriminated-
  union support and `WireEnumConverter`, and the `ITransport` /
  `IAhpSerializer` interface seams.
- **`Microsoft.AgentHostProtocol`** — the async JSON-RPC `AhpClient`, the
  pure state reducers (`Reducers.ApplyToRoot` / `ApplyToSession` /
  `ApplyToTerminal` / `ApplyToChangeset`), the default
  `SystemTextJsonAhpSerializer`, the per-URI subscription fan-out, and the
  `MultiHostClient` runtime under `Microsoft.AgentHostProtocol.Hosts`.
- **`Microsoft.AgentHostProtocol.WebSockets`** — a `ClientWebSocket`-based
  `ITransport` implementation.
