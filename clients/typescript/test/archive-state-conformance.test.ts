import assert from 'node:assert/strict';
import test from 'node:test';
import {
  ARCHIVE_STATE_ACTION,
  ArchiveStateAuthorityNeverConvergedError,
  UnclassifiedProtocolVersionError,
  classifyArchiveStateProtocolVersion,
  defineArchiveStateConformanceRow,
  runArchiveStateConformance,
  summarizeArchiveStateBatchEvidence,
  type ArchiveStateBatchEvidence,
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
      clientImplementations: {
        writer: 'test/writer-client',
        observer: 'test/observer-client',
      },
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
      writerClientArchived: initiating,
      observerClientArchived: other,
      serverSessionArchived: confirmed,
      uiArchived: ui,
    }),
    reconnectClientsAndReadProjection: async (_fixture: ArchiveStateFixture) => ({
      writerClientArchived: confirmed,
      observerClientArchived: confirmed,
      serverSessionArchived: confirmed,
      uiArchived: confirmed,
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
  assert.ok(result.transitions.every(
    transition => transition.clientReconnectProjection === 'exact',
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

test('writer and observer client identities are required independently from host identity', () => {
  const row = providerRow();
  assert.throws(
    () => defineArchiveStateConformanceRow({
      ...row,
      identity: {
        ...row.identity,
        clientImplementations: {
          ...row.identity.clientImplementations,
          observer: '',
        },
      },
    }),
    /requires exactly one non-empty writer and observer client implementation identity/,
  );
});

test('unknown client roles fail row construction', () => {
  const row = providerRow();
  assert.throws(
    () => defineArchiveStateConformanceRow({
      ...row,
      identity: {
        ...row.identity,
        clientImplementations: {
          ...row.identity.clientImplementations,
          other: 'test/unknown-client',
        },
      } as ArchiveStateConformanceRow['identity'],
    }),
    /requires exactly one non-empty writer and observer client implementation identity/,
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

function codexPrefixBatchEvidence(): ArchiveStateBatchEvidence {
  const timedOut = (deploymentId: string, newlyArchived: number) => ({
    deploymentId,
    settlement: {
      kind: 'attempted' as const,
      attempted: 25,
      acceptedEnvelopes: 0,
      rejectedEnvelopes: 0,
      timedOut: 25,
    },
    providerDurability: {
      newlyArchived,
      previouslyArchived: 0,
      unarchivedAfterFreshReopen: 0,
    },
  });
  return {
    identity: {
      implementationId: 'microsoft/vscode:agent-host',
      providerId: 'openai/codex-app-server',
      writerClientImplementationId: 'microsoft/agent-host-protocol:typescript-client',
    },
    applicability: applicability(),
    populationId: 'codex-prefix-archive-2026-08-27',
    observedAt: '2026-08-27T00:00:00-04:00',
    offeredVersion: '1.0.0',
    negotiation: {
      protocolVersion: '1.0.0',
      capabilities: [],
    },
    deployments: [
      timedOut('josh-name', 136),
      timedOut('josh-gmail', 270),
      {
        deploymentId: 'joshua-gmail',
        settlement: {
          kind: 'unavailable',
          affectedResources: 117,
          stage: 'exact-channel-subscription',
          reason: 'provider already archived while the pre-reopen AHP catalog remained stale',
        },
        providerDurability: {
          newlyArchived: 117,
          previouslyArchived: 361,
          unarchivedAfterFreshReopen: 0,
        },
      },
      timedOut('arielle-name', 135),
      {
        deploymentId: 'default',
        settlement: {
          kind: 'not-exercised',
          reason: 'no matching unarchived resources',
        },
        providerDurability: {
          newlyArchived: 0,
          previouslyArchived: 0,
          unarchivedAfterFreshReopen: 0,
        },
      },
    ],
  };
}

test('recorded batch evidence keeps action settlement and provider durability denominators separate', () => {
  const summary = summarizeArchiveStateBatchEvidence(codexPrefixBatchEvidence());
  assert.equal(summary.version.kind, 'run');
  assert.deepEqual(summary.settlement, {
    attempted: 75,
    acceptedEnvelopes: 0,
    rejectedEnvelopes: 0,
    timedOut: 75,
    unavailableAtExactChannelSubscription: 117,
  });
  assert.deepEqual(summary.providerDurability, {
    newlyArchived: 658,
    previouslyArchived: 361,
    matchingResources: 1019,
    archivedAfterFreshReopen: 1019,
    unarchivedAfterFreshReopen: 0,
  });
});

test('batch evidence fails closed for an unclassified negotiated protocol version', () => {
  const evidence = codexPrefixBatchEvidence();
  assert.throws(
    () => summarizeArchiveStateBatchEvidence({
      ...evidence,
      negotiation: {
        ...evidence.negotiation,
        protocolVersion: '1.1.0',
      },
    }),
    (error: unknown) => error instanceof UnclassifiedProtocolVersionError
      && error.code === 'unclassified-protocol-version'
      && error.negotiatedVersion === '1.1.0',
  );
});

test('batch evidence requires a closed action-settlement denominator', () => {
  const evidence = codexPrefixBatchEvidence();
  assert.throws(
    () => summarizeArchiveStateBatchEvidence({
      ...evidence,
      deployments: evidence.deployments.map((deployment, index) => index === 0
        ? {
            ...deployment,
            settlement: {
              kind: 'attempted',
              attempted: 25,
              acceptedEnvelopes: 0,
              rejectedEnvelopes: 0,
              timedOut: 24,
            },
          }
        : deployment),
    }),
    /settlement outcomes 24 do not equal attempts 25/,
  );
});
