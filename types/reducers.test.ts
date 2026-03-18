/**
 * Reducer unit tests.
 *
 * Run: npx tsx --test types/reducers.test.ts
 */

import { describe, it } from 'node:test';
import { strict as assert } from 'node:assert';
import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  rootReducer,
  sessionReducer,
  isClientDispatchable,
} from './reducers.js';
import { IS_CLIENT_DISPATCHABLE } from './action-origin.generated.js';
import { ActionType } from './actions.js';
import type { IRootState, ISessionState } from './state.js';
import {
  SessionLifecycle,
  SessionStatus,
  TurnState,
  ToolCallStatus,
  ToolCallConfirmationReason,
  ToolCallCancellationReason,
  ResponsePartKind,
  PermissionKind,
} from './state.js';

const root = resolve(dirname(fileURLToPath(import.meta.url)));

function readSource(file: string): string {
  return readFileSync(resolve(root, file), 'utf-8');
}

// ─── Test Fixtures ───────────────────────────────────────────────────────────

const S = 'copilot:/test-session';
const T = 'turn-1';
const TC = 'tc-1';

function makeRootState(overrides?: Partial<IRootState>): IRootState {
  return {
    agents: [],
    ...overrides,
  };
}

function makeSessionState(overrides?: Partial<ISessionState>): ISessionState {
  return {
    summary: {
      resource: S,
      provider: 'copilot',
      title: 'Test Session',
      status: SessionStatus.Idle,
      createdAt: 1000,
      modifiedAt: 1000,
    },
    lifecycle: SessionLifecycle.Creating,
    turns: [],
    ...overrides,
  };
}

function makeSessionStateWithActiveTurn(overrides?: Partial<ISessionState>): ISessionState {
  return makeSessionState({
    lifecycle: SessionLifecycle.Ready,
    summary: {
      resource: S,
      provider: 'copilot',
      title: 'Test Session',
      status: SessionStatus.InProgress,
      createdAt: 1000,
      modifiedAt: 2000,
    },
    activeTurn: {
      id: T,
      userMessage: { text: 'Hello' },
      streamingText: '',
      responseParts: [],
      toolCalls: {},
      pendingPermissions: {},
      reasoning: '',
      usage: undefined,
    },
    ...overrides,
  });
}

/** Starts a tool call in streaming state. */
function startToolCall(state: ISessionState, toolCallId = TC): ISessionState {
  return sessionReducer(state, {
    type: ActionType.SessionToolCallStart,
    session: S, turnId: T, toolCallId,
    toolName: 'bash', displayName: 'Run Command',
  });
}

/** Advances a streaming tool call to running (auto-confirmed). */
function readyToolCallAutoConfirm(state: ISessionState, toolCallId = TC): ISessionState {
  return sessionReducer(state, {
    type: ActionType.SessionToolCallReady,
    session: S, turnId: T, toolCallId,
    invocationMessage: 'Run',
    confirmed: ToolCallConfirmationReason.NotNeeded,
  });
}

// ─── IS_CLIENT_DISPATCHABLE validation ───────────────────────────────────────

describe('IS_CLIENT_DISPATCHABLE', () => {
  it('matches @clientDispatchable annotations in actions.ts', () => {
    const source = readSource('actions.ts');

    // Parse JSDoc blocks for @clientDispatchable and extract ActionType references
    const jsdocInterfaceRe = /\/\*\*([\s\S]*?)\*\/\s*export\s+(?:interface|type)\s+(\w+)/g;
    const clientDispatchableTypes = new Set<string>();

    for (const match of source.matchAll(jsdocInterfaceRe)) {
      const [, jsdoc, name] = match;
      if (!name.endsWith('Action')) continue;

      const afterDecl = source.slice(match.index! + match[0].length);
      const typeMatch = afterDecl.match(/type:\s*ActionType\.(\w+)/);
      if (!typeMatch) continue;

      if (jsdoc.includes('@clientDispatchable')) {
        clientDispatchableTypes.add(typeMatch[1]);
      }
    }

    // Map ActionType enum members to their string values
    const enumValueRe = /(\w+)\s*=\s*'([^']+)'/g;
    const enumMap = new Map<string, string>();
    for (const match of source.matchAll(enumValueRe)) {
      enumMap.set(match[1], match[2]);
    }

    for (const [memberName, stringValue] of enumMap) {
      if (!(stringValue in IS_CLIENT_DISPATCHABLE)) continue;
      const expected = clientDispatchableTypes.has(memberName);
      const actual = IS_CLIENT_DISPATCHABLE[stringValue as keyof typeof IS_CLIENT_DISPATCHABLE];
      assert.equal(
        actual,
        expected,
        `IS_CLIENT_DISPATCHABLE['${stringValue}'] should be ${expected} (ActionType.${memberName})`,
      );
    }
  });

  it('covers every ActionType enum member', () => {
    const enumValueRe = /(\w+)\s*=\s*'([^']+)'/g;
    const allValues: string[] = [];
    for (const match of readSource('actions.ts').matchAll(enumValueRe)) {
      allValues.push(match[2]);
    }

    const mapKeys = Object.keys(IS_CLIENT_DISPATCHABLE);
    const missing = allValues.filter(v => !mapKeys.includes(v));
    assert.deepStrictEqual(missing, [], `Missing from IS_CLIENT_DISPATCHABLE: ${missing.join(', ')}`);

    const extra = mapKeys.filter(v => !allValues.includes(v));
    assert.deepStrictEqual(extra, [], `Extra in IS_CLIENT_DISPATCHABLE: ${extra.join(', ')}`);
  });
});

// ─── Root Reducer ────────────────────────────────────────────────────────────

describe('rootReducer', () => {
  it('handles root/agentsChanged', () => {
    const state = makeRootState();
    const agents = [{ provider: 'copilot', displayName: 'Copilot', description: 'AI', models: [] }];
    const next = rootReducer(state, { type: ActionType.RootAgentsChanged, agents });
    assert.deepStrictEqual(next.agents, agents);
  });

  it('handles root/activeSessionsChanged', () => {
    const state = makeRootState();
    const next = rootReducer(state, { type: ActionType.RootActiveSessionsChanged, activeSessions: 5 });
    assert.equal(next.activeSessions, 5);
  });

  it('does not mutate original state', () => {
    const state = makeRootState({ agents: [] });
    const agents = [{ provider: 'x', displayName: 'X', description: 'x', models: [] }];
    rootReducer(state, { type: ActionType.RootAgentsChanged, agents });
    assert.deepStrictEqual(state.agents, []);
  });
});

// ─── Session Reducer: Lifecycle ──────────────────────────────────────────────

describe('sessionReducer — lifecycle', () => {
  it('handles session/ready', () => {
    const state = makeSessionState();
    const next = sessionReducer(state, { type: ActionType.SessionReady, session: S });
    assert.equal(next.lifecycle, SessionLifecycle.Ready);
    assert.equal(next.summary.status, SessionStatus.Idle);
  });

  it('handles session/creationFailed', () => {
    const state = makeSessionState();
    const error = { errorType: 'init', message: 'Failed to start' };
    const next = sessionReducer(state, { type: ActionType.SessionCreationFailed, session: S, error });
    assert.equal(next.lifecycle, SessionLifecycle.CreationFailed);
    assert.deepStrictEqual(next.creationError, error);
  });
});

// ─── Session Reducer: Turn Lifecycle ─────────────────────────────────────────

describe('sessionReducer — turn lifecycle', () => {
  it('handles session/turnStarted', () => {
    const state = makeSessionState({ lifecycle: SessionLifecycle.Ready });
    const next = sessionReducer(state, {
      type: ActionType.SessionTurnStarted, session: S, turnId: T,
      userMessage: { text: 'Hello' },
    });
    assert.equal(next.summary.status, SessionStatus.InProgress);
    assert.ok(next.activeTurn);
    assert.equal(next.activeTurn!.id, T);
    assert.equal(next.activeTurn!.userMessage.text, 'Hello');
    assert.equal(next.activeTurn!.streamingText, '');
    assert.deepStrictEqual(next.activeTurn!.responseParts, []);
    assert.deepStrictEqual(next.activeTurn!.toolCalls, {});
    assert.deepStrictEqual(next.activeTurn!.pendingPermissions, {});
  });

  it('handles session/delta', () => {
    const state = makeSessionStateWithActiveTurn();
    let next = sessionReducer(state, { type: ActionType.SessionDelta, session: S, turnId: T, content: 'Hello ' });
    next = sessionReducer(next, { type: ActionType.SessionDelta, session: S, turnId: T, content: 'world' });
    assert.equal(next.activeTurn!.streamingText, 'Hello world');
  });

  it('ignores session/delta with wrong turnId', () => {
    const state = makeSessionStateWithActiveTurn();
    const next = sessionReducer(state, { type: ActionType.SessionDelta, session: S, turnId: 'wrong-turn', content: 'orphan' });
    assert.equal(next, state);
  });

  it('ignores session/delta without activeTurn', () => {
    const state = makeSessionState();
    const next = sessionReducer(state, { type: ActionType.SessionDelta, session: S, turnId: T, content: 'orphan' });
    assert.equal(next, state);
  });

  it('handles session/responsePart', () => {
    const state = makeSessionStateWithActiveTurn();
    const part = { kind: ResponsePartKind.Markdown as const, content: '# Title' };
    const next = sessionReducer(state, { type: ActionType.SessionResponsePart, session: S, turnId: T, part });
    assert.equal(next.activeTurn!.responseParts.length, 1);
    assert.deepStrictEqual(next.activeTurn!.responseParts[0], part);
  });

  it('handles session/turnComplete — finalizes turn', () => {
    const state = makeSessionStateWithActiveTurn();
    let s = sessionReducer(state, { type: ActionType.SessionDelta, session: S, turnId: T, content: 'Response text' });
    s = sessionReducer(s, { type: ActionType.SessionTurnComplete, session: S, turnId: T });

    assert.equal(s.activeTurn, undefined);
    assert.equal(s.turns.length, 1);
    assert.equal(s.turns[0].id, T);
    assert.equal(s.turns[0].responseText, 'Response text');
    assert.equal(s.turns[0].state, TurnState.Complete);
    assert.equal(s.summary.status, SessionStatus.Idle);
  });

  it('handles session/turnCancelled — finalizes turn', () => {
    const state = makeSessionStateWithActiveTurn();
    const next = sessionReducer(state, { type: ActionType.SessionTurnCancelled, session: S, turnId: T });
    assert.equal(next.activeTurn, undefined);
    assert.equal(next.turns.length, 1);
    assert.equal(next.turns[0].state, TurnState.Cancelled);
    assert.equal(next.summary.status, SessionStatus.Idle);
  });

  it('handles session/error — finalizes turn with error', () => {
    const state = makeSessionStateWithActiveTurn();
    const error = { errorType: 'runtime', message: 'Something broke' };
    const next = sessionReducer(state, { type: ActionType.SessionError, session: S, turnId: T, error });
    assert.equal(next.activeTurn, undefined);
    assert.equal(next.turns.length, 1);
    assert.equal(next.turns[0].state, TurnState.Error);
    assert.deepStrictEqual(next.turns[0].error, error);
    assert.equal(next.summary.status, SessionStatus.Error);
  });

  it('force-cancels in-progress tool calls on turn completion with skipped reason', () => {
    let state = startToolCall(makeSessionStateWithActiveTurn());
    const next = sessionReducer(state, { type: ActionType.SessionTurnComplete, session: S, turnId: T });
    assert.equal(next.turns[0].toolCalls.length, 1);
    assert.equal(next.turns[0].toolCalls[0].status, ToolCallStatus.Cancelled);
    if (next.turns[0].toolCalls[0].status === ToolCallStatus.Cancelled) {
      assert.equal(next.turns[0].toolCalls[0].reason, ToolCallCancellationReason.Skipped);
    }
  });

  it('ignores turn completion with wrong turnId', () => {
    const state = makeSessionStateWithActiveTurn();
    const next = sessionReducer(state, { type: ActionType.SessionTurnComplete, session: S, turnId: 'wrong-turn' });
    assert.ok(next.activeTurn);
    assert.equal(next, state);
  });
});

// ─── Session Reducer: Tool Call State Machine ────────────────────────────────

describe('sessionReducer — tool calls', () => {
  it('handles full tool call lifecycle: start → delta → ready → confirmed → complete', () => {
    let state = startToolCall(makeSessionStateWithActiveTurn());
    assert.equal(state.activeTurn!.toolCalls[TC].status, ToolCallStatus.Streaming);

    // Delta
    state = sessionReducer(state, {
      type: ActionType.SessionToolCallDelta, session: S, turnId: T, toolCallId: TC,
      content: 'ls -la', invocationMessage: 'Listing files',
    });
    const streaming = state.activeTurn!.toolCalls[TC];
    assert.equal(streaming.status, ToolCallStatus.Streaming);
    if (streaming.status === ToolCallStatus.Streaming) {
      assert.equal(streaming.partialInput, 'ls -la');
      assert.equal(streaming.invocationMessage, 'Listing files');
    }

    // Ready (no auto-confirm → pending confirmation)
    state = sessionReducer(state, {
      type: ActionType.SessionToolCallReady, session: S, turnId: T, toolCallId: TC,
      invocationMessage: 'Run: ls -la', toolInput: 'ls -la',
    });
    assert.equal(state.activeTurn!.toolCalls[TC].status, ToolCallStatus.PendingConfirmation);

    // Confirmed (approved)
    state = sessionReducer(state, {
      type: ActionType.SessionToolCallConfirmed, session: S, turnId: T, toolCallId: TC,
      approved: true, confirmed: ToolCallConfirmationReason.UserAction,
    });
    //@ts-ignore
    assert.equal(state.activeTurn!.toolCalls[TC].status, ToolCallStatus.Running);

    // Complete
    state = sessionReducer(state, {
      type: ActionType.SessionToolCallComplete, session: S, turnId: T, toolCallId: TC,
      result: { success: true, pastTenseMessage: 'Ran command' },
    });
    //@ts-ignore
    assert.equal(state.activeTurn!.toolCalls[TC].status, ToolCallStatus.Completed);
  });

  it('handles tool call ready with auto-confirm → running', () => {
    let state = readyToolCallAutoConfirm(startToolCall(makeSessionStateWithActiveTurn()));
    const tc = state.activeTurn!.toolCalls[TC];
    assert.equal(tc.status, ToolCallStatus.Running);
    if (tc.status === ToolCallStatus.Running) {
      assert.equal(tc.confirmed, ToolCallConfirmationReason.NotNeeded);
    }
  });

  it('handles tool call denied → cancelled', () => {
    let state = startToolCall(makeSessionStateWithActiveTurn());
    state = sessionReducer(state, {
      type: ActionType.SessionToolCallReady, session: S, turnId: T, toolCallId: TC,
      invocationMessage: 'Run: rm -rf /',
    });
    state = sessionReducer(state, {
      type: ActionType.SessionToolCallConfirmed, session: S, turnId: T, toolCallId: TC,
      approved: false, reason: ToolCallCancellationReason.Denied,
    });
    const tc = state.activeTurn!.toolCalls[TC];
    assert.equal(tc.status, ToolCallStatus.Cancelled);
    if (tc.status === ToolCallStatus.Cancelled) {
      assert.equal(tc.reason, ToolCallCancellationReason.Denied);
    }
  });

  it('handles tool call complete with result confirmation → pending → approved', () => {
    let state = readyToolCallAutoConfirm(startToolCall(makeSessionStateWithActiveTurn()));
    state = sessionReducer(state, {
      type: ActionType.SessionToolCallComplete, session: S, turnId: T, toolCallId: TC,
      result: { success: true, pastTenseMessage: 'Done' }, requiresResultConfirmation: true,
    });
    assert.equal(state.activeTurn!.toolCalls[TC].status, ToolCallStatus.PendingResultConfirmation);

    state = sessionReducer(state, {
      type: ActionType.SessionToolCallResultConfirmed, session: S, turnId: T, toolCallId: TC,
      approved: true,
    });
    assert.equal(state.activeTurn!.toolCalls[TC].status, ToolCallStatus.Completed);
  });

  it('handles tool call result denied → cancelled with result-denied reason', () => {
    let state = readyToolCallAutoConfirm(startToolCall(makeSessionStateWithActiveTurn()));
    state = sessionReducer(state, {
      type: ActionType.SessionToolCallComplete, session: S, turnId: T, toolCallId: TC,
      result: { success: true, pastTenseMessage: 'Done' }, requiresResultConfirmation: true,
    });
    state = sessionReducer(state, {
      type: ActionType.SessionToolCallResultConfirmed, session: S, turnId: T, toolCallId: TC,
      approved: false,
    });
    const tc = state.activeTurn!.toolCalls[TC];
    assert.equal(tc.status, ToolCallStatus.Cancelled);
    if (tc.status === ToolCallStatus.Cancelled) {
      assert.equal(tc.reason, ToolCallCancellationReason.ResultDenied);
    }
  });

  it('handles tool call complete from pending-confirmation with defaulted confirmed', () => {
    let state = startToolCall(makeSessionStateWithActiveTurn());
    state = sessionReducer(state, {
      type: ActionType.SessionToolCallReady, session: S, turnId: T, toolCallId: TC,
      invocationMessage: 'Run',
    });
    assert.equal(state.activeTurn!.toolCalls[TC].status, ToolCallStatus.PendingConfirmation);
    state = sessionReducer(state, {
      type: ActionType.SessionToolCallComplete, session: S, turnId: T, toolCallId: TC,
      result: { success: true, pastTenseMessage: 'Done' },
    });
    const tc = state.activeTurn!.toolCalls[TC];
    assert.equal(tc.status, ToolCallStatus.Completed);
    if (tc.status === ToolCallStatus.Completed) {
      //@ts-ignore
      assert.equal(tc.confirmed, ToolCallConfirmationReason.NotNeeded);
    }
  });

  it('ignores tool call actions for unknown toolCallId', () => {
    const state = makeSessionStateWithActiveTurn();
    const next = sessionReducer(state, {
      type: ActionType.SessionToolCallDelta, session: S, turnId: T,
      toolCallId: 'nonexistent', content: 'data',
    });
    assert.equal(next, state);
  });
});

// ─── Session Reducer: Permissions ────────────────────────────────────────────

describe('sessionReducer — permissions', () => {
  it('handles session/permissionRequest and session/permissionResolved', () => {
    let state = makeSessionStateWithActiveTurn();
    const request = { requestId: 'perm-1', permissionKind: PermissionKind.Shell, fullCommandText: 'rm -rf /tmp/test' };

    state = sessionReducer(state, { type: ActionType.SessionPermissionRequest, session: S, turnId: T, request });
    assert.ok(state.activeTurn!.pendingPermissions['perm-1']);
    assert.equal(state.activeTurn!.pendingPermissions['perm-1'].requestId, 'perm-1');

    state = sessionReducer(state, { type: ActionType.SessionPermissionResolved, session: S, turnId: T, requestId: 'perm-1', approved: true });
    assert.equal(state.activeTurn!.pendingPermissions['perm-1'], undefined);
  });

  it('permissionRequest transitions associated tool call to pending-confirmation', () => {
    let state = readyToolCallAutoConfirm(startToolCall(makeSessionStateWithActiveTurn()));
    assert.equal(state.activeTurn!.toolCalls[TC].status, ToolCallStatus.Running);

    state = sessionReducer(state, {
      type: ActionType.SessionPermissionRequest, session: S, turnId: T,
      request: { requestId: 'perm-1', permissionKind: PermissionKind.Shell, toolCallId: TC },
    });
    assert.equal(state.activeTurn!.toolCalls[TC].status, ToolCallStatus.PendingConfirmation);
  });

  it('permissionResolved (approved) transitions associated tool call back to running', () => {
    let state = readyToolCallAutoConfirm(startToolCall(makeSessionStateWithActiveTurn()));
    state = sessionReducer(state, {
      type: ActionType.SessionPermissionRequest, session: S, turnId: T,
      request: { requestId: 'perm-1', permissionKind: PermissionKind.Shell, toolCallId: TC },
    });
    assert.equal(state.activeTurn!.toolCalls[TC].status, ToolCallStatus.PendingConfirmation);

    state = sessionReducer(state, {
      type: ActionType.SessionPermissionResolved, session: S, turnId: T,
      requestId: 'perm-1', approved: true,
    });
    assert.equal(state.activeTurn!.toolCalls[TC].status, ToolCallStatus.Running);
  });

  it('permissionResolved (denied) transitions associated tool call to cancelled', () => {
    let state = readyToolCallAutoConfirm(startToolCall(makeSessionStateWithActiveTurn()));
    state = sessionReducer(state, {
      type: ActionType.SessionPermissionRequest, session: S, turnId: T,
      request: { requestId: 'perm-1', permissionKind: PermissionKind.Shell, toolCallId: TC },
    });
    state = sessionReducer(state, {
      type: ActionType.SessionPermissionResolved, session: S, turnId: T,
      requestId: 'perm-1', approved: false,
    });
    assert.equal(state.activeTurn!.toolCalls[TC].status, ToolCallStatus.Cancelled);
  });
});

// ─── Session Reducer: Metadata ───────────────────────────────────────────────

describe('sessionReducer — metadata', () => {
  it('handles session/titleChanged and bumps modifiedAt', () => {
    const state = makeSessionState();
    const next = sessionReducer(state, { type: ActionType.SessionTitleChanged, session: S, title: 'New Title' });
    assert.equal(next.summary.title, 'New Title');
    assert.ok(next.summary.modifiedAt > state.summary.modifiedAt);
  });

  it('handles session/usage', () => {
    const state = makeSessionStateWithActiveTurn();
    const usage = { inputTokens: 100, outputTokens: 50 };
    const next = sessionReducer(state, { type: ActionType.SessionUsage, session: S, turnId: T, usage });
    assert.deepStrictEqual(next.activeTurn!.usage, usage);
  });

  it('handles session/reasoning', () => {
    const state = makeSessionStateWithActiveTurn();
    let next = sessionReducer(state, { type: ActionType.SessionReasoning, session: S, turnId: T, content: 'Thinking about ' });
    next = sessionReducer(next, { type: ActionType.SessionReasoning, session: S, turnId: T, content: 'the answer' });
    assert.equal(next.activeTurn!.reasoning, 'Thinking about the answer');
  });

  it('handles session/modelChanged and bumps modifiedAt', () => {
    const state = makeSessionState();
    const next = sessionReducer(state, { type: ActionType.SessionModelChanged, session: S, model: 'gpt-4' });
    assert.equal(next.summary.model, 'gpt-4');
    assert.ok(next.summary.modifiedAt > state.summary.modifiedAt);
  });

  it('handles session/serverToolsChanged', () => {
    const state = makeSessionState();
    const tools = [{ name: 'bash', description: 'Run shell commands' }];
    const next = sessionReducer(state, { type: ActionType.SessionServerToolsChanged, session: S, tools });
    assert.deepStrictEqual(next.serverTools, tools);
  });

  it('handles session/activeClientChanged — set client', () => {
    const state = makeSessionState();
    const activeClient = { clientId: 'vscode-1', displayName: 'VS Code', tools: [] };
    const next = sessionReducer(state, { type: ActionType.SessionActiveClientChanged, session: S, activeClient });
    assert.deepStrictEqual(next.activeClient, activeClient);
  });

  it('handles session/activeClientChanged — unset client', () => {
    const state = makeSessionState({ activeClient: { clientId: 'vscode-1', tools: [] } });
    const next = sessionReducer(state, { type: ActionType.SessionActiveClientChanged, session: S, activeClient: null });
    assert.equal(next.activeClient, undefined);
  });

  it('handles session/activeClientToolsChanged', () => {
    const state = makeSessionState({ activeClient: { clientId: 'vscode-1', tools: [] } });
    const tools = [{ name: 'openFile', description: 'Open a file' }];
    const next = sessionReducer(state, { type: ActionType.SessionActiveClientToolsChanged, session: S, tools });
    assert.deepStrictEqual(next.activeClient!.tools, tools);
  });

  it('ignores session/activeClientToolsChanged without activeClient', () => {
    const state = makeSessionState();
    const next = sessionReducer(state, { type: ActionType.SessionActiveClientToolsChanged, session: S, tools: [{ name: 'openFile' }] });
    assert.equal(next, state);
  });
});

// ─── Dispatch Validation ─────────────────────────────────────────────────────

describe('isClientDispatchable', () => {
  it('returns true for client-dispatchable actions', () => {
    const action = { type: ActionType.SessionTurnStarted, session: S, turnId: T, userMessage: { text: 'Hello' } } as const;
    assert.equal(isClientDispatchable(action), true);
  });

  it('returns false for server-only actions', () => {
    const action = { type: ActionType.SessionReady, session: S } as const;
    assert.equal(isClientDispatchable(action), false);
  });
});

// ─── Full Turn Flow Integration Test ─────────────────────────────────────────

describe('sessionReducer — full turn flow', () => {
  it('processes a complete turn with tool calls and permissions', () => {
    let state = makeSessionState({ lifecycle: SessionLifecycle.Ready });

    // Turn started
    state = sessionReducer(state, {
      type: ActionType.SessionTurnStarted,
      session: 's',
      turnId: 't1',
      userMessage: { text: 'Fix the bug' },
    });

    // Streaming delta
    state = sessionReducer(state, {
      type: ActionType.SessionDelta, session: 's', turnId: 't1', content: 'I will ',
    });
    state = sessionReducer(state, {
      type: ActionType.SessionDelta, session: 's', turnId: 't1', content: 'fix it.',
    });

    // Tool call lifecycle
    state = sessionReducer(state, {
      type: ActionType.SessionToolCallStart,
      session: 's', turnId: 't1', toolCallId: 'tc1',
      toolName: 'edit', displayName: 'Edit File',
    });
    state = sessionReducer(state, {
      type: ActionType.SessionToolCallReady,
      session: 's', turnId: 't1', toolCallId: 'tc1',
      invocationMessage: 'Edit main.ts', confirmed: ToolCallConfirmationReason.NotNeeded,
    });
    state = sessionReducer(state, {
      type: ActionType.SessionToolCallComplete,
      session: 's', turnId: 't1', toolCallId: 'tc1',
      result: { success: true, pastTenseMessage: 'Edited main.ts' },
    });

    // Permission
    state = sessionReducer(state, {
      type: ActionType.SessionPermissionRequest,
      session: 's', turnId: 't1',
      request: { requestId: 'p1', permissionKind: PermissionKind.Write, path: '/tmp/out' },
    });
    state = sessionReducer(state, {
      type: ActionType.SessionPermissionResolved,
      session: 's', turnId: 't1', requestId: 'p1', approved: true,
    });

    // Usage + reasoning
    state = sessionReducer(state, {
      type: ActionType.SessionUsage,
      session: 's', turnId: 't1',
      usage: { inputTokens: 200, outputTokens: 100 },
    });
    state = sessionReducer(state, {
      type: ActionType.SessionReasoning,
      session: 's', turnId: 't1', content: 'The bug was in line 42',
    });

    // Response part
    state = sessionReducer(state, {
      type: ActionType.SessionResponsePart,
      session: 's', turnId: 't1',
      part: { kind: ResponsePartKind.Markdown, content: '## Fix applied' },
    });

    // Turn complete
    state = sessionReducer(state, {
      type: ActionType.SessionTurnComplete, session: 's', turnId: 't1',
    });

    // Verify final state
    assert.equal(state.activeTurn, undefined);
    assert.equal(state.turns.length, 1);
    const turn = state.turns[0];
    assert.equal(turn.id, 't1');
    assert.equal(turn.responseText, 'I will fix it.');
    assert.equal(turn.state, TurnState.Complete);
    assert.equal(turn.toolCalls.length, 1);
    assert.equal(turn.toolCalls[0].status, ToolCallStatus.Completed);
    assert.equal(turn.responseParts.length, 1);
    assert.deepStrictEqual(turn.usage, { inputTokens: 200, outputTokens: 100 });
    assert.equal(state.summary.status, SessionStatus.Idle);
  });
});
