/**
 * State Types — Source of truth for all AHP state type definitions.
 *
 * @module state
 * @description Complete reference for all state types in the Agent Host Protocol.
 */

// ─── Type Aliases ────────────────────────────────────────────────────────────

/** A URI string (e.g. `agenthost:/root` or `copilot:/<uuid>`). */
export type URI = string;

/**
 * A string that may optionally be rendered as Markdown.
 *
 * - A plain `string` is rendered as-is (no Markdown processing).
 * - An object with `{ markdown: string }` is rendered with Markdown formatting.
 */
export type StringOrMarkdown = string | { markdown: string };

// ─── Root State ──────────────────────────────────────────────────────────────

/**
 * Global state shared with every client subscribed to `agenthost:/root`.
 *
 * @category Root State
 */
export interface IRootState {
  /** Available agent backends and their models */
  agents: IAgentInfo[];
}

/**
 * @category Root State
 */
export interface IAgentInfo {
  /** Agent provider ID (e.g. `'copilot'`) */
  provider: string;
  /** Human-readable name */
  displayName: string;
  /** Description string */
  description: string;
  /** Available models for this agent */
  models: ISessionModelInfo[];
}

/**
 * @category Root State
 */
export interface ISessionModelInfo {
  /** Model identifier */
  id: string;
  /** Provider this model belongs to */
  provider: string;
  /** Human-readable model name */
  name: string;
  /** Maximum context window size */
  maxContextWindow?: number;
  /** Whether the model supports vision */
  supportsVision?: boolean;
  /** Policy configuration state */
  policyState?: 'enabled' | 'disabled' | 'unconfigured';
}

// ─── Session State ───────────────────────────────────────────────────────────

/**
 * Full state for a single session, loaded when a client subscribes to the session's URI.
 *
 * @category Session State
 */
export interface ISessionState {
  /** Lightweight session metadata */
  summary: ISessionSummary;
  /** Session initialization state */
  lifecycle: 'creating' | 'ready' | 'creationFailed';
  /** Error details if creation failed */
  creationError?: IErrorInfo;
  /** Completed turns */
  turns: ITurn[];
  /** Currently in-progress turn */
  activeTurn?: IActiveTurn;
}

/**
 * @category Session State
 */
export interface ISessionSummary {
  /** Session URI */
  resource: URI;
  /** Agent provider ID */
  provider: string;
  /** Session title */
  title: string;
  /** Current session status */
  status: 'idle' | 'in-progress' | 'error';
  /** Creation timestamp */
  createdAt: number;
  /** Last modification timestamp */
  modifiedAt: number;
  /** Currently selected model */
  model?: string;
}

// ─── Turn Types ──────────────────────────────────────────────────────────────

/**
 * A completed request/response cycle.
 *
 * @category Turn Types
 */
export interface ITurn {
  /** Turn identifier */
  id: string;
  /** The user's input */
  userMessage: IUserMessage;
  /** Final response text (captured from streaming) */
  responseText: string;
  /** Structured response content */
  responseParts: IResponsePart[];
  /** Tool invocations in terminal states (completed or cancelled) */
  toolCalls: (IToolCallCompletedState | IToolCallCancelledState)[];
  /** Token usage info */
  usage: IUsageInfo | undefined;
  /** How the turn ended */
  state: 'complete' | 'cancelled' | 'error';
  /** Error details if state is `'error'` */
  error?: IErrorInfo;
}

/**
 * An in-progress turn — the assistant is actively streaming.
 *
 * @category Turn Types
 */
export interface IActiveTurn {
  /** Turn identifier */
  id: string;
  /** The user's input */
  userMessage: IUserMessage;
  /** Accumulated streaming response text */
  streamingText: string;
  /** Structured response content so far */
  responseParts: IResponsePart[];
  /** Active tool invocations keyed by tool call ID */
  toolCalls: Record<string, IToolCallState>;
  /** Pending permission requests keyed by request ID */
  pendingPermissions: Record<string, IPermissionRequest>;
  /** Accumulated reasoning/thinking text */
  reasoning: string;
  /** Token usage info */
  usage: IUsageInfo | undefined;
}

/**
 * @category Turn Types
 */
export interface IUserMessage {
  /** Message text */
  text: string;
  /** File/selection attachments */
  attachments?: IMessageAttachment[];
}

/**
 * @category Turn Types
 */
export interface IMessageAttachment {
  /** Attachment type */
  type: 'file' | 'directory' | 'selection';
  /** File/directory path */
  path: string;
  /** Display name */
  displayName?: string;
}

// ─── Response Parts ──────────────────────────────────────────────────────────

/**
 * @category Response Parts
 */
export interface IMarkdownResponsePart {
  /** Discriminant */
  kind: 'markdown';
  /** Markdown content */
  content: string;
}

/**
 * A reference to large content stored outside the state tree.
 *
 * @category Response Parts
 */
export interface IContentRef {
  /** Discriminant */
  kind: 'contentRef';
  /** Content URI */
  uri: string;
  /** Approximate size in bytes */
  sizeHint?: number;
  /** Content MIME type */
  mimeType?: string;
}

/**
 * @category Response Parts
 */
export type IResponsePart = IMarkdownResponsePart | IContentRef;

// ─── Tool Call Types ─────────────────────────────────────────────────────────

/**
 * Derived status type for the tool call lifecycle. This is the discriminant
 * field (`status`) across all tool call state interfaces.
 *
 * @category Tool Call Types
 */
export type ToolCallStatus = IToolCallState['status'];

/**
 * How a tool call was confirmed for execution.
 *
 * - `'not-needed'` — No confirmation required (auto-approved)
 * - `'user-action'` — User explicitly approved
 * - `'setting'` — Approved by a persistent user setting
 *
 * @category Tool Call Types
 */
export type ToolCallConfirmationReason = 'not-needed' | 'user-action' | 'setting';

/**
 * Metadata common to all tool call states.
 *
 * @category Tool Call Types
 * @remarks
 * Fields like `toolName` carry agent-specific identifiers on the wire despite the
 * agent-agnostic design principle. These exist for debugging and logging purposes.
 * A future version may move these to a separate diagnostic channel or namespace them
 * more clearly.
 */
interface IToolCallBase {
  /** Unique tool call identifier */
  toolCallId: string;
  /** Internal tool name (for debugging/logging) */
  toolName: string;
  /** Human-readable tool name */
  displayName: string;
}

/**
 * Properties available once tool call parameters are fully received.
 *
 * @category Tool Call Types
 */
interface IToolCallParameterFields {
  /** Message describing what the tool will do */
  invocationMessage: StringOrMarkdown;
  /** Raw tool input */
  toolInput?: string;
}

/**
 * Tool execution result details, available after execution completes.
 *
 * @category Tool Call Types
 */
export interface IToolCallResult {
  /** Whether the tool succeeded */
  success: boolean;
  /** Past-tense description of what the tool did */
  pastTenseMessage: StringOrMarkdown;
  /** Tool output text */
  toolOutput?: string;
  /** Error details if the tool failed */
  error?: { message: string; code?: string };
}

/**
 * LM is streaming the tool call parameters.
 *
 * @category Tool Call Types
 */
export interface IToolCallStreamingState extends IToolCallBase {
  status: 'streaming';
  /** Partial parameters accumulated so far */
  partialInput?: string;
  /** Progress message shown while parameters are streaming */
  invocationMessage?: StringOrMarkdown;
}

/**
 * Parameters are complete, waiting for client to confirm execution.
 *
 * @category Tool Call Types
 */
export interface IToolCallPendingConfirmationState extends IToolCallBase, IToolCallParameterFields {
  status: 'pending-confirmation';
}

/**
 * Tool is actively executing.
 *
 * @category Tool Call Types
 */
export interface IToolCallRunningState extends IToolCallBase, IToolCallParameterFields {
  status: 'running';
  /** How the tool was confirmed for execution */
  confirmed: ToolCallConfirmationReason;
}

/**
 * Tool finished executing, waiting for client to approve the result.
 *
 * @category Tool Call Types
 */
export interface IToolCallPendingResultConfirmationState extends IToolCallBase, IToolCallParameterFields, IToolCallResult {
  status: 'pending-result-confirmation';
  /** How the tool was confirmed for execution */
  confirmed: ToolCallConfirmationReason;
}

/**
 * Tool completed successfully or with an error.
 *
 * @category Tool Call Types
 */
export interface IToolCallCompletedState extends IToolCallBase, IToolCallParameterFields, IToolCallResult {
  status: 'completed';
  /** How the tool was confirmed for execution */
  confirmed: ToolCallConfirmationReason;
}

/**
 * Tool call was cancelled before execution.
 *
 * @category Tool Call Types
 */
export interface IToolCallCancelledState extends IToolCallBase, IToolCallParameterFields {
  status: 'cancelled';
  /** Why the tool was cancelled */
  reason: 'denied' | 'skipped' | 'result-denied';
  /** Optional message explaining the cancellation */
  reasonMessage?: StringOrMarkdown;
  /** What the user suggested doing instead */
  userSuggestion?: IUserMessage;
}

/**
 * Discriminated union of all tool call lifecycle states.
 *
 * See the [state model guide](/guide/state-model.html#tool-call-lifecycle)
 * for the full state machine diagram.
 *
 * @category Tool Call Types
 */
export type IToolCallState =
  | IToolCallStreamingState
  | IToolCallPendingConfirmationState
  | IToolCallRunningState
  | IToolCallPendingResultConfirmationState
  | IToolCallCompletedState
  | IToolCallCancelledState;

// ─── Permission Types ────────────────────────────────────────────────────────

/**
 * @category Permission Types
 * @remarks
 * Fields like `serverName`, `toolName`, and `rawRequest` carry agent-specific
 * identifiers on the wire despite the agent-agnostic design principle. These exist
 * for debugging and logging purposes.
 */
export interface IPermissionRequest {
  /** Unique request identifier */
  requestId: string;
  /** Type of permission */
  permissionKind: 'shell' | 'write' | 'mcp' | 'read' | 'url';
  /** Associated tool call */
  toolCallId?: string;
  /** File/directory path */
  path?: string;
  /** Full command to execute */
  fullCommandText?: string;
  /** What the tool intends to do */
  intention?: string;
  /** MCP server name */
  serverName?: string;
  /** Tool requesting permission */
  toolName?: string;
  /** Raw request data */
  rawRequest?: string;
}

// ─── Common Types ────────────────────────────────────────────────────────────

/**
 * @category Common Types
 */
export interface IUsageInfo {
  /** Input tokens consumed */
  inputTokens?: number;
  /** Output tokens generated */
  outputTokens?: number;
  /** Model used */
  model?: string;
  /** Tokens read from cache */
  cacheReadTokens?: number;
}

/**
 * @category Common Types
 */
export interface IErrorInfo {
  /** Error type identifier */
  errorType: string;
  /** Human-readable error message */
  message: string;
  /** Stack trace */
  stack?: string;
}

/**
 * A point-in-time snapshot of a subscribed resource's state, returned by
 * `initialize`, `reconnect`, and `subscribe`.
 *
 * @category Common Types
 */
export interface ISnapshot {
  /** The subscribed resource URI (e.g. `agenthost:/root` or `copilot:/<uuid>`) */
  resource: URI;
  /** The current state of the resource */
  state: IRootState | ISessionState;
  /** The `serverSeq` at which this snapshot was taken. Subsequent actions will have `serverSeq > fromSeq`. */
  fromSeq: number;
}
