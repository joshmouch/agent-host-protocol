#!/usr/bin/env node
/**
 * gen-d1.mjs — D1 schema-surface generator for AHP conformance discovery.
 *
 * Reads the five real JSON schema files (plus the TS errors source) and emits
 * one JSONL inventory row per:
 *   - RPC method (commands.schema.json $defs matching *Params)
 *   - Notification (notifications.schema.json ProtocolNotification oneOf members)
 *   - StateAction variant (actions.schema.json *Action $defs)
 *   - Error code (errors.schema.json AhpErrorCode + JsonRpcErrorCode)
 *   - State channels (state.schema.json top-level state shapes)
 *
 * All citations are REAL: file + 1-based line + verbatim excerpt from the actual
 * file at that line. This generator is the canonical re-runnable pipeline step
 * for the D1 angle.
 *
 * Usage:
 *   node conformance/discovery/scripts/gen-d1.mjs \
 *     > conformance/discovery/out/d1-schema-surface.jsonl
 */

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const FORK_ROOT = resolve(__dirname, '..', '..', '..');

// ─── helpers ─────────────────────────────────────────────────────────────────

function readJson(relPath) {
  const abs = resolve(FORK_ROOT, relPath);
  return JSON.parse(readFileSync(abs, 'utf8'));
}

function readLines(relPath) {
  const abs = resolve(FORK_ROOT, relPath);
  return readFileSync(abs, 'utf8').split('\n');
}

/**
 * Find the 1-based line number where `needle` first appears in lines[] starting
 * from `startLine` (1-based). Returns { line, excerpt } or null.
 */
function findLine(lines, needle, startLine = 1) {
  for (let i = startLine - 1; i < lines.length; i++) {
    if (lines[i].includes(needle)) {
      return { line: i + 1, excerpt: lines[i].trim() };
    }
  }
  return null;
}

/**
 * Find the 1-based line where a $def key first appears in a JSON schema file.
 * JSON schema keys look like:  "InitializeParams": {
 */
function findDefLine(lines, defName) {
  return findLine(lines, `"${defName}"`);
}

/**
 * Given description text, extract RFC-2119 level. Returns NONE if none found.
 */
function extractNormativeLevel(desc) {
  if (!desc) return 'NONE';
  if (/\bMUST NOT\b/.test(desc)) return 'MUST_NOT';
  if (/\bSHOULD NOT\b/.test(desc)) return 'SHOULD_NOT';
  if (/\bMUST\b/.test(desc)) return 'MUST';
  if (/\bSHALL\b/.test(desc)) return 'SHALL';
  if (/\bSHOULD\b/.test(desc)) return 'SHOULD';
  if (/\bREQUIRED\b/.test(desc)) return 'REQUIRED';
  if (/\bMAY\b/.test(desc)) return 'MAY';
  return 'NONE';
}

/** Emit a row as JSON string. */
function row(obj) {
  return JSON.stringify(obj);
}

// ─── load schemas ─────────────────────────────────────────────────────────────

const COMMANDS_FILE = 'schema/commands.schema.json';
const NOTIFICATIONS_FILE = 'schema/notifications.schema.json';
const ACTIONS_FILE = 'schema/actions.schema.json';
const ERRORS_FILE = 'schema/errors.schema.json';
const STATE_FILE = 'schema/state.schema.json';
const ERRORS_TS_FILE = 'types/common/errors.ts';

const commandsSchema = readJson(COMMANDS_FILE);
const notificationsSchema = readJson(NOTIFICATIONS_FILE);
const actionsSchema = readJson(ACTIONS_FILE);
const errorsSchema = readJson(ERRORS_FILE);
const stateSchema = readJson(STATE_FILE);

const commandsLines = readLines(COMMANDS_FILE);
const notificationsLines = readLines(NOTIFICATIONS_FILE);
const actionsLines = readLines(ACTIONS_FILE);
const errorsLines = readLines(ERRORS_FILE);
const stateLines = readLines(STATE_FILE);
const errorsTsLines = readLines(ERRORS_TS_FILE);

const rows = [];

// ─── RPC methods (commands.schema.json) ──────────────────────────────────────
//
// Convention: each method has a <MethodName>Params $def in commands.schema.json.
// The method name is camelCase(MethodName). We enumerate all *Params defs and
// map them to wire method names.

const METHOD_MAP = {
  InitializeParams: 'initialize',
  PingParams: 'ping',
  ReconnectParams: 'reconnect',
  SubscribeParams: 'subscribe',
  UnsubscribeParams: 'unsubscribe',
  DispatchActionParams: 'dispatchAction',
  ResourceReadParams: 'resourceRead',
  ResourceWriteParams: 'resourceWrite',
  ResourceListParams: 'resourceList',
  ResourceCopyParams: 'resourceCopy',
  ResourceDeleteParams: 'resourceDelete',
  ResourceRequestParams: 'resourceRequest',
  ResourceMoveParams: 'resourceMove',
  ResourceResolveParams: 'resourceResolve',
  ResourceMkdirParams: 'resourceMkdir',
  AuthenticateParams: 'authenticate',
  ListSessionsParams: 'listSessions',
  ResolveSessionConfigParams: 'resolveSessionConfig',
  SessionConfigCompletionsParams: 'sessionConfigCompletions',
  CreateSessionParams: 'createSession',
  DisposeSessionParams: 'disposeSession',
  FetchTurnsParams: 'fetchTurns',
  CompletionsParams: 'completions',
  CreateTerminalParams: 'createTerminal',
  DisposeTerminalParams: 'disposeTerminal',
  InvokeChangesetOperationParams: 'invokeChangesetOperation',
  CreateResourceWatchParams: 'createResourceWatch',
};

for (const [defName, methodName] of Object.entries(METHOD_MAP)) {
  const def = commandsSchema.$defs[defName];
  if (!def) continue;
  const desc = def.description || '';
  const normative = extractNormativeLevel(desc);

  // Find the line where this def appears in the schema file
  const found = findDefLine(commandsLines, defName);
  if (!found) continue;

  // Use first line of description as excerpt if available, else the def line
  let citLine = found.line;
  let citExcerpt = found.excerpt;

  // Try to find a more informative line: the "description" key line
  const descFound = findLine(commandsLines, '"description":', found.line);
  if (descFound && descFound.line < found.line + 5) {
    citLine = descFound.line;
    citExcerpt = descFound.excerpt.slice(0, 120);
  }

  // Map to domain
  let domain = 'rpc';
  let concept = methodName;

  rows.push(row({
    'behavior-id': `${domain}.${concept}.happy`,
    source: 'd1-schema',
    method: methodName,
    concept: methodName,
    'scenario-class': 'happy',
    'normative-level': normative || 'NONE',
    citation: {
      file: COMMANDS_FILE,
      line: found.line,
      excerpt: found.excerpt,
    },
    coverage: 'unknown',
    'params-shape-ref': `commands.schema.json#/$defs/${defName}`,
    notes: desc.slice(0, 200).replace(/\n/g, ' '),
    assertion: `Server responds to ${methodName} with valid result shape`,
  }));
}

// Additional error-class rows for methods that have documented error behaviors
const METHOD_ERRORS = {
  initialize: { discriminator: 'unsupported-version', errorDesc: 'UnsupportedProtocolVersion (-32005)' },
  createSession: { discriminator: 'session-already-exists', errorDesc: 'SessionAlreadyExists (-32003)' },
  subscribe: { discriminator: 'not-found', errorDesc: 'channel not found' },
  authenticate: { discriminator: 'auth-required', errorDesc: 'AuthRequired (-32007) or InvalidParams (-32602)' },
  resourceRequest: { discriminator: 'permission-denied', errorDesc: 'PermissionDenied (-32009)' },
  resourceWrite: { discriminator: 'create-only-conflict', errorDesc: 'AlreadyExists when createOnly=true' },
  resourceResolve: { discriminator: 'not-found', errorDesc: 'NotFound (-32008) for missing resource' },
};

for (const [methodName, { discriminator, errorDesc }] of Object.entries(METHOD_ERRORS)) {
  const paramsKey = Object.entries(METHOD_MAP).find(([, m]) => m === methodName)?.[0];
  if (!paramsKey) continue;
  const found = findDefLine(commandsLines, paramsKey);
  if (!found) continue;

  rows.push(row({
    'behavior-id': `rpc.${methodName}.error.${discriminator}`,
    source: 'd1-schema',
    method: methodName,
    concept: methodName,
    'scenario-class': 'error',
    'normative-level': 'MUST',
    citation: {
      file: COMMANDS_FILE,
      line: found.line,
      excerpt: found.excerpt,
    },
    coverage: 'unknown',
    'params-shape-ref': `commands.schema.json#/$defs/${paramsKey}`,
    notes: errorDesc,
    assertion: `Server returns error ${errorDesc} for the described failure condition`,
  }));
}

// ─── Reconnect-specific rows ──────────────────────────────────────────────────

// ReconnectReplayResult and ReconnectSnapshotResult are distinct protocol behaviors
for (const [defName, discriminator, concept] of [
  ['ReconnectReplayResult', 'replay-in-buffer', 'reconnect-replay'],
  ['ReconnectSnapshotResult', 'gap-exceeds-buffer-resnapshot', 'reconnect-resnapshot'],
]) {
  const def = commandsSchema.$defs[defName];
  if (!def) continue;
  const found = findDefLine(commandsLines, defName);
  if (!found) continue;
  rows.push(row({
    'behavior-id': `reconnect.${concept}.happy.${discriminator}`,
    source: 'd1-schema',
    method: 'reconnect',
    concept,
    'scenario-class': 'reconnect',
    'normative-level': 'MUST',
    citation: {
      file: COMMANDS_FILE,
      line: found.line,
      excerpt: found.excerpt,
    },
    coverage: 'unknown',
    'params-shape-ref': `commands.schema.json#/$defs/${defName}`,
    notes: (def.description || '').slice(0, 200).replace(/\n/g, ' '),
    assertion: `Server returns ${defName} when reconnect succeeds with the specified result type`,
  }));
}

// ─── Notifications (notifications.schema.json) ───────────────────────────────

// The ProtocolNotification oneOf lists the notification param types
const protoNotif = notificationsSchema.$defs?.ProtocolNotification;
const notifNames = protoNotif?.oneOf?.map((ref) => ref.$ref?.replace('#/$defs/', '')) ?? [];

// Map param-type names to notification method names (wire-level)
const NOTIF_METHOD_MAP = {
  AuthRequiredParams: 'authRequired',
  SessionAddedParams: 'root/sessionAdded',
  SessionRemovedParams: 'root/sessionRemoved',
  SessionSummaryChangedParams: 'root/sessionSummaryChanged',
  OtlpExportLogsParams: 'otlp/exportLogs',
  OtlpExportTracesParams: 'otlp/exportTraces',
  OtlpExportMetricsParams: 'otlp/exportMetrics',
};

for (const notifDefName of notifNames) {
  const def = notificationsSchema.$defs[notifDefName];
  if (!def) continue;
  const methodName = NOTIF_METHOD_MAP[notifDefName] || notifDefName;
  const desc = def.description || '';
  const normative = extractNormativeLevel(desc);

  const found = findDefLine(notificationsLines, notifDefName);
  if (!found) continue;

  // concept is the notification type name (sans "Params")
  const concept = notifDefName.replace(/Params$/, '');

  rows.push(row({
    'behavior-id': `subscription.${concept}.happy`,
    source: 'd1-schema',
    method: methodName,
    concept,
    'scenario-class': 'happy',
    'normative-level': normative || 'NONE',
    citation: {
      file: NOTIFICATIONS_FILE,
      line: found.line,
      excerpt: found.excerpt,
    },
    coverage: 'unknown',
    notes: desc.slice(0, 200).replace(/\n/g, ' '),
    assertion: `Client receives ${concept} notification with required fields`,
  }));
}

// ─── StateAction variants (actions.schema.json) ───────────────────────────────
//
// We enumerate all *Action $defs that have a discriminant `type` property
// referencing an ActionType. These represent the full StateAction union.

const ACTION_DEFS = [
  // Root actions
  ['RootAgentsChangedAction', 'root/agentsChanged'],
  ['RootActiveSessionsChangedAction', 'root/activeSessionsChanged'],
  ['RootTerminalsChangedAction', 'root/terminalsChanged'],
  ['RootConfigChangedAction', 'root/configChanged'],
  // Session lifecycle
  ['SessionReadyAction', 'session/ready'],
  ['SessionCreationFailedAction', 'session/creationFailed'],
  // Session turn
  ['SessionTurnStartedAction', 'session/turnStarted'],
  ['SessionDeltaAction', 'session/delta'],
  ['SessionResponsePartAction', 'session/responsePart'],
  // Tool call
  ['SessionToolCallStartAction', 'session/toolCallStart'],
  ['SessionToolCallDeltaAction', 'session/toolCallDelta'],
  ['SessionToolCallReadyAction', 'session/toolCallReady'],
  ['SessionToolCallApprovedAction', 'session/toolCallConfirmed'],
  ['SessionToolCallDeniedAction', 'session/toolCallConfirmed'],
  ['SessionToolCallCompleteAction', 'session/toolCallComplete'],
  ['SessionToolCallResultConfirmedAction', 'session/toolCallResultConfirmed'],
  ['SessionToolCallContentChangedAction', 'session/toolCallContentChanged'],
  // Session state
  ['SessionTurnCompleteAction', 'session/turnComplete'],
  ['SessionTurnCancelledAction', 'session/turnCancelled'],
  ['SessionErrorAction', 'session/error'],
  ['SessionTitleChangedAction', 'session/titleChanged'],
  ['SessionUsageAction', 'session/usage'],
  ['SessionReasoningAction', 'session/reasoning'],
  ['SessionModelChangedAction', 'session/modelChanged'],
  ['SessionAgentChangedAction', 'session/agentChanged'],
  ['SessionIsReadChangedAction', 'session/isReadChanged'],
  ['SessionIsArchivedChangedAction', 'session/isArchivedChanged'],
  ['SessionActivityChangedAction', 'session/activityChanged'],
  ['SessionChangesetsChangedAction', 'session/changesetsChanged'],
  ['SessionServerToolsChangedAction', 'session/serverToolsChanged'],
  ['SessionActiveClientChangedAction', 'session/activeClientChanged'],
  ['SessionActiveClientToolsChangedAction', 'session/activeClientToolsChanged'],
  ['SessionCustomizationsChangedAction', 'session/customizationsChanged'],
  ['SessionCustomizationToggledAction', 'session/customizationToggled'],
  ['SessionCustomizationUpdatedAction', 'session/customizationUpdated'],
  ['SessionCustomizationRemovedAction', 'session/customizationRemoved'],
  ['SessionConfigChangedAction', 'session/configChanged'],
  ['SessionMetaChangedAction', 'session/metaChanged'],
  ['SessionTruncatedAction', 'session/truncated'],
  // Pending messages
  ['SessionPendingMessageSetAction', 'session/pendingMessageSet'],
  ['SessionPendingMessageRemovedAction', 'session/pendingMessageRemoved'],
  ['SessionQueuedMessagesReorderedAction', 'session/queuedMessagesReordered'],
  // Input
  ['SessionInputRequestedAction', 'session/inputRequested'],
  ['SessionInputAnswerChangedAction', 'session/inputAnswerChanged'],
  ['SessionInputCompletedAction', 'session/inputCompleted'],
  // Changeset
  ['ChangesetStatusChangedAction', 'changeset/statusChanged'],
  ['ChangesetFileSetAction', 'changeset/fileSet'],
  ['ChangesetFileRemovedAction', 'changeset/fileRemoved'],
  ['ChangesetOperationsChangedAction', 'changeset/operationsChanged'],
  ['ChangesetOperationStatusChangedAction', 'changeset/operationStatusChanged'],
  ['ChangesetClearedAction', 'changeset/cleared'],
  // Terminal
  ['TerminalDataAction', 'terminal/data'],
  ['TerminalInputAction', 'terminal/input'],
  ['TerminalResizedAction', 'terminal/resized'],
  ['TerminalClaimedAction', 'terminal/claimed'],
  ['TerminalTitleChangedAction', 'terminal/titleChanged'],
  ['TerminalCwdChangedAction', 'terminal/cwdChanged'],
  ['TerminalExitedAction', 'terminal/exited'],
  ['TerminalClearedAction', 'terminal/cleared'],
  ['TerminalCommandDetectionAvailableAction', 'terminal/commandDetectionAvailable'],
  ['TerminalCommandExecutedAction', 'terminal/commandExecuted'],
  ['TerminalCommandFinishedAction', 'terminal/commandFinished'],
  // Resource watch
  ['ResourceWatchChangedAction', 'resourceWatch/changed'],
];

// Track used behavior-ids to avoid duplicates (SessionToolCallApproved and Denied share type)
const usedActionIds = new Set();

for (const [defName, actionType] of ACTION_DEFS) {
  const def = actionsSchema.$defs[defName];
  if (!def) continue;
  const desc = def.description || '';
  const normative = extractNormativeLevel(desc);

  const found = findDefLine(actionsLines, defName);
  if (!found) continue;

  // concept = StateAction:<ActionTypeName>
  const actionTypeName = actionType.replace('/', '-');
  const behaviorId = `action.${actionTypeName}.happy`;
  if (usedActionIds.has(behaviorId)) {
    // For the toolCallConfirmed split, emit both approved and denied variants
    const variant = defName.includes('Approved') ? 'approved' : defName.includes('Denied') ? 'denied' : 'alt';
    const altId = `action.${actionTypeName}.happy.${variant}`;
    rows.push(row({
      'behavior-id': altId,
      source: 'd1-schema',
      method: actionType,
      concept: `StateAction:${defName.replace(/Action$/, '')}`,
      'scenario-class': 'happy',
      'normative-level': normative || 'NONE',
      citation: {
        file: ACTIONS_FILE,
        line: found.line,
        excerpt: found.excerpt,
      },
      coverage: 'unknown',
      'params-shape-ref': `actions.schema.json#/$defs/${defName}`,
      notes: desc.slice(0, 200).replace(/\n/g, ' '),
      assertion: `Action ${actionType} (${variant}) reduces correctly in state`,
    }));
    continue;
  }
  usedActionIds.add(behaviorId);

  rows.push(row({
    'behavior-id': behaviorId,
    source: 'd1-schema',
    method: actionType,
    concept: `StateAction:${defName.replace(/Action$/, '')}`,
    'scenario-class': 'happy',
    'normative-level': normative || 'NONE',
    citation: {
      file: ACTIONS_FILE,
      line: found.line,
      excerpt: found.excerpt,
    },
    coverage: 'unknown',
    'params-shape-ref': `actions.schema.json#/$defs/${defName}`,
    notes: desc.slice(0, 200).replace(/\n/g, ' '),
    assertion: `Action ${actionType} reduces state correctly`,
  }));
}

// ─── Error codes ──────────────────────────────────────────────────────────────
//
// Read error names + codes from the TS source (types/common/errors.ts) for
// accurate naming; ground citations in errors.schema.json for the schema surface.

const AHP_ERRORS = [
  ['SessionNotFound', -32001],
  ['ProviderNotFound', -32002],
  ['SessionAlreadyExists', -32003],
  ['TurnInProgress', -32004],
  ['UnsupportedProtocolVersion', -32005],
  ['ContentNotFound', -32006],
  ['AuthRequired', -32007],
  ['NotFound', -32008],
  ['PermissionDenied', -32009],
  ['AlreadyExists', -32010],
  ['Conflict', -32011],
];

const JSON_RPC_ERRORS = [
  ['ParseError', -32700],
  ['InvalidRequest', -32600],
  ['MethodNotFound', -32601],
  ['InvalidParams', -32602],
  ['InternalError', -32603],
];

// Find the AhpErrorCode def in errors.schema.json for citation
const ahpErrorCodeFound = findLine(errorsLines, '"AhpErrorCode"');
const jsonRpcErrorCodeFound = findLine(errorsLines, '"JsonRpcErrorCode"');

// For AHP errors, find them in the TS errors file (which is the real TS source of truth)
for (const [name, code] of AHP_ERRORS) {
  // Ground in the TS source which has actual named constants
  const tsFound = findLine(errorsTsLines, `${name}:`);
  if (!tsFound) continue;

  rows.push(row({
    'behavior-id': `error.${name}.error`,
    source: 'd1-schema',
    method: null,
    concept: `error:${name}`,
    'scenario-class': 'error',
    'normative-level': 'NONE',
    citation: {
      file: ERRORS_TS_FILE,
      line: tsFound.line,
      excerpt: tsFound.excerpt,
    },
    coverage: 'unknown',
    notes: `AHP error code ${code}: ${name}`,
    assertion: `Server returns JSON-RPC error with code ${code} (${name}) in the described failure conditions`,
  }));
}

for (const [name, code] of JSON_RPC_ERRORS) {
  const tsFound = findLine(errorsTsLines, `${name}:`);
  if (!tsFound) continue;

  rows.push(row({
    'behavior-id': `error.${name}.error`,
    source: 'd1-schema',
    method: null,
    concept: `error:${name}`,
    'scenario-class': 'error',
    'normative-level': 'NONE',
    citation: {
      file: ERRORS_TS_FILE,
      line: tsFound.line,
      excerpt: tsFound.excerpt,
    },
    coverage: 'unknown',
    notes: `Standard JSON-RPC error code ${code}: ${name}`,
    assertion: `Server returns JSON-RPC error with code ${code} (${name}) for standard protocol violations`,
  }));
}

// ─── protocolVersion / capabilities surface ───────────────────────────────────

// InitializeResult carries protocolVersion + serverSeq + snapshots + telemetry
const initResultFound = findDefLine(commandsLines, 'InitializeResult');
if (initResultFound) {
  rows.push(row({
    'behavior-id': 'versioning.protocolVersion.happy.server-selects',
    source: 'd1-schema',
    method: 'initialize',
    concept: 'protocolVersion',
    'scenario-class': 'happy',
    'normative-level': 'MUST',
    citation: {
      file: COMMANDS_FILE,
      line: initResultFound.line,
      excerpt: initResultFound.excerpt,
    },
    coverage: 'unknown',
    'params-shape-ref': 'commands.schema.json#/$defs/InitializeResult',
    notes: 'Server selects one entry from client protocolVersions and returns it as InitializeResult.protocolVersion',
    assertion: 'InitializeResult.protocolVersion is one of the values offered in InitializeParams.protocolVersions',
  }));

  rows.push(row({
    'behavior-id': 'versioning.protocolVersion.error.unsupported',
    source: 'd1-schema',
    method: 'initialize',
    concept: 'protocolVersion',
    'scenario-class': 'version',
    'normative-level': 'MUST',
    citation: {
      file: COMMANDS_FILE,
      line: initResultFound.line,
      excerpt: initResultFound.excerpt,
    },
    coverage: 'unknown',
    notes: 'If server cannot speak any offered version, MUST return error -32005 UnsupportedProtocolVersion',
    assertion: 'Server returns error code -32005 when no offered protocolVersion is supported',
  }));
}

// TelemetryCapabilities surface
const telemetryFound = findDefLine(commandsLines, 'TelemetryCapabilities');
if (telemetryFound) {
  rows.push(row({
    'behavior-id': 'channel.telemetry.happy.otlp-capabilities',
    source: 'd1-schema',
    method: 'initialize',
    concept: 'TelemetryCapabilities',
    'scenario-class': 'happy',
    'normative-level': 'MAY',
    citation: {
      file: COMMANDS_FILE,
      line: telemetryFound.line,
      excerpt: telemetryFound.excerpt,
    },
    coverage: 'unknown',
    'params-shape-ref': 'commands.schema.json#/$defs/TelemetryCapabilities',
    notes: 'OTLP telemetry channels the host emits: logs, traces, metrics. Clients MAY ignore signals they cannot process.',
    assertion: 'InitializeResult.telemetry lists OTLP channel URIs; client may subscribe to receive OTLP batches',
  }));
}

// ─── State channel shapes (state.schema.json) ─────────────────────────────────
// The canonical state shapes describe what snapshot data looks like per channel.

const STATE_SHAPES = [
  ['RootState', 'ahp-root://', 'root-channel'],
  ['SessionState', 'ahp-session:/<uuid>', 'session-channel'],
  ['TerminalState', 'ahp-terminal:/<id>', 'terminal-channel'],
  ['ChangesetState', 'ahp-changeset:/<id>', 'changeset-channel'],
];

for (const [defName, channelPattern, concept] of STATE_SHAPES) {
  const def = stateSchema.$defs?.[defName];
  if (!def) continue;
  const desc = def.description || '';
  const found = findDefLine(stateLines, defName);
  if (!found) continue;

  rows.push(row({
    'behavior-id': `state.${concept}.happy.snapshot-shape`,
    source: 'd1-schema',
    method: 'subscribe',
    concept,
    'scenario-class': 'happy',
    'normative-level': 'NONE',
    citation: {
      file: STATE_FILE,
      line: found.line,
      excerpt: found.excerpt,
    },
    coverage: 'unknown',
    'params-shape-ref': `state.schema.json#/$defs/${defName}`,
    notes: `Channel pattern: ${channelPattern}. ${desc.slice(0, 150).replace(/\n/g, ' ')}`,
    assertion: `Snapshot for ${channelPattern} channel has shape matching ${defName}`,
  }));
}

// ─── ActionEnvelope surface ───────────────────────────────────────────────────
const envelopeFound = findDefLine(actionsLines, 'ActionEnvelope');
if (envelopeFound) {
  rows.push(row({
    'behavior-id': 'action.ActionEnvelope.happy.routing-invariant',
    source: 'd1-schema',
    method: 'action',
    concept: 'ActionEnvelope',
    'scenario-class': 'happy',
    'normative-level': 'NONE',
    citation: {
      file: ACTIONS_FILE,
      line: envelopeFound.line,
      excerpt: envelopeFound.excerpt,
    },
    coverage: 'unknown',
    'params-shape-ref': 'actions.schema.json#/$defs/ActionEnvelope',
    notes: 'Every action is wrapped in an ActionEnvelope carrying channel, action, serverSeq, origin. The channel field routes actions uniformly.',
    assertion: 'Every action notification carries channel + action + serverSeq + origin fields',
  }));
}

// ─── Snapshot shape (used in subscribe + initialize + reconnect) ──────────────
const snapshotFoundCmd = findDefLine(commandsLines, 'Snapshot');
if (snapshotFoundCmd) {
  rows.push(row({
    'behavior-id': 'subscription.Snapshot.happy.round-trip',
    source: 'd1-schema',
    method: 'subscribe',
    concept: 'Snapshot',
    'scenario-class': 'happy',
    'normative-level': 'NONE',
    citation: {
      file: COMMANDS_FILE,
      line: snapshotFoundCmd.line,
      excerpt: snapshotFoundCmd.excerpt,
    },
    coverage: 'unknown',
    'params-shape-ref': 'commands.schema.json#/$defs/Snapshot',
    notes: 'A point-in-time snapshot of a subscribed resource state. Returned by initialize, reconnect, and subscribe.',
    assertion: 'SubscribeResult.snapshot has resource + state + fromSeq fields matching the channel type',
  }));
}

// ─── output ───────────────────────────────────────────────────────────────────
for (const r of rows) {
  process.stdout.write(r + '\n');
}
process.stderr.write(`gen-d1: emitted ${rows.length} rows\n`);
