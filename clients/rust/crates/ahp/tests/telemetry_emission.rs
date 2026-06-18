//! Integration test: prove the client's self-instrumentation metrics actually
//! *emit* — not merely that their name constants match the contract.
//!
//! Installs an in-process [`metrics_util::debugging::DebuggingRecorder`] as a
//! thread-local recorder, drives real requests through the in-memory transport
//! pair (the same `MemTransport` shape as `client_roundtrip.rs`), and asserts
//! on the captured snapshot:
//!
//! * the metric **names** fire (`ahp.client.messages.sent`,
//!   `ahp.client.request.duration`),
//! * the in-flight gauge (`ahp.client.requests.in_flight`) goes `+1` while a
//!   request is pending and back to `0` once it settles, and
//! * the attribute keys/values are present
//!   (`rpc.method`, `ahp.outcome=ok`, `ahp.message.kind=request`).
//!
//! The recorder is installed thread-locally for the test's duration
//! ([`metrics::set_default_local_recorder`]) and the tests run on a
//! single-threaded Tokio runtime, so the client's spawned reader task and every
//! `.await` point stay on the recording thread — the documented
//! single-threaded-runtime use case for thread-local recorders.

use ahp::{Client, ClientConfig, Transport, TransportError, TransportMessage};
use ahp_types::messages::{JsonRpcMessage, JsonRpcSuccessResponse, JsonRpcVersion};
use ahp_types::telemetry as names;
use metrics_util::debugging::{DebugValue, DebuggingRecorder, Snapshotter};
use metrics_util::MetricKind;
use std::time::Duration;
use tokio::sync::mpsc;

// ─── In-memory transport (mirrors client_roundtrip.rs) ───────────────────────

struct MemTransport {
    tx: mpsc::Sender<TransportMessage>,
    rx: mpsc::Receiver<TransportMessage>,
}

fn pair() -> (MemTransport, MemTransport) {
    let (a_tx, b_rx) = mpsc::channel(16);
    let (b_tx, a_rx) = mpsc::channel(16);
    (
        MemTransport { tx: a_tx, rx: a_rx },
        MemTransport { tx: b_tx, rx: b_rx },
    )
}

impl Transport for MemTransport {
    async fn send(&mut self, msg: TransportMessage) -> Result<(), TransportError> {
        self.tx.send(msg).await.map_err(|_| TransportError::Closed)
    }

    async fn recv(&mut self) -> Result<Option<TransportMessage>, TransportError> {
        Ok(self.rx.recv().await)
    }
}

// ─── Snapshot helpers ────────────────────────────────────────────────────────
//
// NOTE: `Snapshotter::snapshot()` is DESTRUCTIVE — it swaps counters/gauges to
// 0 and drains histograms on each call. So every test takes a *single*
// snapshot at a coordinated point and reads all metrics out of that one
// `Vec`, never re-snapshotting.

type Row = (MetricKind, metrics::Key, DebugValue);

/// Snapshot the recorder once and return `(kind, key, value)` rows. Destructive
/// per `metrics-util`: drains counters/gauges/histograms.
fn drain(snapshotter: &Snapshotter) -> Vec<Row> {
    snapshotter
        .snapshot()
        .into_vec()
        .into_iter()
        .map(|(composite, _u, _d, value)| {
            let (kind, key) = composite.into_parts();
            (kind, key, value)
        })
        .collect()
}

/// Borrow the first row in an already-taken snapshot matching `kind` + `name`
/// (`DebugValue` is not `Clone`, so callers work with references).
fn pick<'a>(
    rows: &'a [Row],
    kind: MetricKind,
    name: &str,
) -> Option<(&'a metrics::Key, &'a DebugValue)> {
    rows.iter()
        .find(|(k, key, _v)| *k == kind && key.name() == name)
        .map(|(_k, key, value)| (key, value))
}

/// Read the gauge value for `name` from an already-taken snapshot, or `0.0` if
/// absent (gauge untouched this interval).
fn gauge_value(rows: &[Row], name: &str) -> f64 {
    match pick(rows, MetricKind::Gauge, name) {
        Some((_key, DebugValue::Gauge(g))) => g.into_inner(),
        _ => 0.0,
    }
}

/// Assert that `key`'s labels contain `(label_key, label_value)`.
fn assert_label(key: &metrics::Key, label_key: &str, label_value: &str) {
    let found = key
        .labels()
        .any(|l| l.key() == label_key && l.value() == label_value);
    assert!(
        found,
        "expected label {label_key}={label_value} on {}; labels were [{}]",
        key.name(),
        key.labels()
            .map(|l| format!("{}={}", l.key(), l.value()))
            .collect::<Vec<_>>()
            .join(", ")
    );
}

fn init_result() -> serde_json::Value {
    serde_json::json!({
        "protocolVersion": "0.1.0",
        "serverSeq": 0,
        "snapshots": [],
    })
}

// ─── Tests ───────────────────────────────────────────────────────────────────

#[tokio::test(flavor = "current_thread")]
async fn emits_request_metrics() {
    let recorder = DebuggingRecorder::new();
    let snapshotter = recorder.snapshotter();
    let guard = metrics::set_default_local_recorder(&recorder);
    let _ = &guard; // dropped at end of scope, restoring the prior recorder

    let (client_side, mut server_side) = pair();
    let client = Client::connect(client_side, ClientConfig::default())
        .await
        .expect("connect");

    // Deterministic coordination so we can snapshot the in-flight gauge while
    // the request is genuinely pending (no polling — `snapshot()` is
    // destructive, so we take exactly two snapshots at known points):
    //   * `received_tx`: server signals it has READ the request → the client's
    //     `request()` has already incremented the in-flight gauge and emitted
    //     `messages.sent`, but has not yet received a response.
    //   * `release_rx`: test tells the server to send its (held) response.
    // The server task records no client metrics, so its thread-local recorder
    // scope is irrelevant — only the request future (driven inline below)
    // records through `recorder`.
    let (received_tx, received_rx) = tokio::sync::oneshot::channel::<()>();
    let (release_tx, release_rx) = tokio::sync::oneshot::channel::<()>();

    let server = tokio::spawn(async move {
        let msg = server_side.recv().await.unwrap().unwrap();
        let JsonRpcMessage::Request(req) = msg.into_parsed().unwrap() else {
            panic!("expected a Request");
        };
        assert_eq!(req.method, "initialize");
        received_tx.send(()).unwrap();
        release_rx.await.ok();
        let resp = JsonRpcMessage::SuccessResponse(JsonRpcSuccessResponse {
            jsonrpc: JsonRpcVersion::V2,
            id: req.id,
            result: ahp_types::common::AnyValue::from(init_result()),
        });
        server_side
            .send(TransportMessage::encode(&resp).unwrap())
            .await
            .unwrap();
    });

    // Drive the request future inline (on THIS task, sharing its thread-local
    // recorder scope) concurrently with a coordinator that snapshots mid-flight.
    let request_fut = client.initialize("test-client".into(), vec!["0.1.0".into()], vec![]);
    let coordinator = async {
        // Wait until the server has read the request → it is in flight.
        received_rx.await.unwrap();
        // MID-FLIGHT SNAPSHOT (single, destructive): gauge should be +1 and
        // `messages.sent` should already carry the request attributes.
        let mid = drain(&snapshotter);
        release_tx.send(()).unwrap();
        mid
    };

    let (init, mid) = tokio::join!(
        async { request_fut.await.expect("initialize") },
        coordinator
    );
    assert_eq!(init.protocol_version, "0.1.0");
    server.await.unwrap();

    // ── Assert the MID-FLIGHT snapshot ──
    let mid_in_flight = gauge_value(&mid, names::REQUESTS_IN_FLIGHT);
    assert_eq!(
        mid_in_flight, 1.0,
        "in-flight gauge should read +1 while the request is pending"
    );

    // The `messages.sent` counter fired for the request, with the request kind,
    // rpc system, and method attributes.
    let (sent_key, sent_val) = pick(&mid, MetricKind::Counter, names::MESSAGES_SENT)
        .expect("messages.sent counter should have emitted");
    assert!(
        matches!(sent_val, DebugValue::Counter(n) if *n >= 1),
        "messages.sent should have incremented",
    );
    assert_label(sent_key, names::ATTR_RPC_METHOD, "initialize");
    assert_label(
        sent_key,
        names::ATTR_MESSAGE_KIND,
        names::MESSAGE_KIND_REQUEST,
    );
    assert_label(sent_key, names::ATTR_RPC_SYSTEM, names::RPC_SYSTEM_JSONRPC);

    // ── Assert the FINAL snapshot (after the request settled) ──
    let fin = drain(&snapshotter);

    // The mid-flight snapshot consumed (reset) the gauge atomic to 0, so the
    // settle's `decrement(1.0)` leaves it at -1.0 here. mid + final == 0 proves
    // the increment and decrement balanced — i.e. the gauge went +1 then back
    // down by exactly 1.
    let final_in_flight = gauge_value(&fin, names::REQUESTS_IN_FLIGHT);
    assert_eq!(
        final_in_flight, -1.0,
        "settle should decrement the gauge by 1 (consumed-snapshot accounting)"
    );
    assert_eq!(
        mid_in_flight + final_in_flight,
        0.0,
        "in-flight gauge increment and decrement should balance to 0"
    );

    // The `request.duration` histogram fired with the OK outcome + method.
    let (dur_key, dur_val) = pick(&fin, MetricKind::Histogram, names::REQUEST_DURATION)
        .expect("request.duration histogram should have emitted");
    assert!(
        matches!(dur_val, DebugValue::Histogram(samples) if !samples.is_empty()),
        "request.duration should have recorded at least one sample",
    );
    assert_label(dur_key, names::ATTR_RPC_METHOD, "initialize");
    assert_label(dur_key, names::ATTR_OUTCOME, names::OUTCOME_OK);

    client.shutdown().await;
}

#[tokio::test(flavor = "current_thread")]
async fn cancelled_request_tags_outcome_cancelled() {
    let recorder = DebuggingRecorder::new();
    let snapshotter = recorder.snapshotter();
    let guard = metrics::set_default_local_recorder(&recorder);
    let _ = &guard;

    let (client_side, mut server_side) = pair();
    // Generous default timeout: we want the *caller* to cancel, not the
    // in-client deadline to fire (which would be a `timeout`, not a
    // `cancelled`, outcome).
    let config = ClientConfig {
        default_request_timeout: Some(Duration::from_secs(30)),
        ..ClientConfig::default()
    };
    let client = Client::connect(client_side, config).await.expect("connect");

    // Server reads the request but never replies, so it stays in flight until
    // the caller drops the future.
    let server = tokio::spawn(async move {
        let msg = server_side.recv().await.unwrap().unwrap();
        let JsonRpcMessage::Request(req) = msg.into_parsed().unwrap() else {
            panic!("expected a Request");
        };
        assert_eq!(req.method, "initialize");
        std::future::pending::<()>().await; // hold open; never respond
    });

    // Cancel the request by dropping its future via a caller-side timeout —
    // the codebase's established cancellation idiom (see tests/hosts.rs).
    let cancelled = tokio::time::timeout(
        Duration::from_millis(150),
        client.initialize("test-client".into(), vec!["0.1.0".into()], vec![]),
    )
    .await;
    assert!(
        cancelled.is_err(),
        "the request future should have been cancelled by the caller-side timeout",
    );

    // Single snapshot after the cancellation; read both metrics from it.
    let snap = drain(&snapshotter);

    // The duration metric must have been recorded with the CANCELLED outcome
    // (not timeout, not ok) — proves the drop-guard cancellation path.
    let (dur_key, dur_val) = pick(&snap, MetricKind::Histogram, names::REQUEST_DURATION)
        .expect("request.duration should emit even on cancellation");
    assert!(
        matches!(dur_val, DebugValue::Histogram(samples) if !samples.is_empty()),
        "request.duration should have recorded a sample on cancellation",
    );
    assert_label(dur_key, names::ATTR_OUTCOME, names::OUTCOME_CANCELLED);
    // Falsifiability: a regression to the old `OUTCOME_TIMEOUT` mapping would
    // put a `timeout` label here, failing this assert.
    assert!(
        dur_key
            .labels()
            .all(|l| !(l.key() == names::ATTR_OUTCOME && l.value() == names::OUTCOME_TIMEOUT)),
        "a cancelled request must not be tagged ahp.outcome=timeout",
    );

    // The in-flight gauge must balance back to 0 after cancellation: the
    // drop-guard's decrement(1.0) cancels the started increment(1.0). A leak
    // (no drop-guard) would read +1 here.
    assert_eq!(
        gauge_value(&snap, names::REQUESTS_IN_FLIGHT),
        0.0,
        "in-flight gauge should return to 0 after a cancelled request",
    );

    server.abort();
    client.shutdown().await;
}
