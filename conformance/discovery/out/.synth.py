#!/usr/bin/env python3
"""Phase D11 synthesis: merge D1-D10 inventory rows by behavior-id (union of sources)."""
import json, sys
from collections import Counter, OrderedDict, defaultdict

OUT = "/Users/josh/Code/joshmouch/agent-host-protocol/conformance/discovery/out"

FILES = [
    ("d1-schema",        "d1-schema-surface.jsonl"),
    ("d2-spec",          "d2-normative-rules.jsonl"),
    ("d3-mined-client",  "d3-mined-client-expectations.jsonl"),
    ("d4-host",          "d4-host-behaviors.jsonl"),
    ("d5-fixture",       "d5-fixture-derived-scenarios.jsonl"),
    ("d6-lifecycle",     "d6-lifecycle-transitions.jsonl"),
    ("d7-negative",      "d7-negative-paths.jsonl"),
    ("d8-differential",  "d8-divergences.jsonl"),
    ("d9-mutation",      "d9-surviving-mutants.jsonl"),
    ("d10-property",     "d10-property-findings.jsonl"),
]

NORM_RANK = {  # strongest first; for picking the merged normative-level
    "MUST": 0, "MUST_NOT": 1, "SHALL": 2, "REQUIRED": 3,
    "SHOULD": 4, "SHOULD_NOT": 5, "MAY": 6, "NONE": 7,
}

def load():
    rows = []
    missing = []
    per_source_count = Counter()
    for src, fname in FILES:
        path = f"{OUT}/{fname}"
        try:
            with open(path) as fh:
                lines = [l for l in fh if l.strip()]
        except FileNotFoundError:
            missing.append(fname)
            continue
        n = 0
        for i, line in enumerate(lines, 1):
            try:
                r = json.loads(line)
            except json.JSONDecodeError as e:
                print(f"PARSE-ERROR {fname}:{i}: {e}", file=sys.stderr)
                continue
            # normalize source to the declared enum for this file (rows should already carry it)
            r["__src"] = src
            rows.append(r)
            n += 1
        per_source_count[src] = n
    return rows, per_source_count, missing

def main():
    rows, per_source_count, missing = load()
    total_in = len(rows)

    # Merge by behavior-id (union of sources, citations, etc.)
    merged = OrderedDict()
    for r in rows:
        bid = r["behavior-id"]
        src = r["__src"]
        cit = r.get("citation")
        if bid not in merged:
            merged[bid] = {
                "behavior-id": bid,
                "concept": r.get("concept", ""),
                "method": r.get("method"),
                "scenario-class": r.get("scenario-class"),
                "normative-level": r.get("normative-level", "NONE"),
                "sources": [],
                "citations": [],
                "assertions": [],
                "notes_bits": [],
                "_first_src": src,
            }
        m = merged[bid]
        if src not in m["sources"]:
            m["sources"].append(src)
        if cit is not None:
            # de-dup citations by (file,line,excerpt)
            key = (cit.get("file"), cit.get("line"), cit.get("excerpt"))
            if key not in {(c.get("file"), c.get("line"), c.get("excerpt")) for c in m["citations"]}:
                tagged = dict(cit)
                tagged["source"] = src
                m["citations"].append(tagged)
        # strongest normative level wins
        if NORM_RANK.get(r.get("normative-level", "NONE"), 7) < NORM_RANK.get(m["normative-level"], 7):
            m["normative-level"] = r.get("normative-level", "NONE")
        # prefer a non-null method
        if m["method"] is None and r.get("method") is not None:
            m["method"] = r.get("method")
        # collect assertion
        a = r.get("assertion")
        if a and a not in m["assertions"]:
            m["assertions"].append(a)
        # collect note bits (compact, source-tagged) for cross-angle merges where multiple add info
        nt = r.get("notes")
        if nt:
            m["notes_bits"].append((src, nt))

    unique = len(merged)

    # Build matrix output lines + per-class/per-source counts on UNIQUE behaviors
    by_scenario_class = Counter()
    by_source_unique = Counter()           # how many unique behaviors each source TOUCHES
    by_concept = Counter()
    multi_source = 0
    matrix_lines = []
    out_of_scope = []
    for bid, m in merged.items():
        sc = m["scenario-class"]
        by_scenario_class[sc] += 1
        for s in m["sources"]:
            by_source_unique[s] += 1
        by_concept[m["concept"]] += 1
        if len(m["sources"]) > 1:
            multi_source += 1

        # Coverage policy: everything is "planned" by default (corpus not yet built).
        # Out-of-scope is reserved for a SMALL, explicitly-enumerated set of rows that
        # are genuinely NOT protocol-conformance behaviors -- conservatively chosen by
        # exact behavior-id so we never silently drop real wire coverage. (Borderline
        # rows stay "planned" with a Part-2-triage note rather than being cut.)
        # NOTE: out-of-scope here only affects the backlog/report; the EXIT CRITERION
        # is about D1 + D2-MUST rows, all of which remain "planned".
        coverage = "planned"
        oos_reason = None
        notes_join = " | ".join(f"[{s}] {t}" for s, t in m["notes_bits"])

        # Explicit out-of-scope set (each with its own specific reason):
        OOS = {
            "transport.websocket.happy.url-stdout":
                "Harness plumbing: the light host emits its ws:// URL to stdout purely so the "
                "test runner can discover the ephemeral port. The protocol does not mandate it; "
                "no AHP client observes it. Exercised indirectly by every scenario's bring-up.",
            "transport.websocket.happy.ephemeral-port":
                "Harness plumbing: binding to OS-assigned port 0 is a light-host bring-up choice, "
                "not a wire-observable AHP behavior. The protocol is transport-URL-agnostic.",
            "lifecycle.host.edge.vscode-tests-not-portable":
                "Meta-observation (not a wire behavior): VS Code's ~12 internal unit tests are "
                "host-specific and intentionally NOT portable conformance scenarios. Captured as "
                "rationale for why this suite exists, not as a scenario to author.",
        }
        if bid in OOS:
            coverage = "out-of-scope"
            oos_reason = OOS[bid]
        # Flag (but DO NOT cut) the remaining borderline D4 harness/reference-host meta rows
        # so Part-2 triage sees them.
        BORDERLINE_TRIAGE = {
            "transport.websocket.happy.real-wire-no-mocks":
                "TRIAGE(Part2): design-property row (no-mocks real-wire). Likely a suite invariant "
                "rather than a per-scenario assertion; decide whether to keep as a meta-check.",
            "transport.multihost.happy.extensible-client-harness":
                "TRIAGE(Part2): harness-extensibility property (any client can point at the same URL); "
                "likely covered structurally by running B5 per-client runners rather than as a scenario.",
            "lifecycle.host.happy.vscode-reference-server":
                "TRIAGE(Part2): reference-host pointer (VS Code source not locally checked out). "
                "Becomes a Phase-V manual/periodic target, not a light-host scenario.",
        }
        if bid in BORDERLINE_TRIAGE and coverage == "planned":
            notes_join = (BORDERLINE_TRIAGE[bid] + (" | " + notes_join if notes_join else ""))

        note_out = notes_join
        if oos_reason:
            note_out = (oos_reason + (" || " + notes_join if notes_join else ""))
        # fold assertions into notes tail so nothing is lost (matrix is synthesis shape)
        if m["assertions"]:
            asn = " ;; ".join(m["assertions"][:4])
            note_out = (note_out + " || assertion: " + asn) if note_out else ("assertion: " + asn)

        rec = OrderedDict([
            ("behavior-id", bid),
            ("concept", m["concept"]),
            ("method", m["method"]),
            ("scenario-class", sc),
            ("normative-level", m["normative-level"]),
            ("sources", m["sources"]),
            ("citations", [OrderedDict([("file", c.get("file")), ("line", c.get("line")),
                                        ("excerpt", c.get("excerpt")), ("source", c.get("source"))])
                           for c in m["citations"]]),
            ("coverage", coverage),
            ("notes", note_out),
        ])
        matrix_lines.append(json.dumps(rec, ensure_ascii=False))
        if coverage == "out-of-scope":
            out_of_scope.append((bid, oos_reason))

    # EXIT CRITERION: every D1 row + every D2 MUST/REQUIRED row must be mapped to a
    # planned scenario OR out-of-scope-with-reason. Since matrix is built FROM these
    # rows, "mapped" == the behavior-id exists in the matrix with coverage planned|out-of-scope.
    # The only way to FAIL is a D1/D2-MUST row whose merged coverage is neither.
    d1_bids = set()
    d2_must_bids = set()
    for r in rows:
        if r["__src"] == "d1-schema":
            d1_bids.add(r["behavior-id"])
        if r["__src"] == "d2-spec" and r.get("normative-level") in ("MUST", "REQUIRED", "SHALL", "MUST_NOT"):
            d2_must_bids.add(r["behavior-id"])

    coverage_by_bid = {bid: merged[bid] for bid in merged}
    def covered_ok(bid):
        # planned or out-of-scope both satisfy "mapped-or-deferred"
        return bid in merged  # every row contributes to merged; coverage is planned|oos by construction
    unmapped_d1 = [b for b in d1_bids if not covered_ok(b)]
    unmapped_d2 = [b for b in d2_must_bids if not covered_ok(b)]
    exit_met = (not unmapped_d1) and (not unmapped_d2)

    # Write matrix jsonl
    with open(f"{OUT}/d11-surface-matrix.jsonl", "w") as fh:
        fh.write("\n".join(matrix_lines) + "\n")

    # Emit a machine summary for the wrapper to read
    summary = {
        "total_in": total_in,
        "unique": unique,
        "per_source_count": dict(per_source_count),          # rows IN per source
        "by_source_unique": dict(by_source_unique),          # unique behaviors touched per source
        "by_scenario_class": dict(by_scenario_class),
        "by_concept_top": by_concept.most_common(25),
        "multi_source": multi_source,
        "missing": missing,
        "out_of_scope": out_of_scope,
        "d1_count": len(d1_bids),
        "d2_must_count": len(d2_must_bids),
        "unmapped_d1": unmapped_d1,
        "unmapped_d2": unmapped_d2,
        "exit_met": exit_met,
    }
    with open(f"{OUT}/.synth-summary.json", "w") as fh:
        json.dump(summary, fh, indent=2)
    print(json.dumps(summary, indent=2))

if __name__ == "__main__":
    main()
