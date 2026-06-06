// AHP Conformance Runner — Rust build-phase B5.
//
// Ports the B4 JS reference runner (conformance/runner/run-conformance.mjs)
// to Rust. For every scenario file it:
//   1. Spawns the scenario-driven host:
//        node conformance/host/scenario-host.mjs <scenario.json>
//   2. Parses "SCENARIO HOST READY ws://127.0.0.1:<port>" from stdout.
//   3. Opens a real WebSocket (tokio-tungstenite).
//   4. Replays each client.request step, applies every server.notify
//      ActionEnvelope through the real Rust reducers (ahp crate), and
//      collects surfaced JSON-RPC errors.
//   5. Checks every client.assert.* step and reports PASS/FAIL/SKIP.
//
// NO MOCKS — real ws, real host subprocess, real reducers, real assertions.
// (CROSS-SPEC-INTENT-VERIFIED-BY-REAL-EXECUTION + ADR-067/072.)
//
// Usage:
//   cargo run -- [--tranche brief|full] [--verbose] [--scenario <path>]
//
// Exit 0 = all scenarios passed; 1 = one or more failed.

use std::collections::HashMap;
use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::Duration;

use futures_util::{SinkExt, StreamExt};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use tokio::time::timeout;
use tokio_tungstenite::connect_async;
use tokio_tungstenite::tungstenite::Message;

use ahp::reducers::{
    apply_action_to_changeset, apply_action_to_root, apply_action_to_session,
    apply_action_to_terminal,
};
use ahp::{clear_clock_override, set_clock_override};
use ahp_types::actions::StateAction;
use ahp_types::state::{ChangesetState, ChangesetStatus, RootState, SessionState, TerminalState};

// ── Clock pin (mirrors JS pinClock) ─────────────────────────────────────────
//
// The Rust reducers call `SystemTime::now()` for `modified_at` fields.
// We pin the global clock override in `ahp::reducers` via `set_clock_override`
// before replaying each scenario, exactly as the JS runner does `Date.now = () => epochMs`.
// This ensures impure fields (summary.modifiedAt) converge to the scenario's pinClock.

// ── Helpers ──────────────────────────────────────────────────────────────────

/// Deserialize `Option<Value>` treating JSON `null` as `Some(Value::Null)`,
/// not as `None`. This is required because `"equals": null` in a scenario
/// means "assert the value is null", which is distinct from the field being
/// absent (which means "no assertion on this field").
fn deserialize_opt_value_preserve_null<'de, D>(
    deserializer: D,
) -> Result<Option<Value>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    use serde::Deserialize;
    // Deserialize into `Value` directly (Value deserializes null as Value::Null,
    // not as a missing field). Then wrap in Some.
    // If the field is absent serde calls the `default` function → None.
    let v = Value::deserialize(deserializer)?;
    Ok(Some(v))
}

// ── Scenario schema ──────────────────────────────────────────────────────────

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Scenario {
    id: String,
    #[serde(default)]
    pin_clock: Option<i64>,
    steps: Vec<Step>,
}

#[derive(Debug, Deserialize)]
struct Step {
    op: String,
    #[serde(default)]
    label: Option<String>,
    // client.request fields
    #[serde(default)]
    method: Option<String>,
    #[serde(default)]
    id: Option<Value>,
    #[serde(default)]
    params: Option<Value>,
    // server.response fields
    #[serde(default, rename = "forId")]
    for_id: Option<Value>,
    #[serde(default)]
    result: Option<Value>,
    #[serde(default)]
    error: Option<Value>,
    // server.notify fields — method already above
    // client.assert.state fields
    #[serde(default)]
    channel: Option<String>,
    #[serde(default)]
    path: Option<String>,
    // Use a custom deserializer so JSON `null` becomes Some(Value::Null)
    // instead of None, since `"equals": null` means "assert the value is null".
    #[serde(default, deserialize_with = "deserialize_opt_value_preserve_null")]
    equals: Option<Value>,
    // client.assert.event fields
    #[serde(default)]
    matches: Option<Value>,
    // client.assert.error fields
    #[serde(default)]
    code: Option<i64>,
    #[serde(default)]
    message: Option<String>,
}

// ── Per-channel state bag ─────────────────────────────────────────────────────

enum ChannelState {
    Root(RootState),
    Session(SessionState),
    Terminal(TerminalState),
    Changeset(ChangesetState),
    Raw(Value), // for unknown reducers — just store the seeded JSON
}

// ── State collected during a protocol drive ──────────────────────────────────

struct DriveResult {
    channels: HashMap<String, ChannelState>,
    /// Synthetic top-level state: protocolVersion, lastResponseOk, pingSeen.
    synthetic: Value,
    /// Every observed ActionEnvelope or message-level frame (as raw Value).
    observed_events: Vec<Value>,
    /// Surfaced JSON-RPC errors.
    surfaced_errors: Vec<Value>,
    /// Non-fatal warnings.
    warnings: Vec<String>,
}

// ── Canonicalize (null-stripped, key-sorted) ──────────────────────────────────

fn canonicalize(v: &Value) -> Value {
    match v {
        Value::Object(map) => {
            let mut out: Vec<(String, Value)> = map
                .iter()
                .filter(|(_, v)| !v.is_null())
                .map(|(k, v)| (k.clone(), canonicalize(v)))
                .collect();
            out.sort_by(|a, b| a.0.cmp(&b.0));
            Value::Object(out.into_iter().collect())
        }
        Value::Array(arr) => Value::Array(arr.iter().map(canonicalize).collect()),
        other => other.clone(),
    }
}

fn deep_equal_canonical(a: &Value, b: &Value) -> bool {
    canonicalize(a) == canonicalize(b)
}

// Deep containment: every key in `expected` matches in `actual`; extra keys
// in `actual` are ignored. Arrays compare element-wise with containment.
fn deep_contains(actual: &Value, expected: &Value) -> bool {
    match expected {
        Value::Object(exp_map) => {
            let Value::Object(act_map) = actual else {
                return false;
            };
            for (k, ev) in exp_map {
                match act_map.get(k) {
                    Some(av) => {
                        if !deep_contains(av, ev) {
                            return false;
                        }
                    }
                    None => return false,
                }
            }
            true
        }
        Value::Array(exp_arr) => {
            let Value::Array(act_arr) = actual else {
                return false;
            };
            if act_arr.len() != exp_arr.len() {
                return false;
            }
            for (a, e) in act_arr.iter().zip(exp_arr.iter()) {
                if !deep_contains(a, e) {
                    return false;
                }
            }
            true
        }
        _ => actual == expected,
    }
}

// Navigate a dotted path; numeric segments index arrays.
fn navigate<'a>(obj: &'a Value, path: &str) -> Option<&'a Value> {
    if path.is_empty() {
        return Some(obj);
    }
    let mut cur = obj;
    for seg in path.split('.') {
        match cur {
            Value::Object(map) => {
                cur = map.get(seg)?;
            }
            Value::Array(arr) => {
                let idx: usize = seg.parse().ok()?;
                cur = arr.get(idx)?;
            }
            _ => return None,
        }
    }
    Some(cur)
}

// ── Reducer dispatch by action-type prefix ───────────────────────────────────

fn action_type_prefix(action: &Value) -> Option<String> {
    let t = action.get("type")?.as_str()?;
    Some(t.split('/').next()?.to_string())
}

enum ReducerScope {
    Root,
    Session,
    Terminal,
    Changeset,
    Resource, // resourceWatch — not implemented in Rust yet
    Unknown,
}

fn reducer_scope(action: &Value) -> ReducerScope {
    match action_type_prefix(action).as_deref() {
        Some("root") => ReducerScope::Root,
        Some("session") => ReducerScope::Session,
        Some("terminal") => ReducerScope::Terminal,
        Some("changeset") => ReducerScope::Changeset,
        Some("resource") => ReducerScope::Resource,
        _ => ReducerScope::Unknown,
    }
}

// ── Apply action to a channel slot, seeding if needed ───────────────────────

fn apply_notify_action(
    channels: &mut HashMap<String, ChannelState>,
    channel: &str,
    action_val: &Value,
    warnings: &mut Vec<String>,
) {
    let scope = reducer_scope(action_val);

    // Deserialize the action once (needed for root/session/terminal).
    let parsed: Result<StateAction, _> = serde_json::from_value(action_val.clone());

    match scope {
        ReducerScope::Root => {
            let Ok(action) = parsed else {
                return;
            };
            let entry = channels
                .entry(channel.to_string())
                .or_insert_with(|| ChannelState::Root(RootState {
                    agents: vec![],
                    active_sessions: None,
                    terminals: None,
                    config: None,
                }));
            if let ChannelState::Root(ref mut state) = entry {
                apply_action_to_root(state, &action);
            } else {
                warnings.push(format!(
                    "Channel {channel} expected Root state but held different type"
                ));
            }
        }
        ReducerScope::Session => {
            let Ok(action) = parsed else {
                return;
            };
            let entry = channels
                .entry(channel.to_string())
                .or_insert_with(|| ChannelState::Session(default_session_state(channel)));
            if let ChannelState::Session(ref mut state) = entry {
                apply_action_to_session(state, &action);
            } else {
                warnings.push(format!(
                    "Channel {channel} expected Session state but held different type"
                ));
            }
        }
        ReducerScope::Terminal => {
            let Ok(action) = parsed else {
                return;
            };
            let entry = channels
                .entry(channel.to_string())
                .or_insert_with(|| {
                    ChannelState::Terminal(TerminalState {
                        title: String::new(),
                        cwd: None,
                        cols: None,
                        rows: None,
                        content: vec![],
                        exit_code: None,
                        claim: ahp_types::state::TerminalClaim::Session(
                            ahp_types::state::TerminalSessionClaim {
                                session: channel.to_string(),
                                turn_id: None,
                                tool_call_id: None,
                            },
                        ),
                        supports_command_detection: None,
                    })
                });
            if let ChannelState::Terminal(ref mut state) = entry {
                apply_action_to_terminal(state, &action);
            } else {
                warnings.push(format!(
                    "Channel {channel} expected Terminal state but held different type"
                ));
            }
        }
        ReducerScope::Changeset => {
            let Ok(action) = parsed else {
                return;
            };
            let entry = channels
                .entry(channel.to_string())
                .or_insert_with(|| {
                    ChannelState::Changeset(ChangesetState {
                        status: ChangesetStatus::Computing,
                        error: None,
                        files: vec![],
                        operations: None,
                    })
                });
            if let ChannelState::Changeset(ref mut state) = entry {
                apply_action_to_changeset(state, &action);
            } else {
                warnings.push(format!(
                    "Channel {channel} expected Changeset state but held different type"
                ));
            }
        }
        ReducerScope::Resource | ReducerScope::Unknown => {
            // resourceWatch not implemented in Rust yet — the event is still observed;
            // assert.state assertions against these channels will be SKIP.
        }
    }
}

fn default_session_state(resource: &str) -> SessionState {
    use ahp_types::state::{SessionLifecycle, SessionSummary};
    SessionState {
        summary: SessionSummary {
            resource: resource.to_string(),
            provider: String::new(),
            title: String::new(),
            status: ahp_types::state::SessionStatus::Idle.bits(),
            activity: None,
            created_at: 0,
            modified_at: 0,
            project: None,
            model: None,
            agent: None,
            working_directory: None,
            changesets: None,
        },
        lifecycle: SessionLifecycle::Creating,
        creation_error: None,
        server_tools: None,
        active_client: None,
        turns: vec![],
        active_turn: None,
        steering_message: None,
        queued_messages: None,
        input_requests: None,
        config: None,
        customizations: None,
        meta: None,
    }
}

// ── Seed from a snapshot result (mirrors JS seedFromSnapshots) ───────────────

fn seed_from_result(channels: &mut HashMap<String, ChannelState>, synthetic: &mut Value, result: &Value) {
    if let Some(v) = result.get("protocolVersion") {
        synthetic["protocolVersion"] = v.clone();
    }

    // Multi-snapshot array.
    if let Some(snapshots) = result.get("snapshots").and_then(Value::as_array) {
        for snap in snapshots {
            let Some(resource) = snap.get("resource").and_then(Value::as_str) else {
                continue;
            };
            let Some(state_val) = snap.get("state") else {
                continue;
            };
            seed_channel(channels, resource, state_val);
        }
    }

    // Single snapshot form.
    if let Some(snap) = result.get("snapshot") {
        if let (Some(resource), Some(state_val)) = (
            snap.get("resource").and_then(Value::as_str),
            snap.get("state"),
        ) {
            seed_channel(channels, resource, state_val);
        }
    }
}

fn seed_channel(channels: &mut HashMap<String, ChannelState>, resource: &str, state_val: &Value) {
    // Detect type by presence of characteristic fields (same heuristic as B4 JS runner context).
    // We try to deserialize into each candidate state type.
    if let Ok(s) = serde_json::from_value::<SessionState>(state_val.clone()) {
        channels.insert(resource.to_string(), ChannelState::Session(s));
        return;
    }
    if let Ok(r) = serde_json::from_value::<RootState>(state_val.clone()) {
        channels.insert(resource.to_string(), ChannelState::Root(r));
        return;
    }
    if let Ok(t) = serde_json::from_value::<TerminalState>(state_val.clone()) {
        channels.insert(resource.to_string(), ChannelState::Terminal(t));
        return;
    }
    if let Ok(c) = serde_json::from_value::<ChangesetState>(state_val.clone()) {
        channels.insert(resource.to_string(), ChannelState::Changeset(c));
        return;
    }
    // Fallback: store raw JSON for unknown types.
    channels.insert(resource.to_string(), ChannelState::Raw(state_val.clone()));
}

// ── Get the JSON value for a channel state (for assertions) ──────────────────

fn channel_state_to_value(state: &ChannelState) -> Value {
    match state {
        ChannelState::Root(s) => strip_nulls(serde_json::to_value(s).unwrap_or(Value::Null)),
        ChannelState::Session(s) => strip_nulls(serde_json::to_value(s).unwrap_or(Value::Null)),
        ChannelState::Terminal(s) => strip_nulls(serde_json::to_value(s).unwrap_or(Value::Null)),
        ChannelState::Changeset(s) => strip_nulls(serde_json::to_value(s).unwrap_or(Value::Null)),
        ChannelState::Raw(v) => strip_nulls(v.clone()),
    }
}

fn strip_nulls(v: Value) -> Value {
    match v {
        Value::Object(map) => {
            let cleaned: serde_json::Map<String, Value> = map
                .into_iter()
                .filter(|(_, v)| !v.is_null())
                .map(|(k, v)| (k, strip_nulls(v)))
                .collect();
            Value::Object(cleaned)
        }
        Value::Array(arr) => Value::Array(arr.into_iter().map(strip_nulls).collect()),
        other => other,
    }
}

// ── WebSocket drive ───────────────────────────────────────────────────────────

async fn drive_protocol(ws_url: &str, scenario: &Scenario) -> Result<DriveResult, String> {
    let requests: Vec<&Step> = scenario
        .steps
        .iter()
        .filter(|s| s.op == "client.request")
        .collect();

    let mut channels: HashMap<String, ChannelState> = HashMap::new();
    let mut observed_events: Vec<Value> = Vec::new();
    let mut surfaced_errors: Vec<Value> = Vec::new();
    let mut warnings: Vec<String> = Vec::new();
    let mut synthetic = serde_json::json!({});

    // Retry loop for transient pre-open connect errors.
    let (ws_stream, _) = {
        let mut last_err = String::new();
        let mut connected = None;
        for attempt in 0..6 {
            match timeout(Duration::from_secs(10), connect_async(ws_url)).await {
                Ok(Ok(pair)) => {
                    connected = Some(pair);
                    break;
                }
                Ok(Err(e)) => {
                    last_err = e.to_string();
                    if attempt < 5 {
                        tokio::time::sleep(Duration::from_millis(80)).await;
                    }
                }
                Err(_) => {
                    last_err = "connection timeout".to_string();
                    break;
                }
            }
        }
        connected.ok_or_else(|| format!("WebSocket connect failed: {last_err}"))?
    };

    let (mut write, mut read) = ws_stream.split();

    let mut request_cursor = 0usize;

    // Send first request.
    // Helper: build a JSON-RPC request frame.
    fn build_request_frame(step: &Step) -> Value {
        let mut frame = serde_json::json!({
            "jsonrpc": "2.0",
            "method": step.method.as_deref().unwrap_or(""),
            "id": step.id.clone().unwrap_or(Value::Null)
        });
        if let Some(params) = &step.params {
            frame["params"] = params.clone();
        }
        frame
    }

    // Send the first request if any.
    if let Some(step) = requests.get(request_cursor) {
        let frame = build_request_frame(step);
        write
            .send(Message::Text(frame.to_string().into()))
            .await
            .map_err(|e| e.to_string())?;
        request_cursor += 1;
    }

    // Soft timeout — some scenarios leave the socket open after the last frame.
    let drive_timeout = Duration::from_secs(10);

    let result = timeout(drive_timeout, async {
        while let Some(msg_result) = read.next().await {
            let raw = match msg_result {
                Ok(Message::Text(t)) => t.to_string(),
                Ok(Message::Binary(b)) => String::from_utf8_lossy(&b).to_string(),
                Ok(Message::Close(_)) => break,
                Ok(_) => continue,
                Err(e) => return Err(e.to_string()),
            };

            let msg: Value = match serde_json::from_str(&raw) {
                Ok(v) => v,
                Err(_) => continue,
            };

            let has_id = !msg.get("id").map(Value::is_null).unwrap_or(true);
            let has_result = msg.get("result").is_some();
            let has_error = msg.get("error").is_some();
            let has_method = msg.get("method").is_some();

            if has_id && (has_result || has_error) {
                // Response.
                if has_error {
                    surfaced_errors.push(msg["error"].clone());
                    synthetic["lastResponseOk"] = Value::Bool(false);
                } else {
                    synthetic["lastResponseOk"] = Value::Bool(true);
                    seed_from_result(&mut channels, &mut synthetic, &msg["result"]);
                }
                // Drive next request.
                if let Some(step) = requests.get(request_cursor) {
                    let frame = build_request_frame(step);
                    write
                        .send(Message::Text(frame.to_string().into()))
                        .await
                        .map_err(|e| e.to_string())?;
                    request_cursor += 1;
                }
            } else if has_method {
                // Notification.
                let method = msg["method"].as_str().unwrap_or("");
                // Record the message-level event (so assert.event can match { method, params }).
                observed_events.push(serde_json::json!({
                    "method": msg["method"],
                    "params": msg.get("params").cloned().unwrap_or(Value::Null)
                }));

                if method == "action" {
                    let params = msg.get("params").cloned().unwrap_or(Value::Null);
                    // Record the ActionEnvelope-level event (so assert.event can match envelope fields).
                    observed_events.push(params.clone());

                    let channel = params
                        .get("channel")
                        .and_then(Value::as_str)
                        .unwrap_or("")
                        .to_string();
                    if let Some(action_val) = params.get("action") {
                        // Also record the action itself.
                        observed_events.push(action_val.clone());
                        apply_notify_action(
                            &mut channels,
                            &channel,
                            action_val,
                            &mut warnings,
                        );
                    }
                }
            }
        }
        Ok(())
    })
    .await;

    match result {
        Err(_timeout) => {
            // Soft timeout: treat as "done" — we've collected all frames.
        }
        Ok(Err(e)) => return Err(e),
        Ok(Ok(())) => {}
    }

    Ok(DriveResult {
        channels,
        synthetic,
        observed_events,
        surfaced_errors,
        warnings,
    })
}

// ── Assertion check ──────────────────────────────────────────────────────────

#[derive(Debug)]
struct AssertResult {
    ok: bool,
    skipped: bool,
    detail: String,
}

fn check_assertion(step: &Step, result: &DriveResult) -> AssertResult {
    match step.op.as_str() {
        "client.assert.state" => {
            let target_value: Value;
            let bucket_label: String;

            if let Some(ch) = &step.channel {
                // Skip assert.state for channels whose reducers aren't implemented
                // in Rust yet (resourceWatch). The JS runner would fold
                // the actions through the real reducer; Rust just skips convergence
                // checks for these channel schemes.
                let is_unimplemented = ch.starts_with("ahp-resource:");
                if is_unimplemented {
                    return AssertResult {
                        ok: true,
                        skipped: true,
                        detail: format!("SKIP: channel {ch} uses unimplemented reducer"),
                    };
                }

                match result.channels.get(ch.as_str()) {
                    Some(state) => {
                        target_value = channel_state_to_value(state);
                        bucket_label = format!("channel {ch}");
                    }
                    None => {
                        let known: Vec<&str> =
                            result.channels.keys().map(String::as_str).collect();
                        return AssertResult {
                            ok: false,
                            skipped: false,
                            detail: format!(
                                "no reduced state for channel {ch}; known: [{}]",
                                known.join(", ")
                            ),
                        };
                    }
                }
            } else if step.path.is_some() {
                // Path with no channel → synthetic top-level (protocolVersion / lastResponseOk).
                // The JS runner stores these in a `synthetic` map seeded from response results.
                let path = step.path.as_deref().unwrap_or("");
                let actual = navigate(&result.synthetic, path);
                let expected = step.equals.as_ref();
                return match (actual, expected) {
                    (Some(a), Some(e)) => {
                        if deep_equal_canonical(a, e) {
                            AssertResult { ok: true, skipped: false, detail: String::new() }
                        } else {
                            AssertResult {
                                ok: false,
                                skipped: false,
                                detail: format!(
                                    "assert.state @ synthetic path '{path}': expected {}, got {}",
                                    serde_json::to_string(e).unwrap_or_default(),
                                    serde_json::to_string(&canonicalize(a)).unwrap_or_default()
                                ),
                            }
                        }
                    }
                    (None, Some(e)) if e.is_null() => {
                        // Path not found + expected null → treat as ok (JS runner does same).
                        AssertResult { ok: true, skipped: false, detail: String::new() }
                    }
                    (None, _) => AssertResult {
                        ok: false,
                        skipped: false,
                        detail: format!(
                            "assert.state @ synthetic path '{path}': not found in synthetic state {}",
                            serde_json::to_string(&result.synthetic).unwrap_or_default()
                        ),
                    },
                    (_, None) => AssertResult {
                        ok: false,
                        skipped: false,
                        detail: "assert.state missing 'equals' field".to_string(),
                    },
                };
            } else {
                // Whole-state convergence against the single channel.
                if result.channels.len() == 1 {
                    let (ch_key, state) = result.channels.iter().next().unwrap();
                    // Skip if the single channel is an unimplemented reducer type.
                    let is_unimplemented = ch_key.starts_with("ahp-resource:");
                    if is_unimplemented {
                        return AssertResult {
                            ok: true,
                            skipped: true,
                            detail: format!("SKIP: channel {ch_key} uses unimplemented reducer"),
                        };
                    }
                    target_value = channel_state_to_value(state);
                    bucket_label = format!("single channel ({ch_key})");
                } else {
                    let known: Vec<&str> =
                        result.channels.keys().map(String::as_str).collect();
                    return AssertResult {
                        ok: false,
                        skipped: false,
                        detail: format!(
                            "whole-state assertion needs exactly one channel, found {}: [{}]",
                            result.channels.len(),
                            known.join(", ")
                        ),
                    };
                }
            }

            let path = step.path.as_deref().unwrap_or("");
            let actual = navigate(&target_value, path);
            let expected = step.equals.as_ref();

            match (actual, expected) {
                (Some(a), Some(e)) => {
                    if deep_equal_canonical(a, e) {
                        AssertResult { ok: true, skipped: false, detail: String::new() }
                    } else {
                        AssertResult {
                            ok: false,
                            skipped: false,
                            detail: format!(
                                "assert.state @ {bucket_label}{}: expected {}, got {}",
                                if path.is_empty() { " (whole state)".to_string() } else { format!(" path '{path}'") },
                                serde_json::to_string(e).unwrap_or_default(),
                                serde_json::to_string(&canonicalize(a)).unwrap_or_default()
                            ),
                        }
                    }
                }
                (None, Some(e)) if e.is_null() => {
                    // Path not found and expected null → treat as ok (same as JS runner).
                    AssertResult { ok: true, skipped: false, detail: String::new() }
                }
                (None, _) => AssertResult {
                    ok: false,
                    skipped: false,
                    detail: format!("assert.state @ {bucket_label}: path '{path}' not found"),
                },
                (_, None) => AssertResult {
                    ok: false,
                    skipped: false,
                    detail: "assert.state missing 'equals' field".to_string(),
                },
            }
        }

        "client.assert.event" => {
            let Some(expected) = &step.matches else {
                return AssertResult {
                    ok: false,
                    skipped: false,
                    detail: "assert.event missing 'matches' field".to_string(),
                };
            };
            // Try deep containment against every observed event.
            for ev in &result.observed_events {
                if deep_contains(ev, expected) {
                    return AssertResult { ok: true, skipped: false, detail: String::new() };
                }
            }
            AssertResult {
                ok: false,
                skipped: false,
                detail: format!(
                    "assert.event: no observed event deep-contains {}. observed {} event(s)",
                    serde_json::to_string(expected).unwrap_or_default(),
                    result.observed_events.len()
                ),
            }
        }

        "client.assert.error" => {
            let expected_code = step.code.unwrap_or(0);
            for err in &result.surfaced_errors {
                let err_code = err.get("code").and_then(Value::as_i64).unwrap_or(0);
                if err_code != expected_code {
                    continue;
                }
                if let Some(msg_substr) = &step.message {
                    let err_msg = err
                        .get("message")
                        .and_then(Value::as_str)
                        .unwrap_or("");
                    if !err_msg.contains(msg_substr.as_str()) {
                        continue;
                    }
                }
                return AssertResult { ok: true, skipped: false, detail: String::new() };
            }
            AssertResult {
                ok: false,
                skipped: false,
                detail: format!(
                    "assert.error: no surfaced error with code {}. surfaced: {}",
                    expected_code,
                    serde_json::to_string(&result.surfaced_errors).unwrap_or_default()
                ),
            }
        }

        other => AssertResult {
            ok: false,
            skipped: false,
            detail: format!("unknown assertion op: {other}"),
        },
    }
}

// ── Run one scenario ─────────────────────────────────────────────────────────

#[derive(Debug)]
enum ScenarioStatus {
    Pass,
    Fail,
    Skip,
    Error(String),
}

struct ScenarioResult {
    id: String,
    status: ScenarioStatus,
    asserts_pass: usize,
    asserts_fail: usize,
    asserts_skip: usize,
    failures: Vec<String>,
    warnings: Vec<String>,
}

async fn run_scenario(
    scenario_path: &Path,
    host_script: &Path,
    verbose: bool,
) -> ScenarioResult {
    let id = scenario_path
        .file_name()
        .and_then(|n| n.to_str())
        .unwrap_or("unknown")
        .trim_end_matches(".scenario.json")
        .to_string();

    // Parse scenario.
    let raw = match std::fs::read_to_string(scenario_path) {
        Ok(r) => r,
        Err(e) => {
            return ScenarioResult {
                id,
                status: ScenarioStatus::Error(format!("read error: {e}")),
                asserts_pass: 0,
                asserts_fail: 0,
                asserts_skip: 0,
                failures: vec![],
                warnings: vec![],
            };
        }
    };
    let scenario: Scenario = match serde_json::from_str(&raw) {
        Ok(s) => s,
        Err(e) => {
            return ScenarioResult {
                id,
                status: ScenarioStatus::Error(format!("JSON parse error: {e}")),
                asserts_pass: 0,
                asserts_fail: 0,
                asserts_skip: 0,
                failures: vec![],
                warnings: vec![],
            };
        }
    };

    // Pin the clock — must happen before any action is applied, exactly as the
    // JS runner does `Date.now = () => epochMs` before reduction.
    if let Some(clock) = scenario.pin_clock {
        set_clock_override(clock);
    } else {
        clear_clock_override();
    }

    // Spawn host.
    let mut child = match Command::new("node")
        .arg(host_script)
        .arg(scenario_path)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
    {
        Ok(c) => c,
        Err(e) => {
            return ScenarioResult {
                id,
                status: ScenarioStatus::Error(format!("failed to spawn host: {e}")),
                asserts_pass: 0,
                asserts_fail: 0,
                asserts_skip: 0,
                failures: vec![],
                warnings: vec![],
            };
        }
    };

    // Read host stdout until "SCENARIO HOST READY ws://...".
    let stdout = child.stdout.take().unwrap();
    let ws_url = {
        let mut reader = BufReader::new(stdout);
        let mut ws_url = None;
        let deadline = std::time::Instant::now() + Duration::from_secs(10);
        let mut line = String::new();
        loop {
            if std::time::Instant::now() > deadline {
                break;
            }
            line.clear();
            match reader.read_line(&mut line) {
                Ok(0) => break, // EOF
                Ok(_) => {
                    if let Some(pos) = line.find("SCENARIO HOST READY ") {
                        let rest = &line[pos + "SCENARIO HOST READY ".len()..];
                        let url_part = rest.split_whitespace().next().unwrap_or("").to_string();
                        ws_url = Some(url_part);
                        break;
                    }
                }
                Err(_) => break,
            }
        }
        ws_url
    };

    let ws_url = match ws_url {
        Some(u) => u,
        None => {
            let _ = child.kill();
            return ScenarioResult {
                id,
                status: ScenarioStatus::Error("host did not print READY line".to_string()),
                asserts_pass: 0,
                asserts_fail: 0,
                asserts_skip: 0,
                failures: vec![],
                warnings: vec![],
            };
        }
    };

    // Drive the protocol.
    let drive_result = drive_protocol(&ws_url, &scenario).await;
    let _ = child.kill();
    let _ = child.wait();

    let result = match drive_result {
        Ok(r) => r,
        Err(e) => {
            return ScenarioResult {
                id,
                status: ScenarioStatus::Error(format!("drive error: {e}")),
                asserts_pass: 0,
                asserts_fail: 0,
                asserts_skip: 0,
                failures: vec![],
                warnings: vec![],
            };
        }
    };

    // Run assertions.
    let assert_steps: Vec<&Step> = scenario
        .steps
        .iter()
        .filter(|s| s.op.starts_with("client.assert."))
        .collect();

    if assert_steps.is_empty() {
        return ScenarioResult {
            id,
            status: ScenarioStatus::Error("scenario has no client.assert.* steps".to_string()),
            asserts_pass: 0,
            asserts_fail: 0,
            asserts_skip: 0,
            failures: vec![],
            warnings: result.warnings,
        };
    }

    let mut asserts_pass = 0usize;
    let mut asserts_fail = 0usize;
    let mut asserts_skip = 0usize;
    let mut failures: Vec<String> = Vec::new();
    let warnings = result.warnings.clone();

    for step in &assert_steps {
        let ar = check_assertion(step, &result);
        if verbose {
            let label = step.label.as_deref().unwrap_or("");
            if ar.skipped {
                eprintln!("  SKIP  {}  {}", step.op, label);
            } else if ar.ok {
                println!("  PASS  {}  {}", step.op, label);
            } else {
                eprintln!("  FAIL  {}  {}", step.op, label);
                eprintln!("        → {}", ar.detail);
            }
        }
        if ar.skipped {
            asserts_skip += 1;
        } else if ar.ok {
            asserts_pass += 1;
        } else {
            asserts_fail += 1;
            failures.push(format!(
                "{} {}: {}",
                step.op,
                step.label.as_deref().unwrap_or(""),
                ar.detail
            ));
        }
    }

    let status = if asserts_fail == 0 {
        if asserts_pass == 0 && asserts_skip > 0 {
            ScenarioStatus::Skip
        } else {
            ScenarioStatus::Pass
        }
    } else {
        ScenarioStatus::Fail
    };

    ScenarioResult {
        id,
        status,
        asserts_pass,
        asserts_fail,
        asserts_skip,
        failures,
        warnings,
    }
}

// ── Main ─────────────────────────────────────────────────────────────────────

fn collect_scenarios(dir: &Path) -> Vec<PathBuf> {
    let mut paths: Vec<PathBuf> = std::fs::read_dir(dir)
        .unwrap_or_else(|e| panic!("cannot read dir {}: {e}", dir.display()))
        .filter_map(|e| e.ok())
        .filter(|e| {
            e.path()
                .extension()
                .map(|x| x == "json")
                .unwrap_or(false)
        })
        .map(|e| e.path())
        .collect();
    paths.sort();
    paths
}

#[tokio::main]
async fn main() {
    let args: Vec<String> = std::env::args().collect();

    let verbose = args.contains(&"--verbose".to_string());
    let full = args.contains(&"--full".to_string());

    // Find repo root (we're in conformance/rust/).
    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let repo_root = manifest_dir
        .parent() // conformance/
        .unwrap()
        .parent() // repo root
        .unwrap()
        .to_path_buf();

    let host_script = repo_root
        .join("conformance")
        .join("host")
        .join("scenario-host.mjs");

    // Single scenario mode.
    if let Some(idx) = args.iter().position(|a| a == "--scenario") {
        if let Some(path_str) = args.get(idx + 1) {
            let path = PathBuf::from(path_str);
            let r = run_scenario(&path, &host_script, verbose).await;
            match &r.status {
                ScenarioStatus::Pass => {
                    println!("PASS  {}  ({} pass, {} skip)", r.id, r.asserts_pass, r.asserts_skip);
                    std::process::exit(0);
                }
                ScenarioStatus::Skip => {
                    println!("SKIP  {}  (all {} skipped)", r.id, r.asserts_skip);
                    std::process::exit(0);
                }
                ScenarioStatus::Fail => {
                    eprintln!("FAIL  {}", r.id);
                    for f in &r.failures {
                        eprintln!("  ✗ {f}");
                    }
                    std::process::exit(1);
                }
                ScenarioStatus::Error(msg) => {
                    eprintln!("ERROR {}: {msg}", r.id);
                    std::process::exit(2);
                }
            }
        }
    }

    let scenario_base = repo_root.join("types").join("test-cases").join("scenarios");

    let round_trips_dir = scenario_base.join("round-trips");
    let reducers_dir = scenario_base.join("reducers");
    let negatives_dir = scenario_base.join("negatives");

    let mut all_scenarios: Vec<PathBuf> = Vec::new();

    // Brief tranche: all round-trips (23) + first 30 reducers + all negatives (46).
    let round_trips = collect_scenarios(&round_trips_dir);
    let all_reducers = collect_scenarios(&reducers_dir);
    let negatives = collect_scenarios(&negatives_dir);

    all_scenarios.extend(round_trips);
    if full {
        all_scenarios.extend(all_reducers);
    } else {
        all_scenarios.extend(all_reducers.into_iter().take(30));
    }
    all_scenarios.extend(negatives);

    let total = all_scenarios.len();
    let mut pass = 0usize;
    let mut fail = 0usize;
    let mut skip = 0usize;
    let mut error = 0usize;

    for (i, path) in all_scenarios.iter().enumerate() {
        let r = run_scenario(path, &host_script, verbose).await;
        match &r.status {
            ScenarioStatus::Pass => {
                pass += 1;
                if verbose {
                    println!("[{}/{}] PASS  {}", i + 1, total, r.id);
                } else {
                    print!(".");
                    if (i + 1) % 50 == 0 {
                        println!(" [{}/{}]", i + 1, total);
                    }
                }
            }
            ScenarioStatus::Skip => {
                skip += 1;
                if verbose {
                    println!("[{}/{}] SKIP  {}", i + 1, total, r.id);
                } else {
                    print!("s");
                    if (i + 1) % 50 == 0 {
                        println!(" [{}/{}]", i + 1, total);
                    }
                }
            }
            ScenarioStatus::Fail => {
                fail += 1;
                eprintln!("\n[{}/{}] FAIL  {}", i + 1, total, r.id);
                for f in &r.failures {
                    eprintln!("  ✗ {f}");
                }
            }
            ScenarioStatus::Error(msg) => {
                error += 1;
                eprintln!("\n[{}/{}] ERROR  {}: {msg}", i + 1, total, r.id);
            }
        }
        for w in &r.warnings {
            if verbose {
                eprintln!("  WARN  {w}");
            }
        }
    }

    if !verbose {
        println!();
    }

    println!();
    println!(
        "AHP Rust conformance: {}/{} passed  ({} skip, {} fail, {} error)",
        pass, total, skip, fail, error
    );

    if fail > 0 || error > 0 {
        std::process::exit(1);
    }
}
