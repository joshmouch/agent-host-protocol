<!-- Generated from types/*.ts — do not edit -->


# Actions Reference

Complete reference for all action types in the Agent Host Protocol. Actions are the sole mutation mechanism for subscribable state.

## Action Envelope

Every action is wrapped in an `ActionEnvelope`:

```typescript
interface IActionEnvelope {
  readonly action: IStateAction;
  readonly serverSeq: number;
  readonly origin: { clientId: string; clientSeq: number } | undefined;
  readonly rejectionReason?: string;
}
```

## Root Actions

Mutate `RootState`. All are server-only.

### `root/agentsChanged` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L59" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Fired when available agent backends or their models change.

| Field | Type | Description |
|---|---|---|
| `type` | `'root/agentsChanged'` | Discriminant |
| `agents` | [IAgentInfo](/reference/state-types#iagentinfo)[] | Updated agent list |

## Session Actions

Mutate `SessionState`. Scoped to a session URI.

### `session/ready` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L73" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Session backend initialized successfully.

| Field | Type | Description |
|---|---|---|
| `type` | `'session/ready'` | Discriminant |
| `session` | [URI](/reference/state-types#uri) | Session URI |

### `session/creationFailed` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L85" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Session backend failed to initialize.

| Field | Type | Description |
|---|---|---|
| `type` | `'session/creationFailed'` | Discriminant |
| `session` | [URI](/reference/state-types#uri) | Session URI |
| `error` | [IErrorInfo](/reference/state-types#ierrorinfo) | Error details |

### `session/turnStarted` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L100" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

**Client-dispatchable.** User sent a message; server starts agent processing.

| Field | Type | Description |
|---|---|---|
| `type` | `'session/turnStarted'` | Discriminant |
| `session` | [URI](/reference/state-types#uri) | Session URI |
| `turnId` | `string` | Turn identifier |
| `userMessage` | [IUserMessage](/reference/state-types#iusermessage) | User's message |

### `session/delta` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L116" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Streaming text chunk from the assistant.

| Field | Type | Description |
|---|---|---|
| `type` | `'session/delta'` | Discriminant |
| `session` | [URI](/reference/state-types#uri) | Session URI |
| `turnId` | `string` | Turn identifier |
| `content` | `string` | Text chunk |

### `session/responsePart` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L132" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Structured content appended to the response.

| Field | Type | Description |
|---|---|---|
| `type` | `'session/responsePart'` | Discriminant |
| `session` | [URI](/reference/state-types#uri) | Session URI |
| `turnId` | `string` | Turn identifier |
| `part` | [IResponsePart](/reference/state-types#iresponsepart) | Response part (markdown or content ref) |

### `session/toolCallStart` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L152" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

A tool call begins — parameters are streaming from the LM.

For client-provided tools, the server sets `toolClientId` to identify the
owning client. That client is responsible for executing the tool once it
reaches the `running` state and dispatching `session/toolCallComplete`.

| Field | Type | Required | Description |
|---|---|---|---|
| `type` | `'session/toolCallStart'` | Yes | Discriminant |
| `toolName` | `string` | Yes | Internal tool name (for debugging/logging) |
| `displayName` | `string` | Yes | Human-readable tool name |
| `toolClientId` | `string` | No | If this tool is provided by a client, the `clientId` of the owning client.
Absent for server-side tools. |

### `session/toolCallDelta` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L171" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Streaming partial parameters for a tool call.

| Field | Type | Required | Description |
|---|---|---|---|
| `type` | `'session/toolCallDelta'` | Yes | Discriminant |
| `content` | `string` | Yes | Partial parameter content to append |
| `invocationMessage` | [StringOrMarkdown](/reference/state-types#stringormarkdown) | No | Updated progress message |

### `session/toolCallReady` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L190" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Tool call parameters are complete. Transitions to `pending-confirmation`
or directly to `running` if `confirmed` is set.

For client-provided tools, the server typically sets `confirmed` to
`'not-needed'` so the tool transitions directly to `running`, where the
owning client can begin execution immediately.

| Field | Type | Required | Description |
|---|---|---|---|
| `type` | `'session/toolCallReady'` | Yes | Discriminant |
| `invocationMessage` | [StringOrMarkdown](/reference/state-types#stringormarkdown) | Yes | Message describing what the tool will do |
| `toolInput` | `string` | No | Raw tool input |
| `confirmed` | [ToolCallConfirmationReason](/reference/state-types#toolcallconfirmationreason) | No | If set, the tool was auto-confirmed and transitions directly to `running` |

### `session/toolCallConfirmed (approved)` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L207" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

**Client-dispatchable.** Client approves a pending tool call. The tool transitions to `running`.

| Field | Type | Description |
|---|---|---|
| `type` | `'session/toolCallConfirmed'` | Discriminant |
| `approved` | `true` | The tool call was approved |
| `confirmed` | [ToolCallConfirmationReason](/reference/state-types#toolcallconfirmationreason) | How the tool was confirmed |

### `session/toolCallConfirmed (denied)` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L225" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

**Client-dispatchable.** Client denies a pending tool call. The tool transitions to `cancelled`.

For client-provided tools, the owning client MUST dispatch this if it does
not recognize the tool or cannot execute it.

| Field | Type | Required | Description |
|---|---|---|---|
| `type` | `'session/toolCallConfirmed'` | Yes | Discriminant |
| `approved` | `false` | Yes | The tool call was denied |
| `reason` | `'denied' \| 'skipped'` | Yes | Why the tool was cancelled |
| `userSuggestion` | [IUserMessage](/reference/state-types#iusermessage) | No | What the user suggested doing instead |
| `reasonMessage` | [StringOrMarkdown](/reference/state-types#stringormarkdown) | No | Optional explanation for the denial |

### `session/toolCallComplete` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L264" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Tool execution finished. Transitions to `completed` or `pending-result-confirmation`
if `requiresResultConfirmation` is `true`.

For client-provided tools (where `toolClientId` is set on the tool call state),
the owning client dispatches this action with the execution result. The server
SHOULD reject this action if the dispatching client does not match `toolClientId`.

Servers waiting on a client tool call MAY time out after a reasonable duration
if the implementing client disconnects or becomes unresponsive, and dispatch
this action with `result.success = false` and an appropriate error.

| Field | Type | Required | Description |
|---|---|---|---|
| `type` | `'session/toolCallComplete'` | Yes | Discriminant |
| `result` | [IToolCallResult](/reference/state-types#itoolcallresult) | Yes | Execution result |
| `requiresResultConfirmation` | `boolean` | No | If true, the result requires client approval before finalizing |

#### `IToolCallResult` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L296" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

| Field | Type | Required | Description |
|---|---|---|---|
| `success` | `boolean` | Yes | Whether the tool succeeded |
| `pastTenseMessage` | [StringOrMarkdown](/reference/state-types#stringormarkdown) | Yes | Past-tense description of what the tool did |
| `content` | [IToolResultContent](/reference/state-types#itoolresultcontent)[] | No | Unstructured result content blocks.

This mirrors the `content` field of MCP `CallToolResult`. |
| `structuredContent` | `Record<string, unknown>` | No | Optional structured result object.

This mirrors the `structuredContent` field of MCP `CallToolResult`. |
| `error` | `{ message: string; code?: string }` | No | Error details if the tool failed |

### `session/toolCallResultConfirmed` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L281" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

**Client-dispatchable.** Client approves or denies a tool's result.

If `approved` is `false`, the tool transitions to `cancelled` with reason `result-denied`.

| Field | Type | Description |
|---|---|---|
| `type` | `'session/toolCallResultConfirmed'` | Discriminant |
| `approved` | `boolean` | Whether the result was approved |

### `session/permissionRequest` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L293" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Permission needed from the user to proceed.

| Field | Type | Description |
|---|---|---|
| `type` | `'session/permissionRequest'` | Discriminant |
| `session` | [URI](/reference/state-types#uri) | Session URI |
| `turnId` | `string` | Turn identifier |
| `request` | [IPermissionRequest](/reference/state-types#ipermissionrequest) | Permission request details |

### `session/permissionResolved` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L310" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

**Client-dispatchable.** Permission granted or denied.

| Field | Type | Description |
|---|---|---|
| `type` | `'session/permissionResolved'` | Discriminant |
| `session` | [URI](/reference/state-types#uri) | Session URI |
| `turnId` | `string` | Turn identifier |
| `requestId` | `string` | Permission request ID |
| `approved` | `boolean` | Whether permission was granted |

### `session/turnComplete` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L328" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Turn finished — the assistant is idle.

| Field | Type | Description |
|---|---|---|
| `type` | `'session/turnComplete'` | Discriminant |
| `session` | [URI](/reference/state-types#uri) | Session URI |
| `turnId` | `string` | Turn identifier |

### `session/turnCancelled` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L343" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

**Client-dispatchable.** Turn was aborted; server stops processing.

| Field | Type | Description |
|---|---|---|
| `type` | `'session/turnCancelled'` | Discriminant |
| `session` | [URI](/reference/state-types#uri) | Session URI |
| `turnId` | `string` | Turn identifier |

### `session/error` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L357" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Error during turn processing.

| Field | Type | Description |
|---|---|---|
| `type` | `'session/error'` | Discriminant |
| `session` | [URI](/reference/state-types#uri) | Session URI |
| `turnId` | `string` | Turn identifier |
| `error` | [IErrorInfo](/reference/state-types#ierrorinfo) | Error details |

### `session/titleChanged` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L373" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Session title updated (typically auto-generated from conversation).

| Field | Type | Description |
|---|---|---|
| `type` | `'session/titleChanged'` | Discriminant |
| `session` | [URI](/reference/state-types#uri) | Session URI |
| `title` | `string` | New title |

### `session/usage` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L387" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Token usage report for a turn.

| Field | Type | Description |
|---|---|---|
| `type` | `'session/usage'` | Discriminant |
| `session` | [URI](/reference/state-types#uri) | Session URI |
| `turnId` | `string` | Turn identifier |
| `usage` | [IUsageInfo](/reference/state-types#iusageinfo) | Token usage data |

### `session/reasoning` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L403" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Reasoning/thinking text from the model.

| Field | Type | Description |
|---|---|---|
| `type` | `'session/reasoning'` | Discriminant |
| `session` | [URI](/reference/state-types#uri) | Session URI |
| `turnId` | `string` | Turn identifier |
| `content` | `string` | Reasoning text chunk |

### `session/modelChanged` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/actions.ts#L420" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

**Client-dispatchable.** Model changed for this session.

| Field | Type | Description |
|---|---|---|
| `type` | `'session/modelChanged'` | Discriminant |
| `session` | [URI](/reference/state-types#uri) | Session URI |
| `model` | `string` | New model ID |

## Version Introduction

All actions listed above were introduced in protocol version **1**.

| Action Type | Version |
|---|---|
| `root/agentsChanged` | 1 |
| `session/ready` | 1 |
| `session/creationFailed` | 1 |
| `session/turnStarted` | 1 |
| `session/delta` | 1 |
| `session/responsePart` | 1 |
| `session/toolCallStart` | 1 |
| `session/toolCallDelta` | 1 |
| `session/toolCallReady` | 1 |
| `session/toolCallConfirmed (approved)` | 1 |
| `session/toolCallConfirmed (denied)` | 1 |
| `session/toolCallComplete` | 1 |
| `session/toolCallResultConfirmed` | 1 |
| `session/permissionRequest` | 1 |
| `session/permissionResolved` | 1 |
| `session/turnComplete` | 1 |
| `session/turnCancelled` | 1 |
| `session/error` | 1 |
| `session/titleChanged` | 1 |
| `session/usage` | 1 |
| `session/reasoning` | 1 |
| `session/modelChanged` | 1 |
