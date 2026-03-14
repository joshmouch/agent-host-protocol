<!-- Generated from types/*.ts — do not edit -->


# State Types

Complete reference for all state types in the Agent Host Protocol.

## Root State

### `IRootState` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L28" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Global state shared with every client subscribed to `agenthost:/root`.

| Field | Type | Description |
|---|---|---|
| `agents` | [IAgentInfo](#iagentinfo)[] | Available agent backends and their models |

### `IAgentInfo` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L36" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

| Field | Type | Description |
|---|---|---|
| `provider` | `string` | Agent provider ID (e.g. `'copilot'`) |
| `displayName` | `string` | Human-readable name |
| `description` | `string` | Description string |
| `models` | [ISessionModelInfo](#isessionmodelinfo)[] | Available models for this agent |

### `ISessionModelInfo` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L50" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

| Field | Type | Required | Description |
|---|---|---|---|
| `id` | `string` | Yes | Model identifier |
| `provider` | `string` | Yes | Provider this model belongs to |
| `name` | `string` | Yes | Human-readable model name |
| `maxContextWindow` | `number` | No | Maximum context window size |
| `supportsVision` | `boolean` | No | Whether the model supports vision |
| `policyState` | `'enabled' \| 'disabled' \| 'unconfigured'` | No | Policy configuration state |

## Session State

### `ISessionState` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L72" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Full state for a single session, loaded when a client subscribes to the session's URI.

| Field | Type | Required | Description |
|---|---|---|---|
| `summary` | [ISessionSummary](#isessionsummary) | Yes | Lightweight session metadata |
| `lifecycle` | `'creating' \| 'ready' \| 'creationFailed'` | Yes | Session initialization state |
| `creationError` | [IErrorInfo](#ierrorinfo) | No | Error details if creation failed |
| `turns` | [ITurn](#iturn)[] | Yes | Completed turns |
| `activeTurn` | [IActiveTurn](#iactiveturn) | No | Currently in-progress turn |

### `ISessionSummary` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L88" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

| Field | Type | Required | Description |
|---|---|---|---|
| `resource` | [URI](#uri) | Yes | Session URI |
| `provider` | `string` | Yes | Agent provider ID |
| `title` | `string` | Yes | Session title |
| `status` | `'idle' \| 'in-progress' \| 'error'` | Yes | Current session status |
| `createdAt` | `number` | Yes | Creation timestamp |
| `modifiedAt` | `number` | Yes | Last modification timestamp |
| `model` | `string` | No | Currently selected model |

## Turn Types

### `ITurn` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L112" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

A completed request/response cycle.

| Field | Type | Required | Description |
|---|---|---|---|
| `id` | `string` | Yes | Turn identifier |
| `userMessage` | [IUserMessage](#iusermessage) | Yes | The user's input |
| `responseText` | `string` | Yes | Final response text (captured from streaming) |
| `responseParts` | [IResponsePart](#iresponsepart)[] | Yes | Structured response content |
| `toolCalls` | ([IToolCallCompletedState](#itoolcallcompletedstate) \| [IToolCallCancelledState](#itoolcallcancelledstate))[] | Yes | Tool invocations in terminal states (completed or cancelled) |
| `usage` | [IUsageInfo](#iusageinfo) \| undefined | Yes | Token usage info |
| `state` | `'complete' \| 'cancelled' \| 'error'` | Yes | How the turn ended |
| `error` | [IErrorInfo](#ierrorinfo) | No | Error details if state is `'error'` |

### `IActiveTurn` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L136" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

An in-progress turn — the assistant is actively streaming.

| Field | Type | Description |
|---|---|---|
| `id` | `string` | Turn identifier |
| `userMessage` | [IUserMessage](#iusermessage) | The user's input |
| `streamingText` | `string` | Accumulated streaming response text |
| `responseParts` | [IResponsePart](#iresponsepart)[] | Structured response content so far |
| `toolCalls` | Record<string, [IToolCallState](#itoolcallstate)> | Active tool invocations keyed by tool call ID |
| `pendingPermissions` | Record<string, [IPermissionRequest](#ipermissionrequest)> | Pending permission requests keyed by request ID |
| `reasoning` | `string` | Accumulated reasoning/thinking text |
| `usage` | [IUsageInfo](#iusageinfo) \| undefined | Token usage info |

### `IUserMessage` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L158" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

| Field | Type | Required | Description |
|---|---|---|---|
| `text` | `string` | Yes | Message text |
| `attachments` | [IMessageAttachment](#imessageattachment)[] | No | File/selection attachments |

### `IMessageAttachment` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L168" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

| Field | Type | Required | Description |
|---|---|---|---|
| `type` | `'file' \| 'directory' \| 'selection'` | Yes | Attachment type |
| `path` | `string` | Yes | File/directory path |
| `displayName` | `string` | No | Display name |

## Response Parts

### `IMarkdownResponsePart` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L182" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

| Field | Type | Description |
|---|---|---|
| `kind` | `'markdown'` | Discriminant |
| `content` | `string` | Markdown content |

### `IContentRef` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L194" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

A reference to large content stored outside the state tree.

| Field | Type | Required | Description |
|---|---|---|---|
| `kind` | `'contentRef'` | Yes | Discriminant |
| `uri` | `string` | Yes | Content URI |
| `sizeHint` | `number` | No | Approximate size in bytes |
| `mimeType` | `string` | No | Content MIME type |

### `IResponsePart` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L208" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

[IMarkdownResponsePart](#imarkdownresponsepart) | [IContentRef](#icontentref)


## Tool Call Types

### `ToolCallStatus` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L218" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Derived status type for the tool call lifecycle. This is the discriminant
field (`status`) across all tool call state interfaces.

[IToolCallState](#itoolcallstate)['status']


### `ToolCallConfirmationReason` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L229" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

How a tool call was confirmed for execution.

- `'not-needed'` — No confirmation required (auto-approved)
- `'user-action'` — User explicitly approved
- `'setting'` — Approved by a persistent user setting

`'not-needed' | 'user-action' | 'setting'`


### `IToolCallState` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L356" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Discriminated union of all tool call lifecycle states.

See the [state model guide](/guide/state-model.html#tool-call-lifecycle)
for the full state machine diagram.

| [IToolCallStreamingState](#itoolcallstreamingstate) | [IToolCallPendingConfirmationState](#itoolcallpendingconfirmationstate) | [IToolCallRunningState](#itoolcallrunningstate) | [IToolCallPendingResultConfirmationState](#itoolcallpendingresultconfirmationstate) | [IToolCallCompletedState](#itoolcallcompletedstate) | [IToolCallCancelledState](#itoolcallcancelledstate)


### `IToolCallResult` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L267" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Tool execution result details, available after execution completes.

| Field | Type | Required | Description |
|---|---|---|---|
| `success` | `boolean` | Yes | Whether the tool succeeded |
| `pastTenseMessage` | [StringOrMarkdown](#stringormarkdown) | Yes | Past-tense description of what the tool did |
| `toolOutput` | `string` | No | Tool output text |
| `error` | `{ message: string; code?: string }` | No | Error details if the tool failed |

### `IToolCallStreamingState` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L283" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

LM is streaming the tool call parameters.

| Field | Type | Required | Description |
|---|---|---|---|
| `status` | `'streaming'` | Yes |  |
| `partialInput` | `string` | No | Partial parameters accumulated so far |
| `invocationMessage` | [StringOrMarkdown](#stringormarkdown) | No | Progress message shown while parameters are streaming |

### `IToolCallPendingConfirmationState` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L296" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Parameters are complete, waiting for client to confirm execution.

| Field | Type | Description |
|---|---|---|
| `status` | `'pending-confirmation'` |  |

### `IToolCallRunningState` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L305" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Tool is actively executing.

| Field | Type | Description |
|---|---|---|
| `status` | `'running'` |  |
| `confirmed` | [ToolCallConfirmationReason](#toolcallconfirmationreason) | How the tool was confirmed for execution |

### `IToolCallPendingResultConfirmationState` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L316" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Tool finished executing, waiting for client to approve the result.

| Field | Type | Description |
|---|---|---|
| `status` | `'pending-result-confirmation'` |  |
| `confirmed` | [ToolCallConfirmationReason](#toolcallconfirmationreason) | How the tool was confirmed for execution |

### `IToolCallCompletedState` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L327" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Tool completed successfully or with an error.

| Field | Type | Description |
|---|---|---|
| `status` | `'completed'` |  |
| `confirmed` | [ToolCallConfirmationReason](#toolcallconfirmationreason) | How the tool was confirmed for execution |

### `IToolCallCancelledState` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L338" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Tool call was cancelled before execution.

| Field | Type | Required | Description |
|---|---|---|---|
| `status` | `'cancelled'` | Yes |  |
| `reason` | `'denied' \| 'skipped' \| 'result-denied'` | Yes | Why the tool was cancelled |
| `reasonMessage` | [StringOrMarkdown](#stringormarkdown) | No | Optional message explaining the cancellation |
| `userSuggestion` | [IUserMessage](#iusermessage) | No | What the user suggested doing instead |

## Permission Types

### `IPermissionRequest` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L373" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

| Field | Type | Required | Description |
|---|---|---|---|
| `requestId` | `string` | Yes | Unique request identifier |
| `permissionKind` | `'shell' \| 'write' \| 'mcp' \| 'read' \| 'url'` | Yes | Type of permission |
| `toolCallId` | `string` | No | Associated tool call |
| `path` | `string` | No | File/directory path |
| `fullCommandText` | `string` | No | Full command to execute |
| `intention` | `string` | No | What the tool intends to do |
| `serverName` | `string` | No | MCP server name |
| `toolName` | `string` | No | Tool requesting permission |
| `rawRequest` | `string` | No | Raw request data |

## Common Types

### `IUsageInfo` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L399" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

| Field | Type | Required | Description |
|---|---|---|---|
| `inputTokens` | `number` | No | Input tokens consumed |
| `outputTokens` | `number` | No | Output tokens generated |
| `model` | `string` | No | Model used |
| `cacheReadTokens` | `number` | No | Tokens read from cache |

### `IErrorInfo` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L413" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

| Field | Type | Required | Description |
|---|---|---|---|
| `errorType` | `string` | Yes | Error type identifier |
| `message` | `string` | Yes | Human-readable error message |
| `stack` | `string` | No | Stack trace |

### `ISnapshot` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/state.ts#L428" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

A point-in-time snapshot of a subscribed resource's state, returned by
`initialize`, `reconnect`, and `subscribe`.

| Field | Type | Description |
|---|---|---|
| `resource` | [URI](#uri) | The subscribed resource URI (e.g. `agenthost:/root` or `copilot:/&lt;uuid&gt;`) |
| `state` | [IRootState](#irootstate) \| [ISessionState](#isessionstate) | The current state of the resource |
| `fromSeq` | `number` | The `serverSeq` at which this snapshot was taken. Subsequent actions will have `serverSeq &gt; fromSeq`. |
