/**
 * Agent Host Protocol — Type Definitions
 *
 * @module agent-host-protocol
 * @description Canonical TypeScript type definitions for the Agent Host Protocol.
 * These types are the source of truth from which documentation and JSON Schema
 * are generated.
 */

// State types
export type {
  URI,
  StringOrMarkdown,
  IRootState,
  IAgentInfo,
  ISessionModelInfo,
  ISessionState,
  ISessionSummary,
  ITurn,
  IActiveTurn,
  IUserMessage,
  IMessageAttachment,
  IMarkdownResponsePart,
  IContentRef,
  IResponsePart,
  ToolCallStatus,
  ToolCallConfirmationReason,
  IToolCallResult,
  IToolCallStreamingState,
  IToolCallPendingConfirmationState,
  IToolCallRunningState,
  IToolCallPendingResultConfirmationState,
  IToolCallCompletedState,
  IToolCallCancelledState,
  IToolCallState,
  IPermissionRequest,
  IUsageInfo,
  IErrorInfo,
  ISnapshot,
} from './state.js';

// Action types
export type {
  IActionEnvelope,
  IRootAgentsChangedAction,
  ISessionReadyAction,
  ISessionCreationFailedAction,
  ISessionTurnStartedAction,
  ISessionDeltaAction,
  ISessionResponsePartAction,
  ISessionToolCallStartAction,
  ISessionToolCallDeltaAction,
  ISessionToolCallReadyAction,
  ISessionToolCallApprovedAction,
  ISessionToolCallDeniedAction,
  ISessionToolCallConfirmedAction,
  ISessionToolCallCompleteAction,
  ISessionToolCallResultConfirmedAction,
  ISessionPermissionRequestAction,
  ISessionPermissionResolvedAction,
  ISessionTurnCompleteAction,
  ISessionTurnCancelledAction,
  ISessionErrorAction,
  ISessionTitleChangedAction,
  ISessionUsageAction,
  ISessionReasoningAction,
  ISessionModelChangedAction,
  IStateAction,
} from './actions.js';

// Command types
export type {
  IInitializeParams,
  IInitializeResult,
  IReconnectParams,
  IReconnectReplayResult,
  IReconnectSnapshotResult,
  IReconnectResult,
  ISubscribeParams,
  ISubscribeResult,
  ICreateSessionParams,
  IDisposeSessionParams,
  IListSessionsParams,
  IListSessionsResult,
  IFetchContentParams,
  IFetchContentResult,
  IBrowseDirectoryParams,
  IDirectoryEntry,
  IBrowseDirectoryResult,
  IFetchTurnsParams,
  IFetchTurnsResult,
  IUnsubscribeParams,
  IDispatchActionParams,
} from './commands.js';

// Notification types
export type {
  ISessionAddedNotification,
  ISessionRemovedNotification,
  IProtocolNotification,
} from './notifications.js';

// Error codes
export {
  JsonRpcErrorCodes,
  AhpErrorCodes,
} from './errors.js';
export type {
  AhpErrorCode,
  JsonRpcErrorCode,
} from './errors.js';

// Version registry
export {
  PROTOCOL_VERSION,
  MIN_PROTOCOL_VERSION,
  ACTION_INTRODUCED_IN,
  isActionKnownToVersion,
  capabilitiesForVersion,
} from './version/registry.js';
export type { ProtocolCapabilities } from './version/registry.js';
