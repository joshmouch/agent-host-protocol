#!/usr/bin/env node
// AHP Conformance Discovery — inventory-row validator (Phase D0).
//
// Dependency-free by design: the discovery fan-out (D1-D11) must be able to run
// `node scripts/validate-inventory.mjs out/<phase>.jsonl` without an npm install.
// It implements exactly the subset of JSON Schema 2020-12 that
// schema/inventory-row.schema.json uses: type (incl. union + null), enum,
// required, properties (recursive), additionalProperties:false, pattern,
// minLength.
//
// REAL EXECUTION (no theater): it reads the REAL schema file and the REAL input
// files from disk; the PASS/FAIL verdict is derived from real structural
// validation. A malformed row — missing required field, out-of-enum value,
// wrong type, pattern miss, unknown property, or a duplicate behavior-id within
// one angle's file — makes it print a per-line report and exit non-zero.
//
// Usage:
//   node scripts/validate-inventory.mjs out/d1-schema-surface.jsonl [more.jsonl ...]
//   node scripts/validate-inventory.mjs out/*.jsonl

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SCHEMA_PATH = resolve(__dirname, '..', 'schema', 'inventory-row.schema.json');

function typeOf(v) {
  if (v === null) return 'null';
  if (Array.isArray(v)) return 'array';
  if (typeof v === 'number') return Number.isInteger(v) ? 'integer' : 'number';
  return typeof v; // string | boolean | object
}

function matchesType(value, type) {
  const actual = typeOf(value);
  const types = Array.isArray(type) ? type : [type];
  return types.some((t) => {
    if (t === 'integer') return actual === 'integer';
    if (t === 'number') return actual === 'integer' || actual === 'number';
    return actual === t;
  });
}

// Returns an array of human-readable error strings (empty array = valid).
function validate(value, schema, path = '') {
  const errs = [];
  const at = path || '(root)';

  if (schema.enum) {
    if (!schema.enum.some((e) => e === value)) {
      errs.push(`${at}: value ${JSON.stringify(value)} not in enum [${schema.enum.join(', ')}]`);
    }
    return errs; // enum is terminal in our schema
  }

  if (schema.type && !matchesType(value, schema.type)) {
    errs.push(`${at}: expected type ${JSON.stringify(schema.type)} but got ${typeOf(value)}`);
    return errs; // type mismatch: deeper checks are meaningless
  }

  if (typeof value === 'string') {
    if (schema.minLength != null && value.length < schema.minLength) {
      errs.push(`${at}: string shorter than minLength ${schema.minLength}`);
    }
    if (schema.pattern && !new RegExp(schema.pattern).test(value)) {
      errs.push(`${at}: ${JSON.stringify(value)} does not match pattern ${schema.pattern}`);
    }
  }

  if (typeOf(value) === 'object' && schema.properties) {
    for (const req of schema.required ?? []) {
      if (!(req in value)) errs.push(`${at}: missing required property '${req}'`);
    }
    if (schema.additionalProperties === false) {
      for (const k of Object.keys(value)) {
        if (!(k in schema.properties)) {
          errs.push(`${at}: unknown property '${k}' (additionalProperties:false)`);
        }
      }
    }
    for (const [k, sub] of Object.entries(schema.properties)) {
      if (k in value) errs.push(...validate(value[k], sub, path ? `${path}.${k}` : k));
    }
  }

  return errs;
}

const files = process.argv.slice(2);
if (files.length === 0) {
  console.error('usage: node scripts/validate-inventory.mjs <out/*.jsonl ...>');
  process.exit(2);
}

let schema;
try {
  schema = JSON.parse(readFileSync(SCHEMA_PATH, 'utf8'));
} catch (e) {
  console.error(`FATAL: cannot load schema at ${SCHEMA_PATH}: ${e.message}`);
  process.exit(2);
}

let totalRows = 0;
let totalErrors = 0;
const seenIds = new Map(); // `${file}::${behavior-id}` -> first line number

for (const file of files) {
  let text;
  try {
    text = readFileSync(file, 'utf8');
  } catch (e) {
    console.error(`READ-ERROR ${file}: ${e.message}`);
    totalErrors++;
    continue;
  }
  const lines = text.split('\n');
  for (let i = 0; i < lines.length; i++) {
    const raw = lines[i].trim();
    if (raw === '') continue; // tolerate blank lines
    totalRows++;
    let row;
    try {
      row = JSON.parse(raw);
    } catch (e) {
      console.error(`${file}:${i + 1}: JSON parse error: ${e.message}`);
      totalErrors++;
      continue;
    }
    const errs = validate(row, schema);
    if (errs.length) {
      totalErrors += errs.length;
      for (const er of errs) console.error(`${file}:${i + 1}: ${er}`);
    }
    // D11 reconciles ACROSS sources by union; WITHIN one angle's file each
    // behavior-id must be unique (a repeated id in the same file is a bug).
    const id = row['behavior-id'];
    if (typeof id === 'string') {
      const key = `${file}::${id}`;
      if (seenIds.has(key)) {
        console.error(`${file}:${i + 1}: duplicate behavior-id '${id}' (first at line ${seenIds.get(key)})`);
        totalErrors++;
      } else {
        seenIds.set(key, i + 1);
      }
    }
  }
}

if (totalErrors > 0) {
  console.error(`\nFAIL — ${totalErrors} error(s) across ${totalRows} row(s) in ${files.length} file(s).`);
  process.exit(1);
}
console.log(`PASS — ${totalRows} row(s) valid across ${files.length} file(s).`);
