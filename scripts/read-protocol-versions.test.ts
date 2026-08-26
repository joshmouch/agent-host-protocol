import assert from 'node:assert/strict';
import test from 'node:test';
import { Project } from 'ts-morph';
import { readProtocolVersions } from './read-protocol-versions.js';

function projectWithRegistry(source: string): Project {
  const project = new Project({ useInMemoryFileSystem: true });
  project.createSourceFile('/types/version/registry.ts', source);
  return project;
}

test('readProtocolVersions derives current and supported from one release roster', () => {
  const project = projectWithRegistry(`
    export const PROTOCOL_RELEASES = [
      { version: '2.0.0', negotiable: true },
      { version: '1.0.0', negotiable: false },
      { version: '0.9.0', negotiable: true },
    ] as const;
  `);
  assert.deepEqual(readProtocolVersions(project), {
    current: '2.0.0',
    supported: ['2.0.0', '0.9.0'],
  });
});

test('readProtocolVersions fails when the release roster is not literal', () => {
  const project = projectWithRegistry(`
    const negotiable = true;
    export const PROTOCOL_RELEASES = [
      { version: '2.0.0', negotiable },
    ] as const;
  `);
  assert.throws(
    () => readProtocolVersions(project),
    /PROTOCOL_RELEASES missing or empty/,
  );
});
