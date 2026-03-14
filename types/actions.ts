/**
 * Action Types — Source of truth for all AHP action type definitions.
 *
 * @module actions
 * @description Complete reference for all action types in the Agent Host Protocol.
 * Actions are the sole mutation mechanism for subscribable state.
 */

import type {
  URI,
  StringOrMarkdown,
  IAgentInfo,
  IErrorInfo,
  IUserMessage,
  IResponsePart,
  IToolCallResult,
  ToolCallConfirmationReason,
  IUsageInfo,
  IPermissionRequest,
} from './state.js';

// ─── Action Envelope ─────────────────────────────────────────────────────────

/**
 * Every action is wrapped in an `ActionEnvelope`.
 */
export interface IActionEnvelope {
  readonly action: IStateAction;
  readonly serverSeq: number;
  readonly origin: { clientId: string; clientSeq: number } | undefined;
  readonly rejectionReason?: string;
}

// ─── Root Actions ────────────────────────────────────────────────────────────

/**
 * Base interface for all tool-call-scoped actions, carrying the common
 * session, turn, and tool call identifiers.
 *
 * @category Session Actions
 */
interface IToolCallActionBase {
  /** Session URI */
  session: URI;
  /** Turn identifier */
  turnId: string;
  /** Tool call identifier */
  toolCallId: string;
}

/**
 * Fired when available agent backends or their models change.
 *
 * @category Root Actions
 * @version 1
 */
export interface IRootAgentsChangedAction {
  type: 'root/agentsChanged';
  /** Updated agent list */
  agents: IAgentInfo[];
}

// ─── Session Actions ─────────────────────────────────────────────────────────

/**
 * Session backend initialized successfully.
 *
 * @category Session Actions
 * @version 1
 */
export interface ISessionReadyAction {
  type: 'session/ready';
  /** Session URI */
  session: URI;
}

/**
 * Session backend failed to initialize.
 *
 * @category Session Actions
 * @version 1
 */
export interface ISessionCreationFailedAction {
  type: 'session/creationFailed';
  /** Session URI */
  session: URI;
  /** Error details */
  error: IErrorInfo;
}

/**
 * User sent a message; server starts agent processing.
 *
 * @category Session Actions
 * @version 1
 * @clientDispatchable
 */
export interface ISessionTurnStartedAction {
  type: 'session/turnStarted';
  /** Session URI */
  session: URI;
  /** Turn identifier */
  turnId: string;
  /** User's message */
  userMessage: IUserMessage;
}

/**
 * Streaming text chunk from the assistant.
 *
 * @category Session Actions
 * @version 1
 */
export interface ISessionDeltaAction {
  type: 'session/delta';
  /** Session URI */
  session: URI;
  /** Turn identifier */
  turnId: string;
  /** Text chunk */
  content: string;
}

/**
 * Structured content appended to the response.
 *
 * @category Session Actions
 * @version 1
 */
export interface ISessionResponsePartAction {
  type: 'session/responsePart';
  /** Session URI */
  session: URI;
  /** Turn identifier */
  turnId: string;
  /** Response part (markdown or content ref) */
  part: IResponsePart;
}

/**
 * A tool call begins — parameters are streaming from the LM.
 *
 * @category Session Actions
 * @version 1
 */
export interface ISessionToolCallStartAction extends IToolCallActionBase {
  type: 'session/toolCallStart';
  /** Internal tool name (for debugging/logging) */
  toolName: string;
  /** Human-readable tool name */
  displayName: string;
}

/**
 * Streaming partial parameters for a tool call.
 *
 * @category Session Actions
 * @version 1
 */
export interface ISessionToolCallDeltaAction extends IToolCallActionBase {
  type: 'session/toolCallDelta';
  /** Partial parameter content to append */
  content: string;
  /** Updated progress message */
  invocationMessage?: StringOrMarkdown;
}

/**
 * Tool call parameters are complete. Transitions to `pending-confirmation`
 * or directly to `running` if `confirmed` is set.
 *
 * @category Session Actions
 * @version 1
 */
export interface ISessionToolCallReadyAction extends IToolCallActionBase {
  type: 'session/toolCallReady';
  /** Message describing what the tool will do */
  invocationMessage: StringOrMarkdown;
  /** Raw tool input */
  toolInput?: string;
  /** If set, the tool was auto-confirmed and transitions directly to `running` */
  confirmed?: ToolCallConfirmationReason;
}

/**
 * Client approves a pending tool call. The tool transitions to `running`.
 *
 * @category Session Actions
 * @version 1
 * @clientDispatchable
 */
export interface ISessionToolCallApprovedAction extends IToolCallActionBase {
  type: 'session/toolCallConfirmed';
  /** The tool call was approved */
  approved: true;
  /** How the tool was confirmed */
  confirmed: ToolCallConfirmationReason;
}

/**
 * Client denies a pending tool call. The tool transitions to `cancelled`.
 *
 * @category Session Actions
 * @version 1
 * @clientDispatchable
 */
export interface ISessionToolCallDeniedAction extends IToolCallActionBase {
  type: 'session/toolCallConfirmed';
  /** The tool call was denied */
  approved: false;
  /** Why the tool was cancelled */
  reason: 'denied' | 'skipped';
  /** What the user suggested doing instead */
  userSuggestion?: IUserMessage;
  /** Optional explanation for the denial */
  reasonMessage?: StringOrMarkdown;
}

/**
 * Client confirms or denies a pending tool call.
 *
 * @category Session Actions
 * @version 1
 * @clientDispatchable
 */
export type ISessionToolCallConfirmedAction =
  | ISessionToolCallApprovedAction
  | ISessionToolCallDeniedAction;

/**
 * Tool execution finished. Transitions to `completed` or `pending-result-confirmation`
 * if `requiresResultConfirmation` is `true`.
 *
 * @category Session Actions
 * @version 1
 */
export interface ISessionToolCallCompleteAction extends IToolCallActionBase {
  type: 'session/toolCallComplete';
  /** Execution result */
  result: IToolCallResult;
  /** If true, the result requires client approval before finalizing */
  requiresResultConfirmation?: boolean;
}

/**
 * Client approves or denies a tool's result.
 *
 * If `approved` is `false`, the tool transitions to `cancelled` with reason `result-denied`.
 *
 * @category Session Actions
 * @version 1
 * @clientDispatchable
 */
export interface ISessionToolCallResultConfirmedAction extends IToolCallActionBase {
  type: 'session/toolCallResultConfirmed';
  /** Whether the result was approved */
  approved: boolean;
}

/**
 * Permission needed from the user to proceed.
 *
 * @category Session Actions
 * @version 1
 */
export interface ISessionPermissionRequestAction {
  type: 'session/permissionRequest';
  /** Session URI */
  session: URI;
  /** Turn identifier */
  turnId: string;
  /** Permission request details */
  request: IPermissionRequest;
}

/**
 * Permission granted or denied.
 *
 * @category Session Actions
 * @version 1
 * @clientDispatchable
 */
export interface ISessionPermissionResolvedAction {
  type: 'session/permissionResolved';
  /** Session URI */
  session: URI;
  /** Turn identifier */
  turnId: string;
  /** Permission request ID */
  requestId: string;
  /** Whether permission was granted */
  approved: boolean;
}

/**
 * Turn finished — the assistant is idle.
 *
 * @category Session Actions
 * @version 1
 */
export interface ISessionTurnCompleteAction {
  type: 'session/turnComplete';
  /** Session URI */
  session: URI;
  /** Turn identifier */
  turnId: string;
}

/**
 * Turn was aborted; server stops processing.
 *
 * @category Session Actions
 * @version 1
 * @clientDispatchable
 */
export interface ISessionTurnCancelledAction {
  type: 'session/turnCancelled';
  /** Session URI */
  session: URI;
  /** Turn identifier */
  turnId: string;
}

/**
 * Error during turn processing.
 *
 * @category Session Actions
 * @version 1
 */
export interface ISessionErrorAction {
  type: 'session/error';
  /** Session URI */
  session: URI;
  /** Turn identifier */
  turnId: string;
  /** Error details */
  error: IErrorInfo;
}

/**
 * Session title updated (typically auto-generated from conversation).
 *
 * @category Session Actions
 * @version 1
 */
export interface ISessionTitleChangedAction {
  type: 'session/titleChanged';
  /** Session URI */
  session: URI;
  /** New title */
  title: string;
}

/**
 * Token usage report for a turn.
 *
 * @category Session Actions
 * @version 1
 */
export interface ISessionUsageAction {
  type: 'session/usage';
  /** Session URI */
  session: URI;
  /** Turn identifier */
  turnId: string;
  /** Token usage data */
  usage: IUsageInfo;
}

/**
 * Reasoning/thinking text from the model.
 *
 * @category Session Actions
 * @version 1
 */
export interface ISessionReasoningAction {
  type: 'session/reasoning';
  /** Session URI */
  session: URI;
  /** Turn identifier */
  turnId: string;
  /** Reasoning text chunk */
  content: string;
}

/**
 * Model changed for this session.
 *
 * @category Session Actions
 * @version 1
 * @clientDispatchable
 */
export interface ISessionModelChangedAction {
  type: 'session/modelChanged';
  /** Session URI */
  session: URI;
  /** New model ID */
  model: string;
}

// ─── Discriminated Union ─────────────────────────────────────────────────────

/**
 * Discriminated union of all state actions.
 */
export type IStateAction =
  | IRootAgentsChangedAction
  | ISessionReadyAction
  | ISessionCreationFailedAction
  | ISessionTurnStartedAction
  | ISessionDeltaAction
  | ISessionResponsePartAction
  | ISessionToolCallStartAction
  | ISessionToolCallDeltaAction
  | ISessionToolCallReadyAction
  | ISessionToolCallConfirmedAction
  | ISessionToolCallCompleteAction
  | ISessionToolCallResultConfirmedAction
  | ISessionPermissionRequestAction
  | ISessionPermissionResolvedAction
  | ISessionTurnCompleteAction
  | ISessionTurnCancelledAction
  | ISessionErrorAction
  | ISessionTitleChangedAction
  | ISessionUsageAction
  | ISessionReasoningAction
  | ISessionModelChangedAction;
