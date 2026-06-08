#!/usr/bin/env node
// AHP Conformance Discovery — citation grounding verifier (Phase D0).
//
// Anti-fabrication gate. Companion to validate-inventory.mjs: that one checks
// row SHAPE; this one checks that each row's citation actually GROUNDS in a real
// file in the fork. citation.file must exist and citation.excerpt must really
// appear at/around citation.line. A fabricated path, guessed line, or invented
// excerpt fails here — which is the whole point for a conformance suite.
//
// Matching is whitespace-normalized (newlines/tabs/space-runs -> one space) so
// excerpts that span lines or were copied with reflowed whitespace still match.
// For line != null we search a +/-3 line window; for line == null, the whole
// file. Real execution: reads the REAL fork files; verdict is derived from real
// substring grounding, no theater.
//
// Usage: node scripts/verify-citations.mjs out/d1-schema-surface.jsonl [...]

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
// conformance/discovery/scripts -> fork root is three levels up.
const FORK_ROOT = resolve(__dirname, '..', '..', '..');
const WINDOW = 3;

const norm = (s) => s.replace(/\s+/g, ' ').trim();

const files = process.argv.slice(2);
if (files.length === 0) {
  console.error('usage: node scripts/verify-citations.mjs <out/*.jsonl ...>');
  process.exit(2);
}

const fileCache = new Map();
function readFork(rel) {
  if (fileCache.has(rel)) return fileCache.get(rel);
  let content = null;
  try {
    content = readFileSync(resolve(FORK_ROOT, rel), 'utf8');
  } catch {
    content = null;
  }
  fileCache.set(rel, content);
  return content;
}

let rows = 0;
let checked = 0;
let ungrounded = 0;

for (const f of files) {
  let text;
  try {
    text = readFileSync(f, 'utf8');
  } catch (e) {
    console.error(`READ-ERROR ${f}: ${e.message}`);
    ungrounded++;
    continue;
  }
  const lines = text.split('\n');
  for (let i = 0; i < lines.length; i++) {
    const raw = lines[i].trim();
    if (raw === '') continue;
    rows++;
    let row;
    try {
      row = JSON.parse(raw);
    } catch {
      continue; // shape gate (validate-inventory) reports JSON errors
    }
    const c = row.citation;
    if (!c || typeof c.file !== 'string' || typeof c.excerpt !== 'string') continue;
    checked++;
    const content = readFork(c.file);
    if (content === null) {
      console.error(`${f}:${i + 1}: citation.file not found in fork: ${c.file}`);
      ungrounded++;
      continue;
    }
    const needle = norm(c.excerpt);
    if (needle === '') continue;
    let hay;
    if (c.line == null) {
      hay = norm(content);
    } else {
      const fl = content.split('\n');
      const lo = Math.max(0, c.line - 1 - WINDOW);
      const hi = Math.min(fl.length, c.line - 1 + WINDOW + 1);
      hay = norm(fl.slice(lo, hi).join(' '));
    }
    if (!hay.includes(needle)) {
      console.error(
        `${f}:${i + 1}: excerpt NOT FOUND at ${c.file}:${c.line ?? '(whole-file)'} — "${c.excerpt.slice(0, 70)}${c.excerpt.length > 70 ? '…' : ''}"`,
      );
      ungrounded++;
    }
  }
}

if (ungrounded > 0) {
  console.error(`\nFAIL — ${ungrounded} ungrounded citation(s) of ${checked} checked (${rows} rows).`);
  process.exit(1);
}
console.log(`GROUNDED — ${checked} citation(s) verified against the fork (${rows} rows).`);
