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

- **Chat draft + activity actions** (upstream AHP 0.5.0, microsoft/agent-host-protocol#264):
  `ChatDraftChangedAction` (`chat/draftChanged`, client-dispatchable) syncs the
  user's in-progress `ChatState.Draft` (a `Message`, carrying its model/agent
  selection and attachments) so it survives reloads and is visible to other
  clients; `ChatActivityChangedAction` (`chat/activityChanged`) updates
  `ChatState.Activity`.
- **`root/progress` notification** (upstream #263): a generic `ProgressParams`
  progress notification for long-running operations (e.g. the first-use SDK
  download), correlated back to the request via the new
  `CreateSessionParams.ProgressToken`.
- **OpenTelemetry-native self-instrumentation**: the client originates traces +
  metrics from a single `System.Diagnostics.ActivitySource` and `Meter`
  (`AhpTelemetry.Name == "Microsoft.AgentHostProtocol"`), near zero-cost when
  nothing is listening. One `ahp.request {method}` span per JSON-RPC request
  (`rpc.system` / `rpc.method` / `ahp.outcome` tags) and the `ahp.client.*`
  metric family (messages sent/received, request duration, requests in-flight,
  active subscriptions, reconnects, dropped events, malformed frames). The
  span / metric / attribute NAMES are codegen'd into `AhpTelemetryNames` from a
  client-private registry (`clients/dotnet/codegen/telemetry/registry.ts`) so
  they live in one place; the registry is structured for promotion to a shared
  cross-client contract if AHP ever specs one. See `TELEMETRY.md`.
- `IMultiHostClient` — an interface extracted from `MultiHostClient` so consumers
  can depend on (and mock) the multi-host runtime rather than the concrete sealed
  facade. `AddAgentHostProtocol()` now also registers `IMultiHostClient`,
  forwarding to the same singleton.
- `AhpServiceCollectionExtensions.TelemetrySourceName` — the instrumentation-scope
  name constant to pass to OpenTelemetry's `AddSource(...)` / `AddMeter(...)`, with
  the wiring snippet in its XML docs (the library takes no OpenTelemetry
  dependency itself).
- `AhpTelemetryNames.StreamHost{Event,Subscription,Resource,Snapshot,Summaries}`
  constants for the multi-host per-stream `ahp.stream` drop-tag values, and a
  generated `AhpTelemetryNames.*Description` constant per metric (the single
  source for each instrument's runtime description).
- A new **`examples/OtelExport`** sample (Shape C): wires the AHP instrumentation
  scope into an OpenTelemetry pipeline with a console exporter and drives one
  client operation so the request span + metrics print.
- `ChangesetOperationStatus.Disabled` — new value for changeset operations
  that are currently unavailable and cannot be invoked (upstream #233).
- `ChangesetOperation.Group` — optional identifier for grouping related
  changeset operations together in the UI (upstream #233).
- Full support for the **`ahp-chat:` channel** (upstream #213, multi-chat
  sessions): `ChatState` (turns, active turn, steering message, queued
  messages, input requests), the `SessionState.Chats` catalog (`ChatSummary`)
  + `SessionState.DefaultChat` input-routing hint, the full `Chat*Action`
  family (`ChatTurnStarted`, `ChatDelta`, `ChatResponsePart`, and the chat
  tool-call / input / messaging actions), the `ChatInputQuestion` /
  `ChatInputAnswer` / `ChatInputRequest` types, `ChatOrigin` provenance, and
  the `CreateChat` / `DisposeChat` commands; reduced by the new
  `Reducers.ApplyToChat`, a faithful port of the canonical TypeScript chat
  reducer exercised by all 90 shared `reducer: "chat"` fixtures. Brings the
  .NET client to cross-language conformance parity on the chat channel.
- `SessionChatAddedAction`, `SessionChatRemovedAction`,
  `SessionChatUpdatedAction`, and `DefaultChatChangedAction` handling for
  incremental chat-catalog updates on `SessionState`.
- The per-turn chat actions (`ChatTurnStarted`, `ChatDelta`,
  `ChatResponsePart`, `ChatReasoning`, `ChatUsage`, `ChatTurnComplete`,
  `ChatTurnCancelled`, `ChatError`) now carry an optional `_meta` property bag
  (`Dictionary<string, JsonElement>? Meta`) so agent hosts can stamp portable
  per-event metadata on the action stream, mirroring the MCP `_meta`
  convention (upstream #240).
- `SessionSummary` and `PartialSessionSummary` now carry an optional `_meta`
  property bag (`Dictionary<string, JsonElement>? Meta`) for lightweight
  server-defined session-list presentation hints; the protocol does not
  interpret the values (upstream #254).
- Error metadata fields from upstream #216.
- `RootState` now exposes an optional `_meta` property bag
  (`Dictionary<string, JsonElement>? Meta`) for implementation-defined
  agent-host metadata, such as a well-known `hostBuild` key carrying the
  host's build version/commit/date.
- Full support for the per-session **annotations channel**
  (`ahp-session:/<uuid>/annotations`): the `AnnotationsState`, `Annotation`,
  `AnnotationEntry`, and `AnnotationsSummary` wire types; the four
  `annotations/{set,removed,entrySet,entryRemoved}` actions; and
  `Reducers.ApplyToAnnotations`, a faithful port of the canonical reducer
  (append-or-replace an annotation by id, drop a matching annotation,
  append-or-replace an entry within an annotation, drop a matching entry;
  unknown target ids are no-ops). Brings the .NET client to full
  cross-language conformance parity on the annotations channel.
- `SessionSummary.Annotations` (and `PartialSessionSummary.Annotations`),
  an optional `AnnotationsSummary` carrying annotation / entry counts for
  badge UI without subscribing to the channel.
- `MessageAnnotationsAttachment` — the `annotations` variant of the
  `MessageAttachment` union, referencing annotations on a session's
  annotations channel.
- `IAhpSerializer.SerializeToElement<T>(T)` — serializes a value directly to a
  `JsonElement` without the intermediate string + `JsonDocument.Parse`. Custom
  `IAhpSerializer` implementations must implement this member.
- `IAhpSerializer.Deserialize<T>(JsonElement)`: deserializes directly from an
  already-parsed `JsonElement`, avoiding the `GetRawText()` string + re-parse on
  the inbound hot path. Custom `IAhpSerializer` implementations must implement
  this member.
- `HostReconnectFailedException` (a `HostException` subclass), surfaced on
  `HostState.Error` when a host's reconnection cannot proceed: the transport
  dropped while the reconnect policy was disabled, or the attempt budget was
  exhausted.
- Trim / AOT support: the three shipping packages declare `IsTrimmable` /
  `IsAotCompatible` and annotate the reflection-based serialization entry points
  with `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]`, so trimmed or
  Native-AOT consumers receive accurate analyzer warnings.
- Tracks protocol 0.5.0. New `ChangesetContentChangedAction`
  (`changeset/contentChanged`) with its reducer: full-replacement semantics
  where `files` always replaces the file list, `operations` replaces the
  operation list only when present, and `error` is set when present and cleared
  otherwise (parity with the canonical reducer; upstream #159 fixtures). New
  `MessageOrigin` type and `MessageKind.Agent` / `MessageKind.Tool` members for
  non-user-initiated turns (`Message.Origin` is now the typed `MessageOrigin`
  rather than an opaque `JsonElement`; upstream #247). `ConfigPropertySchema`
  and `SessionConfigPropertySchema` gain `AdditionalProperties` (upstream #245).
- `SessionModelInfo.MaxOutputTokens` and `SessionModelInfo.MaxPromptTokens`
  optional fields for communicating model token limits (upstream).

### Changed

- **Tracks protocol 0.5.1** (the AHP 0.5.0 release line, upstream
  microsoft/agent-host-protocol#264/#278/#263): negotiated protocol versions are
  now `[0.5.1, 0.5.0]`.
- **Flat `SessionState`** (upstream #264): `SessionState` no longer nests a
  `Summary`. The fields shared with the catalog representation
  (`Provider`/`Title`/`Status`/`Activity`/`Project`/`WorkingDirectory`/
  `Annotations` — the new `SessionMetadata` interface) are denormalized directly
  onto `SessionState`, so a subscriber receives one flat object. The session
  reducers act on the flat fields, and — because `SessionState` no longer carries
  a `modifiedAt` clock — the session reducers are now pure (no host-authoritative
  `modifiedAt` overlay is needed for convergence). `SessionSummary.CreatedAt` and
  `SessionSummary.ModifiedAt` are now ISO 8601 `string`s (were epoch-ms numbers).
- **Per-message model/agent selection** (upstream #264): `session/modelChanged`
  and `session/agentChanged` actions are removed, and `Model`/`Agent` no longer
  live on `SessionState`, `SessionSummary`, `ChatState`, `ChatSummary`,
  `CreateSessionParams`, or `CreateChatParams`. The selection now travels on the
  individual `Message` (new `Message.Model` / `Message.Agent`), recording the
  model/agent a message was — or, for a `draft`, will be — sent with.
- **Multiple active clients per session** (upstream
  microsoft/agent-host-protocol#261): `SessionState.ActiveClient`
  (`SessionActiveClient?`) becomes `SessionState.ActiveClients`
  (`List<SessionActiveClient>`, required), so several clients can provide tools
  and customizations to one session at once. The two session actions are
  replaced accordingly: `SessionActiveClientChangedAction`
  (`session/activeClientChanged`) → `SessionActiveClientSetAction`
  (`session/activeClientSet`, upsert keyed by `clientId`, no longer nullable),
  and `SessionActiveClientToolsChangedAction`
  (`session/activeClientToolsChanged`) → `SessionActiveClientRemovedAction`
  (`session/activeClientRemoved`, carrying the `clientId` to remove). The
  reducer upserts on `activeClientSet` and removes-by-`clientId` (no-op on miss)
  on `activeClientRemoved`.
- `ConfigPropertySchema.Enum` and `SessionConfigPropertySchema.Enum` are now
  `List<JsonElement>?` instead of `List<string>?`, allowing numeric, boolean,
  and null enum values (the `JsonPrimitive` widening in `types/common/state.ts`).
- `ModelSelection.Config` values are now `Dictionary<string, JsonElement>?`
  instead of `Dictionary<string, string>?`, allowing numeric, boolean, and null
  configuration values carried through as-is.

- The `MultiHostClient` reconnect supervisor now emits the `ahp.client.reconnects`
  counter — tagged `ahp.outcome=ok` on a successful reconnect and `ahp.outcome=error`
  on each failed attempt and on attempt-budget exhaustion — so multi-host reconnects
  are observable, matching the single-host client's instrumentation.
- The multi-host per-stream drop tags and the metric instrument descriptions now
  reference the generated `AhpTelemetryNames` constants instead of hand-copied
  literals, so they cannot drift from the generated contract. The drop tags are
  also cached (one allocation per stream kind, not per evicted event).
- Per-turn / tool-call / input / messaging reducer logic moved from
  `SessionState` to `ChatState`, matching upstream #213's split of the session
  turn surface into the per-chat channel.
- `NowIso()` now emits ISO 8601 UTC with a `Z` suffix (was the C# round-trip
  format ending `+00:00`), matching the cross-client wire timestamp format.
- Generated write-once wire payloads (every `*Action` / `*Command` /
  `*Notification` and value object) are now `sealed record` types with
  `init`-only properties; the state types the reducers mutate in place
  (`SessionState`, `RootState`, `TerminalState`, `SessionSummary`, … ) remain
  mutable `sealed class`. Named-initializer construction is unchanged.
- Required non-nullable wire fields now use the C# `required` modifier instead
  of fabricated `""` / `null!` defaults. A payload that omits a required field
  is rejected on deserialize (matching the schema's `required` array) rather
  than silently materializing an empty value.
- The tool-call lifecycle reducers (`delta`, `ready`, `confirmed`, `complete`,
  `resultConfirmed`, `contentChanged`) now propagate the action's `_meta` onto
  the resulting tool-call state, so provider metadata stays synchronized as a
  tool call advances beyond its initial `start` event (parity with the
  canonical reducer; upstream #211).
- `AhpClient.RequestAsync<TParams, TResult>` now returns `Task<TResult?>` (was
  `Task<TResult>`), making the empty-result case explicit for callers. The typed
  protocol methods (`InitializeAsync`, `ReconnectAsync`, the subscribe helpers)
  throw `AhpRpcException` when the server returns no result rather than handing
  back a null.
- Failures surfaced on `AhpClient.Error` and `HostState.Error` are now typed
  (`AhpTransportException`, `HostReconnectFailedException`) instead of a bare
  `System.Exception`, so callers can pattern-match them.
- `HostConfig.Id` and `HostHandle.Id` now use the C# `required` modifier.
- Reduced per-message allocations on the hottest paths: inbound notifications and
  request results deserialize straight from the parsed `JsonElement` (no string
  round-trip), the notification fan-out skips its snapshot allocations when there
  are no subscribers, and the WebSocket receive loop decodes single-frame
  messages without a `MemoryStream`.

### Fixed

- `ChangesetOperationRangeTarget.Range` now serializes as the canonical
  `TextRange` (nested `{ line, character }` start/end positions) instead of a
  flat `{ Start, End }` integer index pair. The flat shape was a
  code-generation drift from the schema (`ChangesetOperationTarget.range` is a
  `TextRange`) and could not represent a real source range; the .NET wire form
  now matches the other language clients.
- `ActionEnvelope.Origin` is omitted from the wire when absent
  (server-originated) instead of being serialized as `"origin": null`, matching
  the `ActionOrigin | undefined` schema (`undefined` ⇒ omit).
- Client teardown could deadlock when a keep-alive ping failure triggered
  shutdown from within the keep-alive loop itself (the loop awaited its own
  task). Teardown now skips that self-await.
- A fragmented WebSocket text message whose frames exactly filled the 64 KiB
  receive buffer dropped a frame; the receive loop now grows the buffer after
  copying the previous frame rather than before.

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
