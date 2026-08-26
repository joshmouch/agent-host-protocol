import { ActionType } from '../../types/actions.js';
import {
  ACTION_INTRODUCED_IN,
  compareProtocolVersions,
  SUPPORTED_PROTOCOL_VERSIONS,
  type NegotiableProtocolVersion,
  type ProtocolVersion,
} from '../../types/version/registry.js';

export const ARCHIVE_STATE_ACTION = ActionType.SessionIsArchivedChanged;
export const ARCHIVE_STATE_ACTION_INTRODUCED_IN =
  ACTION_INTRODUCED_IN[ARCHIVE_STATE_ACTION];

export type ArchiveStateBaseVersionDisposition =
  | { readonly kind: 'run' }
  | { readonly kind: 'same-as'; readonly version: NegotiableProtocolVersion }
  | { readonly kind: 'not-introduced' }
  | {
      readonly kind: 'superseded';
      readonly replacement: NegotiableProtocolVersion;
      readonly replacementContract: string;
      readonly migration: string;
    };

export interface ArchiveStateCapabilityConditionedDisposition {
  readonly kind: 'capability-conditioned';
  readonly capability: string;
  readonly whenPresent: ArchiveStateBaseVersionDisposition;
  readonly whenAbsent: ArchiveStateBaseVersionDisposition;
}

export type ArchiveStateVersionDisposition =
  | ArchiveStateBaseVersionDisposition
  | ArchiveStateCapabilityConditionedDisposition;

export type ArchiveStateVersionDispositions = {
  readonly [K in NegotiableProtocolVersion]: ArchiveStateVersionDisposition;
};

export interface ArchiveStateProtocolApplicability {
  readonly requiredAction: typeof ARCHIVE_STATE_ACTION;
  /** Total over the literal canonical negotiable-version roster. */
  readonly versions: ArchiveStateVersionDispositions;
}

export interface ArchiveStateImplementationIdentity {
  /** Stable executable/source implementation, never a profile or account. */
  readonly implementationId: string;
  /** Client/reconciler implementation exercised by this row. */
  readonly clientImplementationId: string;
  readonly providerId: string;
  /** Optional coverage instance of the implementation. */
  readonly deploymentId?: string;
}

export interface ArchiveStateFixture {
  readonly resource: string;
}

export interface ArchiveStateProjection {
  readonly initiatingClientArchived: boolean;
  readonly otherClientArchived: boolean;
  readonly serverSessionArchived: boolean;
  readonly uiArchived?: boolean;
}

export interface ArchiveStateEnvelopeObservation {
  readonly action: typeof ARCHIVE_STATE_ACTION;
  readonly isArchived: boolean;
  /** True only when both clientId and clientSeq match the dispatch. */
  readonly originMatches: boolean;
  readonly rejectionReason?: string;
}

/** Test-only control around the row's real authority seam. */
export interface ArchiveStateTransitionProbe {
  readonly authorityStarted: Promise<void>;
  envelopes(): readonly ArchiveStateEnvelopeObservation[];
  settleSuccess(): Promise<void>;
  settleFailure(reason: string): Promise<void>;
  waitForEnvelope(): Promise<ArchiveStateEnvelopeObservation>;
}

export interface ProviderArchiveAuthority {
  readonly kind: 'provider';
  queryArchived(fixture: ArchiveStateFixture): Promise<boolean>;
}

export interface HostArchiveAuthority {
  readonly kind: 'host';
  readonly durabilityBoundary: string;
  reopenAndQueryArchived(fixture: ArchiveStateFixture): Promise<boolean>;
}

export type ArchiveAuthority =
  | ProviderArchiveAuthority
  | HostArchiveAuthority;

export interface ArchiveStateNegotiationResult {
  readonly protocolVersion: string;
  readonly capabilities: readonly string[];
}

export interface ArchiveStateConformanceRow {
  readonly identity: ArchiveStateImplementationIdentity;
  readonly applicability: ArchiveStateProtocolApplicability;
  readonly archiveAuthority: ArchiveAuthority;
  readonly uiProjection: 'required' | 'not-applicable';
  readonly delayedObservationMs: number;
  negotiate(
    offeredVersions: readonly NegotiableProtocolVersion[],
  ): Promise<ArchiveStateNegotiationResult>;
  createFixture(): Promise<ArchiveStateFixture>;
  beginTransition(
    fixture: ArchiveStateFixture,
    isArchived: boolean,
  ): Promise<ArchiveStateTransitionProbe>;
  readProjection(fixture: ArchiveStateFixture): Promise<ArchiveStateProjection>;
  cleanup(fixture: ArchiveStateFixture): Promise<void>;
}

export type ArchiveStateVersionClassification =
  | {
      readonly kind: 'run';
      readonly negotiatedVersion: NegotiableProtocolVersion;
      readonly semanticVersion: NegotiableProtocolVersion;
      readonly actionIntroducedIn: ProtocolVersion;
    }
  | {
      readonly kind: 'not-introduced';
      readonly negotiatedVersion: NegotiableProtocolVersion;
      readonly actionIntroducedIn: ProtocolVersion;
    }
  | {
      readonly kind: 'superseded';
      readonly negotiatedVersion: NegotiableProtocolVersion;
      readonly replacementVersion: NegotiableProtocolVersion;
      readonly replacementContract: string;
      readonly migration: string;
    };

export class UnclassifiedProtocolVersionError extends Error {
  readonly code = 'unclassified-protocol-version';

  constructor(
    readonly negotiatedVersion: string,
    readonly implementationId: string,
  ) {
    super(
      `unclassified-protocol-version: ${implementationId} negotiated ${negotiatedVersion}; `
        + 'update the archive-state conformance contract deliberately',
    );
  }
}

export function isNegotiableProtocolVersion(
  version: string,
): version is NegotiableProtocolVersion {
  return SUPPORTED_PROTOCOL_VERSIONS.some(candidate => candidate === version);
}

function selectCapabilityDisposition(
  disposition: ArchiveStateVersionDisposition,
  capabilities: ReadonlySet<string>,
): ArchiveStateBaseVersionDisposition {
  if (disposition.kind !== 'capability-conditioned') return disposition;
  return capabilities.has(disposition.capability)
    ? disposition.whenPresent
    : disposition.whenAbsent;
}

function resolveApplicableVersion(
  row: ArchiveStateConformanceRow,
  start: NegotiableProtocolVersion,
  capabilities: ReadonlySet<string>,
): NegotiableProtocolVersion {
  const visited = new Set<NegotiableProtocolVersion>();
  let version = start;
  while (true) {
    if (visited.has(version)) {
      throw new Error(`archive-state replacement cycle at ${version}`);
    }
    visited.add(version);
    const declared = row.applicability.versions[version];
    if (!declared) {
      throw new Error(`archive-state replacement target is missing: ${version}`);
    }
    const disposition = selectCapabilityDisposition(declared, capabilities);
    if (disposition.kind === 'run') return version;
    if (disposition.kind === 'same-as') {
      version = disposition.version;
      continue;
    }
    if (disposition.kind === 'superseded') {
      version = disposition.replacement;
      continue;
    }
    throw new Error(
      `archive-state replacement ${version} is ${disposition.kind}, not applicable`,
    );
  }
}

export function classifyArchiveStateProtocolVersion(
  row: ArchiveStateConformanceRow,
  offeredVersion: NegotiableProtocolVersion,
  negotiation: ArchiveStateNegotiationResult,
): ArchiveStateVersionClassification {
  if (!isNegotiableProtocolVersion(negotiation.protocolVersion)) {
    throw new UnclassifiedProtocolVersionError(
      negotiation.protocolVersion,
      row.identity.implementationId,
    );
  }
  if (negotiation.protocolVersion !== offeredVersion) {
    throw new Error(
      `archive-state exact negotiation offered ${offeredVersion} but host selected ${negotiation.protocolVersion}`,
    );
  }
  const capabilities = new Set(negotiation.capabilities);
  const declared = row.applicability.versions[offeredVersion];
  if (!declared) {
    throw new UnclassifiedProtocolVersionError(
      offeredVersion,
      row.identity.implementationId,
    );
  }
  const disposition = selectCapabilityDisposition(declared, capabilities);
  if (disposition.kind === 'not-introduced') {
    if (compareProtocolVersions(
      offeredVersion,
      ARCHIVE_STATE_ACTION_INTRODUCED_IN,
    ) >= 0) {
      throw new Error(
        `archive-state ${offeredVersion} is classified not-introduced after ${ARCHIVE_STATE_ACTION_INTRODUCED_IN}`,
      );
    }
    return {
      kind: 'not-introduced',
      negotiatedVersion: offeredVersion,
      actionIntroducedIn: ARCHIVE_STATE_ACTION_INTRODUCED_IN,
    };
  }
  if (disposition.kind === 'superseded') {
    return {
      kind: 'superseded',
      negotiatedVersion: offeredVersion,
      replacementVersion: resolveApplicableVersion(
        row,
        disposition.replacement,
        capabilities,
      ),
      replacementContract: disposition.replacementContract,
      migration: disposition.migration,
    };
  }
  if (compareProtocolVersions(
    ARCHIVE_STATE_ACTION_INTRODUCED_IN,
    offeredVersion,
  ) > 0) {
    throw new Error(
      `archive-state action is not introduced at negotiated version ${offeredVersion}`,
    );
  }
  return {
    kind: 'run',
    negotiatedVersion: offeredVersion,
    semanticVersion: disposition.kind === 'same-as'
      ? resolveApplicableVersion(row, disposition.version, capabilities)
      : offeredVersion,
    actionIntroducedIn: ARCHIVE_STATE_ACTION_INTRODUCED_IN,
  };
}

export function defineArchiveStateConformanceRow(
  row: ArchiveStateConformanceRow,
): ArchiveStateConformanceRow {
  if (!row.identity.implementationId
    || !row.identity.clientImplementationId
    || !row.identity.providerId) {
    throw new Error(
      'archive-state row requires implementationId, clientImplementationId, and providerId',
    );
  }
  if (row.applicability.requiredAction !== ARCHIVE_STATE_ACTION) {
    throw new Error(`archive-state row requires ${ARCHIVE_STATE_ACTION}`);
  }
  if (row.delayedObservationMs < 0 || row.delayedObservationMs > 5_000) {
    throw new Error('archive-state delayedObservationMs must be between 0 and 5000');
  }
  if (row.archiveAuthority.kind === 'host'
    && !row.archiveAuthority.durabilityBoundary) {
    throw new Error('host archive authority requires a durabilityBoundary');
  }
  for (const version of SUPPORTED_PROTOCOL_VERSIONS) {
    if (!row.applicability.versions[version]) {
      throw new Error(`archive-state row does not classify ${version}`);
    }
  }
  return row;
}
