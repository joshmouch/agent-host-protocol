//! Minimal example: install a metrics recorder and observe the AHP client's
//! own self-instrumentation.
//!
//! The `ahp` client emits its self-instrumentation through the [`metrics`]
//! facade (counters, gauges, histograms named by `ahp_types::telemetry`). The
//! facade is a no-op until a *consumer* installs a recorder — at which point
//! the client's metrics flow into whatever backend the consumer wires up.
//!
//! In production a consumer would install an OpenTelemetry-backed recorder
//! (e.g. `metrics-exporter-opentelemetry`, or a Prometheus exporter) so the
//! `ahp.client.*` metrics land in their existing telemetry pipeline. This
//! example keeps the dependency surface tiny by installing the in-process
//! [`metrics_util::debugging::DebuggingRecorder`] and printing the captured
//! values — the same observation point an OTel exporter would tap, minus the
//! network.
//!
//! Run with:
//!
//! ```sh
//! cargo run --example otel_export
//! ```

use ahp::{Client, ClientConfig, Transport, TransportError, TransportMessage};
use ahp_types::messages::{JsonRpcMessage, JsonRpcSuccessResponse, JsonRpcVersion};
use metrics_util::debugging::{DebugValue, DebuggingRecorder};
use metrics_util::MetricKind;
use tokio::sync::mpsc;

/// A bidirectional in-memory transport pair (same shape the tests use), so the
/// example is self-contained and needs no running server.
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

#[tokio::main(flavor = "current_thread")]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    // 1. A consumer installs a recorder. Swap `DebuggingRecorder` for an
    //    OpenTelemetry/Prometheus exporter recorder in a real deployment.
    let recorder = DebuggingRecorder::new();
    let snapshotter = recorder.snapshotter();
    let _guard = metrics::set_default_local_recorder(&recorder);

    // 2. Drive one real request through the client so it emits metrics.
    let (client_side, mut server_side) = pair();
    let client = Client::connect(client_side, ClientConfig::default()).await?;

    let server = tokio::spawn(async move {
        let msg = server_side.recv().await.unwrap().unwrap();
        let JsonRpcMessage::Request(req) = msg.into_parsed().unwrap() else {
            panic!("expected a Request");
        };
        let result = serde_json::json!({
            "protocolVersion": "0.1.0",
            "serverSeq": 0,
            "snapshots": [],
        });
        let resp = JsonRpcMessage::SuccessResponse(JsonRpcSuccessResponse {
            jsonrpc: JsonRpcVersion::V2,
            id: req.id,
            result: ahp_types::common::AnyValue::from(result),
        });
        server_side
            .send(TransportMessage::encode(&resp).unwrap())
            .await
            .unwrap();
    });

    let init = client
        .initialize("otel-example".into(), vec!["0.1.0".into()], vec![])
        .await?;
    println!("connected (protocolVersion={})", init.protocol_version);
    server.await?;
    client.shutdown().await;

    // 3. Observe what the client reported — exactly the data an OTel exporter
    //    would forward to a collector.
    println!("\n--- ahp.client.* metrics observed by the consumer ---");
    for (composite, _unit, _desc, value) in snapshotter.snapshot().into_vec() {
        let kind = match composite.kind() {
            MetricKind::Counter => "counter",
            MetricKind::Gauge => "gauge",
            MetricKind::Histogram => "histogram",
        };
        let key = composite.key();
        let labels: Vec<String> = key
            .labels()
            .map(|l| format!("{}={}", l.key(), l.value()))
            .collect();
        let rendered = match value {
            DebugValue::Counter(n) => format!("{n}"),
            DebugValue::Gauge(g) => format!("{}", g.into_inner()),
            DebugValue::Histogram(samples) => {
                format!("{} sample(s)", samples.len())
            }
        };
        println!(
            "{kind:9} {:40} {{{}}} = {rendered}",
            key.name(),
            labels.join(", ")
        );
    }

    Ok(())
}
