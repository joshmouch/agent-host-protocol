#!/usr/bin/env node
// AHP Conformance — scenario-fixture validator (Part 2, build-phase B1).
//
// Dependency-free by design (same discipline as
// conformance/discovery/scripts/validate-inventory.mjs): every client runner +
// the host runner must be able to `node scripts/validate-scenarios.mjs` with no
// `npm install`. It implements exactly the JSON-Schema-2020-12 subset that
// schema/scenario.schema.json uses: type (incl. union + null), enum, const,
// required, properties (recursive), additionalProperties:false, pattern,
// minLength, items (array element schema), minItems, and `oneOf` discriminated
// by a `const` key (here: each step's `op`).
//
// REAL EXECUTION (no theater): it reads the REAL schema file and the REAL
// *.scenario.json files from disk; the PASS/FAIL verdict is derived from real
// structural validation. A malformed scenario — bad id, unknown step op, missing
// required field, wrong type, unknown property, server.response with both/neither
// result+error, or a duplicate scenario id across the corpus — prints a per-file
// report and exits non-zero.
//
// Usage:
//   node scripts/validate-scenarios.mjs                       # all **/*.scenario.json under ../
//   node scripts/validate-scenarios.mjs examples/foo.scenario.json [more ...]
//   node scripts/validate-scenarios.mjs path/to/dir           # recurse a dir

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, relative, join, sep } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SCENARIOS_ROOT = resolve(__dirname, '..');
const SCHEMA_PATH = resolve(__dirname, '..', 'schema', 'scenario.schema.json');

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

// For a step value, pick the oneOf branch whose `op` const matches, so errors
// are reported against the INTENDED op rather than as a wall of mismatches.
function pickOneOfBranch(value, branches, path) {
  const opOfValue = typeOf(value) === 'object' ? value.op : undefined;
  // Branch discriminator: properties.op.const
  for (const b of branches) {
    const c = b?.properties?.op?.const;
    if (c !== undefined && c === opOfValue) return { branch: b, err: null };
  }
  const known = branches
    .map((b) => b?.properties?.op?.const)
    .filter((c) => c !== undefined);
  if (opOfValue === undefined) {
    return { branch: null, err: `${path || '(root)'}: step is missing required 'op' (one of: ${known.join(', ')})` };
  }
  return { branch: null, err: `${path || '(root)'}: unknown step op ${JSON.stringify(opOfValue)} (expected one of: ${known.join(', ')})` };
}

// Returns an array of human-readable error strings (empty array = valid).
function validate(value, schema, path = '') {
  const errs = [];
  const at = path || '(root)';

  if (schema.const !== undefined) {
    if (value !== schema.const) {
      errs.push(`${at}: value ${JSON.stringify(value)} !== const ${JSON.stringify(schema.const)}`);
    }
    return errs; // const is terminal
  }

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

  if (typeOf(value) === 'array') {
    if (schema.minItems != null && value.length < schema.minItems) {
      errs.push(`${at}: array shorter than minItems ${schema.minItems}`);
    }
    if (schema.items) {
      for (let i = 0; i < value.length; i++) {
        errs.push(...validate(value[i], schema.items, `${at === '(root)' ? '' : path}[${i}]`));
      }
    }
  }

  if (typeOf(value) === 'object') {
    // oneOf discriminated by a `const` key (steps: by `op`).
    if (Array.isArray(schema.oneOf)) {
      const { branch, err } = pickOneOfBranch(value, schema.oneOf, path);
      if (err) {
        errs.push(err);
      } else if (branch) {
        errs.push(...validate(value, branch, path));
        errs.push(...semanticStepChecks(value, path));
      }
      // a oneOf node in OUR schema carries no sibling property constraints, so
      // we don't also descend `schema.properties` here.
    }

    if (schema.properties) {
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
    } else if (schema.required) {
      // object with required but no declared properties (e.g. server.response.error
      // uses additionalProperties:true): still enforce required.
      for (const req of schema.required) {
        if (!(req in value)) errs.push(`${at}: missing required property '${req}'`);
      }
      if (schema.properties === undefined && schema.additionalProperties !== false) {
        // still descend into known sub-schemas if any were declared
      }
    }
  }

  return errs;
}

// Semantic checks that the structural subset can't express, keyed on op.
function semanticStepChecks(step, path) {
  const errs = [];
  const at = path || '(root)';
  if (step.op === 'server.response') {
    const hasResult = 'result' in step;
    const hasError = 'error' in step;
    if (hasResult && hasError) {
      errs.push(`${at}: server.response has BOTH 'result' and 'error' (exactly one is required)`);
    } else if (!hasResult && !hasError) {
      errs.push(`${at}: server.response has NEITHER 'result' nor 'error' (exactly one is required)`);
    }
  }
  return errs;
}

// --- file discovery -------------------------------------------------------

function isScenarioFile(p) {
  return p.endsWith('.scenario.json');
}

function walkDir(dir, acc) {
  for (const name of readdirSync(dir)) {
    const full = join(dir, name);
    let st;
    try {
      st = statSync(full);
    } catch {
      continue;
    }
    if (st.isDirectory()) {
      // skip node_modules / dotdirs for hygiene
      if (name === 'node_modules' || name.startsWith('.')) continue;
      walkDir(full, acc);
    } else if (isScenarioFile(full)) {
      acc.push(full);
    }
  }
}

function resolveInputs(argv) {
  const out = [];
  if (argv.length === 0) {
    walkDir(SCENARIOS_ROOT, out);
    out.sort();
    return out;
  }
  for (const arg of argv) {
    const abs = resolve(process.cwd(), arg);
    let st;
    try {
      st = statSync(abs);
    } catch {
      out.push(abs); // let the read step report the missing file
      continue;
    }
    if (st.isDirectory()) {
      walkDir(abs, out);
    } else {
      out.push(abs);
    }
  }
  out.sort();
  return out;
}

// --- main -----------------------------------------------------------------

let schema;
try {
  schema = JSON.parse(readFileSync(SCHEMA_PATH, 'utf8'));
} catch (e) {
  console.error(`FATAL: cannot load schema at ${SCHEMA_PATH}: ${e.message}`);
  process.exit(2);
}

const files = resolveInputs(process.argv.slice(2));
if (files.length === 0) {
  console.error(`No *.scenario.json files found under ${SCENARIOS_ROOT}`);
  process.exit(2);
}

let totalFiles = 0;
let totalErrors = 0;
const seenIds = new Map(); // scenario id -> first file that used it

for (const file of files) {
  totalFiles++;
  const rel = relative(SCENARIOS_ROOT, file) || file;
  let text;
  try {
    text = readFileSync(file, 'utf8');
  } catch (e) {
    console.error(`READ-ERROR ${rel}: ${e.message}`);
    totalErrors++;
    continue;
  }
  let scenario;
  try {
    scenario = JSON.parse(text);
  } catch (e) {
    console.error(`${rel}: JSON parse error: ${e.message}`);
    totalErrors++;
    continue;
  }
  const errs = validate(scenario, schema);
  if (errs.length) {
    totalErrors += errs.length;
    for (const er of errs) console.error(`${rel}: ${er}`);
  }
  // Cross-file: scenario id must be unique across the whole corpus (the id is
  // the merge key; two files claiming the same id is a bug).
  const id = scenario && typeof scenario.id === 'string' ? scenario.id : undefined;
  if (id !== undefined) {
    if (seenIds.has(id)) {
      console.error(`${rel}: duplicate scenario id '${id}' (first used by ${seenIds.get(id)})`);
      totalErrors++;
    } else {
      seenIds.set(id, rel);
    }
  }
  // Filename ⇄ id discoverability: <id>.scenario.json (kebab id stays the basename).
  if (id !== undefined) {
    const base = file.split(sep).pop().replace(/\.scenario\.json$/, '');
    if (base !== id) {
      console.error(`${rel}: filename basename '${base}' must equal scenario id '${id}' (file should be '${id}.scenario.json')`);
      totalErrors++;
    }
  }
}

if (totalErrors > 0) {
  console.error(`\nFAIL — ${totalErrors} error(s) across ${totalFiles} scenario file(s).`);
  process.exit(1);
}
console.log(`PASS — ${totalFiles} scenario file(s) valid (${seenIds.size} unique id(s)).`);
