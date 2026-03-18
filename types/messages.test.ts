/**
 * Validates that every @method in commands.ts is registered in the message maps
 * (messages.ts) and that no stale entries remain.
 *
 * Run: npx tsx --test types/messages.test.ts
 */

import { describe, it } from 'node:test';
import { strict as assert } from 'node:assert';
import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)));

function readSource(file: string): string {
  return readFileSync(resolve(root, file), 'utf-8');
}

// ─── Parse commands.ts ───────────────────────────────────────────────────────

interface MethodInfo {
  method: string;
  messageType: 'Request' | 'Notification';
}

function parseCommandMethods(source: string): MethodInfo[] {
  const results: MethodInfo[] = [];
  // Match JSDoc blocks containing @method and @messageType
  const jsdocRe = /\/\*\*[\s\S]*?\*\//g;
  for (const match of source.matchAll(jsdocRe)) {
    const block = match[0];
    const methodMatch = block.match(/@method\s+(\w+)/);
    const typeMatch = block.match(/@messageType\s+(Request|Notification)/);
    if (methodMatch && typeMatch) {
      results.push({
        method: methodMatch[1],
        messageType: typeMatch[1] as 'Request' | 'Notification',
      });
    }
  }
  return results;
}

// ─── Parse messages.ts maps ──────────────────────────────────────────────────

function parseMapKeys(source: string, interfaceName: string): string[] {
  // Find the interface opening brace, then count braces to find the matching close
  const startRe = new RegExp(`interface\\s+${interfaceName}\\s*\\{`);
  const startMatch = startRe.exec(source);
  if (!startMatch) {
    throw new Error(`Interface ${interfaceName} not found in messages.ts`);
  }
  let depth = 1;
  let i = startMatch.index + startMatch[0].length;
  while (i < source.length && depth > 0) {
    if (source[i] === '{') depth++;
    else if (source[i] === '}') depth--;
    i++;
  }
  const body = source.slice(startMatch.index + startMatch[0].length, i - 1);
  const keys: string[] = [];
  // Match top-level keys only: 'methodName': at the start of a line (within body)
  for (const line of body.split('\n')) {
    const m = line.match(/^\s*'([^']+)'\s*:/);
    if (m) keys.push(m[1]);
  }
  return keys;
}

function parseExpectedUnion(source: string, typeName: string): string[] {
  // Match: type _TypeName = | 'a' | 'b' | 'c';
  const re = new RegExp(`type\\s+${typeName}\\s*=[^;]+;`, 's');
  const match = source.match(re);
  if (!match) {
    throw new Error(`Type ${typeName} not found in source`);
  }
  const values: string[] = [];
  for (const m of match[0].matchAll(/'([^']+)'/g)) {
    values.push(m[1]);
  }
  return values;
}

// ─── Tests ───────────────────────────────────────────────────────────────────

const commandsSrc = readSource('commands.ts');
const messagesSrc = readSource('messages.ts');
const messageChecksSrc = readSource('version/message-checks.ts');

const allMethods = parseCommandMethods(commandsSrc);
// Deduplicate (browseDirectory appears twice in commands.ts)
const requests = [...new Set(allMethods.filter(m => m.messageType === 'Request').map(m => m.method))];
const clientNotifications = [...new Set(allMethods.filter(m => m.messageType === 'Notification').map(m => m.method))];

describe('ICommandMap', () => {
  const mapKeys = parseMapKeys(messagesSrc, 'ICommandMap');

  it('contains every @messageType Request method from commands.ts', () => {
    const missing = requests.filter(m => !mapKeys.includes(m));
    assert.deepStrictEqual(missing, [], `Missing from ICommandMap: ${missing.join(', ')}`);
  });

  it('contains no extra methods beyond commands.ts', () => {
    const extra = mapKeys.filter(m => !requests.includes(m));
    assert.deepStrictEqual(extra, [], `Extra in ICommandMap: ${extra.join(', ')}`);
  });
});

describe('IClientNotificationMap', () => {
  const mapKeys = parseMapKeys(messagesSrc, 'IClientNotificationMap');

  it('contains every @messageType Notification method from commands.ts', () => {
    const missing = clientNotifications.filter(m => !mapKeys.includes(m));
    assert.deepStrictEqual(missing, [], `Missing from IClientNotificationMap: ${missing.join(', ')}`);
  });

  it('contains no extra methods beyond commands.ts', () => {
    const extra = mapKeys.filter(m => !clientNotifications.includes(m));
    assert.deepStrictEqual(extra, [], `Extra in IClientNotificationMap: ${extra.join(', ')}`);
  });
});

describe('_ExpectedCommands matches ICommandMap', () => {
  const expectedCommands = parseExpectedUnion(messageChecksSrc, '_ExpectedCommands');
  const mapKeys = parseMapKeys(messagesSrc, 'ICommandMap');

  it('_ExpectedCommands lists exactly the ICommandMap keys', () => {
    assert.deepStrictEqual(expectedCommands.sort(), mapKeys.sort());
  });

  it('_ExpectedCommands lists exactly the @method Request annotations', () => {
    assert.deepStrictEqual(expectedCommands.sort(), [...requests].sort());
  });
});

describe('_ExpectedClientNotifications matches IClientNotificationMap', () => {
  const expected = parseExpectedUnion(messageChecksSrc, '_ExpectedClientNotifications');
  const mapKeys = parseMapKeys(messagesSrc, 'IClientNotificationMap');

  it('_ExpectedClientNotifications lists exactly the IClientNotificationMap keys', () => {
    assert.deepStrictEqual(expected.sort(), mapKeys.sort());
  });

  it('_ExpectedClientNotifications lists exactly the @method Notification annotations', () => {
    assert.deepStrictEqual(expected.sort(), [...clientNotifications].sort());
  });
});

describe('_ExpectedServerNotifications matches IServerNotificationMap', () => {
  const expected = parseExpectedUnion(messageChecksSrc, '_ExpectedServerNotifications');
  const mapKeys = parseMapKeys(messagesSrc, 'IServerNotificationMap');

  it('_ExpectedServerNotifications lists exactly the IServerNotificationMap keys', () => {
    assert.deepStrictEqual(expected.sort(), mapKeys.sort());
  });
});

describe('command params/result naming conventions', () => {
  for (const method of requests) {
    const pascal = method[0].toUpperCase() + method.slice(1);
    const paramsName = `I${pascal}Params`;
    const resultName = `I${pascal}Result`;

    it(`${method}: ${paramsName} is exported from commands.ts`, () => {
      assert.ok(
        new RegExp(`export\\s+interface\\s+${paramsName}\\b`).test(commandsSrc),
        `Expected ${paramsName} to be exported from commands.ts`,
      );
    });

    it(`${method}: result is ${resultName} or null`, () => {
      // Check that ICommandMap entry references the right result type (or null)
      const entryRe = new RegExp(`'${method}'\\s*:\\s*\\{[^}]*result:\\s*(\\S+)`);
      const match = messagesSrc.match(entryRe);
      assert.ok(match, `Could not find ICommandMap entry for ${method}`);
      const resultType = match[1].replace(/[;\s}]+$/, '');
      assert.ok(
        resultType === resultName || resultType === 'null',
        `Expected result type ${resultName} or null, got ${resultType}`,
      );
    });
  }
});
