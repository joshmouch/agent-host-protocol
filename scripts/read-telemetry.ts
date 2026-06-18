/**
 * Shared helper for the protocol type generators: read the telemetry name
 * enums from `types/telemetry/registry.ts` via ts-morph, returning each name's
 * identifier, wire value, and `getJsDocs()`-extracted description.
 *
 * This is the SAME extraction mechanism the generators use for every protocol
 * enum (`enumDecl.getMembers()` + `member.getJsDocs()`) — telemetry doesn't get
 * a second comment mechanism. The generators consume the returned data and emit
 * a flat per-language constant holder (telemetry names are used as raw strings,
 * not as language enums, so the output shape stays a flat holder).
 *
 * Throws loudly if the registry's expected enums/const are missing or
 * malformed, so a refactor of `registry.ts` fails the generator rather than
 * silently producing stale output.
 */

import { EnumDeclaration, Project, SyntaxKind } from 'ts-morph';

/** A single telemetry name: its identifier, wire value, and description. */
export interface TelemetryName {
  /** Enum member identifier, e.g. `MessagesSent`. */
  readonly id: string;
  /** Wire value, e.g. `ahp.client.messages.sent`. */
  readonly value: string;
  /** `getJsDocs()`-extracted description (empty if the member has none). */
  readonly doc: string;
}

/** A telemetry metric: a name plus its OTel unit annotation. */
export interface TelemetryMetricName extends TelemetryName {
  /** OTel unit, e.g. `{message}` or `ms`. */
  readonly unit: string;
}

/** A group of attribute values, e.g. the `outcome` values `{ok, error, ...}`. */
export interface TelemetryValueGroup {
  /** Group identifier (enum name minus the `Telemetry` prefix), e.g. `Outcome`. */
  readonly group: string;
  /** The values in the group. */
  readonly members: readonly TelemetryName[];
}

/** Everything the generators need to emit a telemetry-names holder. */
export interface TelemetryData {
  readonly source: { readonly value: string; readonly doc: string };
  readonly spans: readonly TelemetryName[];
  readonly metrics: readonly TelemetryMetricName[];
  readonly attributes: readonly TelemetryName[];
  readonly values: readonly TelemetryValueGroup[];
}

/** Enums that are attribute-VALUE groups (everything except spans/metrics/attributes). */
const VALUE_ENUMS = [
  'TelemetryRpcSystem',
  'TelemetryOutcome',
  'TelemetryMessageKind',
  'TelemetryStream',
] as const;

function members(enumDecl: EnumDeclaration): TelemetryName[] {
  return enumDecl.getMembers().map((m) => {
    const value = m.getValue();
    if (typeof value !== 'string') {
      throw new Error(
        `readTelemetry: ${enumDecl.getName()}.${m.getName()} is not a string-valued enum member`,
      );
    }
    return {
      id: m.getName(),
      value,
      doc: m.getJsDocs()[0]?.getDescription().trim() ?? '',
    };
  });
}

/** Read the telemetry registry. The project must include `types/telemetry/registry.ts`. */
export function readTelemetry(project: Project): TelemetryData {
  const sf = project
    .getSourceFiles()
    .find((f) => f.getFilePath().endsWith('/telemetry/registry.ts'));
  if (!sf) {
    throw new Error('readTelemetry: could not locate types/telemetry/registry.ts in project');
  }
  const getEnum = (name: string): EnumDeclaration => {
    const decl = sf.getEnum(name);
    if (!decl) throw new Error(`readTelemetry: enum ${name} not found in registry.ts`);
    return decl;
  };

  // TELEMETRY_SOURCE — a top-level const; getJsDocs() reads the statement-level doc.
  const sourceDecl = sf.getVariableDeclaration('TELEMETRY_SOURCE');
  if (!sourceDecl) throw new Error('readTelemetry: TELEMETRY_SOURCE not found');
  const sourceValue = sourceDecl.getInitializerOrThrow().asKind(SyntaxKind.StringLiteral)?.getLiteralValue();
  if (sourceValue === undefined) throw new Error('readTelemetry: TELEMETRY_SOURCE is not a string literal');
  // The first declaration in the file inherits the module-level JSDoc as its
  // first leading doc; the const's OWN doc is the last one immediately above it.
  const sourceJsDocs = sourceDecl.getVariableStatementOrThrow().getJsDocs();
  const sourceDoc = sourceJsDocs.at(-1)?.getDescription().trim() ?? '';

  // TELEMETRY_METRIC_UNITS — Record<TelemetryMetric, string>; map metric VALUE -> unit.
  const unitsDecl = sf.getVariableDeclarationOrThrow('TELEMETRY_METRIC_UNITS');
  const unitsObj = unitsDecl.getInitializerIfKindOrThrow(SyntaxKind.ObjectLiteralExpression);
  const unitByMetricValue = new Map<string, string>();
  for (const prop of unitsObj.getProperties()) {
    const pa = prop.asKind(SyntaxKind.PropertyAssignment);
    if (!pa) continue;
    // key is a computed [TelemetryMetric.X] -> resolve to the enum member's value
    const nameNode = pa.getNameNode();
    const computed = nameNode.asKind(SyntaxKind.ComputedPropertyName);
    const access = computed?.getExpression().asKind(SyntaxKind.PropertyAccessExpression);
    const memberId = access?.getName();
    const metricValue = memberId
      ? getEnum('TelemetryMetric').getMemberOrThrow(memberId).getValue()
      : undefined;
    const unit = pa.getInitializerOrThrow().asKind(SyntaxKind.StringLiteral)?.getLiteralValue();
    if (typeof metricValue === 'string' && unit !== undefined) {
      unitByMetricValue.set(metricValue, unit);
    }
  }

  const metrics: TelemetryMetricName[] = members(getEnum('TelemetryMetric')).map((m) => {
    const unit = unitByMetricValue.get(m.value);
    if (unit === undefined) throw new Error(`readTelemetry: no unit for metric ${m.value}`);
    return { ...m, unit };
  });

  const values: TelemetryValueGroup[] = VALUE_ENUMS.map((enumName) => ({
    group: enumName.replace(/^Telemetry/, ''),
    members: members(getEnum(enumName)),
  }));

  return {
    source: { value: sourceValue, doc: sourceDoc },
    spans: members(getEnum('TelemetrySpan')),
    metrics,
    attributes: members(getEnum('TelemetryAttribute')),
    values,
  };
}
