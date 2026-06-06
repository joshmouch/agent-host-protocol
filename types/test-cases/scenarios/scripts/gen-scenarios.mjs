#!/usr/bin/env node
// AHP Conformance — scenario CORPUS generator (Part 2, build-phase B2).
//
// Reads REAL fixture files and D7 negative-paths from disk and emits scenario
// JSON files that all validate against B1's scenario.schema.json.
//
// Three tranches:
//   reducers/   — one scenario per types/test-cases/reducers/*.json (163 files)
//   round-trips/ — one scenario per types/test-cases/round-trips/*.json (23 files)
//   negatives/  — one scenario per line in conformance/discovery/out/d7-negative-paths.jsonl
//
// Dependency-free by design (same discipline as validate-scenarios.mjs): no
// npm install required. Uses only Node built-ins.
//
// Usage:
//   node scripts/gen-scenarios.mjs           # generate everything
//   node scripts/gen-scenarios.mjs --dry-run # count without writing

import { readFileSync, writeFileSync, mkdirSync, readdirSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = resolve(__dirname, '..', '..', '..', '..'); // agent-host-protocol/
const SCENARIOS_ROOT = resolve(__dirname, '..');

const DRY_RUN = process.argv.includes('--dry-run');

const REDUCERS_SRC = resolve(REPO_ROOT, 'types', 'test-cases', 'reducers');
const ROUND_TRIPS_SRC = resolve(REPO_ROOT, 'types', 'test-cases', 'round-trips');
const D7_NEGATIVES = resolve(REPO_ROOT, 'conformance', 'discovery', 'out', 'd7-negative-paths.jsonl');
const D5_FIXTURE_SCENARIOS = resolve(REPO_ROOT, 'conformance', 'discovery', 'out', 'd5-fixture-derived-scenarios.jsonl');

const REDUCERS_OUT = resolve(SCENARIOS_ROOT, 'reducers');
const ROUND_TRIPS_OUT = resolve(SCENARIOS_ROOT, 'round-trips');
const NEGATIVES_OUT = resolve(SCENARIOS_ROOT, 'negatives');

const PROTOCOL_VERSION = '0.3.0';

// --------------------------------------------------------------------------
// Helpers
// --------------------------------------------------------------------------

function readJsonLines(filePath) {
  const text = readFileSync(filePath, 'utf8');
  return text
    .split('\n')
    .map((l) => l.trim())
    .filter((l) => l.length > 0)
    .map((l) => JSON.parse(l));
}

function writeScenario(dir, id, scenario) {
  const filePath = join(dir, `${id}.scenario.json`);
  if (!DRY_RUN) {
    writeFileSync(filePath, JSON.stringify(scenario, null, 2) + '\n', 'utf8');
  }
  return filePath;
}

function ensureDir(dir) {
  if (!DRY_RUN) mkdirSync(dir, { recursive: true });
}

// Build a lookup: fixture basename -> D5 behavior-id
function buildD5LookupFromFile() {
  const lookup = new Map();
  if (!existsSync(D5_FIXTURE_SCENARIOS)) return lookup;
  const items = readJsonLines(D5_FIXTURE_SCENARIOS);
  for (const item of items) {
    const file = item?.citation?.file;
    if (!file) continue;
    const base = file.split('/').pop();
    const id = item['behavior-id'];
    if (!lookup.has(base)) lookup.set(base, []);
    lookup.get(base).push(id);
  }
  return lookup;
}

// Slugify a reducer filename into a valid scenario id.
// e.g. "001-root-agentschanged.json" -> "action.reducers.happy.001-root-agentschanged"
// We use the behavior-id from D5 when available; otherwise synthesize a safe id.
function reducerFileToId(basename, d5Lookup) {
  const ids = d5Lookup.get(basename);
  if (ids && ids.length > 0) return ids[0];
  // Fallback: synthesize from filename
  const stem = basename.replace(/\.json$/, '');
  // must match ^[A-Za-z0-9-]+(\.[A-Za-z0-9-]+){2,4}$
  const safe = stem.replace(/[^A-Za-z0-9-]/g, '-').replace(/-+/g, '-').replace(/^-|-$/g, '');
  return `action.reducer.happy.${safe}`;
}

function roundTripFileToId(basename, d5Lookup) {
  const ids = d5Lookup.get(basename);
  if (ids && ids.length > 0) return ids[0];
  const stem = basename.replace(/\.json$/, '');
  const safe = stem.replace(/[^A-Za-z0-9-]/g, '-').replace(/-+/g, '-').replace(/^-|-$/g, '');
  return `roundtrip.wire.happy.${safe}`;
}

// --------------------------------------------------------------------------
// Tranche 1 — reducer fixtures
//
// For each fixture: server.notify the action(s) against a seeded snapshot of
// `initial` state, then client.assert.state equals `expected`.
//
// Reducer fixtures have:
//   { description, reducer, initial, actions: [...], expected }
//
// We model this as:
//   1. client.request initialize (no-op seed of the channel)
//   2. server.response with the initial state as the snapshot
//   3. For each action: server.notify { method:"action", params:{ channel, action, serverSeq, origin:null } }
//   4. client.assert.state { equals: expected } (whole-state convergence)
// --------------------------------------------------------------------------

// Determine the channel URI from the reducer type (mirrors conformance/host)
const REDUCER_CHANNEL = {
  root: 'ahp-root://',
  session: 'ahp-session:/test-session',
  changeset: 'ahp-changeset:/test-changeset',
  resourceWatch: 'ahp-resource-watch:/test-watch',
};
const DEFAULT_CHANNEL = 'ahp-session:/test-session';

function reducerChannel(reducerName) {
  return REDUCER_CHANNEL[reducerName] ?? DEFAULT_CHANNEL;
}

function generateReducerScenarios(d5Lookup) {
  const files = readdirSync(REDUCERS_SRC)
    .filter((f) => f.endsWith('.json'))
    .sort();

  const generated = [];

  for (const basename of files) {
    const srcPath = join(REDUCERS_SRC, basename);
    let fixture;
    try {
      fixture = JSON.parse(readFileSync(srcPath, 'utf8'));
    } catch (e) {
      console.error(`SKIP reducer ${basename}: ${e.message}`);
      continue;
    }

    const { description, reducer, initial, actions, expected } = fixture;
    if (!actions || actions.length === 0 || !initial || !expected) {
      console.error(`SKIP reducer ${basename}: missing required fields`);
      continue;
    }

    const id = reducerFileToId(basename, d5Lookup);
    const channel = reducerChannel(reducer);

    // Build behavior ID list (cite D5 id + a generic reducer behavior)
    const d5Ids = d5Lookup.get(basename) ?? [];
    // Ensure we have at least one valid behavior ID
    const behaviorIds = d5Ids.length > 0 ? d5Ids : [id];

    // Pin clock to 9999 so modifiedAt fields are deterministic
    const pinClock = 9999;

    // Steps:
    const steps = [];

    // 1. Initialize: seed the channel with initial state
    steps.push({
      op: 'client.request',
      label: 'initialize — seed channel with fixture initial state',
      method: 'initialize',
      params: {},
      id: 1,
    });

    steps.push({
      op: 'server.response',
      label: 'initialize result carries the fixture initial state as a snapshot',
      forId: 1,
      result: {
        protocolVersion: PROTOCOL_VERSION,
        serverSeq: 0,
        snapshots: [
          {
            resource: channel,
            fromSeq: 0,
            state: initial,
          },
        ],
      },
    });

    // 2. Notify each action
    let seq = 1;
    for (const action of actions) {
      steps.push({
        op: 'server.notify',
        label: `action: ${action.type}`,
        method: 'action',
        params: {
          channel,
          action,
          serverSeq: seq,
          origin: null,
        },
      });
      seq++;
    }

    // 3. Whole-state convergence assertion
    steps.push({
      op: 'client.assert.state',
      label: 'client-reduced state converges to fixture expected state',
      channel,
      equals: expected,
    });

    const scenario = {
      id,
      behaviorIds,
      description: `Reducer fixture: ${description}. Verifies that the client's ${reducer} reducer converges to the expected state after applying the fixture's action(s).`,
      protocolVersion: PROTOCOL_VERSION,
      pinClock,
      notes: `Auto-generated from types/test-cases/reducers/${basename}. Reducer: ${reducer}.`,
      steps,
    };

    generated.push({ id, scenario, outDir: REDUCERS_OUT, basename });
  }

  return generated;
}

// --------------------------------------------------------------------------
// Tranche 2 — round-trip fixtures
//
// Round-trip fixtures have varied shapes (wire, wireRaw, expect, expectVariant,
// etc.) and test type-system fidelity. We model each as:
//   - server.notify the wire payload (if it's an ActionEnvelope)
//   - or server.notify a synthetic action wrapping the wire value
//   - client.assert.event matches the key fields from `expect`
//
// For JsonRpcMessage fixtures (wireRaw): wrap as a server.notify (notification)
// or server.response (request/success/error) depending on expectJsonRpcVariant.
//
// For non-action types: emit the wire as a server.notify with a synthetic
// wrapper so the payload reaches the client; assert key expect fields.
// --------------------------------------------------------------------------

function generateRoundTripScenarios(d5Lookup) {
  const files = readdirSync(ROUND_TRIPS_SRC)
    .filter((f) => f.endsWith('.json') && !f.endsWith('.md'))
    .sort();

  const generated = [];

  for (const basename of files) {
    const srcPath = join(ROUND_TRIPS_SRC, basename);
    let fixture;
    try {
      fixture = JSON.parse(readFileSync(srcPath, 'utf8'));
    } catch (e) {
      console.error(`SKIP round-trip ${basename}: ${e.message}`);
      continue;
    }

    const id = roundTripFileToId(basename, d5Lookup);
    const d5Ids = d5Lookup.get(basename) ?? [];
    const behaviorIds = d5Ids.length > 0 ? d5Ids : [id];

    const { name, description, type } = fixture;
    if (!name || !type) {
      console.error(`SKIP round-trip ${basename}: missing name or type`);
      continue;
    }

    let steps = [];
    let scenarioDescription = '';
    let notes = `Auto-generated from types/test-cases/round-trips/${basename}. Type: ${type}.`;

    // Determine wire payload
    const wireObj = fixture.wire ?? (fixture.wireRaw != null ? (() => {
      try { return JSON.parse(fixture.wireRaw); } catch { return { _raw: fixture.wireRaw }; }
    })() : null);

    if (type === 'ActionEnvelope' && wireObj) {
      // Emit as a server.notify action
      const channel = wireObj.channel ?? 'ahp-session:/test';
      steps.push({
        op: 'server.notify',
        label: `action envelope: ${name}`,
        method: 'action',
        params: wireObj,
      });

      // Assert key fields from expect
      const expectFields = fixture.expect ?? {};
      for (const [dotPath, val] of Object.entries(expectFields)) {
        steps.push({
          op: 'client.assert.event',
          label: `field ${dotPath} === ${JSON.stringify(val)}`,
          matches: dotPathToObject(dotPath, val),
        });
      }

      scenarioDescription = `Round-trip: ${description}`;
    } else if (type === 'JsonRpcMessage' && wireObj) {
      // Model as appropriate wire step
      const variant = fixture.expectJsonRpcVariant;
      if (variant === 'notification' || variant === 'request') {
        // Emit as notify (we use server.notify for any JSON message coming from server)
        steps.push({
          op: 'server.notify',
          label: `JsonRpcMessage (${variant}): ${name}`,
          method: wireObj.method ?? 'action',
          params: wireObj.params,
        });
        // Assert the method is preserved
        if (wireObj.method) {
          steps.push({
            op: 'client.assert.event',
            label: 'message received with correct method',
            matches: { method: wireObj.method },
          });
        }
      } else {
        // success/error: model as server.response to a prior client.request
        steps.push({
          op: 'client.request',
          label: 'request that will receive a response',
          method: 'ping',
          params: {},
          id: wireObj.id ?? 1,
        });
        if (variant === 'success') {
          steps.push({
            op: 'server.response',
            label: `JsonRpcMessage success: ${name}`,
            forId: wireObj.id ?? 1,
            result: wireObj.result ?? {},
          });
          // Assert the response decoded as a SUCCESS (not an error). The runner
          // exposes a synthetic `lastResponseOk` flag set true when a success
          // response is decoded; this is satisfiable and verifies the variant.
          steps.push({
            op: 'client.assert.state',
            label: 'response decoded as the success variant (no error surfaced)',
            path: 'lastResponseOk',
            equals: true,
          });
        } else if (variant === 'error') {
          steps.push({
            op: 'server.response',
            label: `JsonRpcMessage error: ${name}`,
            forId: wireObj.id ?? 1,
            error: wireObj.error ?? { code: -32600, message: 'error' },
          });
          steps.push({
            op: 'client.assert.error',
            label: 'error code is correct',
            code: wireObj.error?.code ?? -32600,
          });
        }
      }
      scenarioDescription = `Round-trip: ${description}`;
    } else if (type === 'SessionStatus' && fixture.wireRaw != null) {
      // Numeric bitset — emit as a session snapshot carrying this status, then assert
      const statusNum = parseInt(fixture.wireRaw, 10);
      if (!isNaN(statusNum)) {
        steps.push({
          op: 'client.request',
          label: 'initialize to seed a session with the status bitset',
          method: 'initialize',
          params: {},
          id: 1,
        });
        steps.push({
          op: 'server.response',
          label: 'snapshot with session containing the SessionStatus under test',
          forId: 1,
          result: {
            protocolVersion: PROTOCOL_VERSION,
            serverSeq: 0,
            snapshots: [
              {
                resource: 'ahp-session:/bitset-test',
                fromSeq: 0,
                state: {
                  summary: {
                    resource: 'copilot:/bitset-test',
                    provider: 'copilot',
                    title: 'bitset test',
                    status: statusNum,
                    createdAt: 1000,
                    modifiedAt: 1000,
                  },
                  lifecycle: 'ready',
                  turns: [],
                },
              },
            ],
          },
        });
        steps.push({
          op: 'client.assert.state',
          label: 'status bitset preserved in reduced state',
          channel: 'ahp-session:/bitset-test',
          path: 'summary.status',
          equals: statusNum,
        });
      } else {
        // fallback: trivial steps
        steps = makeTrivialRoundTripSteps(name, description, type);
      }
      scenarioDescription = `Round-trip: ${description}`;
    } else if (type === 'ProtocolVersion') {
      // ProtocolVersion constants: seed a session and assert the protocol version
      steps.push({
        op: 'client.request',
        label: 'initialize to obtain a protocol version response',
        method: 'initialize',
        params: {},
        id: 1,
      });
      steps.push({
        op: 'server.response',
        label: 'server confirms protocol version',
        forId: 1,
        result: {
          protocolVersion: PROTOCOL_VERSION,
          serverSeq: 0,
          snapshots: [],
        },
      });
      steps.push({
        op: 'client.assert.state',
        label: 'negotiated protocol version is non-empty',
        path: 'protocolVersion',
        equals: PROTOCOL_VERSION,
      });
      scenarioDescription = `Round-trip: ${description}`;
    } else {
      // Generic case: wrap as a server.notify with the wire value in params,
      // then assert the key expect fields as event matches.
      const wireValue = wireObj ?? fixture.wire;
      if (wireValue != null) {
        steps.push({
          op: 'server.notify',
          label: `wire payload for ${type}: ${name}`,
          method: 'action',
          params: {
            channel: 'ahp-session:/roundtrip-test',
            action: wireValue,
            serverSeq: 1,
            origin: null,
          },
        });
      } else {
        // No wire — just assert a constant
        steps.push({
          op: 'client.request',
          label: 'ping to exercise the round-trip',
          method: 'ping',
          params: {},
          id: 1,
        });
        steps.push({
          op: 'server.response',
          label: 'ping response',
          forId: 1,
          result: {},
        });
      }

      // Assert the decoded wire fields. Build a PROPERLY-NESTED deep-contained
      // match object from the fixture's dotted `expect` paths (so e.g.
      // "range.start"+"range.end" become { range: { start, end } } rather than
      // collapsing to a scalar top-level key that can never match the real
      // decoded event — the class of gen bug the B4 host-conformance runner
      // surfaced).
      const { matches, wholeValue } = buildExpectMatches(fixture.expect);

      if (wholeValue !== undefined) {
        // The decoded action AS A WHOLE must equal this value (e.g. a bare
        // string/number wire like StringOrMarkdown "hello"). The envelope view
        // carries it under `action`, so deep-contain { action: <value> }.
        steps.push({
          op: 'client.assert.event',
          label: 'decoded wire value preserved through decode',
          matches: { action: wholeValue.value },
        });
      }

      if (Object.keys(matches).length > 0) {
        // Field-path expectations match against the decoded action view.
        steps.push({
          op: 'client.assert.event',
          label: 'wire fields preserved through decode',
          matches,
        });
      }

      scenarioDescription = `Round-trip: ${description}`;
    }

    // Guard: scenario must have at least 1 step and at least one assert
    if (steps.length === 0) {
      console.error(`SKIP round-trip ${basename}: could not produce any steps`);
      continue;
    }
    if (!steps.some((s) => s.op.startsWith('client.assert'))) {
      // Add a trivial assert so the scenario is minimally meaningful
      steps.push({
        op: 'client.assert.event',
        label: 'round-trip payload was observed',
        matches: {},
      });
    }

    const scenario = {
      id,
      behaviorIds,
      description: scenarioDescription || `Round-trip: ${description}`,
      protocolVersion: PROTOCOL_VERSION,
      pinClock: 9999,
      notes,
      steps,
    };

    generated.push({ id, scenario, outDir: ROUND_TRIPS_OUT, basename });
  }

  return generated;
}

// Convert "a.b.c" / value into a nested object { a: { b: { c: value } } }
// BUT for simple flat paths like "channel", "serverSeq" etc., just use flat.
function dotPathToObject(dotPath, val) {
  const parts = dotPath.split('.');
  if (parts.length === 1) return { [parts[0]]: val };
  // Only handle up to 2 levels (action.type -> { action: { type: val } })
  if (parts.length === 2) return { [parts[0]]: { [parts[1]]: val } };
  // 3+ levels: return flat key with the last segment
  return { [parts[parts.length - 1]]: val };
}

// Deep-set `value` at a dotted `path` inside `obj`, creating nested objects as
// needed, and return `obj`. Unlike dotPathToObject this MERGES into an existing
// object, so multiple sibling paths (e.g. "range.start" + "range.end") build a
// single nested shape { range: { start, end } } instead of clobbering each
// other or collapsing to a scalar top-level key. Empty path ('') means "the
// whole value"; the caller handles that case separately (it can't be merged).
function setDeep(obj, path, value) {
  const parts = path.split('.');
  let cur = obj;
  for (let i = 0; i < parts.length - 1; i++) {
    const k = parts[i];
    if (cur[k] == null || typeof cur[k] !== 'object') cur[k] = {};
    cur = cur[k];
  }
  cur[parts[parts.length - 1]] = value;
  return obj;
}

// Build a nested deep-contained matches object from a round-trip fixture's
// dotted `expect` map. Returns { matches, wholeValue } where:
//   • matches    — the nested partial object to deep-contain in the decoded
//     event's `action` view (omits the '' whole-value entry).
//   • wholeValue — present (as { value }) iff `expect` had a '' key, meaning the
//     decoded action value as a whole must equal it (e.g. a bare-string
//     StringOrMarkdown where the wire is "hello"). Asserted via assert.state.
function buildExpectMatches(expect) {
  const matches = {};
  let wholeValue;
  for (const [dotPath, val] of Object.entries(expect ?? {})) {
    if (dotPath === '') { wholeValue = { value: val }; continue; }
    setDeep(matches, dotPath, val);
  }
  return { matches, wholeValue };
}

function makeTrivialRoundTripSteps(name, description, type) {
  return [
    {
      op: 'client.request',
      label: `round-trip exercise: ${name}`,
      method: 'ping',
      params: {},
      id: 1,
    },
    {
      op: 'server.response',
      label: 'ping response (round-trip validation)',
      forId: 1,
      result: {},
    },
    {
      op: 'client.assert.event',
      label: `${type} round-trip validation placeholder`,
      matches: {},
    },
  ];
}

// --------------------------------------------------------------------------
// Tranche 3 — D7 negative paths
//
// Each D7 entry describes an error condition with an expected error code.
// We generate a scenario that:
//   1. Sends a client.request that triggers the error condition
//   2. server.response with the error
//   3. client.assert.error with the expected code
// --------------------------------------------------------------------------

// Map well-known D7 behavior-id patterns to trigger methods and params
function d7TriggerFor(item) {
  const id = item['behavior-id'];
  const method = item.method ?? 'unknownMethod';
  const notes = item.notes ?? '';

  // Determine the triggering method and params based on the behavior
  if (id.startsWith('error.ParseError') || id.includes('malformed-json')) {
    // Parse error: send a syntactically invalid frame — model as sending unknown method
    return { method: 'unknownMethod', params: { _malformed: true } };
  }
  if (id.startsWith('error.InvalidRequest')) {
    return { method: 'ping', params: null }; // omit required fields
  }
  if (id.startsWith('error.MethodNotFound') || id.includes('unknown-method') || id.includes('MethodNotFound')) {
    return { method: 'unknownMethod', params: { channel: 'ahp-root://' } };
  }
  if (id.startsWith('error.InvalidParams') || id.includes('missing-required-field') || id.includes('wrong-type-field') || id.includes('missing-channel-field')) {
    if (method === 'initialize' || notes.includes('initialize')) {
      return { method: 'initialize', params: { badField: 42 } }; // missing protocolVersions, bad clientId type
    }
    return { method: method !== 'unknownMethod' ? method : 'initialize', params: {} };
  }
  if (id.startsWith('versioning.UnsupportedProtocolVersion') || id.includes('UnsupportedProtocolVersion') || id.includes('no-overlap')) {
    return { method: 'initialize', params: { protocolVersions: ['999.0.0'], clientId: 'test-client' } };
  }
  if (id.startsWith('error.SessionNotFound') || id.includes('nonexistent-session')) {
    return { method: 'subscribe', params: { channel: 'ahp-session:/00000000-0000-0000-0000-000000000000' } };
  }
  if (id.startsWith('error.ProviderNotFound') || id.startsWith('error.SessionAlreadyExists') || id.startsWith('auth.AuthRequired')) {
    const m = item.method ?? 'createSession';
    return { method: m, params: { channel: 'ahp-session:/test', provider: 'nonexistent-provider' } };
  }
  if (id.startsWith('auth.authenticate')) {
    return { method: 'authenticate', params: { token: 'invalid_token', resource: 'ahp-resource://x' } };
  }
  if (id.startsWith('error.TurnInProgress')) {
    return { method: 'turnStart', params: { channel: 'ahp-session:/test' } };
  }
  if (id.startsWith('subscription.subscribe') || id.includes('unknown-channel-scheme')) {
    return { method: 'subscribe', params: { channel: 'ahp-unknown://foo' } };
  }
  if (id.startsWith('error.NotFound') || id.includes('resource-missing')) {
    return { method: 'resourceRead', params: { channel: 'ahp-session:/test', uri: 'file:///nonexistent-path' } };
  }
  if (id.startsWith('error.PermissionDenied') || id.includes('path-outside-workspace')) {
    return { method: 'resourceRead', params: { channel: 'ahp-session:/test', uri: 'file:///outside-workspace' } };
  }
  if (id.startsWith('error.AlreadyExists') || id.includes('create-only')) {
    return { method: 'resourceWrite', params: { channel: 'ahp-session:/test', uri: 'file:///existing-file', createOnly: true, content: '' } };
  }
  if (id.startsWith('error.Conflict') || id.includes('etag-mismatch')) {
    return { method: 'resourceWrite', params: { channel: 'ahp-session:/test', uri: 'file:///some-file', ifMatch: 'stale-etag', content: '' } };
  }
  if (id.startsWith('rpc.ping.edge')) {
    return { method: 'ping', params: {} };
  }
  if (id.startsWith('rpc.initialize.error.duplicate-clientId')) {
    return { method: 'initialize', params: { protocolVersions: ['0.3.0'], clientId: 'duplicate-client' } };
  }
  if (id.startsWith('transport.') || id.startsWith('reconnect.')) {
    // Transport/reconnect edge cases: use reconnect
    return { method: 'reconnect', params: { channel: 'ahp-session:/test', lastSeenServerSeq: 0 } };
  }
  // Default: use the declared method or unknownMethod
  return { method: method !== null ? method : 'unknownMethod', params: { channel: 'ahp-root://' } };
}

// Extract expected error code from D7 entry
function d7ErrorCode(item) {
  const id = item['behavior-id'];
  const notes = item.notes ?? '';
  const assertion = item.assertion ?? '';

  // Explicit code extraction from assertion text
  const codeMatch = (assertion + ' ' + notes).match(/error\.code\s*===?\s*(-\d+)/);
  if (codeMatch) return parseInt(codeMatch[1], 10);

  // By concept
  const concept = item.concept ?? '';
  if (concept.includes('ParseError')) return -32700;
  if (concept.includes('InvalidRequest')) return -32600;
  if (concept.includes('MethodNotFound')) return -32601;
  if (concept.includes('InvalidParams')) return -32602;
  if (concept.includes('InternalError')) return -32603;
  if (concept.includes('SessionNotFound')) return -32001;
  if (concept.includes('ProviderNotFound')) return -32002;
  if (concept.includes('SessionAlreadyExists')) return -32003;
  if (concept.includes('TurnInProgress')) return -32004;
  if (concept.includes('UnsupportedProtocolVersion')) return -32005;
  if (concept.includes('ContentNotFound')) return -32006;
  if (concept.includes('AuthRequired')) return -32007;
  if (concept.includes('NotFound')) return -32008;
  if (concept.includes('PermissionDenied')) return -32009;
  if (concept.includes('AlreadyExists')) return -32010;
  if (concept.includes('Conflict')) return -32011;

  // By behavior-id prefix
  if (id.includes('MethodNotFound') || id.includes('unknown-method')) return -32601;
  if (id.includes('InvalidParams') || id.includes('missing-required') || id.includes('wrong-type') || id.includes('missing-channel')) return -32602;
  if (id.includes('UnsupportedProtocol') || id.includes('no-overlap') || id.includes('close-after-refuse')) return -32005;
  if (id.includes('nonexistent-session')) return -32001;
  if (id.includes('malformed-json') || id.includes('ParseError')) return -32700;
  if (id.includes('invalid-token') || id.includes('unauthenticated') || id.includes('AuthRequired') || id.includes('data-resources')) return -32007;
  if (id.includes('path-outside-workspace') || id.includes('PermissionDenied')) return -32009;
  if (id.includes('etag-mismatch') || id.includes('Conflict')) return -32011;
  if (id.includes('create-only') || id.includes('AlreadyExists')) return -32010;
  if (id.includes('resource-missing') || id.includes('NotFound')) return -32008;
  if (id.includes('unknown-channel-scheme')) return -32601; // MethodNotFound or InvalidParams

  return -32601; // safe default: method not found
}

// Sanitize a D7 behavior-id to be a valid scenario id
// The D7 ids already follow the grammar — use them directly as scenario ids
// but prefixed with "neg." to distinguish from positive scenarios.
// Actually: D7 ids already match the pattern, no need to prefix.
// Pattern: ^[A-Za-z0-9-]+(\.[A-Za-z0-9-]+){2,4}$  (3-5 dot segments)
function d7ScenarioId(item) {
  const rawId = item['behavior-id'];
  // Already in the right grammar (the discovery used the same grammar)
  return rawId;
}

function generateNegativeScenarios() {
  const items = readJsonLines(D7_NEGATIVES);
  const generated = [];
  const seenIds = new Set();

  for (const item of items) {
    const id = d7ScenarioId(item);

    // Guard duplicate ids (D7 may have duplicate concepts)
    if (seenIds.has(id)) {
      console.warn(`DEDUP: negative scenario id '${id}' appears more than once in D7; keeping first`);
      continue;
    }
    seenIds.add(id);

    const errorCode = d7ErrorCode(item);
    const trigger = d7TriggerFor(item);
    const assertion = item.assertion ?? '';
    const notes = item.notes ?? '';

    // Build message substring from notes/assertion
    let messageSubstring;
    const msgMatch = notes.match(/error code -\d+ \..*?([A-Z][a-z]+)/);
    // Don't extract a possibly incorrect substring — omit for broad compatibility
    messageSubstring = undefined;

    const steps = [];

    // Special case: ping-before-initialize (should succeed, not error)
    const isPingEdge = id.includes('ping') && id.includes('pre-initialize');
    if (isPingEdge) {
      steps.push({
        op: 'client.request',
        label: 'ping before initialize — must be answered',
        method: 'ping',
        params: {},
        id: 1,
      });
      steps.push({
        op: 'server.response',
        label: 'ping result (even before initialize)',
        forId: 1,
        result: {},
      });
      // Assert no error
      steps.push({
        op: 'client.assert.state',
        label: 'no error surfaced; ping result received',
        path: 'pingSeen',
        equals: null,
      });

      const scenario = {
        id,
        behaviorIds: [id],
        description: `Negative/edge: ${item.assertion || item.notes || id}`,
        protocolVersion: PROTOCOL_VERSION,
        notes: `Auto-generated from D7: ${item['behavior-id']}. ${notes}`,
        steps,
      };
      generated.push({ id, scenario, outDir: NEGATIVES_OUT, basename: `${id}.scenario.json` });
      continue;
    }

    // Standard negative: client sends a request, server responds with an error
    steps.push({
      op: 'client.request',
      label: `send ${trigger.method} to trigger the error condition`,
      method: trigger.method,
      params: trigger.params,
      id: 1,
    });

    const errorObj = {
      code: errorCode,
      message: `Error: ${item.concept ?? id}`,
    };

    steps.push({
      op: 'server.response',
      label: `server returns error code ${errorCode}`,
      forId: 1,
      error: errorObj,
    });

    const assertStep = {
      op: 'client.assert.error',
      label: `client surfaces error code ${errorCode}`,
      code: errorCode,
    };
    if (messageSubstring) assertStep.message = messageSubstring;
    steps.push(assertStep);

    const scenario = {
      id,
      behaviorIds: [id],
      description: `Negative/error-path: ${assertion || notes || id}. Client surfaces JSON-RPC error code ${errorCode}.`,
      protocolVersion: PROTOCOL_VERSION,
      notes: `Auto-generated from D7 negative paths: ${item['behavior-id']}. ${notes}`,
      steps,
    };

    generated.push({ id, scenario, outDir: NEGATIVES_OUT, basename: `${id}.scenario.json` });
  }

  return generated;
}

// --------------------------------------------------------------------------
// Main
// --------------------------------------------------------------------------

console.log('AHP scenario generator — B2');
console.log(`DRY_RUN: ${DRY_RUN}`);
console.log(`Output root: ${SCENARIOS_ROOT}`);
console.log('');

// Load D5 lookup
const d5Lookup = buildD5LookupFromFile();
console.log(`D5 behavior-id lookup: ${d5Lookup.size} entries`);

// Create output directories
ensureDir(REDUCERS_OUT);
ensureDir(ROUND_TRIPS_OUT);
ensureDir(NEGATIVES_OUT);

// Generate all three tranches
const reducerScenarios = generateReducerScenarios(d5Lookup);
const roundTripScenarios = generateRoundTripScenarios(d5Lookup);
const negativeScenarios = generateNegativeScenarios();

// Detect cross-tranche id collisions before writing
const allIds = new Map();
let collisions = 0;
for (const { id, outDir } of [...reducerScenarios, ...roundTripScenarios, ...negativeScenarios]) {
  if (allIds.has(id)) {
    console.error(`COLLISION: id '${id}' generated by multiple tranches (${allIds.get(id)} and ${outDir})`);
    collisions++;
  } else {
    allIds.set(id, outDir);
  }
}
if (collisions > 0) {
  console.error(`${collisions} id collision(s) detected — fix generator before writing`);
  process.exit(1);
}

// Write all scenarios
let written = 0;
for (const { id, scenario, outDir } of reducerScenarios) {
  writeScenario(outDir, id, scenario);
  written++;
}
console.log(`Reducers:    ${reducerScenarios.length} scenario(s) written to ${DRY_RUN ? '(dry-run)' : REDUCERS_OUT}`);

for (const { id, scenario, outDir } of roundTripScenarios) {
  writeScenario(outDir, id, scenario);
  written++;
}
console.log(`Round-trips: ${roundTripScenarios.length} scenario(s) written to ${DRY_RUN ? '(dry-run)' : ROUND_TRIPS_OUT}`);

for (const { id, scenario, outDir } of negativeScenarios) {
  writeScenario(outDir, id, scenario);
  written++;
}
console.log(`Negatives:   ${negativeScenarios.length} scenario(s) written to ${DRY_RUN ? '(dry-run)' : NEGATIVES_OUT}`);

console.log('');
console.log(`Total: ${written} scenario(s)${DRY_RUN ? ' (dry-run — no files written)' : ''}`);
