import { SUPPORTED_PROTOCOL_VERSIONS } from '../../types/version/registry.js';
import {
  ARCHIVE_STATE_ACTION,
  classifyArchiveStateProtocolVersion,
  defineArchiveStateConformanceRow,
  type ArchiveStateConformanceRow,
  type ArchiveStateEnvelopeObservation,
  type ArchiveStateFixture,
  type ArchiveStateProjection,
  type ArchiveStateVersionClassification,
} from './contract.js';

export interface ArchiveStateConformanceResult {
  readonly identity: ArchiveStateConformanceRow['identity'];
  readonly versions: readonly ArchiveStateVersionClassification[];
  readonly transitions: readonly ArchiveStateAcceptedTransitionResult[];
}

export type ArchiveStateAuthorityConvergence =
  | 'immediate'
  | 'delayed'
  | 'restart-exact'
  | 'never-converged';

export interface ArchiveStateAcceptedTransitionResult {
  readonly negotiatedVersion: string;
  readonly requestedArchived: boolean;
  readonly authorityConvergence: ArchiveStateAuthorityConvergence;
}

export class ArchiveStateAuthorityNeverConvergedError extends Error {
  readonly code = 'never-converged';

  constructor(
    readonly observation: ArchiveStateAcceptedTransitionResult,
  ) {
    super(
      `never-converged: archive authority remained stale after the bounded delayed observation `
        + `for protocol ${observation.negotiatedVersion} requestedArchived=${observation.requestedArchived}`,
    );
    this.name = 'ArchiveStateAuthorityNeverConvergedError';
  }
}

function assertEqual<T>(actual: T, expected: T, message: string): void {
  if (!Object.is(actual, expected)) {
    throw new Error(
      `${message}: expected ${String(expected)}, received ${String(actual)}`,
    );
  }
}

function wait(milliseconds: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

async function readAuthority(
  row: ArchiveStateConformanceRow,
  fixture: ArchiveStateFixture,
): Promise<boolean> {
  return row.archiveAuthority.kind === 'provider'
    ? row.archiveAuthority.queryArchived(fixture)
    : row.archiveAuthority.reopenAndQueryArchived(fixture);
}

function assertProjection(
  row: ArchiveStateConformanceRow,
  actual: ArchiveStateProjection,
  expected: {
    readonly initiating: boolean;
    readonly other: boolean;
    readonly server: boolean;
    readonly ui: boolean;
  },
  phase: string,
): void {
  assertEqual(actual.initiatingClientArchived, expected.initiating, `${phase}: initiating client`);
  assertEqual(actual.otherClientArchived, expected.other, `${phase}: other client`);
  assertEqual(actual.serverSessionArchived, expected.server, `${phase}: AHP server session status`);
  if (row.uiProjection === 'required') {
    assertEqual(actual.uiArchived, expected.ui, `${phase}: UI projection`);
  } else {
    assertEqual(actual.uiArchived, undefined, `${phase}: undeclared UI projection`);
  }
}

function assertEnvelope(
  envelope: ArchiveStateEnvelopeObservation,
  isArchived: boolean,
  rejectionReason: string | undefined,
): void {
  assertEqual(envelope.action, ARCHIVE_STATE_ACTION, 'envelope action');
  assertEqual(envelope.isArchived, isArchived, 'envelope archive value');
  assertEqual(envelope.originMatches, true, 'envelope clientId/clientSeq correlation');
  assertEqual(envelope.rejectionReason, rejectionReason, 'envelope rejection');
}

async function assertRejectedTransition(
  row: ArchiveStateConformanceRow,
  fixture: ArchiveStateFixture,
  confirmed: boolean,
  requested: boolean,
): Promise<void> {
  const probe = await row.beginTransition(fixture, requested);
  await probe.authorityStarted;
  assertEqual(
    probe.envelopes().some(envelope => envelope.rejectionReason === undefined),
    false,
    'accepted final-state envelope before authority settlement',
  );
  assertProjection(row, await row.readProjection(fixture), {
    initiating: requested,
    other: confirmed,
    server: confirmed,
    ui: requested,
  }, 'optimistic-before-rejection');

  const reason = `injected-${requested ? 'archive' : 'unarchive'}-failure`;
  await probe.settleFailure(reason);
  assertEnvelope(await probe.waitForEnvelope(), requested, reason);
  assertEqual(probe.envelopes().length, 1, 'rejection envelope count');
  assertProjection(row, await row.readProjection(fixture), {
    initiating: confirmed,
    other: confirmed,
    server: confirmed,
    ui: confirmed,
  }, 'after-rejection');
  assertEqual(await readAuthority(row, fixture), confirmed, 'rejected authority state');
}

async function assertAcceptedTransition(
  row: ArchiveStateConformanceRow,
  fixture: ArchiveStateFixture,
  negotiatedVersion: string,
  confirmed: boolean,
  requested: boolean,
): Promise<ArchiveStateAcceptedTransitionResult> {
  const probe = await row.beginTransition(fixture, requested);
  await probe.authorityStarted;
  assertEqual(
    probe.envelopes().some(envelope => envelope.rejectionReason === undefined),
    false,
    'accepted final-state envelope before authority settlement',
  );
  assertProjection(row, await row.readProjection(fixture), {
    initiating: requested,
    other: confirmed,
    server: confirmed,
    ui: requested,
  }, 'optimistic-before-acceptance');

  await probe.settleSuccess();
  assertEnvelope(await probe.waitForEnvelope(), requested, undefined);
  assertEqual(probe.envelopes().length, 1, 'accepted envelope count');
  assertProjection(row, await row.readProjection(fixture), {
    initiating: requested,
    other: requested,
    server: requested,
    ui: requested,
  }, 'after-acceptance');
  if (row.archiveAuthority.kind === 'host') {
    assertEqual(
      await row.archiveAuthority.reopenAndQueryArchived(fixture),
      requested,
      'restart/reopen host authority query',
    );
    return {
      negotiatedVersion,
      requestedArchived: requested,
      authorityConvergence: 'restart-exact',
    };
  }

  const immediate = await row.archiveAuthority.queryArchived(fixture);
  if (immediate === requested) {
    return {
      negotiatedVersion,
      requestedArchived: requested,
      authorityConvergence: 'immediate',
    };
  }

  await wait(row.delayedObservationMs);
  const delayed = await row.archiveAuthority.queryArchived(fixture);
  if (delayed === requested) {
    return {
      negotiatedVersion,
      requestedArchived: requested,
      authorityConvergence: 'delayed',
    };
  }

  throw new ArchiveStateAuthorityNeverConvergedError({
    negotiatedVersion,
    requestedArchived: requested,
    authorityConvergence: 'never-converged',
  });
}

async function runExactVersion(
  row: ArchiveStateConformanceRow,
  classification: Extract<ArchiveStateVersionClassification, { readonly kind: 'run' }>,
): Promise<readonly ArchiveStateAcceptedTransitionResult[]> {
  const fixture = await row.createFixture();
  try {
    assertProjection(row, await row.readProjection(fixture), {
      initiating: false,
      other: false,
      server: false,
      ui: false,
    }, `initial-${classification.negotiatedVersion}`);
    assertEqual(await readAuthority(row, fixture), false, 'initial archive authority');
    await assertRejectedTransition(row, fixture, false, true);
    const archive = await assertAcceptedTransition(
      row,
      fixture,
      classification.negotiatedVersion,
      false,
      true,
    );
    await assertRejectedTransition(row, fixture, true, false);
    const unarchive = await assertAcceptedTransition(
      row,
      fixture,
      classification.negotiatedVersion,
      true,
      false,
    );
    return [archive, unarchive];
  } finally {
    await row.cleanup(fixture);
  }
}

export async function runArchiveStateConformance(
  candidate: ArchiveStateConformanceRow,
): Promise<ArchiveStateConformanceResult> {
  const row = defineArchiveStateConformanceRow(candidate);
  const classifications: ArchiveStateVersionClassification[] = [];
  const transitions: ArchiveStateAcceptedTransitionResult[] = [];
  for (const version of SUPPORTED_PROTOCOL_VERSIONS) {
    const negotiation = await row.negotiate([version]);
    const classification = classifyArchiveStateProtocolVersion(
      row,
      version,
      negotiation,
    );
    classifications.push(classification);
    if (classification.kind === 'run') {
      transitions.push(...await runExactVersion(row, classification));
    }
  }
  return { identity: row.identity, versions: classifications, transitions };
}
