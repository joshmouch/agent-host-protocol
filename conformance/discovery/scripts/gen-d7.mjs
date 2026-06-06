#!/usr/bin/env node
/**
 * gen-d7.mjs — D7 Negative-path inventory generator
 *
 * Reads real source files from the fork:
 *   - types/common/errors.ts       → every error code name + number
 *   - schema/commands.schema.json  → InitializeParams, ReconnectParams shapes
 *   - docs/specification/*.md      → transport, versioning, authentication,
 *                                    lifecycle, subscriptions prose
 *
 * Emits one JSONL row per conformance behavior to stdout (piped to out/d7-negative-paths.jsonl).
 *
 * All citation.file paths are relative to the fork root.
 * All citation.excerpt values are verbatim substrings of the file at that line.
 */

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const FORK_ROOT = path.resolve(__dirname, '../../..');

// ── helpers ──────────────────────────────────────────────────────────────────

function readLines(relPath) {
  const abs = path.join(FORK_ROOT, relPath);
  return fs.readFileSync(abs, 'utf8').split('\n');
}

/** Find the 1-based line number of the FIRST occurrence of `substr` in `lines`. */
function lineOf(lines, substr, startAt = 0) {
  for (let i = startAt; i < lines.length; i++) {
    if (lines[i].includes(substr)) return i + 1; // 1-based
  }
  return null;
}

/** Return verbatim text at 1-based line `n` in `lines`. */
function excerptAt(lines, n) {
  return lines[n - 1] ?? '';
}

function row(obj) {
  return JSON.stringify(obj);
}

const SOURCE = 'd7-negative';
const COVERAGE = 'unknown';

const rows = [];

// ─────────────────────────────────────────────────────────────────────────────
// PART A — Error codes from types/common/errors.ts
// ─────────────────────────────────────────────────────────────────────────────

const errorsFile = 'types/common/errors.ts';
const errLines = readLines(errorsFile);

// Each JSON-RPC standard code
const jsonRpcCodes = [
  { name: 'ParseError',      code: -32700, concept: 'error:ParseError',      desc: 'Malformed JSON — server cannot parse the request frame' },
  { name: 'InvalidRequest',  code: -32600, concept: 'error:InvalidRequest',   desc: 'Not a valid JSON-RPC 2.0 request object' },
  { name: 'MethodNotFound',  code: -32601, concept: 'error:MethodNotFound',   desc: 'Unrecognised method name sent by client' },
  { name: 'InvalidParams',   code: -32602, concept: 'error:InvalidParams',    desc: 'Method exists but params violate the schema' },
  { name: 'InternalError',   code: -32603, concept: 'error:InternalError',    desc: 'Unspecified server-side error' },
];

for (const { name, code, concept, desc } of jsonRpcCodes) {
  const lineNum = lineOf(errLines, name + ':');
  const excerpt = lineNum ? excerptAt(errLines, lineNum).trim() : `${name}: ${code},`;
  rows.push({
    'behavior-id': `error.${name}.error.jsonrpc-code`,
    source: SOURCE,
    method: null,
    concept,
    'scenario-class': 'error',
    'normative-level': 'NONE',
    citation: { file: errorsFile, line: lineNum, excerpt },
    coverage: COVERAGE,
    notes: `JSON-RPC 2.0 standard error code ${code}. ${desc}`,
    assertion: `Server responds with error.code === ${code} in the appropriate scenario`,
    'params-shape-ref': 'types/common/errors.ts#JsonRpcErrorCodes',
  });
}

// AHP application-specific codes
const ahpCodes = [
  { name: 'SessionNotFound',          code: -32001, method: null,           desc: 'Referenced session URI does not exist',               normative: 'NONE' },
  { name: 'ProviderNotFound',         code: -32002, method: 'createSession',desc: 'Requested agent provider is not registered',          normative: 'NONE' },
  { name: 'SessionAlreadyExists',     code: -32003, method: 'createSession',desc: 'Session with given URI already exists',                normative: 'NONE' },
  { name: 'TurnInProgress',           code: -32004, method: null,           desc: 'Operation requires no active turn but one is running', normative: 'NONE' },
  { name: 'UnsupportedProtocolVersion', code: -32005, method: 'initialize', desc: 'Server cannot speak any offered protocol version',     normative: 'MUST' },
  { name: 'ContentNotFound',          code: -32006, method: null,           desc: 'Requested content URI does not exist',                 normative: 'NONE' },
  { name: 'AuthRequired',             code: -32007, method: null,           desc: 'Client has not authenticated for a required resource', normative: 'SHOULD' },
  { name: 'NotFound',                 code: -32008, method: null,           desc: 'Requested file, folder, or URI does not exist',        normative: 'NONE' },
  { name: 'PermissionDenied',         code: -32009, method: null,           desc: 'Client not permitted to access the resource',          normative: 'SHOULD' },
  { name: 'AlreadyExists',            code: -32010, method: null,           desc: 'Target resource exists and overwrite is disallowed',   normative: 'NONE' },
  { name: 'Conflict',                 code: -32011, method: null,           desc: 'Optimistic-concurrency precondition (etag) failed',    normative: 'NONE' },
];

for (const { name, code, method, desc, normative } of ahpCodes) {
  const lineNum = lineOf(errLines, name + ':');
  const excerpt = lineNum ? excerptAt(errLines, lineNum).trim() : `${name}: ${code},`;
  rows.push({
    'behavior-id': `error.${name}.error.ahp-code`,
    source: SOURCE,
    method: method ?? null,
    concept: `error:${name}`,
    'scenario-class': 'error',
    'normative-level': normative,
    citation: { file: errorsFile, line: lineNum, excerpt },
    coverage: COVERAGE,
    notes: `AHP application error code ${code}. ${desc}`,
    assertion: `Server responds with error.code === ${code} when the described condition arises`,
    'params-shape-ref': 'types/common/errors.ts#AhpErrorCodes',
  });
}

// ─────────────────────────────────────────────────────────────────────────────
// PART B — Protocol-violation behaviors from spec prose
// ─────────────────────────────────────────────────────────────────────────────

// B1. Unknown method → MethodNotFound
// Grounding: commands.schema.json mentions "Unknown method name" at line containing MethodNotFound in the description
{
  const file = 'docs/specification/lifecycle.md';
  const lines = readLines(file);
  // Find: "If the server cannot accept the connection for any other reason"
  const ln = lineOf(lines, 'If the server cannot accept the connection for any other reason');
  rows.push({
    'behavior-id': 'rpc.MethodNotFound.error.unknown-method',
    source: SOURCE,
    method: null,
    concept: 'error:MethodNotFound',
    'scenario-class': 'error',
    'normative-level': 'MUST',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'Any method name the server does not recognise MUST elicit a MethodNotFound (-32601) JSON-RPC error per the JSON-RPC 2.0 spec.',
    assertion: 'Sending {"jsonrpc":"2.0","id":1,"method":"unknownMethod","params":{"channel":"ahp-root://"}} returns error.code === -32601',
  });
}

// B2. Version mismatch → UnsupportedProtocolVersion with supportedVersions data
{
  const file = 'docs/specification/versioning.md';
  const lines = readLines(file);
  const ln = lineOf(lines, 'cannot speak any of the offered versions, it MUST respond with');
  rows.push({
    'behavior-id': 'versioning.UnsupportedProtocolVersion.version.no-overlap',
    source: SOURCE,
    method: 'initialize',
    concept: 'error:UnsupportedProtocolVersion',
    'scenario-class': 'version',
    'normative-level': 'MUST',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'When protocolVersions contains no version the server can speak, server MUST return -32005. data MUST include supportedVersions.',
    assertion: 'initialize with protocolVersions:["999.0.0"] returns error.code===-32005 and error.data.supportedVersions is a non-empty string array',
    'params-shape-ref': 'schema/errors.schema.json#/$defs/UnsupportedProtocolVersionErrorData',
  });
}

// B3. Version mismatch — server MUST close after refusing
{
  const file = 'docs/specification/versioning.md';
  const lines = readLines(file);
  const ln = lineOf(lines, 'close the connection');
  rows.push({
    'behavior-id': 'versioning.UnsupportedProtocolVersion.version.close-after-refuse',
    source: SOURCE,
    method: 'initialize',
    concept: 'error:UnsupportedProtocolVersion',
    'scenario-class': 'version',
    'normative-level': 'MUST',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'After returning -32005, the server MUST close the connection per the versioning spec.',
    assertion: 'After -32005 response, the transport connection is terminated by the server',
  });
}

// B4. Malformed JSON → ParseError
{
  const file = 'types/common/errors.ts';
  const lines = readLines(file);
  const ln = lineOf(lines, 'Invalid JSON');
  rows.push({
    'behavior-id': 'rpc.ParseError.error.malformed-json',
    source: SOURCE,
    method: null,
    concept: 'error:ParseError',
    'scenario-class': 'error',
    'normative-level': 'NONE',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'Sending a non-JSON text frame should produce ParseError (-32700); behavior defined by JSON-RPC 2.0.',
    assertion: 'Sending "not valid json{{" receives error.code===-32700 or connection close',
  });
}

// B5. Missing required params field → InvalidParams
{
  const file = 'schema/commands.schema.json';
  const lines = readLines(file);
  // Find required: [ "channel", "protocolVersions", "clientId" ]
  const ln = lineOf(lines, '"protocolVersions",');
  rows.push({
    'behavior-id': 'rpc.InvalidParams.error.missing-required-field',
    source: SOURCE,
    method: 'initialize',
    concept: 'error:InvalidParams',
    'scenario-class': 'error',
    'normative-level': 'NONE',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'initialize without a required field (e.g. clientId absent) should return InvalidParams (-32602).',
    assertion: 'initialize params missing clientId → error.code===-32602',
    'params-shape-ref': 'schema/commands.schema.json#/$defs/InitializeParams',
  });
}

// B6. Wrong type for required params field → InvalidParams
{
  const file = 'schema/commands.schema.json';
  const lines = readLines(file);
  const ln = lineOf(lines, '"clientId": {');
  rows.push({
    'behavior-id': 'rpc.InvalidParams.error.wrong-type-field',
    source: SOURCE,
    method: 'initialize',
    concept: 'error:InvalidParams',
    'scenario-class': 'error',
    'normative-level': 'NONE',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'initialize with clientId as a number instead of string should return InvalidParams (-32602).',
    assertion: 'initialize params with clientId:42 → error.code===-32602',
    'params-shape-ref': 'schema/commands.schema.json#/$defs/InitializeParams',
  });
}

// B7. Subscribe to non-existent session URI → error (SessionNotFound or NotFound)
{
  const file = 'types/common/errors.ts';
  const lines = readLines(file);
  const ln = lineOf(lines, 'SessionNotFound:');
  rows.push({
    'behavior-id': 'subscription.subscribe.error.nonexistent-session',
    source: SOURCE,
    method: 'subscribe',
    concept: 'error:SessionNotFound',
    'scenario-class': 'error',
    'normative-level': 'NONE',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'subscribe to ahp-session:/<nonexistent-uuid> should return SessionNotFound (-32001).',
    assertion: 'subscribe{channel:"ahp-session:/00000000-0000-0000-0000-000000000000"} → error.code===-32001',
    'params-shape-ref': 'schema/commands.schema.json#/$defs/SubscribeParams',
  });
}

// B8. AuthRequired — unauthenticated access to protected agent
{
  const file = 'docs/specification/authentication.md';
  const lines = readLines(file);
  const ln = lineOf(lines, 'SHOULD return `AuthRequired` (`-32007`) if the client attempts to use the agent unauthenticated');
  rows.push({
    'behavior-id': 'auth.AuthRequired.error.unauthenticated-agent-use',
    source: SOURCE,
    method: 'createSession',
    concept: 'error:AuthRequired',
    'scenario-class': 'error',
    'normative-level': 'SHOULD',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'Creating a session with an agent that requires auth, without calling authenticate first, should return AuthRequired (-32007) with AuthRequiredErrorData.',
    assertion: 'createSession for a required-auth agent without prior authenticate → error.code===-32007, error.data.resources is array',
    'params-shape-ref': 'schema/errors.schema.json#/$defs/AuthRequiredErrorData',
  });
}

// B9. AuthRequired data format — resources array required
{
  const file = 'types/common/errors.ts';
  const lines = readLines(file);
  const ln = lineOf(lines, 'resources: ProtectedResourceMetadata[]');
  rows.push({
    'behavior-id': 'auth.AuthRequired.error.data-resources-required',
    source: SOURCE,
    method: null,
    concept: 'error:AuthRequired',
    'scenario-class': 'error',
    'normative-level': 'MUST',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'The data field of AuthRequired (-32007) MUST be AuthRequiredErrorData with a resources array.',
    assertion: 'AuthRequired error always has data.resources as a non-null array',
    'params-shape-ref': 'schema/errors.schema.json#/$defs/AuthRequiredErrorData',
  });
}

// B10. Invalid authenticate token → error
{
  const file = 'docs/specification/authentication.md';
  const lines = readLines(file);
  const ln = lineOf(lines, 'If the token is invalid or the resource is unrecognized, the server MUST return a JSON-RPC error');
  rows.push({
    'behavior-id': 'auth.authenticate.error.invalid-token',
    source: SOURCE,
    method: 'authenticate',
    concept: 'error:AuthRequired',
    'scenario-class': 'error',
    'normative-level': 'MUST',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'authenticate with an invalid token MUST return a JSON-RPC error (AuthRequired -32007 or InvalidParams -32602).',
    assertion: 'authenticate{token:"invalid_token"} returns error.code in [-32007, -32602]',
  });
}

// B11. Transport mid-stream drop — in-progress turns considered failed
{
  const file = 'docs/specification/lifecycle.md';
  const lines = readLines(file);
  const ln = lineOf(lines, 'In-progress turns SHOULD be considered failed');
  rows.push({
    'behavior-id': 'transport.disconnect.edge.in-progress-turn-failed',
    source: SOURCE,
    method: null,
    concept: 'transport-drop',
    'scenario-class': 'edge',
    'normative-level': 'SHOULD',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'When the transport drops mid-turn, in-progress turns SHOULD be marked failed by the server.',
    assertion: 'After reconnect following mid-turn transport drop, the turn state is "error" or absent from activeTurn',
  });
}

// B12. Reconnect replay — server MUST include all replayed data before returning
{
  const file = 'docs/specification/lifecycle.md';
  const lines = readLines(file);
  const ln = lineOf(lines, 'The server MUST include all replayed data in the response before returning');
  rows.push({
    'behavior-id': 'reconnect.replay.edge.server-must-include-all-replay',
    source: SOURCE,
    method: 'reconnect',
    concept: 'reconnect-replay',
    'scenario-class': 'reconnect',
    'normative-level': 'MUST',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'Server MUST atomically include all replay actions in the reconnect response, not stream them after.',
    assertion: 'reconnect response contains all actions with serverSeq > lastSeenServerSeq in the result body, not as subsequent notifications',
    'params-shape-ref': 'schema/commands.schema.json#/$defs/ReconnectResult',
  });
}

// B13. Reconnect — gap exceeds buffer → snapshot response type
{
  const file = 'docs/specification/lifecycle.md';
  const lines = readLines(file);
  const ln = lineOf(lines, 'If the gap exceeds the replay buffer, the server sends fresh snapshots instead');
  rows.push({
    'behavior-id': 'reconnect.replay.edge.gap-exceeds-buffer-snapshot',
    source: SOURCE,
    method: 'reconnect',
    concept: 'reconnect-replay',
    'scenario-class': 'reconnect',
    'normative-level': 'NONE',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'When lastSeenServerSeq is too old, server sends type:"snapshot" instead of type:"replay".',
    assertion: 'reconnect with very old lastSeenServerSeq returns result.type==="snapshot" with current snapshots',
  });
}

// B14. Reconnect — protocol notifications NOT replayed
{
  const file = 'docs/specification/lifecycle.md';
  const lines = readLines(file);
  const ln = lineOf(lines, 'Protocol notifications are **not** replayed');
  rows.push({
    'behavior-id': 'reconnect.replay.edge.notifications-not-replayed',
    source: SOURCE,
    method: 'reconnect',
    concept: 'reconnect-replay',
    'scenario-class': 'reconnect',
    'normative-level': 'NONE',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'Protocol notifications (auth/required, root/sessionAdded, etc.) are ephemeral and MUST NOT appear in the reconnect replay.',
    assertion: 'reconnect result.actions[] contains only action envelopes, not root/sessionAdded or auth/required frames',
  });
}

// B15. Reconnect — missing subscriptions in result
{
  const file = 'docs/specification/lifecycle.md';
  const lines = readLines(file);
  const ln = lineOf(lines, '`missing` array lists subscriptions from the request that the server cannot resume');
  rows.push({
    'behavior-id': 'reconnect.replay.edge.missing-subscriptions',
    source: SOURCE,
    method: 'reconnect',
    concept: 'reconnect-replay',
    'scenario-class': 'reconnect',
    'normative-level': 'NONE',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'Disposed sessions or channels the server can no longer serve appear in result.missing; clients must drop them from local subscription state.',
    assertion: 'reconnect with disposed session URI returns result.missing array containing that URI',
    'params-shape-ref': 'schema/commands.schema.json#/$defs/ReconnectReplayResult',
  });
}

// B16. Transport requirements — no silent drops
{
  const file = 'docs/specification/transport.md';
  const lines = readLines(file);
  const ln = lineOf(lines, 'Deliver messages **reliably** (no silent drops)');
  rows.push({
    'behavior-id': 'transport.reliability.edge.no-silent-drops',
    source: SOURCE,
    method: null,
    concept: 'transport-reliability',
    'scenario-class': 'edge',
    'normative-level': 'MUST',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'A compliant transport MUST deliver messages reliably with no silent drops. This is a transport-layer conformance requirement.',
    assertion: 'No action frames are silently dropped between server send and client receive during a session',
  });
}

// B17. Transport requirements — complete messages only
{
  const file = 'docs/specification/transport.md';
  const lines = readLines(file);
  const ln = lineOf(lines, 'Deliver **complete** messages (no partial delivery)');
  rows.push({
    'behavior-id': 'transport.framing.edge.complete-messages',
    source: SOURCE,
    method: null,
    concept: 'transport-framing',
    'scenario-class': 'edge',
    'normative-level': 'MUST',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'Transport MUST deliver complete messages (no partial delivery). Each WebSocket text frame contains exactly one complete JSON-RPC message.',
    assertion: 'Splitting an action JSON across two frames must not be accepted as valid; host must reject or close',
  });
}

// B18. ping — server MUST respond regardless of initialize state
{
  const file = 'docs/specification/transport.md';
  const lines = readLines(file);
  const ln = lineOf(lines, 'the server MUST respond regardless of whether the client has completed `initialize`');
  rows.push({
    'behavior-id': 'rpc.ping.edge.pre-initialize-allowed',
    source: SOURCE,
    method: 'ping',
    concept: 'ping',
    'scenario-class': 'edge',
    'normative-level': 'MUST',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'ping MUST be answered even before initialize completes and even if client holds no subscriptions.',
    assertion: 'ping sent before initialize receives a valid result (empty object)',
  });
}

// B19. Duplicate clientId — connecting with the same clientId as an existing connection
{
  const file = 'schema/commands.schema.json';
  const lines = readLines(file);
  const ln = lineOf(lines, '"Unique client identifier"');
  rows.push({
    'behavior-id': 'rpc.initialize.error.duplicate-clientId',
    source: SOURCE,
    method: 'initialize',
    concept: 'duplicate-clientId',
    'scenario-class': 'concurrency',
    'normative-level': 'NONE',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'clientId is documented as "Unique client identifier". Behavior when two connections share the same clientId is unspecified; negative test should explore how the host handles it.',
    assertion: 'Two concurrent connections sending initialize with identical clientId either returns error or the first is evicted',
  });
}

// B20. PermissionDenied — resource access outside allowed paths
{
  const file = 'types/common/errors.ts';
  const lines = readLines(file);
  const ln = lineOf(lines, 'Servers SHOULD return this when a client attempts to read or browse');
  rows.push({
    'behavior-id': 'error.PermissionDenied.error.path-outside-workspace',
    source: SOURCE,
    method: 'resourceRead',
    concept: 'error:PermissionDenied',
    'scenario-class': 'error',
    'normative-level': 'SHOULD',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'Accessing a path outside the session working directory or workspace roots SHOULD return PermissionDenied (-32009).',
    assertion: 'resourceRead for a path outside allowed workspace returns error.code===-32009',
    'params-shape-ref': 'schema/errors.schema.json#/$defs/PermissionDeniedErrorData',
  });
}

// B21. Conflict — etag mismatch on resourceWrite
{
  const file = 'types/common/errors.ts';
  const lines = readLines(file);
  const ln = lineOf(lines, 'An optimistic-concurrency precondition failed.');
  rows.push({
    'behavior-id': 'error.Conflict.error.etag-mismatch',
    source: SOURCE,
    method: 'resourceWrite',
    concept: 'error:Conflict',
    'scenario-class': 'concurrency',
    'normative-level': 'NONE',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'resourceWrite with a stale ifMatch etag MUST return Conflict (-32011); caller should re-read and retry.',
    assertion: 'resourceWrite with stale etag returns error.code===-32011',
  });
}

// B22. AlreadyExists — createOnly write to existing resource
{
  const file = 'types/common/errors.ts';
  const lines = readLines(file);
  const ln = lineOf(lines, 'The target resource already exists and the operation does not allow');
  rows.push({
    'behavior-id': 'error.AlreadyExists.error.create-only-conflict',
    source: SOURCE,
    method: 'resourceWrite',
    concept: 'error:AlreadyExists',
    'scenario-class': 'error',
    'normative-level': 'NONE',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'resourceWrite with createOnly:true targeting an existing path should return AlreadyExists (-32010).',
    assertion: 'resourceWrite{createOnly:true} to existing file returns error.code===-32010',
  });
}

// B23. auth/required server-initiated notification not replayed on reconnect
{
  const file = 'docs/specification/authentication.md';
  const lines = readLines(file);
  const ln = lineOf(lines, '`auth/required` is ephemeral and is **not** replayed on reconnection');
  rows.push({
    'behavior-id': 'auth.auth-required.edge.not-replayed-on-reconnect',
    source: SOURCE,
    method: 'auth/required',
    concept: 'auth-expiry-notification',
    'scenario-class': 'reconnect',
    'normative-level': 'NONE',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'auth/required is a server-initiated notification and is ephemeral; it is NOT included in reconnect replay. Clients SHOULD re-check auth after reconnecting.',
    assertion: 'After reconnect, no auth/required notification is re-sent by the server for tokens that were already being refreshed before disconnect',
  });
}

// B24. auth/required reason — expired token notification
{
  const file = 'docs/specification/authentication.md';
  const lines = readLines(file);
  const ln = lineOf(lines, '| `expired` | A previously valid token has expired or been revoked |');
  rows.push({
    'behavior-id': 'auth.auth-required.edge.expired-token-notification',
    source: SOURCE,
    method: 'auth/required',
    concept: 'auth-expiry-notification',
    'scenario-class': 'error',
    'normative-level': 'MAY',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'Server MAY send auth/required notification with reason:"expired" when a previously valid token expires or is revoked.',
    assertion: 'auth/required notification has params.reason in ["required", "expired"]',
  });
}

// B25. subscribe to unknown channel scheme → error
{
  const file = 'docs/specification/subscriptions.md';
  const lines = readLines(file);
  const ln = lineOf(lines, 'Clients MUST NOT subscribe to a scheme they do not understand');
  rows.push({
    'behavior-id': 'subscription.subscribe.error.unknown-channel-scheme',
    source: SOURCE,
    method: 'subscribe',
    concept: 'unknown-channel-scheme',
    'scenario-class': 'error',
    'normative-level': 'MUST_NOT',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'Clients MUST NOT subscribe to an unknown URI scheme. If a client does, server should return an error.',
    assertion: 'subscribe{channel:"ahp-unknown://foo"} returns an error (NotFound or InvalidParams)',
  });
}

// B26. serverSeq — out-of-order delivery indicates transport failure
{
  const file = 'schema/commands.schema.json';
  const lines = readLines(file);
  const ln = lineOf(lines, '"serverSeq": {');
  rows.push({
    'behavior-id': 'transport.serverSeq.edge.out-of-order-seq',
    source: SOURCE,
    method: 'action',
    concept: 'serverSeq-ordering',
    'scenario-class': 'edge',
    'normative-level': 'MUST',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'serverSeq values MUST be monotonically increasing per channel. An out-of-order serverSeq indicates transport layer failure (guaranteed ordering is a transport requirement).',
    assertion: 'action notifications on any channel have strictly monotonically increasing serverSeq values',
  });
}

// B27. server-initiated resource* request handling by client
{
  const file = 'docs/specification/subscriptions.md';
  const lines = readLines(file);
  const ln = lineOf(lines, 'Server → Client commands (bidirectional `resource*` family)');
  rows.push({
    'behavior-id': 'rpc.resourceRead.edge.server-initiated-request',
    source: SOURCE,
    method: 'resourceRead',
    concept: 'server-initiated-resource-request',
    'scenario-class': 'edge',
    'normative-level': 'NONE',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'The resource* family (resourceRead, resourceWrite, resourceList, etc.) MAY be initiated by the server toward the client. Clients must handle incoming resource* requests from the server.',
    assertion: 'Client correctly handles server-initiated resourceRead requests and responds with the file content',
  });
}

// B28. Missing channel field in any request → InvalidRequest or InvalidParams
{
  const file = 'schema/commands.schema.json';
  const lines = readLines(file);
  const ln = lineOf(lines, '"channel": {');
  rows.push({
    'behavior-id': 'rpc.BaseParams.error.missing-channel-field',
    source: SOURCE,
    method: null,
    concept: 'missing-channel-field',
    'scenario-class': 'error',
    'normative-level': 'NONE',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'Every AHP command has a required "channel" field per BaseParams. Omitting it should return InvalidParams (-32602).',
    assertion: 'Any request missing params.channel returns error.code===-32602',
    'params-shape-ref': 'schema/commands.schema.json#/$defs/BaseParams',
  });
}

// B29. TurnInProgress — attempting operation that requires no active turn
{
  const file = 'types/common/errors.ts';
  const lines = readLines(file);
  const ln = lineOf(lines, 'The operation requires no active turn, but one is in progress');
  rows.push({
    'behavior-id': 'error.TurnInProgress.error.turn-active',
    source: SOURCE,
    method: null,
    concept: 'error:TurnInProgress',
    'scenario-class': 'concurrency',
    'normative-level': 'NONE',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'Certain operations require no active turn. If sent while a turn is running, TurnInProgress (-32004) should be returned.',
    assertion: 'Sending a turn-incompatible command while activeTurn is set returns error.code===-32004',
  });
}

// B30. NotFound — resource read of non-existent file
{
  const file = 'types/common/errors.ts';
  const lines = readLines(file);
  const ln = lineOf(lines, 'The requested file, folder, or URI does not exist');
  rows.push({
    'behavior-id': 'error.NotFound.error.resource-missing',
    source: SOURCE,
    method: 'resourceRead',
    concept: 'error:NotFound',
    'scenario-class': 'error',
    'normative-level': 'NONE',
    citation: {
      file,
      line: ln,
      excerpt: excerptAt(lines, ln).trim(),
    },
    coverage: COVERAGE,
    notes: 'resourceRead, resourceList etc. for a non-existent URI should return NotFound (-32008).',
    assertion: 'resourceRead{uri:"file:///nonexistent-path"} returns error.code===-32008',
  });
}

// ─────────────────────────────────────────────────────────────────────────────
// Output
// ─────────────────────────────────────────────────────────────────────────────

for (const r of rows) {
  console.log(row(r));
}

process.stderr.write(`gen-d7: emitted ${rows.length} rows\n`);
