/**
 * Version Registry — Maps action types and features to protocol versions.
 *
 * @module version/registry
 */

import type { IStateAction } from '../actions.js';

// ─── Protocol Version Constants ──────────────────────────────────────────────

/** The current protocol version that new code speaks. */
export const PROTOCOL_VERSION = 1;

/** The oldest protocol version the implementation maintains compatibility with. */
export const MIN_PROTOCOL_VERSION = 1;

// ─── Exhaustive Action → Version Map ─────────────────────────────────────────

/**
 * Maps every action type to the protocol version that introduced it.
 * Adding a new action to `IStateAction` without adding it here is a compile error.
 */
export const ACTION_INTRODUCED_IN: { readonly [K in IStateAction['type']]: number } = {
  'root/agentsChanged': 1,
  'session/ready': 1,
  'session/creationFailed': 1,
  'session/turnStarted': 1,
  'session/delta': 1,
  'session/responsePart': 1,
  'session/toolCallStart': 1,
  'session/toolCallDelta': 1,
  'session/toolCallReady': 1,
  'session/toolCallConfirmed': 1,
  'session/toolCallComplete': 1,
  'session/toolCallResultConfirmed': 1,
  'session/permissionRequest': 1,
  'session/permissionResolved': 1,
  'session/turnComplete': 1,
  'session/turnCancelled': 1,
  'session/error': 1,
  'session/titleChanged': 1,
  'session/usage': 1,
  'session/reasoning': 1,
  'session/modelChanged': 1,
  'session/serverToolsChanged': 1,
  'session/activeClientChanged': 1,
  'session/activeClientToolsChanged': 1,
};

/**
 * Returns whether the given action type is known to the specified protocol version.
 */
export function isActionKnownToVersion(action: IStateAction, clientVersion: number): boolean {
  return ACTION_INTRODUCED_IN[action.type] <= clientVersion;
}

// ─── Capabilities ────────────────────────────────────────────────────────────

/**
 * Feature capabilities gated by protocol version.
 */
export interface ProtocolCapabilities {
  /** v1 — always present */
  readonly sessions: true;
  /** v1 — always present */
  readonly tools: true;
  /** v1 — always present */
  readonly permissions: true;
}

/**
 * Derives capabilities from a protocol version number.
 */
export function capabilitiesForVersion(_version: number): ProtocolCapabilities {
  return {
    sessions: true,
    tools: true,
    permissions: true,
  };
}
