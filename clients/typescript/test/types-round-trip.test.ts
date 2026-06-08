/**
 * types-round-trip.test.ts — data-driven wire round-trip parity for TypeScript.
 *
 * Loads the SHARED, language-agnostic round-trip corpus under
 * `types/test-cases/round-trips/*.json` (the same fixtures the .NET client runs
 * via clients/dotnet/tests/.../TypesRoundTripFixtures.cs and the Swift client
 * runs via clients/swift/.../TypesRoundTripFixtureTests.swift) and asserts each
 * via the REAL generated TypeScript wire types.
 *
 * --- Why TS is structurally different from Swift / .NET here ---------------
 *
 * Swift and .NET have RUNTIME deserializers (Codable / System.Text.Json) that
 * impose the type's shape on decode: required-field enforcement, discriminated-
 * union dispatch, unknown-key dropping, computed-discriminator omission, and
 * unknown-variant passthrough are all decisions baked into generated decode/
 * encode code. The shared corpus exercises exactly those decisions, so those
 * two clients catch real encode/decode-fidelity bugs.
 *
 * TypeScript's generated types are COMPILE-TIME ONLY. There is no runtime
 * decoder: the canonical way to "decode the wire" with the real generated types
 * is `JSON.parse(wire) as T` — a structural cast, not a validating constructor.
 * Re-encoding is `JSON.stringify(value)`. Consequences:
 *
 *   - Unknown discriminators (002 StateAction, 003 Customization) are PRESERVED
 *     verbatim — `JSON.parse`/`JSON.stringify` keeps every field — so the TS
 *     client passes the fixtures Swift fails. The corpus's `expectVariant`
 *     "JsonElement" passthrough name maps to "the decoded object's discriminator
 *     is NOT a known variant" (a structurally-preserved passthrough object).
 *   - The discriminator field (012/013 `kind`) is a real property of the parsed
 *     object, so it survives re-encode (Swift drops it; TS does not).
 *   - 64-bit integers (016) are JS numbers; values up to Number.MAX_SAFE_INTEGER
 *     round-trip exactly (the corpus values are well under 2^53).
 *   - UNKNOWN KEYS ARE NOT DROPPED on re-encode (017, 019). `JSON.stringify`
 *     re-emits whatever `JSON.parse` produced, including unrecognized keys.
 *     Stripping unknown keys would require a runtime schema/decoder the
 *     published TS client does not ship. These are recorded as
 *     `knownRepresentationalGaps` with a drift tripwire (mirroring Swift's
 *     mechanism), because they are a property of the type SYSTEM, not a bug in
 *     any one generated file.
 *
 * --- Neutral discriminators (shared with .NET / Swift) --------------------
 *   * expect                 — dotted JSON paths checked against the RE-ENCODED
 *                              wire. "" means the whole re-encoded value.
 *   * expectVariant          — { accessor: ConcreteTypeName }; "" means the whole
 *                              decoded value's active variant. The corpus uses
 *                              .NET concrete type names; mapped here to the TS
 *                              structural discriminator (a wire `type`/`kind`).
 *   * expectJsonRpcVariant   — request|notification|success|error, mapped to the
 *                              structural JSON-RPC shape (method+id / method /
 *                              id+result / id+error).
 *   * expectBitset           — SessionStatus flag membership + numeric value,
 *                              checked with HasFlag semantics ((v & flag)===flag).
 *   * expectNumberAbove      — a re-encoded numeric field exceeds a 64-bit bound.
 *   * expectReencodedAbsent  — keys that must NOT appear in the re-encoded wire.
 *   * reencodes              — re-encode is structurally equal to the input bytes.
 *   * roundTripStable        — decode→encode→decode→encode is a fixed point (and
 *                              any `expect` paths still hold on the 2nd pass).
 *   * expectConstant         — ProtocolVersion constants (no wire decode).
 *
 * Run: npm test (node --test --import tsx test/*.test.ts) from clients/typescript.
 *
 * Real-execution: no mocks. Every fixture is decoded with `JSON.parse` against
 * the REAL generated types and re-encoded with `JSON.stringify`, then the
 * fixture's expectations are asserted against the decoded value and the
 * re-encoded bytes. The `type` → decode dispatch is a deliberate, explicit
 * mapping; the corpus never decodes arbitrary types reflectively.
 */

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// NOTE on imports: the generated types are a literal copy of the canonical
// `types/*.ts`. Several discriminant enums are `const enum`s whose VALUE export
// the barrel (`index.ts`) only re-exports type-only (`export type { ... }`), so
// they have no runtime binding through the barrel. Import those const enums
// DIRECTLY from their source module (where they are `export const enum`), the
// same way the existing client.test.ts imports `ActionType`. `SessionStatus`,
// `PROTOCOL_VERSION`, and `SUPPORTED_PROTOCOL_VERSIONS` ARE value-exported by
// the barrel.
import {
  SessionStatus,
  PROTOCOL_VERSION,
  SUPPORTED_PROTOCOL_VERSIONS,
} from '../src/types/index.js';
import { ActionType } from '../src/types/common/actions.js';
import type {
  ActionEnvelope,
  StateAction,
} from '../src/types/common/actions.js';
import type { StringOrMarkdown } from '../src/types/common/state.js';
import {
  ChangesetOperationTargetKind,
} from '../src/types/channels-changeset/commands.js';
import type { ChangesetOperationTarget } from '../src/types/channels-changeset/commands.js';
import {
  SessionInputQuestionKind,
  CustomizationType,
} from '../src/types/channels-session/state.js';
import type {
  SessionInputQuestion,
  Customization,
  SessionSummary,
} from '../src/types/channels-session/state.js';
import type { SessionAddedParams } from '../src/types/channels-root/notifications.js';

// ─── Known representational gaps (documented, not silent) ────────────────────
//
// A corpus fixture in this set asserts behavior that depends on a RUNTIME
// DECODER imposing the type's shape — something the published TypeScript client
// does not have. Its generated types are compile-time only (a literal copy of
// the canonical `types/*.ts`; there is no `System.Text.Json`-style converter
// layer like .NET, nor a `Codable` synthesis like Swift). "Decoding the wire"
// with the real generated types is `JSON.parse(wire) as T` — a structural cast
// — and re-encoding is `JSON.stringify(value)`. A gap here is a property of the
// type SYSTEM, not a bug in any one generated file, and closing it would mean
// SHIPPING a runtime validator/decoder the client deliberately omits.
//
// Each fixture in this set is RUN, observed to fail-to-represent for the precise
// documented reason, and reported out of the suite. The test asserts that the
// set of fixtures that actually fail equals THIS set (drift tripwire, mirroring
// Swift's `knownRepresentationalGaps`): if a gap closes (e.g. a validating
// decoder ships) or a new one opens, the suite fails loudly and forces this
// list to be updated.
//
// 017 unknown-wire-keys-ignored:
//     The corpus asserts (`expectReencodedAbsent`) that unrecognized keys
//     (unknownFutureKey, anotherUnknown) are DROPPED on re-encode. Swift/.NET
//     drop them because their decoders only read declared properties. TS
//     `JSON.parse`→`JSON.stringify` preserves every key verbatim — there is no
//     runtime schema to strip unknowns — so the unknown keys survive and the
//     `expectReencodedAbsent` assertion fails. Stripping unknown keys would
//     require a runtime decoder/validator the TS client does not ship. This is
//     the one genuine TS type-system representational gap in the corpus.
const knownRepresentationalGaps: ReadonlySet<string> = new Set<string>([
  '017-unknown-wire-keys-ignored',
]);

// ─── Known-broken fixtures (excluded from the run) ───────────────────────────
//
// These fixtures are themselves invalid (not a client-fidelity issue) and are
// being repaired OUTSIDE this client. They are skipped entirely — neither
// asserted against (so this suite is not coupled to a fixture known to be wrong)
// nor counted as a representational gap (TS's lack of runtime validation means
// the gap would not even reproduce here). When the upstream repair lands, remove
// the fixture from this set and it rejoins the real-assertion path.
//
// 019 channel-scoped-notification-uri:
//     Schema-invalid: the wire payload omits the schema-REQUIRED `summary` field
//     of SessionAddedParams (schema/notifications.schema.json marks both
//     `channel` and `summary` required — see KNOWN-FIDELITY-GAPS.md Gap 5). The
//     .NET agent is repairing the fixture (giving it a valid `summary`). On TS
//     it would actually pass by accident (no runtime decoder ⇒ the missing
//     required field is not enforced, and `roundTripStable` holds on the
//     degenerate `{channel,session}`), but asserting on a fixture known to be
//     wrong — and which is about to change shape upstream — would make this
//     suite's green status depend on malformed input. Skip until repaired.
const knownBrokenFixtures: ReadonlySet<string> = new Set<string>([
  '019-channel-scoped-notification-uri',
]);

// ─── Fixture directory ───────────────────────────────────────────────────────

const THIS_FILE = fileURLToPath(import.meta.url);
// clients/typescript/test/types-round-trip.test.ts → repo root → types/test-cases/round-trips
const REPO_ROOT = path.resolve(path.dirname(THIS_FILE), '..', '..', '..');
const FIXTURE_DIR = path.join(REPO_ROOT, 'types', 'test-cases', 'round-trips');

function fixtureFiles(): string[] {
  return fs
    .readdirSync(FIXTURE_DIR)
    .filter(f => f.endsWith('.json'))
    .sort();
}

function stem(file: string): string {
  return file.replace(/\.json$/, '');
}

// ─── Loaded-something guard ──────────────────────────────────────────────────

test('round-trip corpus is present', () => {
  assert.ok(
    fixtureFiles().length > 0,
    `No round-trip fixtures found at ${FIXTURE_DIR}. Ensure the checkout includes types/test-cases/round-trips/.`,
  );
});

// ─── Whole-corpus runner ─────────────────────────────────────────────────────

test('round-trip corpus decodes + re-encodes via the real generated types', () => {
  const failures: string[] = [];
  const gapHits = new Set<string>();
  let ranRealAssertions = 0;

  let skipped = 0;
  for (const file of fixtureFiles()) {
    const s = stem(file);

    // Known-broken fixtures are excluded entirely — see knownBrokenFixtures.
    if (knownBrokenFixtures.has(s)) {
      skipped += 1;
      console.log(`⊝ ${file}: skipped (known-broken fixture, repaired upstream)`);
      continue;
    }

    const raw = fs.readFileSync(path.join(FIXTURE_DIR, file), 'utf-8');
    const root = JSON.parse(raw) as FixtureRoot;

    try {
      runFixture(file, root);
      ranRealAssertions += 1;
    } catch (err) {
      if (knownRepresentationalGaps.has(s)) {
        gapHits.add(s);
        console.log(`⊘ ${file}: known TS representational gap — ${(err as Error).message}`);
      } else {
        failures.push(`✗ ${file}: ${(err as Error).message}`);
      }
    }
  }

  // Every fixture that is neither known-broken nor a representational gap must
  // have run a real assertion.
  const expectedReal =
    fixtureFiles().length - knownRepresentationalGaps.size - knownBrokenFixtures.size;
  void skipped;
  assert.equal(
    ranRealAssertions,
    expectedReal,
    `Expected ${expectedReal} fixtures to decode+assert for real; only ${ranRealAssertions} did.`,
  );

  // The gap set must be exactly the fixtures that failed to represent. If a gap
  // closes, gapHits shrinks → mismatch → update the list. If a new fixture
  // can't be represented, it lands in `failures` → loud.
  assert.deepEqual(
    [...gapHits].sort(),
    [...knownRepresentationalGaps].sort(),
    `Known-gap set drifted. Hit gaps: ${[...gapHits].sort().join(', ')}; declared: ${[...knownRepresentationalGaps].sort().join(', ')}. A gap that no longer reproduces must be removed from knownRepresentationalGaps (and ideally promoted to a real assertion).`,
  );

  assert.equal(
    failures.length,
    0,
    `${failures.length} round-trip fixture(s) failed:\n${failures.join('\n')}`,
  );
});

// ─── Fixture shape ───────────────────────────────────────────────────────────

interface FixtureRoot {
  readonly name?: string;
  readonly description?: string;
  readonly type: string;
  readonly wire?: unknown;
  readonly wireRaw?: string;
  readonly expect?: Record<string, unknown>;
  readonly expectVariant?: Record<string, string>;
  readonly expectJsonRpcVariant?: string;
  readonly expectBitset?: { has?: string[]; lacks?: string[]; numeric?: number };
  readonly expectNumberAbove?: Record<string, number>;
  readonly expectReencodedAbsent?: string[];
  readonly reencodes?: boolean;
  readonly roundTripStable?: boolean;
  readonly expectConstant?: Record<string, unknown>;
}

type JsonValue =
  | null
  | boolean
  | number
  | string
  | JsonValue[]
  | { [key: string]: JsonValue };

// ─── Per-fixture dispatch ────────────────────────────────────────────────────

function runFixture(file: string, root: FixtureRoot): void {
  const type = root.type;
  if (typeof type !== 'string') {
    throw new Error(`${file}: missing \`type\``);
  }

  // ProtocolVersion fixtures assert constants, not wire decode.
  if (type === 'ProtocolVersion') {
    verifyProtocolConstant(file, root);
    return;
  }

  const inputJson = readInputJson(file, root);
  const { decoded, reencoded } = decodeAndReencode(file, type, inputJson);

  let assertedSomething = false;

  if (root.expect) {
    const reObj = JSON.parse(reencoded) as JsonValue;
    for (const [pathExpr, want] of Object.entries(root.expect)) {
      const got = resolvePath(reObj, pathExpr, file);
      assertJsonEquals(want as JsonValue, got, `${file}: expect["${pathExpr}"]`);
      assertedSomething = true;
    }
  }

  if (root.expectVariant) {
    verifyVariant(file, type, decoded, root.expectVariant);
    assertedSomething = true;
  }

  if (root.expectJsonRpcVariant !== undefined) {
    verifyJsonRpcVariant(file, decoded, root.expectJsonRpcVariant);
    assertedSomething = true;
  }

  if (root.expectBitset) {
    verifyBitset(file, type, decoded, reencoded, root.expectBitset);
    assertedSomething = true;
  }

  if (root.expectNumberAbove) {
    const reObj = JSON.parse(reencoded) as JsonValue;
    for (const [pathExpr, bound] of Object.entries(root.expectNumberAbove)) {
      const got = resolvePath(reObj, pathExpr, file);
      const gotN = asNumber(got);
      if (gotN === undefined) {
        throw new Error(`${file}: expectNumberAbove["${pathExpr}"] — non-numeric (${describe(got)})`);
      }
      if (!(gotN > bound)) {
        throw new Error(`${file}: expectNumberAbove["${pathExpr}"] — ${gotN} is not > ${bound}`);
      }
      assertedSomething = true;
    }
  }

  if (root.expectReencodedAbsent) {
    const reObj = JSON.parse(reencoded) as JsonValue;
    const obj = isObject(reObj) ? reObj : {};
    for (const key of root.expectReencodedAbsent) {
      if (Object.prototype.hasOwnProperty.call(obj, key)) {
        throw new Error(
          `${file}: re-encoded JSON must NOT contain key "${key}" but it does. Re-encoded: ${reencoded}`,
        );
      }
      assertedSomething = true;
    }
  }

  if (root.reencodes) {
    assertCanonicalEqual(inputJson, reencoded, `${file}: reencodes (byte/structure-exact)`);
    assertedSomething = true;
  }

  if (root.roundTripStable) {
    const second = decodeAndReencode(file, type, reencoded).reencoded;
    if (root.expect) {
      const re2 = JSON.parse(second) as JsonValue;
      for (const [pathExpr, want] of Object.entries(root.expect)) {
        const got = resolvePath(re2, pathExpr, file);
        assertJsonEquals(want as JsonValue, got, `${file}: roundTripStable expect["${pathExpr}"] (2nd decode)`);
      }
    } else {
      assertCanonicalEqual(reencoded, second, `${file}: roundTripStable fixed-point`);
    }
    assertedSomething = true;
  }

  if (!assertedSomething) {
    throw new Error(`${file}: fixture made no assertions — coverage theater.`);
  }
}

// ─── Real decode dispatch ────────────────────────────────────────────────────
//
// Mirrors the .NET / Swift DecodeAndReencode switch. In TS there is no runtime
// decoder, so "decode" is `JSON.parse(...) as <GeneratedType>` (a structural
// cast) and "re-encode" is `JSON.stringify(...)`. Each case binds the parsed
// value to its real generated type so the variant/bitset assertions inspect the
// typed shape. Adding a wire type to the corpus is a deliberate edit here; the
// corpus never decodes arbitrary types reflectively.

type DecodedValue =
  | { kind: 'ActionEnvelope'; value: ActionEnvelope }
  | { kind: 'StateAction'; value: StateAction }
  | { kind: 'Customization'; value: Customization }
  | { kind: 'SessionStatus'; value: SessionStatus }
  | { kind: 'StringOrMarkdown'; value: StringOrMarkdown }
  | { kind: 'JsonRpcMessage'; value: JsonValue }
  | { kind: 'ChangesetOperationTarget'; value: ChangesetOperationTarget }
  | { kind: 'SessionInputQuestion'; value: SessionInputQuestion }
  | { kind: 'SessionSummary'; value: SessionSummary }
  | { kind: 'SessionAddedParams'; value: SessionAddedParams }
  | { kind: 'PartialSessionSummary'; value: Partial<SessionSummary> };

function decodeAndReencode(
  file: string,
  type: string,
  inputJson: string,
): { decoded: DecodedValue; reencoded: string } {
  const parsed = JSON.parse(inputJson) as unknown;
  const reencoded = JSON.stringify(parsed);

  let decoded: DecodedValue;
  switch (type) {
    case 'ActionEnvelope':
      decoded = { kind: 'ActionEnvelope', value: parsed as ActionEnvelope };
      break;
    case 'StateAction':
      decoded = { kind: 'StateAction', value: parsed as StateAction };
      break;
    case 'Customization':
      decoded = { kind: 'Customization', value: parsed as Customization };
      break;
    case 'SessionStatus':
      decoded = { kind: 'SessionStatus', value: parsed as SessionStatus };
      break;
    case 'StringOrMarkdown':
      decoded = { kind: 'StringOrMarkdown', value: parsed as StringOrMarkdown };
      break;
    case 'JsonRpcMessage':
      // TS has no single JsonRpcMessage union type; the wire is structurally
      // discriminated (see verifyJsonRpcVariant). Keep the parsed JSON.
      decoded = { kind: 'JsonRpcMessage', value: parsed as JsonValue };
      break;
    case 'ChangesetOperationTarget':
      decoded = { kind: 'ChangesetOperationTarget', value: parsed as ChangesetOperationTarget };
      break;
    case 'SessionInputQuestion':
      decoded = { kind: 'SessionInputQuestion', value: parsed as SessionInputQuestion };
      break;
    case 'SessionSummary':
      decoded = { kind: 'SessionSummary', value: parsed as SessionSummary };
      break;
    case 'SessionAddedParams':
      decoded = { kind: 'SessionAddedParams', value: parsed as SessionAddedParams };
      break;
    case 'PartialSessionSummary':
      // TS models this as `Partial<SessionSummary>` (a utility type; no nominal
      // PartialSessionSummary). Decode structurally.
      decoded = { kind: 'PartialSessionSummary', value: parsed as Partial<SessionSummary> };
      break;
    default:
      throw new Error(
        `${file}: unknown wire type "${type}". Add a decode entry to decodeAndReencode.`,
      );
  }

  return { decoded, reencoded };
}

// ─── Variant identity (maps .NET concrete-type names → TS discriminators) ─────

function verifyVariant(
  file: string,
  type: string,
  decoded: DecodedValue,
  variants: Record<string, string>,
): void {
  for (const [accessor, want] of Object.entries(variants)) {
    const actual =
      accessor.length === 0
        ? wholeVariantTypeName(file, decoded)
        : namedAccessorVariantTypeName(file, decoded, accessor);
    if (actual !== want) {
      const ctx = accessor.length === 0 ? 'expectVariant[""]' : `expectVariant["${accessor}"]`;
      throw new Error(`${file}: ${ctx} — active variant is ${actual ?? 'nil'}, expected ${want}`);
    }
  }
  void type;
}

/**
 * Maps the active variant of a top-level decoded union to the .NET concrete
 * type name the corpus uses. Returns `undefined` for non-union decoded values.
 */
function wholeVariantTypeName(file: string, decoded: DecodedValue): string | undefined {
  switch (decoded.kind) {
    case 'StateAction':
      return stateActionVariantName(decoded.value);
    case 'Customization':
      return customizationVariantName(decoded.value);
    case 'ChangesetOperationTarget':
      return changesetTargetVariantName(decoded.value);
    case 'SessionInputQuestion':
      return inputQuestionVariantName(decoded.value);
    case 'StringOrMarkdown':
      return typeof decoded.value === 'string' ? 'String' : 'MarkdownString';
    default:
      void file;
      return undefined;
  }
}

function namedAccessorVariantTypeName(
  file: string,
  decoded: DecodedValue,
  accessor: string,
): string | undefined {
  if (decoded.kind === 'ActionEnvelope' && accessor.toLowerCase() === 'action') {
    return stateActionVariantName(decoded.value.action);
  }
  throw new Error(`${file}: expectVariant accessor "${accessor}" not wired for this decoded type`);
}

/** Known ActionType discriminant string → its .NET concrete action type name. */
const ACTION_TYPE_TO_VARIANT: Readonly<Record<string, string>> = {
  [ActionType.SessionTitleChanged]: 'SessionTitleChangedAction',
};

function stateActionVariantName(a: StateAction): string {
  // The wire discriminant is the `type` field. A recognized discriminant maps
  // to its concrete *Action type name; an UNRECOGNIZED discriminant is the
  // passthrough case — TS preserves the whole object (no nominal unknown type),
  // which the corpus names "JsonElement" (the .NET raw-JsonElement passthrough).
  const wireType = (a as unknown as { type?: unknown }).type;
  if (typeof wireType === 'string' && wireType in ACTION_TYPE_TO_VARIANT) {
    return ACTION_TYPE_TO_VARIANT[wireType];
  }
  return 'JsonElement';
}

function customizationVariantName(c: Customization): string {
  const t = (c as unknown as { type?: unknown }).type;
  if (t === CustomizationType.Plugin) return 'PluginCustomization';
  if (t === CustomizationType.Directory) return 'DirectoryCustomization';
  // Unknown `type` — TS preserves the object verbatim (passthrough). The corpus
  // names this passthrough "JsonElement" (.NET's allowUnknown raw element).
  return 'JsonElement';
}

function changesetTargetVariantName(t: ChangesetOperationTarget): string {
  const kind = (t as unknown as { kind?: unknown }).kind;
  if (kind === ChangesetOperationTargetKind.Resource) return 'ChangesetOperationResourceTarget';
  if (kind === ChangesetOperationTargetKind.Range) return 'ChangesetOperationRangeTarget';
  return `Unknown(${String(kind)})`;
}

function inputQuestionVariantName(q: SessionInputQuestion): string {
  const kind = (q as unknown as { kind?: unknown }).kind;
  switch (kind) {
    case SessionInputQuestionKind.Text:
      return 'SessionInputTextQuestion';
    // BOTH `number` and `integer` map to the same concrete number-question type
    // (SessionInputNumberQuestion); the typed Kind preserves the distinction.
    case SessionInputQuestionKind.Number:
    case SessionInputQuestionKind.Integer:
      return 'SessionInputNumberQuestion';
    case SessionInputQuestionKind.Boolean:
      return 'SessionInputBooleanQuestion';
    case SessionInputQuestionKind.SingleSelect:
      return 'SessionInputSingleSelectQuestion';
    case SessionInputQuestionKind.MultiSelect:
      return 'SessionInputMultiSelectQuestion';
    default:
      return `Unknown(${String(kind)})`;
  }
}

// ─── JSON-RPC variant (structural) ───────────────────────────────────────────
//
// TS has no JsonRpcMessage union with named accessors; the wire shape IS the
// discriminator (see types/common/messages.ts ProtocolMessage doc):
//   request      — has `method` and `id`
//   notification — has `method`, no `id`
//   success      — has `id` and `result`, no `method`
//   error        — has `id` and `error`, no `method`

function verifyJsonRpcVariant(file: string, decoded: DecodedValue, kind: string): void {
  if (decoded.kind !== 'JsonRpcMessage') {
    throw new Error(`${file}: expectJsonRpcVariant requires a JsonRpcMessage`);
  }
  const allowed = ['request', 'notification', 'success', 'error'];
  if (!allowed.includes(kind)) {
    throw new Error(`${file}: expectJsonRpcVariant "${kind}" is not one of ${allowed.join('/')}`);
  }

  const msg = decoded.value;
  if (!isObject(msg)) {
    throw new Error(`${file}: expectJsonRpcVariant — decoded value is not a JSON object`);
  }
  const hasMethod = Object.prototype.hasOwnProperty.call(msg, 'method');
  const hasId = Object.prototype.hasOwnProperty.call(msg, 'id');
  const hasResult = Object.prototype.hasOwnProperty.call(msg, 'result');
  const hasError = Object.prototype.hasOwnProperty.call(msg, 'error');

  let actual: string;
  if (hasMethod && hasId) actual = 'request';
  else if (hasMethod && !hasId) actual = 'notification';
  else if (!hasMethod && hasId && hasResult) actual = 'success';
  else if (!hasMethod && hasId && hasError) actual = 'error';
  else actual = `indeterminate(method=${hasMethod},id=${hasId},result=${hasResult},error=${hasError})`;

  if (actual !== kind) {
    throw new Error(`${file}: expectJsonRpcVariant — decoded as ${actual}, expected ${kind}`);
  }
}

// ─── Bitset ──────────────────────────────────────────────────────────────────

function verifyBitset(
  file: string,
  type: string,
  decoded: DecodedValue,
  reencoded: string,
  bitset: { has?: string[]; lacks?: string[]; numeric?: number },
): void {
  if (decoded.kind !== 'SessionStatus') {
    throw new Error(`${file}: expectBitset requires a SessionStatus, got ${decoded.kind}`);
  }
  const value = asNumber(decoded.value as unknown as JsonValue);
  if (value === undefined) {
    throw new Error(`${file}: SessionStatus must decode to a number, got ${describe(decoded.value as unknown as JsonValue)}`);
  }

  if (bitset.has) {
    for (const name of bitset.has) {
      const flag = statusFlag(file, name);
      // HasFlag semantics: every bit of the (possibly composite) flag must be set.
      if ((value & flag) !== flag) {
        throw new Error(
          `${file}: SessionStatus must have flag ${name} but does not (value ${value})`,
        );
      }
    }
  }

  if (bitset.lacks) {
    for (const name of bitset.lacks) {
      const flag = statusFlag(file, name);
      if ((value & flag) === flag) {
        throw new Error(
          `${file}: SessionStatus must NOT have flag ${name} but does (value ${value})`,
        );
      }
    }
  }

  if (bitset.numeric !== undefined) {
    if (value !== bitset.numeric) {
      throw new Error(`${file}: SessionStatus numeric — got ${value}, expected ${bitset.numeric}`);
    }
    // The re-encoded wire form must be the same bare number.
    const reObj = JSON.parse(reencoded) as JsonValue;
    const reNum = asNumber(reObj);
    if (reNum === undefined) {
      throw new Error(`${file}: SessionStatus must re-encode as a JSON number, got ${reencoded}`);
    }
    if (reNum !== bitset.numeric) {
      throw new Error(`${file}: SessionStatus re-encoded numeric — got ${reNum}, expected ${bitset.numeric}`);
    }
  }
  void type;
}

/** Maps a .NET SessionStatus flag name to the TS const-enum member value. */
function statusFlag(file: string, name: string): number {
  switch (name) {
    case 'Idle':
      return SessionStatus.Idle;
    case 'Error':
      return SessionStatus.Error;
    case 'InProgress':
      return SessionStatus.InProgress;
    case 'InputNeeded':
      return SessionStatus.InputNeeded;
    case 'IsRead':
      return SessionStatus.IsRead;
    case 'IsArchived':
      return SessionStatus.IsArchived;
    default:
      throw new Error(`${file}: unknown SessionStatus flag "${name}"`);
  }
}

// ─── ProtocolVersion constants ───────────────────────────────────────────────

function verifyProtocolConstant(file: string, root: FixtureRoot): void {
  const c = root.expectConstant;
  if (!c) {
    throw new Error(`${file}: ProtocolVersion fixture missing expectConstant`);
  }
  let asserted = false;

  if ('current' in c) {
    if (c.current !== 'non-empty') {
      throw new Error(`${file}: expectConstant.current must be "non-empty"`);
    }
    if (typeof PROTOCOL_VERSION !== 'string' || PROTOCOL_VERSION.trim().length === 0) {
      throw new Error(`${file}: PROTOCOL_VERSION must be non-empty`);
    }
    asserted = true;
  }

  if ('supported' in c) {
    if (c.supported !== 'non-empty-list') {
      throw new Error(`${file}: expectConstant.supported must be "non-empty-list"`);
    }
    if (!Array.isArray(SUPPORTED_PROTOCOL_VERSIONS) || SUPPORTED_PROTOCOL_VERSIONS.length === 0) {
      throw new Error(`${file}: SUPPORTED_PROTOCOL_VERSIONS must be non-empty`);
    }
    asserted = true;
  }

  if ('firstSupportedEqualsCurrent' in c && c.firstSupportedEqualsCurrent === true) {
    if (SUPPORTED_PROTOCOL_VERSIONS.length === 0) {
      throw new Error(`${file}: SUPPORTED_PROTOCOL_VERSIONS is empty`);
    }
    if (SUPPORTED_PROTOCOL_VERSIONS[0] !== PROTOCOL_VERSION) {
      throw new Error(
        `${file}: first supported ${SUPPORTED_PROTOCOL_VERSIONS[0]} != current ${PROTOCOL_VERSION}`,
      );
    }
    asserted = true;
  }

  if (!asserted) {
    throw new Error(`${file}: ProtocolVersion fixture asserted no constant`);
  }
}

// ─── Input bytes ─────────────────────────────────────────────────────────────

function readInputJson(file: string, root: FixtureRoot): string {
  const hasRaw = root.wireRaw !== undefined;
  const hasWire = root.wire !== undefined;
  if (hasRaw === hasWire) {
    throw new Error(
      `${file}: exactly one of \`wire\` / \`wireRaw\` is required (wire=${hasWire}, wireRaw=${hasRaw}).`,
    );
  }
  if (hasRaw) {
    if (typeof root.wireRaw !== 'string') {
      throw new Error(`${file}: \`wireRaw\` is not a string`);
    }
    return root.wireRaw;
  }
  // `wire` is a JSON value; compact-serialize it.
  return JSON.stringify(root.wire);
}

// ─── JSON path + equality ────────────────────────────────────────────────────

/** Resolves a dotted path against a parsed JSON value. Empty path → the value. */
function resolvePath(rootObj: JsonValue, pathExpr: string, file: string): JsonValue {
  if (pathExpr.length === 0) return rootObj;
  let cur: JsonValue = rootObj;
  for (const seg of pathExpr.split('.')) {
    if (!isObject(cur) || !Object.prototype.hasOwnProperty.call(cur, seg)) {
      throw new Error(`${file}: path "${pathExpr}" — segment "${seg}" not found`);
    }
    cur = cur[seg];
  }
  return cur;
}

function assertJsonEquals(want: JsonValue, got: JsonValue, ctx: string): void {
  if (typeof want === 'string') {
    if (got !== want) {
      throw new Error(`${ctx} — expected string "${want}", got ${describe(got)}`);
    }
    return;
  }
  if (typeof want === 'number') {
    const gotN = asNumber(got);
    if (gotN !== want) {
      throw new Error(`${ctx} — expected number ${want}, got ${describe(got)}`);
    }
    return;
  }
  if (typeof want === 'boolean') {
    if (got !== want) {
      throw new Error(`${ctx} — expected ${want}, got ${describe(got)}`);
    }
    return;
  }
  if (want === null) {
    if (got !== null) {
      throw new Error(`${ctx} — expected null, got ${describe(got)}`);
    }
    return;
  }
  // Objects / arrays — compare canonical JSON.
  const wd = canonicalJson(want);
  const gd = canonicalJson(got);
  if (wd !== gd) {
    throw new Error(`${ctx} — expected ${wd}, got ${gd}`);
  }
}

/**
 * Compares two JSON documents structurally (key order independent, value and
 * key-presence sensitive). Used for `reencodes` / fixed-point checks.
 */
function assertCanonicalEqual(lhs: string, rhs: string, ctx: string): void {
  const lo = canonicalJson(JSON.parse(lhs) as JsonValue);
  const ro = canonicalJson(JSON.parse(rhs) as JsonValue);
  if (lo !== ro) {
    throw new Error(`${ctx}\n  lhs: ${lhs}\n  rhs: ${rhs}`);
  }
}

/** Deterministic, key-sorted JSON serialization for structural comparison. */
function canonicalJson(value: JsonValue): string {
  if (value === null || typeof value !== 'object') {
    return JSON.stringify(value);
  }
  if (Array.isArray(value)) {
    return `[${value.map(canonicalJson).join(',')}]`;
  }
  const keys = Object.keys(value).sort();
  return `{${keys.map(k => `${JSON.stringify(k)}:${canonicalJson(value[k])}`).join(',')}}`;
}

function isObject(v: JsonValue): v is { [key: string]: JsonValue } {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function asNumber(v: JsonValue): number | undefined {
  return typeof v === 'number' ? v : undefined;
}

function describe(v: JsonValue): string {
  if (typeof v === 'string') return `string "${v}"`;
  if (v === null) return 'null';
  if (typeof v === 'number') return `number ${v}`;
  return JSON.stringify(v);
}
