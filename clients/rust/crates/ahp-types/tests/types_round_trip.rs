//! TypesRoundTripFixtureTests — data-driven wire round-trip parity for Rust.
//!
//! Loads the SHARED, language-agnostic round-trip corpus under
//! `types/test-cases/round-trips/*.json` (the same fixtures the .NET client runs
//! via `clients/dotnet/tests/.../TypesRoundTripFixtures.cs` and the Swift client
//! runs via `clients/swift/.../TypesRoundTripFixtureTests.swift`) and asserts
//! each via REAL serde decode/encode of the corresponding generated wire type.
//!
//! This mirrors the existing reducer fixture runner in
//! `crates/ahp/src/reducers.rs::json_fixture_tests` (same `CARGO_MANIFEST_DIR`
//! -> repo-root navigation, same fail-loud assertion style) and the Swift
//! round-trip harness (same neutral-discriminator vocabulary).
//!
//! The corpus carries language-neutral discriminators:
//!
//! * `expect` — dotted JSON paths checked against the RE-ENCODED wire.
//! * `expectVariant` — { accessor: ConcreteTypeName }; "" means the whole
//!   decoded union's active case maps to that canonical type name. Here we map
//!   each canonical type name to the Rust enum variant with the same payload.
//! * `expectJsonRpcVariant` request|notification|success|error -> JsonRpcMessage
//!   variants Request / Notification / SuccessResponse / ErrorResponse.
//! * `expectBitset` — SessionStatus flag membership + numeric value.
//! * `expectNumberAbove` — a re-encoded numeric field exceeds a bound (64-bit).
//! * `expectReencodedAbsent` — keys that must NOT appear in the re-encoded wire.
//! * `reencodes` — re-encode is structurally exact with the input bytes.
//! * `roundTripStable` — decode->encode->decode->encode is a fixed point (and
//!   any `expect` paths still hold on the 2nd pass).
//! * `expectConstant` — ProtocolVersion constants (no wire decode).
//!
//! Run: `cargo test -p ahp-types --test types_round_trip`
//!
//! Real-execution: NO mocks. Every fixture decodes with `serde_json` and the
//! real generated types and re-encodes with `serde_json`, then asserts the
//! fixture's expectations against the decoded value and the re-encoded bytes.

use serde_json::Value;
use std::collections::BTreeSet;
use std::path::{Path, PathBuf};

use ahp_types::actions::{ActionEnvelope, StateAction};
use ahp_types::commands::ChangesetOperationTarget;
use ahp_types::common::StringOrMarkdown;
use ahp_types::messages::JsonRpcMessage;
use ahp_types::notifications::{PartialSessionSummary, SessionAddedParams};
use ahp_types::state::{Customization, SessionInputQuestion, SessionStatus, SessionSummary};
use ahp_types::{PROTOCOL_VERSION, SUPPORTED_PROTOCOL_VERSIONS};

// ─── Known representational gaps (documented, not silent) ───────────────────
//
// Corpus fixtures that the current Rust generated types cannot represent. These
// are REAL type gaps, not test shortcuts — each is reported out of the suite
// (printed) and listed here with the precise reason. The runner asserts that
// the set of fixtures that actually fail-to-represent equals THIS set, so a
// future Rust type change that closes a gap (or opens a new one) fails loudly
// and forces this list to be updated.
//
// 019 channel-scoped-notification-uri:
//     KNOWN-BROKEN fixture (schema-invalid): the wire payload is
//     `{ channel, session }` with NO `summary`, but `schema/notifications.schema.json`
//     declares `summary` REQUIRED on SessionAddedParams. Rust models
//     `SessionAddedParams.summary` as a non-optional `SessionSummary` (the
//     spec-faithful modeling — same as Swift), so decode fails with a missing-
//     `summary` error. This is NOT a Rust bug; it is the corpus fixture being
//     schema-invalid, and is being repaired separately. Left as a known gap.
//
// FIXED (no longer gaps; promoted to real assertions):
//
// 004 session-status-bitset-flags / 005 session-status-unknown-bits-preserved:
//     SessionStatus is a numeric BITSET ("Use bitwise checks"). It was
//     previously generated as a closed `#[repr(u32)]` enum (Idle=1, Error=2,
//     InProgress=8, InputNeeded=24, IsRead=32, IsArchived=64), which can only
//     hold a DECLARED discriminant value — so the wire `72` (InProgress|IsArchived)
//     and `2147483720` (…|bit31) were both rejected by `Deserialize_repr`, and
//     unknown forward-compat bits could never round-trip. FIXED at the codegen
//     level (`scripts/generate-rust.ts::generateRustBitset` + `BITSET_ENUMS`):
//     `SessionStatus` is now a `#[serde(transparent)] pub struct SessionStatus(pub u32)`
//     bitset newtype with associated flag consts + `contains()`/`bits()`/bitwise
//     ops. Any `u32` value now decodes, exposes its set bits, and re-encodes
//     verbatim (forward-compat bits included). Fixtures 004/005 now run real
//     `expectBitset` assertions.
// 019-channel-scoped-notification-uri was a known gap (schema-invalid fixture),
// but the corpus fixture was repaired and now passes — gap retired.
const KNOWN_REPRESENTATIONAL_GAPS: &[&str] = &[];

// ─── Fixture directory ──────────────────────────────────────────────────────

fn fixture_dir() -> PathBuf {
    // This crate: clients/rust/crates/ahp-types
    // Walk up to the repo root, then into types/test-cases/round-trips.
    // (Same shape as crates/ahp/src/reducers.rs::json_fixture_tests.)
    let manifest = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    manifest
        .parent() // crates/
        .unwrap()
        .parent() // rust/
        .unwrap()
        .parent() // clients/
        .unwrap()
        .parent() // repo root
        .unwrap()
        .join("types")
        .join("test-cases")
        .join("round-trips")
}

fn fixture_files() -> Vec<PathBuf> {
    let dir = fixture_dir();
    assert!(
        dir.is_dir(),
        "Round-trip fixture directory not found: {}. Ensure the checkout includes types/test-cases/round-trips/.",
        dir.display()
    );
    let mut files: Vec<PathBuf> = std::fs::read_dir(&dir)
        .expect("failed to read round-trip fixture dir")
        .filter_map(|e| e.ok())
        .map(|e| e.path())
        .filter(|p| p.extension().map(|x| x == "json").unwrap_or(false))
        .collect();
    files.sort();
    files
}

fn stem(path: &Path) -> String {
    path.file_stem().unwrap().to_string_lossy().to_string()
}

// ─── Loaded-something guard ─────────────────────────────────────────────────

#[test]
fn corpus_is_present() {
    assert!(
        !fixture_files().is_empty(),
        "No round-trip fixtures found at {}.",
        fixture_dir().display()
    );
}

// ─── Whole-corpus runner ────────────────────────────────────────────────────

#[test]
fn round_trip_corpus() {
    let files = fixture_files();
    let mut failures: Vec<String> = Vec::new();
    let mut gap_hits: BTreeSet<String> = BTreeSet::new();
    let mut ran_real_assertions = 0usize;

    let declared_gaps: BTreeSet<String> = KNOWN_REPRESENTATIONAL_GAPS
        .iter()
        .map(|s| s.to_string())
        .collect();

    for path in &files {
        let s = stem(path);
        match run_fixture(path) {
            Ok(()) => ran_real_assertions += 1,
            Err(e) => {
                if declared_gaps.contains(&s) {
                    gap_hits.insert(s.clone());
                    eprintln!("⊘ {s}: known Rust representational gap — {e}");
                } else {
                    failures.push(format!("✗ {s}: {e}"));
                }
            }
        }
    }

    // Every fixture NOT in the gap set must have run a real assertion.
    let expected_real = files.len() - declared_gaps.len();
    assert_eq!(
        ran_real_assertions, expected_real,
        "Expected {expected_real} fixtures to decode+assert for real; only {ran_real_assertions} did."
    );

    // The gap set must be exactly the fixtures that failed to represent.
    // If a gap closes, gap_hits shrinks -> mismatch -> update the list.
    // If a new fixture can't be represented, it lands in `failures` -> loud.
    assert_eq!(
        gap_hits, declared_gaps,
        "Known-gap set drifted. Hit gaps: {gap_hits:?}; declared: {declared_gaps:?}. \
         A gap that no longer reproduces must be removed from KNOWN_REPRESENTATIONAL_GAPS \
         (and ideally promoted to a real assertion)."
    );

    assert!(
        failures.is_empty(),
        "{} round-trip fixture(s) failed:\n{}",
        failures.len(),
        failures.join("\n")
    );
}

// ─── Per-fixture dispatch ───────────────────────────────────────────────────

type FixtureResult = Result<(), String>;

fn run_fixture(path: &Path) -> FixtureResult {
    let file = path.file_name().unwrap().to_string_lossy().to_string();
    let raw = std::fs::read_to_string(path).map_err(|e| format!("{file}: read error: {e}"))?;
    let root: Value =
        serde_json::from_str(&raw).map_err(|e| format!("{file}: JSON parse error: {e}"))?;

    let ty = root
        .get("type")
        .and_then(Value::as_str)
        .ok_or_else(|| format!("{file}: missing `type`"))?
        .to_string();

    // ProtocolVersion fixtures assert constants, not wire decode.
    if ty == "ProtocolVersion" {
        return verify_protocol_constant(&file, &root);
    }

    let input_json = read_input_json(&file, &root)?;
    let decoded = decode_value(&ty, &input_json, &file)?;
    let reencoded = decoded.reencode(&file)?;

    let mut asserted_something = false;

    // expect — dotted paths against the RE-ENCODED wire.
    if let Some(expect) = root.get("expect").and_then(Value::as_object) {
        let re_obj: Value = serde_json::from_str(&reencoded)
            .map_err(|e| format!("{file}: re-encoded JSON parse error: {e}"))?;
        for (k, want) in expect {
            let got = resolve_path(&re_obj, k, &file)?;
            assert_json_equals(want, got, &format!("{file}: expect[\"{k}\"]"))?;
            asserted_something = true;
        }
    }

    // expectVariant — active union case identity.
    if let Some(variants) = root.get("expectVariant").and_then(Value::as_object) {
        verify_variant(&file, &decoded, variants)?;
        asserted_something = true;
    }

    // expectJsonRpcVariant — request|notification|success|error.
    if let Some(jrpc) = root.get("expectJsonRpcVariant").and_then(Value::as_str) {
        verify_jsonrpc_variant(&file, &decoded, jrpc)?;
        asserted_something = true;
    }

    // expectBitset — SessionStatus flag membership + numeric.
    if let Some(bitset) = root.get("expectBitset").and_then(Value::as_object) {
        verify_bitset(&file, &decoded, &reencoded, bitset)?;
        asserted_something = true;
    }

    // expectNumberAbove — a re-encoded numeric field exceeds a bound.
    if let Some(above) = root.get("expectNumberAbove").and_then(Value::as_object) {
        let re_obj: Value = serde_json::from_str(&reencoded)
            .map_err(|e| format!("{file}: re-encoded JSON parse error: {e}"))?;
        for (k, bound_v) in above {
            let got = resolve_path(&re_obj, k, &file)?;
            let bound = as_i64(bound_v)
                .ok_or_else(|| format!("{file}: expectNumberAbove[\"{k}\"] bound non-numeric"))?;
            let got_n = as_i64(got)
                .ok_or_else(|| format!("{file}: expectNumberAbove[\"{k}\"] value non-numeric"))?;
            if got_n <= bound {
                return Err(format!(
                    "{file}: expectNumberAbove[\"{k}\"] — {got_n} is not > {bound}"
                ));
            }
            asserted_something = true;
        }
    }

    // expectReencodedAbsent — keys that must NOT appear in the re-encoded wire.
    if let Some(absent) = root.get("expectReencodedAbsent").and_then(Value::as_array) {
        let re_obj: Value = serde_json::from_str(&reencoded)
            .map_err(|e| format!("{file}: re-encoded JSON parse error: {e}"))?;
        let obj = re_obj.as_object().ok_or_else(|| {
            format!(
                "{file}: expectReencodedAbsent requires re-encoded JSON object, got {reencoded}"
            )
        })?;
        for key_v in absent {
            if let Some(key) = key_v.as_str() {
                if obj.contains_key(key) {
                    return Err(format!(
                        "{file}: re-encoded JSON must NOT contain key \"{key}\" but it does. Re-encoded: {reencoded}"
                    ));
                }
                asserted_something = true;
            }
        }
    }

    // reencodes — re-encode is structurally exact with the input bytes.
    if root
        .get("reencodes")
        .and_then(Value::as_bool)
        .unwrap_or(false)
    {
        assert_canonical_equal(
            &input_json,
            &reencoded,
            &format!("{file}: reencodes (structure-exact)"),
        )?;
        asserted_something = true;
    }

    // roundTripStable — decode->encode->decode->encode is a fixed point.
    if root
        .get("roundTripStable")
        .and_then(Value::as_bool)
        .unwrap_or(false)
    {
        let decoded2 = decode_value(&ty, &reencoded, &file)?;
        let reencoded2 = decoded2.reencode(&file)?;
        if let Some(expect) = root.get("expect").and_then(Value::as_object) {
            let re2_obj: Value = serde_json::from_str(&reencoded2)
                .map_err(|e| format!("{file}: 2nd re-encoded JSON parse error: {e}"))?;
            for (k, want) in expect {
                let got = resolve_path(&re2_obj, k, &file)?;
                assert_json_equals(
                    want,
                    got,
                    &format!("{file}: roundTripStable expect[\"{k}\"] (2nd decode)"),
                )?;
            }
        } else {
            assert_canonical_equal(
                &reencoded,
                &reencoded2,
                &format!("{file}: roundTripStable fixed-point"),
            )?;
        }
        asserted_something = true;
    }

    if !asserted_something {
        return Err(format!(
            "{file}: fixture made no assertions — coverage theater."
        ));
    }
    Ok(())
}

// ─── Real decode dispatch ───────────────────────────────────────────────────
//
// Mirrors the .NET / Swift DecodeAndReencode switch. Adding a wire type to the
// corpus is a deliberate edit here; the corpus never decodes arbitrary types
// reflectively. Returns a typed enum so variant assertions can inspect the
// active case, and re-encodes via the same real type on demand.

enum Decoded {
    ActionEnvelope(ActionEnvelope),
    StateAction(StateAction),
    Customization(Customization),
    SessionStatus(SessionStatus),
    StringOrMarkdown(StringOrMarkdown),
    JsonRpcMessage(JsonRpcMessage),
    ChangesetTarget(ChangesetOperationTarget),
    InputQuestion(SessionInputQuestion),
    SessionSummary(SessionSummary),
    SessionAddedParams(SessionAddedParams),
    PartialSummary(PartialSessionSummary),
}

impl Decoded {
    fn reencode(&self, file: &str) -> Result<String, String> {
        let v = match self {
            Decoded::ActionEnvelope(x) => serde_json::to_string(x),
            Decoded::StateAction(x) => serde_json::to_string(x),
            Decoded::Customization(x) => serde_json::to_string(x),
            Decoded::SessionStatus(x) => serde_json::to_string(x),
            Decoded::StringOrMarkdown(x) => serde_json::to_string(x),
            Decoded::JsonRpcMessage(x) => serde_json::to_string(x),
            Decoded::ChangesetTarget(x) => serde_json::to_string(x),
            Decoded::InputQuestion(x) => serde_json::to_string(x),
            Decoded::SessionSummary(x) => serde_json::to_string(x),
            Decoded::SessionAddedParams(x) => serde_json::to_string(x),
            Decoded::PartialSummary(x) => serde_json::to_string(x),
        };
        v.map_err(|e| format!("{file}: re-encode error: {e}"))
    }
}

fn decode_value(ty: &str, input_json: &str, file: &str) -> Result<Decoded, String> {
    macro_rules! dec {
        ($t:ty, $variant:ident) => {{
            let v: $t = serde_json::from_str(input_json)
                .map_err(|e| format!("{file}: decode {} failed: {e}", ty))?;
            Ok(Decoded::$variant(v))
        }};
    }
    match ty {
        "ActionEnvelope" => dec!(ActionEnvelope, ActionEnvelope),
        "StateAction" => dec!(StateAction, StateAction),
        "Customization" => dec!(Customization, Customization),
        "SessionStatus" => dec!(SessionStatus, SessionStatus),
        "StringOrMarkdown" => dec!(StringOrMarkdown, StringOrMarkdown),
        "JsonRpcMessage" => dec!(JsonRpcMessage, JsonRpcMessage),
        "ChangesetOperationTarget" => dec!(ChangesetOperationTarget, ChangesetTarget),
        "SessionInputQuestion" => dec!(SessionInputQuestion, InputQuestion),
        "SessionSummary" => dec!(SessionSummary, SessionSummary),
        "SessionAddedParams" => dec!(SessionAddedParams, SessionAddedParams),
        "PartialSessionSummary" => dec!(PartialSessionSummary, PartialSummary),
        other => Err(format!(
            "round-trip fixture: unknown wire type \"{other}\". Add a decode entry to decode_value."
        )),
    }
}

// ─── Variant identity (maps canonical type names -> Rust variants) ───────────

fn verify_variant(
    file: &str,
    decoded: &Decoded,
    variants: &serde_json::Map<String, Value>,
) -> FixtureResult {
    for (accessor, want_v) in variants {
        let Some(want) = want_v.as_str() else {
            continue;
        };
        if accessor.is_empty() {
            let actual = whole_variant_name(decoded);
            if actual.as_deref() != Some(want) {
                return Err(format!(
                    "{file}: expectVariant[\"\"] — active variant is {:?}, expected {want}",
                    actual
                ));
            }
        } else {
            let actual = named_accessor_variant_name(decoded, accessor, file)?;
            if actual.as_deref() != Some(want) {
                return Err(format!(
                    "{file}: expectVariant[\"{accessor}\"] — active variant is {:?}, expected {want}",
                    actual
                ));
            }
        }
    }
    Ok(())
}

fn whole_variant_name(decoded: &Decoded) -> Option<String> {
    match decoded {
        Decoded::StateAction(a) => state_action_variant_name(a),
        Decoded::Customization(c) => customization_variant_name(c),
        Decoded::ChangesetTarget(t) => changeset_target_variant_name(t),
        Decoded::InputQuestion(q) => input_question_variant_name(q),
        Decoded::StringOrMarkdown(s) => Some(
            match s {
                StringOrMarkdown::Plain(_) => "String",
                StringOrMarkdown::Markdown { .. } => "MarkdownString",
            }
            .to_string(),
        ),
        _ => None,
    }
}

fn named_accessor_variant_name(
    decoded: &Decoded,
    accessor: &str,
    file: &str,
) -> Result<Option<String>, String> {
    match (decoded, accessor.to_lowercase().as_str()) {
        (Decoded::ActionEnvelope(env), "action") => Ok(state_action_variant_name(&env.action)),
        _ => Err(format!(
            "{file}: expectVariant accessor \"{accessor}\" not wired for this decoded type"
        )),
    }
}

fn state_action_variant_name(a: &StateAction) -> Option<String> {
    match a {
        StateAction::SessionTitleChanged(_) => Some("SessionTitleChangedAction".to_string()),
        // Corpus name for the raw passthrough case.
        StateAction::Unknown(_) => Some("JsonElement".to_string()),
        // Other variants: derive a stable PascalCase name + "Action" suffix
        // from the Debug label (e.g. `SessionDelta(..)` -> "SessionDeltaAction").
        other => {
            let dbg = format!("{other:?}");
            dbg.split('(').next().map(|name| format!("{name}Action"))
        }
    }
}

fn customization_variant_name(c: &Customization) -> Option<String> {
    Some(
        match c {
            Customization::Plugin(_) => "PluginCustomization",
            Customization::Directory(_) => "DirectoryCustomization",
            Customization::Unknown(_) => "JsonElement",
        }
        .to_string(),
    )
}

fn changeset_target_variant_name(t: &ChangesetOperationTarget) -> Option<String> {
    Some(
        match t {
            ChangesetOperationTarget::Resource { .. } => "ChangesetOperationResourceTarget",
            ChangesetOperationTarget::Range { .. } => "ChangesetOperationRangeTarget",
        }
        .to_string(),
    )
}

fn input_question_variant_name(q: &SessionInputQuestion) -> Option<String> {
    Some(
        match q {
            SessionInputQuestion::Text(_) => "SessionInputTextQuestion",
            // The corpus maps BOTH `number` and `integer` kinds to the same
            // canonical concrete type (SessionInputNumberQuestion); Rust's enum
            // has two variants (Number / Integer) that both wrap
            // SessionInputNumberQuestion.
            SessionInputQuestion::Number(_) | SessionInputQuestion::Integer(_) => {
                "SessionInputNumberQuestion"
            }
            SessionInputQuestion::Boolean(_) => "SessionInputBooleanQuestion",
            SessionInputQuestion::SingleSelect(_) => "SessionInputSingleSelectQuestion",
            SessionInputQuestion::MultiSelect(_) => "SessionInputMultiSelectQuestion",
            SessionInputQuestion::Unknown(_) => "JsonElement",
        }
        .to_string(),
    )
}

// ─── JSON-RPC variant ───────────────────────────────────────────────────────

fn verify_jsonrpc_variant(file: &str, decoded: &Decoded, kind: &str) -> FixtureResult {
    let Decoded::JsonRpcMessage(msg) = decoded else {
        return Err(format!(
            "{file}: expectJsonRpcVariant requires a JsonRpcMessage"
        ));
    };
    let actual = match msg {
        JsonRpcMessage::Request(_) => "request",
        JsonRpcMessage::Notification(_) => "notification",
        JsonRpcMessage::SuccessResponse(_) => "success",
        JsonRpcMessage::ErrorResponse(_) => "error",
    };
    let allowed = ["request", "notification", "success", "error"];
    if !allowed.contains(&kind) {
        return Err(format!(
            "{file}: expectJsonRpcVariant \"{kind}\" is not one of {allowed:?}"
        ));
    }
    if actual != kind {
        return Err(format!(
            "{file}: expectJsonRpcVariant — decoded as {actual}, expected {kind}"
        ));
    }
    Ok(())
}

// ─── Bitset ─────────────────────────────────────────────────────────────────

fn verify_bitset(
    file: &str,
    decoded: &Decoded,
    reencoded: &str,
    bitset: &serde_json::Map<String, Value>,
) -> FixtureResult {
    let Decoded::SessionStatus(status) = decoded else {
        return Err(format!("{file}: expectBitset requires a SessionStatus"));
    };
    // SessionStatus is a `u32` bitset newtype; read its raw bits.
    let numeric = status.bits() as u64;

    if let Some(has) = bitset.get("has").and_then(Value::as_array) {
        for name_v in has {
            let Some(name) = name_v.as_str() else {
                continue;
            };
            let flag = status_flag(name, file)?;
            if numeric & flag != flag {
                return Err(format!(
                    "{file}: SessionStatus must have flag {name} but does not (value {numeric})"
                ));
            }
        }
    }
    if let Some(lacks) = bitset.get("lacks").and_then(Value::as_array) {
        for name_v in lacks {
            let Some(name) = name_v.as_str() else {
                continue;
            };
            let flag = status_flag(name, file)?;
            if numeric & flag != 0 {
                return Err(format!(
                    "{file}: SessionStatus must NOT have flag {name} but does (value {numeric})"
                ));
            }
        }
    }
    if let Some(want) = bitset.get("numeric").and_then(as_i64) {
        if numeric as i64 != want {
            return Err(format!(
                "{file}: SessionStatus numeric — got {numeric}, expected {want}"
            ));
        }
        let re: Value = serde_json::from_str(reencoded)
            .map_err(|e| format!("{file}: re-encoded SessionStatus parse error: {e}"))?;
        let re_num = as_i64(&re).ok_or_else(|| {
            format!("{file}: SessionStatus must re-encode as a JSON number, got {reencoded}")
        })?;
        if re_num != want {
            return Err(format!(
                "{file}: SessionStatus re-encoded numeric — got {re_num}, expected {want}"
            ));
        }
    }
    Ok(())
}

/// Maps a canonical SessionStatus flag name to its numeric bit value.
fn status_flag(name: &str, file: &str) -> Result<u64, String> {
    Ok(match name {
        "Idle" => 1,
        "Error" => 2,
        "InProgress" => 8,
        "InputNeeded" => 24,
        "IsRead" => 32,
        "IsArchived" => 64,
        other => return Err(format!("{file}: unknown SessionStatus flag \"{other}\"")),
    })
}

// ─── ProtocolVersion constants ──────────────────────────────────────────────

fn verify_protocol_constant(file: &str, root: &Value) -> FixtureResult {
    let c = root
        .get("expectConstant")
        .and_then(Value::as_object)
        .ok_or_else(|| format!("{file}: ProtocolVersion fixture missing expectConstant"))?;
    let mut asserted = false;

    if let Some(cur) = c.get("current").and_then(Value::as_str) {
        if cur != "non-empty" {
            return Err(format!(
                "{file}: expectConstant.current must be \"non-empty\""
            ));
        }
        if PROTOCOL_VERSION.trim().is_empty() {
            return Err(format!("{file}: PROTOCOL_VERSION must be non-empty"));
        }
        asserted = true;
    }
    if let Some(sup) = c.get("supported").and_then(Value::as_str) {
        if sup != "non-empty-list" {
            return Err(format!(
                "{file}: expectConstant.supported must be \"non-empty-list\""
            ));
        }
        if SUPPORTED_PROTOCOL_VERSIONS.is_empty() {
            return Err(format!(
                "{file}: SUPPORTED_PROTOCOL_VERSIONS must be non-empty"
            ));
        }
        asserted = true;
    }
    if let Some(first) = c
        .get("firstSupportedEqualsCurrent")
        .and_then(Value::as_bool)
    {
        if first {
            let head = SUPPORTED_PROTOCOL_VERSIONS
                .first()
                .ok_or_else(|| format!("{file}: SUPPORTED_PROTOCOL_VERSIONS is empty"))?;
            if *head != PROTOCOL_VERSION {
                return Err(format!(
                    "{file}: first supported {head} != current {PROTOCOL_VERSION}"
                ));
            }
            asserted = true;
        }
    }
    if !asserted {
        return Err(format!(
            "{file}: ProtocolVersion fixture asserted no constant"
        ));
    }
    Ok(())
}

// ─── Input bytes ────────────────────────────────────────────────────────────

fn read_input_json(file: &str, root: &Value) -> Result<String, String> {
    let has_raw = root.get("wireRaw").is_some();
    let has_wire = root.get("wire").is_some();
    if has_raw == has_wire {
        return Err(format!(
            "{file}: exactly one of `wire` / `wireRaw` is required (wire={has_wire}, wireRaw={has_raw})."
        ));
    }
    if has_raw {
        let raw = root
            .get("wireRaw")
            .and_then(Value::as_str)
            .ok_or_else(|| format!("{file}: `wireRaw` is not a string"))?;
        return Ok(raw.to_string());
    }
    // `wire` is a JSON value; compact-serialize it.
    let wire = root.get("wire").unwrap();
    serde_json::to_string(wire).map_err(|e| format!("{file}: serializing `wire`: {e}"))
}

// ─── JSON path + equality ────────────────────────────────────────────────────

/// Resolves a dotted path against a parsed JSON value. Empty path -> the value
/// itself (scalar unions whose whole value is the payload).
fn resolve_path<'a>(root: &'a Value, path: &str, file: &str) -> Result<&'a Value, String> {
    if path.is_empty() {
        return Ok(root);
    }
    let mut cur = root;
    for seg in path.split('.') {
        cur = cur
            .get(seg)
            .ok_or_else(|| format!("{file}: path \"{path}\" — segment \"{seg}\" not found"))?;
    }
    Ok(cur)
}

fn assert_json_equals(want: &Value, got: &Value, ctx: &str) -> FixtureResult {
    // Numbers compared numerically so 0 == 0.0 and large ints stay exact.
    if want.is_number() && got.is_number() {
        if let (Some(w), Some(g)) = (as_i64(want), as_i64(got)) {
            if w != g {
                return Err(format!("{ctx} — expected number {w}, got {g}"));
            }
            return Ok(());
        }
        if let (Some(w), Some(g)) = (want.as_f64(), got.as_f64()) {
            if w != g {
                return Err(format!("{ctx} — expected number {w}, got {g}"));
            }
            return Ok(());
        }
    }
    if want != got {
        return Err(format!("{ctx} — expected {}, got {}", want, got));
    }
    Ok(())
}

/// Compares two JSON documents structurally (key order independent, value and
/// key-presence sensitive). Used for `reencodes` / fixed-point checks.
fn assert_canonical_equal(lhs: &str, rhs: &str, ctx: &str) -> FixtureResult {
    let lo: Value =
        serde_json::from_str(lhs).map_err(|e| format!("{ctx}: lhs parse error: {e}"))?;
    let ro: Value =
        serde_json::from_str(rhs).map_err(|e| format!("{ctx}: rhs parse error: {e}"))?;
    // serde_json::Value's PartialEq is structural and order-independent for
    // objects (backed by a Map). Numbers compare by their parsed value.
    if lo != ro {
        return Err(format!("{ctx}\n  lhs: {lhs}\n  rhs: {rhs}"));
    }
    Ok(())
}

fn as_i64(v: &Value) -> Option<i64> {
    if let Some(i) = v.as_i64() {
        return Some(i);
    }
    if let Some(u) = v.as_u64() {
        if u <= i64::MAX as u64 {
            return Some(u as i64);
        }
    }
    // Whole-valued floats (e.g. 72.0) count as integers.
    if let Some(f) = v.as_f64() {
        if f.fract() == 0.0 && f >= i64::MIN as f64 && f <= i64::MAX as f64 {
            return Some(f as i64);
        }
    }
    None
}
