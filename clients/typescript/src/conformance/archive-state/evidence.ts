import type { NegotiableProtocolVersion } from '../../types/version/registry.js';
import {
  classifyArchiveStateProtocolVersion,
  type ArchiveStateNegotiationResult,
  type ArchiveStateProtocolApplicability,
  type ArchiveStateVersionClassification,
} from './contract.js';

export interface ArchiveStateBatchEvidenceIdentity {
  /** Stable host executable/source implementation, never a profile or account. */
  readonly implementationId: string;
  readonly providerId: string;
  readonly writerClientImplementationId: string;
}

export interface ArchiveStateAttemptedSettlementEvidence {
  readonly kind: 'attempted';
  readonly attempted: number;
  readonly acceptedEnvelopes: number;
  readonly rejectedEnvelopes: number;
  readonly timedOut: number;
}

export interface ArchiveStateUnavailableSettlementEvidence {
  readonly kind: 'unavailable';
  /** Resources for which the settlement observation could not be attempted. */
  readonly affectedResources: number;
  readonly stage: 'exact-channel-subscription';
  readonly reason: string;
}

export interface ArchiveStateNotExercisedSettlementEvidence {
  readonly kind: 'not-exercised';
  readonly reason: string;
}

export type ArchiveStateSettlementEvidence =
  | ArchiveStateAttemptedSettlementEvidence
  | ArchiveStateUnavailableSettlementEvidence
  | ArchiveStateNotExercisedSettlementEvidence;

export interface ArchiveStateProviderDurabilityEvidence {
  readonly newlyArchived: number;
  readonly previouslyArchived: number;
  /** Matching resources still projected unarchived after a fresh authority reopen. */
  readonly unarchivedAfterFreshReopen: number;
}

export interface ArchiveStateDeploymentEvidence {
  /** A profile/account/service instance, distinct from implementation identity. */
  readonly deploymentId: string;
  readonly settlement: ArchiveStateSettlementEvidence;
  readonly providerDurability: ArchiveStateProviderDurabilityEvidence;
}

export interface ArchiveStateBatchEvidence {
  readonly identity: ArchiveStateBatchEvidenceIdentity;
  readonly applicability: ArchiveStateProtocolApplicability;
  readonly populationId: string;
  readonly observedAt: string;
  readonly offeredVersion: NegotiableProtocolVersion;
  readonly negotiation: ArchiveStateNegotiationResult;
  readonly deployments: readonly ArchiveStateDeploymentEvidence[];
}

export interface ArchiveStateSettlementEvidenceSummary {
  /** Denominator for accepted/rejected/timeout envelope outcomes only. */
  readonly attempted: number;
  readonly acceptedEnvelopes: number;
  readonly rejectedEnvelopes: number;
  readonly timedOut: number;
  /** Separate population that never entered the envelope-outcome denominator. */
  readonly unavailableAtExactChannelSubscription: number;
}

export interface ArchiveStateProviderDurabilityEvidenceSummary {
  readonly newlyArchived: number;
  readonly previouslyArchived: number;
  /** Denominator for the fresh-reopen durability observation. */
  readonly matchingResources: number;
  readonly archivedAfterFreshReopen: number;
  readonly unarchivedAfterFreshReopen: number;
}

export interface ArchiveStateBatchEvidenceSummary {
  readonly identity: ArchiveStateBatchEvidenceIdentity;
  readonly populationId: string;
  readonly observedAt: string;
  readonly version: Extract<ArchiveStateVersionClassification, { readonly kind: 'run' }>;
  readonly settlement: ArchiveStateSettlementEvidenceSummary;
  readonly providerDurability: ArchiveStateProviderDurabilityEvidenceSummary;
}

function assertNonNegativeInteger(value: number, field: string): void {
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new Error(`${field} must be a non-negative safe integer`);
  }
}

function assertNonEmpty(value: string, field: string): void {
  if (!value.trim()) {
    throw new Error(`${field} must be non-empty`);
  }
}

/**
 * Summarize recorded batch evidence without collapsing action settlement into
 * provider durability. This records observations; it does not add a receipt or
 * alternate production authority to the archive action.
 */
export function summarizeArchiveStateBatchEvidence(
  evidence: ArchiveStateBatchEvidence,
): ArchiveStateBatchEvidenceSummary {
  assertNonEmpty(evidence.identity.implementationId, 'implementationId');
  assertNonEmpty(evidence.identity.providerId, 'providerId');
  assertNonEmpty(
    evidence.identity.writerClientImplementationId,
    'writerClientImplementationId',
  );
  assertNonEmpty(evidence.populationId, 'populationId');
  assertNonEmpty(evidence.observedAt, 'observedAt');

  const version = classifyArchiveStateProtocolVersion(
    evidence,
    evidence.offeredVersion,
    evidence.negotiation,
  );
  if (version.kind !== 'run') {
    throw new Error(
      `archive-state batch evidence requires runnable semantics at ${evidence.offeredVersion}; received ${version.kind}`,
    );
  }

  const deploymentIds = new Set<string>();
  let attempted = 0;
  let acceptedEnvelopes = 0;
  let rejectedEnvelopes = 0;
  let timedOut = 0;
  let unavailableAtExactChannelSubscription = 0;
  let newlyArchived = 0;
  let previouslyArchived = 0;
  let unarchivedAfterFreshReopen = 0;

  for (const deployment of evidence.deployments) {
    assertNonEmpty(deployment.deploymentId, 'deploymentId');
    if (deploymentIds.has(deployment.deploymentId)) {
      throw new Error(`duplicate archive-state deploymentId: ${deployment.deploymentId}`);
    }
    deploymentIds.add(deployment.deploymentId);

    const durability = deployment.providerDurability;
    assertNonNegativeInteger(durability.newlyArchived, 'newlyArchived');
    assertNonNegativeInteger(durability.previouslyArchived, 'previouslyArchived');
    assertNonNegativeInteger(
      durability.unarchivedAfterFreshReopen,
      'unarchivedAfterFreshReopen',
    );
    const deploymentPopulation = durability.newlyArchived
      + durability.previouslyArchived;
    if (durability.unarchivedAfterFreshReopen > deploymentPopulation) {
      throw new Error(
        `unarchivedAfterFreshReopen exceeds the matching population for ${deployment.deploymentId}`,
      );
    }
    newlyArchived += durability.newlyArchived;
    previouslyArchived += durability.previouslyArchived;
    unarchivedAfterFreshReopen += durability.unarchivedAfterFreshReopen;

    const settlement = deployment.settlement;
    if (settlement.kind === 'attempted') {
      assertNonNegativeInteger(settlement.attempted, 'attempted');
      assertNonNegativeInteger(settlement.acceptedEnvelopes, 'acceptedEnvelopes');
      assertNonNegativeInteger(settlement.rejectedEnvelopes, 'rejectedEnvelopes');
      assertNonNegativeInteger(settlement.timedOut, 'timedOut');
      const outcomes = settlement.acceptedEnvelopes
        + settlement.rejectedEnvelopes
        + settlement.timedOut;
      if (outcomes !== settlement.attempted) {
        throw new Error(
          `settlement outcomes ${outcomes} do not equal attempts ${settlement.attempted} for ${deployment.deploymentId}`,
        );
      }
      attempted += settlement.attempted;
      acceptedEnvelopes += settlement.acceptedEnvelopes;
      rejectedEnvelopes += settlement.rejectedEnvelopes;
      timedOut += settlement.timedOut;
    } else if (settlement.kind === 'unavailable') {
      assertNonNegativeInteger(settlement.affectedResources, 'affectedResources');
      assertNonEmpty(settlement.reason, 'unavailable settlement reason');
      unavailableAtExactChannelSubscription += settlement.affectedResources;
    } else {
      assertNonEmpty(settlement.reason, 'not-exercised settlement reason');
    }
  }

  const matchingResources = newlyArchived + previouslyArchived;
  return {
    identity: evidence.identity,
    populationId: evidence.populationId,
    observedAt: evidence.observedAt,
    version,
    settlement: {
      attempted,
      acceptedEnvelopes,
      rejectedEnvelopes,
      timedOut,
      unavailableAtExactChannelSubscription,
    },
    providerDurability: {
      newlyArchived,
      previouslyArchived,
      matchingResources,
      archivedAfterFreshReopen: matchingResources - unarchivedAfterFreshReopen,
      unarchivedAfterFreshReopen,
    },
  };
}
