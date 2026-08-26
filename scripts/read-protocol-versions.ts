/**
 * Shared helper for the protocol type generators: read the
 * canonical `PROTOCOL_RELEASES` roster from `types/version/registry.ts` using
 * ts-morph AST traversal. Current and supported versions derive from that one
 * roster, so generators never require parallel literals.
 *
 * Returns plain strings for downstream code-emit. Throws if either
 * symbol is missing or has an unexpected shape so that a refactor of
 * `registry.ts` fails the generator loudly rather than silently
 * producing stale or empty output.
 */

import { Node, Project } from 'ts-morph';

/** Parsed values from `types/version/registry.ts`. */
export interface ProtocolVersions {
  /** Value of `PROTOCOL_VERSION` ("the version new code speaks"). */
  readonly current: string;
  /**
   * Every entry of `SUPPORTED_PROTOCOL_VERSIONS`, in source order
   * (most-preferred-first). Guaranteed non-empty.
   */
  readonly supported: readonly string[];
}

/**
 * Read the protocol version constants from `types/version/registry.ts`.
 * Throws if the registry source file is not present in `project`, or if
 * the roster is missing, malformed, or violates the ordering
 * invariant. Callers building a partial ts-morph project must include
 * `types/version/registry.ts` among the source files.
 */
export function readProtocolVersions(project: Project): ProtocolVersions {
  const sf = project
    .getSourceFiles()
    .find((f) => f.getFilePath().endsWith('/version/registry.ts'));
  if (!sf) {
    throw new Error(
      'readProtocolVersions: could not locate types/version/registry.ts in project',
    );
  }

  let releases: ProtocolRelease[] | undefined;

  for (const decl of sf.getVariableDeclarations()) {
    if (decl.getName() === 'PROTOCOL_RELEASES') {
      releases = protocolReleaseValues(decl.getInitializer());
    }
  }

  if (!releases || releases.length === 0) {
    throw new Error(
      'readProtocolVersions: PROTOCOL_RELEASES missing or empty',
    );
  }
  const current = releases[0].version;
  const supported = releases
    .filter(release => release.negotiable)
    .map(release => release.version);
  if (!supported || supported.length === 0) {
    throw new Error(
      'readProtocolVersions: PROTOCOL_RELEASES has no negotiable versions',
    );
  }
  if (supported[0] !== current) {
    throw new Error(
      `readProtocolVersions: SUPPORTED_PROTOCOL_VERSIONS[0] (${supported[0]}) ` +
        `must equal PROTOCOL_VERSION (${current})`,
    );
  }

  return { current, supported };
}

interface ProtocolRelease {
  readonly version: string;
  readonly negotiable: boolean;
}

/** Extracts the literal string from a `'x'` / `"x"` initializer. */
function stringLiteralValue(init: Node | undefined): string | undefined {
  if (!init) return undefined;
  if (Node.isStringLiteral(init) || Node.isNoSubstitutionTemplateLiteral(init)) {
    return init.getLiteralValue();
  }
  return undefined;
}

/** Extract the canonical array of `{ version, negotiable }` literals. */
function protocolReleaseValues(init: Node | undefined): ProtocolRelease[] | undefined {
  if (!init) return undefined;

  let arr: Node = init;
  if (Node.isAsExpression(arr)) {
    arr = arr.getExpression();
  }

  if (!Node.isArrayLiteralExpression(arr)) return undefined;

  const values: ProtocolRelease[] = [];
  for (const el of arr.getElements()) {
    if (!Node.isObjectLiteralExpression(el)) return undefined;
    const versionProperty = el.getProperty('version');
    const negotiableProperty = el.getProperty('negotiable');
    if (!Node.isPropertyAssignment(versionProperty)
      || !Node.isPropertyAssignment(negotiableProperty)) return undefined;
    const version = stringLiteralValue(versionProperty.getInitializer());
    const negotiableInitializer = negotiableProperty.getInitializer();
    if (!version || !negotiableInitializer) return undefined;
    const negotiableText = negotiableInitializer.getText();
    if (negotiableText !== 'true' && negotiableText !== 'false') return undefined;
    values.push({ version, negotiable: negotiableText === 'true' });
  }
  return values;
}
