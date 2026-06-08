#!/usr/bin/env node
// AHP Conformance Discovery — D2 generator (normative-rules angle).
//
// Scans all 11 spec files in docs/specification/*.md for RFC-2119 keywords
// (MUST, MUST NOT, SHOULD, SHOULD NOT, REQUIRED, SHALL, MAY) and emits one
// inventory row per normative clause.
//
// REAL-EXECUTION policy: reads the REAL spec files from the fork; citation
// excerpts are verbatim text from the matched line. No fabricated rows,
// no hard-coded line numbers — all derived from live file reads.
//
// Usage:
//   node conformance/discovery/scripts/gen-d2.mjs
// Writes: conformance/discovery/out/d2-normative-rules.jsonl

import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, basename } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const FORK_ROOT = resolve(__dirname, '..', '..', '..'); // three up: scripts -> discovery -> conformance -> fork-root
const SPEC_DIR = resolve(FORK_ROOT, 'docs', 'specification');
const OUT_DIR = resolve(__dirname, '..', 'out');
const OUT_FILE = resolve(OUT_DIR, 'd2-normative-rules.jsonl');

// RFC-2119 keyword priority order for normative-level mapping
const KEYWORD_MAP = [
  { pattern: /\bMUST NOT\b/, level: 'MUST_NOT' },
  { pattern: /\bSHOULD NOT\b/, level: 'SHOULD_NOT' },
  { pattern: /\bSHALL NOT\b/, level: 'MUST_NOT' }, // RFC-2119: SHALL NOT = MUST NOT
  { pattern: /\bMUST\b/, level: 'MUST' },
  { pattern: /\bSHALL\b/, level: 'SHALL' },
  { pattern: /\bREQUIRED\b/, level: 'REQUIRED' },
  { pattern: /\bSHOULD\b/, level: 'SHOULD' },
  { pattern: /\bMAY\b/, level: 'MAY' },
];

// Detect the normative level of a line (returns the MOST STRICT keyword found)
function detectLevel(line) {
  for (const { pattern, level } of KEYWORD_MAP) {
    if (pattern.test(line)) return level;
  }
  return null;
}

// Map spec filename to domain (best-guess from spec file context)
const FILE_DOMAIN = {
  'authentication.md': 'auth',
  'lifecycle.md': 'lifecycle',
  'overview.md': 'rpc',
  'resource-watch-channel.md': 'channel',
  'root-channel.md': 'channel',
  'session-channel.md': 'session',
  'subscriptions.md': 'subscription',
  'telemetry-channel.md': 'channel',
  'terminal-channel.md': 'channel',
  'transport.md': 'transport',
  'versioning.md': 'versioning',
};

// Infer a concept label from the line text + file context
function inferConcept(line, file) {
  const lc = line.toLowerCase();

  // Method-specific
  if (/\binitialize\b/.test(lc)) return 'initialize';
  if (/\bping\b/.test(lc)) return 'ping';
  if (/\bauthenticate\b/.test(lc)) return 'authenticate';
  if (/\blistsessions\b/i.test(line)) return 'listSessions';
  if (/\bsubscribe\b/.test(lc) && !/\bunsubscribe\b/.test(lc)) return 'subscribe';
  if (/\bunsubscribe\b/.test(lc)) return 'unsubscribe';
  if (/\bcreateresourcewatch\b/i.test(line)) return 'createResourceWatch';

  // Error / negative
  if (/unsupportedprotocolversion/i.test(line)) return 'UnsupportedProtocolVersion';
  if (/authrequired/i.test(line)) return 'AuthRequired';
  if (/permissiondenied/i.test(line)) return 'PermissionDenied';
  if (/-32007/.test(line)) return 'AuthRequired';
  if (/-32005/.test(line)) return 'UnsupportedProtocolVersion';
  if (/-32009/.test(line)) return 'PermissionDenied';
  if (/error/.test(lc)) return 'error-response';

  // Session actions
  if (/session\/toolcallconfirmed/i.test(line)) return 'session-action-toolCallConfirmed';
  if (/session\/turncancelled/i.test(line)) return 'session-action-turnCancelled';
  if (/session\/modelchanged/i.test(line)) return 'session-action-modelChanged';
  if (/session\/agentchanged/i.test(line)) return 'session-action-agentChanged';
  if (/session\/inputanswerchanged/i.test(line)) return 'session-action-inputAnswerChanged';
  if (/session\/inputcompleted/i.test(line)) return 'session-action-inputCompleted';
  if (/session\/pendingmessageremoved/i.test(line)) return 'session-action-pendingMessageRemoved';
  if (/session\/inputrequested/i.test(line)) return 'session-action-inputRequested';
  if (/\bqueuedmessages\b/i.test(line)) return 'session-queuedMessages';
  if (/\bsteeringmessages\b/i.test(line)) return 'session-steeringMessages';
  if (/\baction\b.*\benvelope\b/i.test(line) || /\benvelope\b.*\baction\b/i.test(line)) return 'ActionEnvelope';

  // Root channel notifications
  if (/session.*updated|root\/.*session/i.test(line)) return 'root-sessionUpdated';
  if (/terminals?changed|root\/terminalsc/i.test(line)) return 'root-terminalsChanged';
  if (/auth\/required/i.test(line)) return 'auth-required-notification';

  // Auth / protected resources
  if (/protectedresource/i.test(line)) return 'protectedResources';
  if (/bearer.*token|token.*bearer/i.test(line)) return 'bearer-token';
  if (/\bauthentication\b/.test(lc)) return 'authentication';
  if (/auth/i.test(line)) return 'auth';

  // Protocol-version / versioning
  if (/protocolversion/i.test(line)) return 'protocol-version';
  if (/version/i.test(line)) return 'versioning';

  // Replay / reconnect
  if (/replay/i.test(line)) return 'reconnect-replay';
  if (/lastseenserverseq/i.test(line)) return 'reconnect-replay';
  if (/reconnect/i.test(line)) return 'reconnect';

  // Telemetry / transport
  if (/telemetry/i.test(line)) return 'telemetry';
  if (/\botlp\b/i.test(line)) return 'telemetry-otlp';
  if (/transport/i.test(line)) return 'transport';
  if (/websocket/i.test(line)) return 'websocket';

  // Subscription / channel
  if (/\bstateless\b/.test(lc)) return 'stateless-channel';
  if (/channel/i.test(line)) return 'channel';
  if (/\bsubscription\b/.test(lc)) return 'subscription';

  // Locale / notification
  if (/\blocale\b/i.test(line)) return 'locale';
  if (/\bnotification\b/i.test(line)) return 'notification';

  // Watch
  if (/watch/i.test(line)) return 'resource-watch';

  // Terminal
  if (/terminal/i.test(line)) return 'terminal';

  // Fallback: use file domain
  const fb = FILE_DOMAIN[file] ?? 'protocol';
  return fb;
}

// Infer scenario-class from line content
function inferScenarioClass(line) {
  const lc = line.toLowerCase();
  if (/error|invalid|reject|denied|fail|cannot|cannot speak|cannot resume|unrecognized|undefined|unrecognised|corrupt/.test(lc)) return 'error';
  if (/reconnect|replay|missed|gap|resnapshot|resume|missing/.test(lc)) return 'reconnect';
  if (/version|upgrade|negotiate|compat|fallback|incompatible/.test(lc)) return 'version';
  if (/concurren|race|parallel|overlap|in-flight|in progress/.test(lc)) return 'concurrency';
  if (/optional|may |may\b|if|when|stateless|coalesce|debounce|idle|drop/.test(lc)) return 'edge';
  return 'happy';
}

// Infer the method that a line is most closely associated with
function inferMethod(line) {
  if (/\binitialize\b/i.test(line)) return 'initialize';
  if (/\bping\b/i.test(line)) return 'ping';
  if (/\bauthenticate\b/i.test(line)) return 'authenticate';
  if (/\blistsessions\b/i.test(line)) return 'listSessions';
  if (/\bcreateresourcewatch\b/i.test(line)) return 'createResourceWatch';
  if (/\bsubscribe\b/i.test(line) && !/\bunsubscribe\b/i.test(line)) return 'subscribe';
  if (/\bunsubscribe\b/i.test(line)) return 'unsubscribe';
  return null;
}

// Build a stable behavior-id from domain, concept, scenario-class, and discriminator
const seenIds = new Map(); // id -> count (for dedup with numeric suffix)

function makeBehaviorId(domain, concept, scenarioClass, discriminator) {
  // Sanitize each segment: only [A-Za-z0-9-]
  const clean = (s) => s.replace(/[^A-Za-z0-9-]/g, '-').replace(/-+/g, '-').replace(/^-|-$/g, '');
  const segs = [domain, concept, scenarioClass, discriminator].map(clean).filter(Boolean);
  // Must be 3-5 segments; truncate if over 5
  const base = segs.slice(0, 5).join('.');
  return base;
}

const SPEC_FILES = [
  'authentication.md',
  'lifecycle.md',
  'overview.md',
  'resource-watch-channel.md',
  'root-channel.md',
  'session-channel.md',
  'subscriptions.md',
  'telemetry-channel.md',
  'terminal-channel.md',
  'transport.md',
  'versioning.md',
];

const rows = [];
// Track behavior-id -> count within this file (for uniqueness)
const idCounts = new Map();

function allocateId(base) {
  if (!idCounts.has(base)) {
    idCounts.set(base, 0);
    return base;
  }
  const n = idCounts.get(base) + 1;
  idCounts.set(base, n);
  return `${base}-${n}`;
}

for (const fname of SPEC_FILES) {
  const relPath = `docs/specification/${fname}`;
  const absPath = resolve(FORK_ROOT, relPath);
  let content;
  try {
    content = readFileSync(absPath, 'utf8');
  } catch (e) {
    console.error(`SKIP ${relPath}: ${e.message}`);
    continue;
  }

  const lines = content.split('\n');
  const domain = FILE_DOMAIN[fname] ?? 'protocol';

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const level = detectLevel(line);
    if (level === null) continue;

    // Skip the RFC 2119 boilerplate definition line itself
    if (line.includes('RFC 2119') && line.includes('key words')) continue;
    // Skip table header dashes / separator rows
    if (/^[\s|:-]+$/.test(line)) continue;
    // Skip lines that are only markdown headers or bullets with just the keyword in a title
    if (/^#+\s/.test(line) && !/[A-Z]{4}/.test(line.replace(/^#+\s+/, ''))) continue;

    const concept = inferConcept(line, fname);
    const scenarioClass = inferScenarioClass(line);
    const method = inferMethod(line);

    // Build discriminator from a short slug of the line content
    const lineSlug = line
      .replace(/[`*_[\]]/g, '')
      .trim()
      .toLowerCase()
      .slice(0, 40)
      .replace(/[^a-z0-9]+/g, '-')
      .replace(/^-|-$/g, '');

    const baseId = makeBehaviorId(domain, concept, scenarioClass, lineSlug.slice(0, 20));
    const behaviorId = allocateId(baseId);

    // Use up to 100 chars of verbatim line text for the excerpt
    const excerpt = line.trim().slice(0, 120);

    rows.push({
      'behavior-id': behaviorId,
      source: 'd2-spec',
      method,
      concept,
      'scenario-class': scenarioClass,
      'normative-level': level,
      citation: {
        file: relPath,
        line: i + 1,
        excerpt,
      },
      coverage: 'unknown',
    });
  }
}

mkdirSync(OUT_DIR, { recursive: true });
const jsonl = rows.map((r) => JSON.stringify(r)).join('\n') + '\n';
writeFileSync(OUT_FILE, jsonl, 'utf8');
console.log(`Wrote ${rows.length} rows to ${OUT_FILE}`);
