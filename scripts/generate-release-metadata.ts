/**
 * Release Metadata Generator — Writes `clients/<lang>/release-metadata.json`
 * for each client, capturing two independent pieces of information:
 *
 *   1. `packageVersion` — the version the next release of this client would
 *      ship under, read from the client's own native manifest (Cargo.toml,
 *      gradle.properties, package.json, or the Swift `VERSION` file).
 *   2. `supportedProtocolVersions` — every protocol version the client is
 *      willing to negotiate, read from `types/version/registry.ts`.
 *
 * The file is generated and committed. CI re-runs the generator and fails
 * on diff (`scripts/verify-release-metadata.ts`) so consumers can rely on
 * the committed file matching what's actually in the source tree.
 *
 * NOTE: `packageVersion` describes the package version of the **checked-in
 * source**, not necessarily the most recent published artifact. For
 * example, if `Cargo.toml` says `0.1.0` but `SUPPORTED_PROTOCOL_VERSIONS`
 * includes a newer protocol version than the published 0.1.0 supported,
 * the metadata reflects the in-tree drift — the next release would ship
 * as 0.1.0 (or whatever bump the maintainers pick) and advertise the new
 * supported protocol versions.
 */

import fs from 'fs';
import path from 'path';
import { Project } from 'ts-morph';

import { readProtocolVersions } from './read-protocol-versions.js';

/**
 * A `release-metadata.json` payload. The schema is intentionally tiny;
 * adding fields is fine, removing or renaming them is a breaking change
 * for the verify script and the publish workflows.
 */
export interface ReleaseMetadata {
  /** Identifier of the per-language artifact, e.g. `"rust"`, `"kotlin"`. */
  readonly client: 'rust' | 'kotlin' | 'swift' | 'typescript' | 'go' | 'dotnet';
  /** Native package version of the checked-in source. */
  readonly packageVersion: string;
  /**
   * Protocol versions this source can negotiate, most-preferred-first.
   * Mirrors `SUPPORTED_PROTOCOL_VERSIONS` from `types/version/registry.ts`.
   */
  readonly supportedProtocolVersions: readonly string[];
}

/** Reads the workspace version from a Cargo.toml file. */
export function readRustPackageVersion(cargoToml: string): string {
  // The workspace `version` lives under `[workspace.package]`. A regex
  // scoped to the right section avoids picking up `version =` from
  // `[dependencies]` tables further down the file.
  const sectionMatch = cargoToml.match(
    /\[workspace\.package\][\s\S]*?(?=\n\[|\n*$)/,
  );
  if (!sectionMatch) {
    throw new Error('readRustPackageVersion: [workspace.package] section not found');
  }
  const m = sectionMatch[0].match(/^version\s*=\s*"([^"]+)"\s*$/m);
  if (!m) {
    throw new Error('readRustPackageVersion: version key not found in [workspace.package]');
  }
  return m[1];
}

/** Reads `VERSION_NAME` from a Gradle properties file. */
export function readKotlinPackageVersion(gradleProperties: string): string {
  const m = gradleProperties.match(/^\s*VERSION_NAME\s*=\s*(.+?)\s*$/m);
  if (!m) {
    throw new Error('readKotlinPackageVersion: VERSION_NAME not found');
  }
  return m[1];
}

/** Reads `version` from a package.json file. */
export function readTypeScriptPackageVersion(packageJson: string): string {
  const parsed = JSON.parse(packageJson) as { version?: unknown };
  if (typeof parsed.version !== 'string' || parsed.version.length === 0) {
    throw new Error('readTypeScriptPackageVersion: version not a non-empty string');
  }
  return parsed.version;
}

/** Reads the bare-semver Swift package version from the VERSION file. */
export function readSwiftPackageVersion(versionFile: string): string {
  const trimmed = versionFile.trim();
  if (trimmed.length === 0) {
    throw new Error('readSwiftPackageVersion: VERSION file is empty');
  }
  return trimmed;
}

/** Reads the bare-semver Go module version from the VERSION file. */
export function readGoPackageVersion(versionFile: string): string {
  const trimmed = versionFile.trim();
  if (trimmed.length === 0) {
    throw new Error('readGoPackageVersion: VERSION file is empty');
  }
  return trimmed;
}

/** Reads the bare-semver .NET package version from the VERSION file. */
export function readDotnetPackageVersion(versionFile: string): string {
  const trimmed = versionFile.trim();
  if (trimmed.length === 0) {
    throw new Error('readDotnetPackageVersion: VERSION file is empty');
  }
  return trimmed;
}

const CLIENTS = ['rust', 'kotlin', 'swift', 'typescript', 'go', 'dotnet'] as const;

interface ClientLocation {
  readonly metadataPath: string;
  readonly readVersion: (rootDir: string) => string;
}

function clientLocations(rootDir: string): Record<(typeof CLIENTS)[number], ClientLocation> {
  return {
    rust: {
      metadataPath: path.join(rootDir, 'clients', 'rust', 'release-metadata.json'),
      readVersion: (root) =>
        readRustPackageVersion(
          fs.readFileSync(path.join(root, 'clients', 'rust', 'Cargo.toml'), 'utf-8'),
        ),
    },
    kotlin: {
      metadataPath: path.join(rootDir, 'clients', 'kotlin', 'release-metadata.json'),
      readVersion: (root) =>
        readKotlinPackageVersion(
          fs.readFileSync(
            path.join(root, 'clients', 'kotlin', 'gradle.properties'),
            'utf-8',
          ),
        ),
    },
    swift: {
      metadataPath: path.join(rootDir, 'clients', 'swift', 'release-metadata.json'),
      readVersion: (root) =>
        readSwiftPackageVersion(
          fs.readFileSync(path.join(root, 'clients', 'swift', 'VERSION'), 'utf-8'),
        ),
    },
    typescript: {
      metadataPath: path.join(rootDir, 'clients', 'typescript', 'release-metadata.json'),
      readVersion: (root) =>
        readTypeScriptPackageVersion(
          fs.readFileSync(
            path.join(root, 'clients', 'typescript', 'package.json'),
            'utf-8',
          ),
        ),
    },
    go: {
      metadataPath: path.join(rootDir, 'clients', 'go', 'release-metadata.json'),
      readVersion: (root) =>
        readGoPackageVersion(
          fs.readFileSync(path.join(root, 'clients', 'go', 'VERSION'), 'utf-8'),
        ),
    },
    dotnet: {
      metadataPath: path.join(rootDir, 'clients', 'dotnet', 'release-metadata.json'),
      readVersion: (root) =>
        readDotnetPackageVersion(
          fs.readFileSync(path.join(root, 'clients', 'dotnet', 'VERSION'), 'utf-8'),
        ),
    },
  };
}

/**
 * Compute the expected metadata payload for one client. Pure — does not
 * touch disk for output; callers decide whether to write or just verify.
 */
export function computeReleaseMetadata(
  client: (typeof CLIENTS)[number],
  rootDir: string,
  supportedProtocolVersions: readonly string[],
): ReleaseMetadata {
  const loc = clientLocations(rootDir)[client];
  return {
    client,
    packageVersion: loc.readVersion(rootDir),
    supportedProtocolVersions: [...supportedProtocolVersions],
  };
}

/** Serializes a metadata object to the canonical on-disk format. */
export function serializeReleaseMetadata(meta: ReleaseMetadata): string {
  // Two-space indent + trailing newline matches the project's other JSON
  // artifacts (schema/*.json, marketplace.json) so editors that auto-
  // format on save don't produce spurious diffs.
  return `${JSON.stringify(meta, null, 2)}\n`;
}

/**
 * Generate `release-metadata.json` for every client. `rootDir` is the
 * absolute repository root.
 */
export function generateReleaseMetadata(project: Project, rootDir: string): void {
  const { supported } = readProtocolVersions(project);
  for (const client of CLIENTS) {
    const meta = computeReleaseMetadata(client, rootDir, supported);
    const loc = clientLocations(rootDir)[client];
    fs.writeFileSync(loc.metadataPath, serializeReleaseMetadata(meta));
  }
}

/** Names of all clients that emit a release-metadata.json file. */
export const RELEASE_METADATA_CLIENTS: readonly (typeof CLIENTS)[number][] = CLIENTS;

/** Returns the absolute path of `clients/<client>/release-metadata.json`. */
export function releaseMetadataPath(
  client: (typeof CLIENTS)[number],
  rootDir: string,
): string {
  return clientLocations(rootDir)[client].metadataPath;
}
