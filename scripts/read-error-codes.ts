/**
 * Shared helper for the protocol type generators: read the
 * `JsonRpcErrorCodes` and `AhpErrorCodes` constant objects from
 * `types/common/errors.ts` using ts-morph AST traversal, so a generator
 * emits the full, current code list rather than a hand-maintained copy
 * that silently goes stale when a new code is added (the cause of the
 * `Conflict` code being missing from the .NET client).
 *
 * Each code carries its leading `/** ... *\/` comment text so generators can
 * emit a doc comment on the constant they produce. Throws loudly if either
 * constant is missing or malformed, so a refactor of `errors.ts` fails the
 * generator rather than silently producing a stale list.
 */

import { Node, Project, SyntaxKind } from 'ts-morph';

/** One error code parsed from `types/common/errors.ts`. */
export interface ErrorCode {
  /** Member name, e.g. `Conflict`. */
  readonly name: string;
  /** Numeric code, e.g. `-32011`. */
  readonly code: number;
  /** Leading-comment description (empty string if the member has none). */
  readonly doc: string;
}

/** Parsed error-code lists from `types/common/errors.ts`. */
export interface ErrorCodes {
  /** Standard JSON-RPC 2.0 reserved codes (`JsonRpcErrorCodes`). */
  readonly jsonRpc: readonly ErrorCode[];
  /** AHP application-specific codes (`AhpErrorCodes`). */
  readonly ahp: readonly ErrorCode[];
}

/** Collapse a `/** ... *\/` (or `//`) comment block into a single line of text. */
function stripComment(raw: string): string {
  return raw
    .replace(/^\/\*\*?/, '')
    .replace(/\*\/\s*$/, '')
    .replace(/^\/\/+/, '')
    .split('\n')
    .map((line) => line.replace(/^\s*\*?\s?/, '').trim())
    .filter((line) => line.length > 0)
    .join(' ')
    .trim();
}

function readObject(project: Project, constName: string): ErrorCode[] {
  const sf = project
    .getSourceFiles()
    .find((f) => f.getFilePath().endsWith('/common/errors.ts'));
  if (!sf) {
    throw new Error('readErrorCodes: could not locate types/common/errors.ts in project');
  }
  const decl = sf.getVariableDeclaration(constName);
  if (!decl) {
    throw new Error(`readErrorCodes: ${constName} not found in types/common/errors.ts`);
  }
  let init: Node | undefined = decl.getInitializer();
  const asExpr = init?.asKind(SyntaxKind.AsExpression);
  if (asExpr) init = asExpr.getExpression();
  const obj = init?.asKind(SyntaxKind.ObjectLiteralExpression);
  if (!obj) {
    throw new Error(`readErrorCodes: ${constName} is not an \`... as const\` object literal`);
  }

  const out: ErrorCode[] = [];
  for (const prop of obj.getProperties()) {
    const pa = prop.asKind(SyntaxKind.PropertyAssignment);
    if (!pa) continue;
    const name = pa.getName();
    const code = Number(pa.getInitializerOrThrow().getText());
    if (!Number.isFinite(code)) {
      throw new Error(`readErrorCodes: ${constName}.${name} is not a numeric literal`);
    }
    const ranges = pa.getLeadingCommentRanges();
    const doc = ranges.length ? stripComment(ranges[ranges.length - 1].getText()) : '';
    out.push({ name, code, doc });
  }
  if (out.length === 0) {
    throw new Error(`readErrorCodes: ${constName} has no members`);
  }
  return out;
}

/**
 * Read both error-code constant objects from `types/common/errors.ts`.
 * Callers building a partial ts-morph project must include
 * `types/common/errors.ts` among the source files.
 */
export function readErrorCodes(project: Project): ErrorCodes {
  return {
    jsonRpc: readObject(project, 'JsonRpcErrorCodes'),
    ahp: readObject(project, 'AhpErrorCodes'),
  };
}
