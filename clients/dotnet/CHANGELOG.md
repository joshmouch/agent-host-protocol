# Changelog

All notable changes to the .NET client (`Microsoft.AgentHostProtocol*`
NuGet packages) are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

This client tracks the Agent Host Protocol spec on its own version line; see
[`release-metadata.json`](release-metadata.json) for the protocol versions
this release negotiates.

## [Unreleased]

### Added

- `RootState` now exposes an optional `_meta` property bag
  (`Dictionary<string, JsonElement>? Meta`) for implementation-defined
  agent-host metadata, such as a well-known `hostBuild` key carrying the
  host's build version/commit/date.

## [0.3.0]

Implements AHP 0.3.0.

### Added

- `McpServerCustomization` now exposes the full MCP lifecycle: `Enabled`,
  the discriminated `McpServerState` union
  (`Starting`/`Ready`/`AuthRequired`/`Error`/`Stopped`), optional
  `Channel` URI for the `mcp://` side-channel, and an optional `McpApp`
  block carrying `AhpMcpUiHostCapabilities` for MCP Apps.
- `McpServerAuthRequiredState` variant carries `ProtectedResourceMetadata`
  plus `Reason` / `RequiredScopes` / `Description` so the existing
  `authenticate` command can drive per-server auth.
- The top-level `Customization` union now includes `McpServerCustomization`
  — hosts MAY surface bare MCP servers directly rather than only inside a
  plugin or directory.
- `SessionMcpServerStateChangedAction` and the matching
  `Reducers.ApplyToSession` case — a narrow upsert of `State` + `Channel`
  on an existing MCP server customization (located by id at the top level
  or among a container's children; a no-op for an unknown id or a non-MCP
  customization type).
- `ClientCapabilities` on `InitializeParams.Capabilities`, with the
  `McpApps` capability.
- `ChangeKind` field on `Changeset` (well-known values: `session`,
  `branch`, `uncommitted`, `turn`, `compare-turns`; unrecognized values
  are preserved on the wire and fall back to a client default).
- `Status` and `Error` on `ChangesetOperation`, and the
  `changeset/operationStatusChanged` action, tracking the
  `idle → running → error` lifecycle of a changeset operation.
- `_meta` provider-metadata field on `AgentCustomization`.
- Optional `Changes` field on `SessionSummary` (`ChangesSummary` with
  optional `Additions`, `Deletions`, and `Files` counts) summarising a
  session's file-change footprint.

### Changed

- `ToolCallBase.ToolClientId` (a `string?`) is replaced by
  `ToolCallBase.Contributor`, a `ToolCallContributor` discriminated union
  with `ToolCallClientContributor { ClientId }` and
  `ToolCallMcpContributor { CustomizationId }` variants.
  `SessionToolCallStartAction` carries the new `Contributor` field, and the
  reducer threads it through each tool-call transition.
- Renamed the `ChangesetSummary` type to `Changeset`. The on-the-wire shape
  is unchanged.
- The `changesets` catalogue moved from `SessionSummary` to `SessionState`;
  the `session/changesetsChanged` action now updates `state.Changesets`
  directly instead of `state.Summary.Changesets`.
- `Reducers.ApplyToChangeset` is now fully implemented (previously a no-op
  stub), so `changeset/*` actions fold into `ChangesetState`. Brings the
  .NET client to full cross-language conformance parity on the changeset
  channel.

### Removed

- Removed the `Additions`, `Deletions`, and `Files` fields from the former
  `ChangesetSummary`. Aggregate counts now live on `SessionSummary.Changes`;
  per-changeset views derive their own totals from `ChangesetState.Files`.

## [0.1.0]

Initial release of the .NET client.

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
