import assert from 'node:assert/strict';
import test from 'node:test';
import {
  ARCHIVE_STATE_ACTION,
  ArchiveStateAuthorityNeverConvergedError,
  UnclassifiedProtocolVersionError,
  classifyArchiveStateProtocolVersion,
  defineArchiveStateConformanceRow,
  runArchiveStateConformance,
  type ArchiveStateConformanceRow,
  type ArchiveStateEnvelopeObservation,
  type ArchiveStateFixture,
} from '../src/conformance/archive-state/index.js';

function applicability(): ArchiveStateConformanceRow['applicability'] {
  return {
    requiredAction: ARCHIVE_STATE_ACTION,
    versions: {
      '1.0.0': { kind: 'run' },
      '0.8.0': { kind: 'same-as', version: '1.0.0' },
      '0.7.0': { kind: 'same-as', version: '1.0.0' },
      '0.6.0': { kind: 'same-as', version: '1.0.0' },
      '0.5.2': { kind: 'same-as', version: '1.0.0' },
      '0.5.1': { kind: 'same-as', version: '1.0.0' },
    },
  };
}

function providerRow(
  convergence: 'immediate' | 'delayed' | 'never' = 'immediate',
): ArchiveStateConformanceRow {
  let providerArchived = false;
  let pendingProviderArchived: boolean | undefined;
  let confirmed = false;
  let initiating = false;
  let other = false;
  let ui = false;

  return {
    identity: {
      implementationId: 'test/host',
      clientImplementationId: 'test/client',
      providerId: 'test/provider',
      deploymentId: 'fixture-1',
    },
    applicability: applicability(),
    archiveAuthority: {
      kind: 'provider',
      queryArchived: async () => {
        if (pendingProviderArchived === undefined) return providerArchived;
        if (convergence === 'never') return providerArchived;
        const observed = providerArchived;
        providerArchived = pendingProviderArchived;
        pendingProviderArchived = undefined;
        return observed;
      },
    },
    uiProjection: 'required',
    delayedObservationMs: 0,
    negotiate: async offered => ({
      protocolVersion: offered[0],
      capabilities: [],
    }),
    createFixture: async () => {
      providerArchived = false;
      pendingProviderArchived = undefined;
      confirmed = false;
      initiating = false;
      other = false;
      ui = false;
      return { resource: 'test:/session' };
    },
    beginTransition: async (_fixture, isArchived) => {
      initiating = isArchived;
      ui = isArchived;
      const envelopes: ArchiveStateEnvelopeObservation[] = [];
      let resolveEnvelope: ((value: ArchiveStateEnvelopeObservation) => void) | undefined;
      const envelopePromise = new Promise<ArchiveStateEnvelopeObservation>(resolve => {
        resolveEnvelope = resolve;
      });
      const emit = (rejectionReason?: string) => {
        const envelope: ArchiveStateEnvelopeObservation = {
          action: ARCHIVE_STATE_ACTION,
          isArchived,
          originMatches: true,
          rejectionReason,
        };
        envelopes.push(envelope);
        resolveEnvelope?.(envelope);
      };
      return {
        authorityStarted: Promise.resolve(),
        envelopes: () => envelopes,
        settleSuccess: async () => {
          if (convergence === 'immediate') {
            providerArchived = isArchived;
          } else {
            pendingProviderArchived = isArchived;
          }
          confirmed = isArchived;
          initiating = isArchived;
          other = isArchived;
          ui = isArchived;
          emit();
        },
        settleFailure: async reason => {
          initiating = confirmed;
          ui = confirmed;
          emit(reason);
        },
        waitForEnvelope: () => envelopePromise,
      };
    },
    readProjection: async (_fixture: ArchiveStateFixture) => ({
      initiatingClientArchived: initiating,
      otherClientArchived: other,
      serverSessionArchived: confirmed,
      uiArchived: ui,
    }),
    cleanup: async () => {},
  };
}

test('provider row runs archive, rejection, rollback, convergence, and unarchive for every negotiated version', async () => {
  const result = await runArchiveStateConformance(providerRow());
  assert.equal(result.versions.length, 6);
  assert.ok(result.versions.every(version => version.kind === 'run'));
  assert.equal(result.transitions.length, 12);
  assert.ok(result.transitions.every(
    transition => transition.authorityConvergence === 'immediate',
  ));
});

test('provider convergence is classified delayed only after a stale immediate query', async () => {
  const result = await runArchiveStateConformance(providerRow('delayed'));
  assert.equal(result.transitions.length, 12);
  assert.ok(result.transitions.every(
    transition => transition.authorityConvergence === 'delayed',
  ));
});

test('a provider that remains stale reports never-converged', async () => {
  await assert.rejects(
    runArchiveStateConformance(providerRow('never')),
    (error: unknown) => error instanceof ArchiveStateAuthorityNeverConvergedError
      && error.code === 'never-converged'
      && error.observation.authorityConvergence === 'never-converged'
      && error.observation.requestedArchived,
  );
});

test('an unknown negotiated version fails closed', () => {
  const row = providerRow();
  assert.throws(
    () => classifyArchiveStateProtocolVersion(row, '1.0.0', {
      protocolVersion: '1.1.0',
      capabilities: [],
    }),
    (error: unknown) => error instanceof UnclassifiedProtocolVersionError
      && error.code === 'unclassified-protocol-version'
      && error.negotiatedVersion === '1.1.0',
  );
});

test('exact behavior negotiation rejects a different selected version', () => {
  assert.throws(
    () => classifyArchiveStateProtocolVersion(providerRow(), '0.8.0', {
      protocolVersion: '1.0.0',
      capabilities: [],
    }),
    /offered 0\.8\.0 but host selected 1\.0\.0/,
  );
});

test('host authority cannot be constructed without a restart durability boundary', () => {
  const row: ArchiveStateConformanceRow = {
    ...providerRow(),
    archiveAuthority: {
      kind: 'host',
      durabilityBoundary: '',
      reopenAndQueryArchived: async () => false,
    },
  };
  assert.throws(
    () => defineArchiveStateConformanceRow(row),
    /requires a durabilityBoundary/,
  );
});

test('client implementation identity is required independently from host identity', () => {
  const row = providerRow();
  assert.throws(
    () => defineArchiveStateConformanceRow({
      ...row,
      identity: { ...row.identity, clientImplementationId: '' },
    }),
    /requires implementationId, clientImplementationId, and providerId/,
  );
});

test('superseded replacement cycles fail instead of skipping', () => {
  const row: ArchiveStateConformanceRow = {
    ...providerRow(),
    applicability: {
      requiredAction: ARCHIVE_STATE_ACTION,
      versions: {
        ...applicability().versions,
        '0.8.0': {
          kind: 'superseded',
          replacement: '0.7.0',
          replacementContract: 'replacement',
          migration: 'migrate',
        },
        '0.7.0': {
          kind: 'superseded',
          replacement: '0.8.0',
          replacementContract: 'replacement',
          migration: 'migrate',
        },
      },
    },
  };
  assert.throws(
    () => classifyArchiveStateProtocolVersion(row, '0.8.0', {
      protocolVersion: '0.8.0',
      capabilities: [],
    }),
    /replacement cycle/,
  );
});
