//! Integration test: round-trip a JSON-RPC request and a broadcast
//! action through an in-memory transport pair.
//!
//! Exercises the full client state machine end-to-end: request/response
//! correlation, subscription fan-out, and dispatch notification routing.

use ahp::{Client, ClientConfig, SubscriptionEvent, Transport, TransportError, TransportMessage};
use ahp_types::actions::{ActionEnvelope, SessionTitleChangedAction, StateAction};
use ahp_types::messages::{
    ActionNotificationParams, JsonRpcMessage, JsonRpcNotification, JsonRpcSuccessResponse,
    JsonRpcVersion,
};
use tokio::sync::mpsc;

/// A bidirectional in-memory transport pair. Each half owns one sender
/// and one receiver; sends on one side are received on the other.
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

#[tokio::test]
async fn request_response_and_action_fanout() {
    let (client_side, mut server_side) = pair();
    let client = Client::connect(client_side, ClientConfig::default())
        .await
        .expect("connect");

    // Server task: respond to an `initialize` request, then emit an
    // `action` notification targeting a session URI the client subscribes to.
    let server = tokio::spawn(async move {
        // Read initialize request.
        let msg = server_side.recv().await.unwrap().unwrap();
        let parsed = msg.into_parsed().unwrap();
        let JsonRpcMessage::Request(req) = parsed else {
            panic!("expected Request")
        };
        assert_eq!(req.method, "initialize");

        // Reply with a minimal InitializeResult.
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

        // Read subscribe request.
        let msg = server_side.recv().await.unwrap().unwrap();
        let parsed = msg.into_parsed().unwrap();
        let JsonRpcMessage::Request(req) = parsed else {
            panic!("expected Request")
        };
        assert_eq!(req.method, "subscribe");

        let sub_result = serde_json::json!({
            "snapshot": {
                "resource": "copilot:/s1",
                "state": { "agents": [] },
                "fromSeq": 0
            }
        });
        let resp = JsonRpcMessage::SuccessResponse(JsonRpcSuccessResponse {
            jsonrpc: JsonRpcVersion::V2,
            id: req.id,
            result: ahp_types::common::AnyValue::from(sub_result),
        });
        server_side
            .send(TransportMessage::encode(&resp).unwrap())
            .await
            .unwrap();

        // Fan out an action envelope for the subscribed session.
        let envelope = ActionEnvelope {
            action: StateAction::SessionTitleChanged(SessionTitleChangedAction {
                session: "copilot:/s1".into(),
                title: "Hello".into(),
            }),
            server_seq: 1,
            origin: None,
            rejection_reason: None,
        };
        let notif = JsonRpcMessage::Notification(JsonRpcNotification {
            jsonrpc: JsonRpcVersion::V2,
            method: "action".into(),
            params: Some(ahp_types::common::AnyValue::from(
                serde_json::to_value(ActionNotificationParams::from(envelope)).unwrap(),
            )),
        });
        server_side
            .send(TransportMessage::encode(&notif).unwrap())
            .await
            .unwrap();
    });

    // Initialize handshake.
    let init = client
        .initialize("test-client".into(), vec!["0.1.0".into()], vec![])
        .await
        .expect("initialize");
    assert_eq!(init.protocol_version, "0.1.0");

    // Subscribe and await the action broadcast.
    let (_snap, mut sub) = client
        .subscribe("copilot:/s1".into())
        .await
        .expect("subscribe");

    let event = tokio::time::timeout(std::time::Duration::from_secs(2), sub.recv())
        .await
        .expect("timed out")
        .expect("channel closed");

    match event {
        SubscriptionEvent::Action(env) => {
            assert_eq!(env.server_seq, 1);
            match env.action {
                StateAction::SessionTitleChanged(a) => {
                    assert_eq!(a.title, "Hello");
                    assert_eq!(a.session, "copilot:/s1");
                }
                other => panic!("unexpected action: {:?}", other),
            }
        }
        SubscriptionEvent::Notification(_) => panic!("expected an Action event"),
    }

    client.shutdown().await;
    server.await.unwrap();
}

/// Cancelling a request future via `select!` against another branch
/// must drop the pending entry immediately rather than leaking it
/// until (or beyond) the server response. The RAII `PendingGuard` on
/// `Client::request` is what enforces that — without it, the pending
/// map would grow on every cancelled typeahead / debounce request.
#[tokio::test]
async fn cancelled_request_future_clears_pending_entry() {
    let (client_side, mut server_side) = pair();
    let client = Client::connect(client_side, ClientConfig::default())
        .await
        .expect("connect");

    // Server drains messages but never replies — so the only way
    // for the request future to resolve is the caller cancelling it.
    let drain = tokio::spawn(async move {
        loop {
            match server_side.recv().await {
                Ok(Some(_)) => continue,
                Ok(None) | Err(_) => return,
            }
        }
    });

    // Spawn the request on a task we can abort.
    let client_clone = client.clone();
    let request_task = tokio::spawn(async move {
        let _: Result<serde_json::Value, _> = client_clone
            .request("never-replied", serde_json::Value::Null)
            .await;
    });

    // Give the client time to push the outbound message + insert the
    // pending entry, then verify the entry is there.
    tokio::time::sleep(std::time::Duration::from_millis(50)).await;
    assert_eq!(
        client.pending_request_count(),
        1,
        "request should have inserted a pending entry"
    );

    // Cancel the request by aborting the task. The guard's Drop should
    // remove the pending entry deterministically; without the guard
    // the entry would linger until the server eventually replied (it
    // never will) or until `Client::shutdown`.
    request_task.abort();
    let _ = request_task.await;

    // Allow the abort + drop glue to run.
    tokio::time::sleep(std::time::Duration::from_millis(50)).await;
    assert_eq!(
        client.pending_request_count(),
        0,
        "cancelled request must clean up its pending entry"
    );

    client.shutdown().await;
    let _ = drain.await;
}
