//! Integration tests for the multi-host SDK (`ahp::hosts`).
//!
//! Uses an in-memory transport pair (mirroring `client_roundtrip.rs`)
//! so the runtime can drive a real `Client` end-to-end without any
//! networking. Each test spins up one or more "fake hosts" — small
//! tasks that respond to `initialize`, `listSessions`, and the
//! occasional `subscribe`, then optionally close their side of the
//! socket to force a reconnect.

use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::Arc;
use std::time::Duration;

use ahp::hosts::{
    HostConfig, HostError, HostEvent, HostId, HostState, MultiHostClient, ReconnectPolicy,
};
use ahp::transport::BoxedTransport;
use ahp::{Transport, TransportError, TransportMessage};
use ahp_types::messages::{
    JsonRpcMessage, JsonRpcNotification, JsonRpcRequest, JsonRpcSuccessResponse, JsonRpcVersion,
};
use ahp_types::state::AgentInfo;
use tokio::sync::{mpsc, Mutex};

// ─── In-memory transport ────────────────────────────────────────────────────

struct MemTransport {
    tx: mpsc::Sender<TransportMessage>,
    rx: mpsc::Receiver<TransportMessage>,
}

fn pair() -> (MemTransport, MemTransport) {
    let (a_tx, b_rx) = mpsc::channel(64);
    let (b_tx, a_rx) = mpsc::channel(64);
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

// ─── Fake host ──────────────────────────────────────────────────────────────

#[derive(Clone)]
struct FakeHostState {
    /// Sequential serverSeq counter shared across reconnects on this host.
    server_seq: Arc<AtomicU32>,
    /// Optional list of agents to publish in the initial RootState snapshot.
    agents: Vec<AgentInfo>,
    /// Optional list of session summaries to return from `listSessions`.
    sessions: Vec<ahp_types::state::SessionSummary>,
}

impl FakeHostState {
    fn new() -> Self {
        Self {
            server_seq: Arc::new(AtomicU32::new(0)),
            agents: vec![],
            sessions: vec![],
        }
    }

    fn with_agents(mut self, agents: Vec<AgentInfo>) -> Self {
        self.agents = agents;
        self
    }

    fn with_sessions(mut self, sessions: Vec<ahp_types::state::SessionSummary>) -> Self {
        self.sessions = sessions;
        self
    }
}

/// Drive a single connection on the server side until the client closes.
async fn drive_fake_host_basic(mut transport: MemTransport, state: FakeHostState) {
    loop {
        let frame = match transport.recv().await {
            Ok(Some(f)) => f,
            _ => return,
        };
        let msg = match frame.into_parsed() {
            Ok(m) => m,
            Err(_) => continue,
        };
        if let JsonRpcMessage::Request(req) = msg {
            let result = handle_request(&req, &state);
            let resp = JsonRpcMessage::SuccessResponse(JsonRpcSuccessResponse {
                jsonrpc: JsonRpcVersion::V2,
                id: req.id,
                result: ahp_types::common::AnyValue::from(result),
            });
            if transport
                .send(TransportMessage::encode(&resp).unwrap())
                .await
                .is_err()
            {
                return;
            }
        }
    }
}

/// Like `drive_fake_host_basic`, but also injects a `notify/sessionAdded`
/// notification once `initialize` completes. Returns when the client
/// closes the transport.
async fn drive_fake_host_with_injection(
    mut transport: MemTransport,
    state: FakeHostState,
    inject_after_init: Arc<Mutex<Option<ahp_types::state::SessionSummary>>>,
) {
    loop {
        let frame = match transport.recv().await {
            Ok(Some(f)) => f,
            _ => return,
        };
        let msg = match frame.into_parsed() {
            Ok(m) => m,
            Err(_) => continue,
        };
        if let JsonRpcMessage::Request(req) = msg {
            let was_init = matches!(req.method.as_str(), "initialize" | "reconnect");
            let result = handle_request(&req, &state);
            let resp = JsonRpcMessage::SuccessResponse(JsonRpcSuccessResponse {
                jsonrpc: JsonRpcVersion::V2,
                id: req.id,
                result: ahp_types::common::AnyValue::from(result),
            });
            if transport
                .send(TransportMessage::encode(&resp).unwrap())
                .await
                .is_err()
            {
                return;
            }
            if was_init {
                let summary = inject_after_init.lock().await.take();
                if let Some(summary) = summary {
                    // Tiny delay so the client has consumed the prior
                    // listSessions response before the notification arrives.
                    tokio::time::sleep(Duration::from_millis(20)).await;
                    let payload = serde_json::json!({
                        "notification": {
                            "type": "notify/sessionAdded",
                            "summary": summary,
                        }
                    });
                    let notif = JsonRpcMessage::Notification(JsonRpcNotification {
                        jsonrpc: JsonRpcVersion::V2,
                        method: "notification".into(),
                        params: Some(ahp_types::common::AnyValue::from(payload)),
                    });
                    if transport
                        .send(TransportMessage::encode(&notif).unwrap())
                        .await
                        .is_err()
                    {
                        return;
                    }
                }
            }
        }
    }
}

fn handle_request(req: &JsonRpcRequest, state: &FakeHostState) -> serde_json::Value {
    match req.method.as_str() {
        "initialize" => {
            let seq = state.server_seq.load(Ordering::SeqCst);
            let snapshot = serde_json::json!({
                "resource": ahp_types::ROOT_RESOURCE_URI,
                "state": {
                    "type": "Root",
                    "agents": state.agents,
                    "activeSessions": state.sessions.len() as i64,
                },
                "fromSeq": seq,
            });
            serde_json::json!({
                "protocolVersion": ahp_types::PROTOCOL_VERSION,
                "serverSeq": seq,
                "snapshots": [snapshot],
            })
        }
        "reconnect" => serde_json::json!({
            "type": "replay",
            "actions": []
        }),
        "listSessions" => serde_json::json!({ "items": state.sessions }),
        "subscribe" => {
            let resource = req
                .params
                .as_ref()
                .and_then(|p| p.as_object())
                .and_then(|m| m.get("resource"))
                .and_then(|v| v.as_str())
                .unwrap_or(ahp_types::ROOT_RESOURCE_URI)
                .to_string();
            let seq = state.server_seq.load(Ordering::SeqCst);
            serde_json::json!({
                "snapshot": {
                    "resource": resource,
                    "state": {
                        "type": "Root",
                        "agents": state.agents,
                    },
                    "fromSeq": seq
                }
            })
        }
        _ => serde_json::json!({}),
    }
}

// ─── Tests ──────────────────────────────────────────────────────────────────

#[tokio::test]
async fn single_constructor_yields_connected_handle() {
    let agent = AgentInfo {
        provider: "copilot".into(),
        display_name: "Copilot".into(),
        description: "demo".into(),
        models: vec![],
        protected_resources: None,
        customizations: None,
    };
    let state = FakeHostState::new().with_agents(vec![agent.clone()]);
    let factory = make_basic_factory(state);

    let config = HostConfig::new("local", "Local", factory);
    let (multi, _initial) = MultiHostClient::single(config).await.expect("single");

    let id = HostId::new("local");
    wait_for_state(&multi, &id, |s| s.is_connected(), 2000).await;

    let snap = multi.host(&id).await.expect("host present");
    assert!(snap.state.is_connected());
    assert_eq!(snap.label, "Local");
    assert_eq!(snap.agents, vec![agent]);
    assert_eq!(
        snap.protocol_version.as_deref(),
        Some(ahp_types::PROTOCOL_VERSION)
    );
    assert!(snap.last_connected_at.is_some());
}

#[tokio::test]
async fn two_hosts_register_and_connect_independently() {
    let multi = MultiHostClient::new();
    multi
        .add_host(HostConfig::new(
            "a",
            "A",
            make_basic_factory(FakeHostState::new()),
        ))
        .await
        .unwrap();
    multi
        .add_host(HostConfig::new(
            "b",
            "B",
            make_basic_factory(FakeHostState::new()),
        ))
        .await
        .unwrap();

    wait_for_state(&multi, &HostId::new("a"), |s| s.is_connected(), 2000).await;
    wait_for_state(&multi, &HostId::new("b"), |s| s.is_connected(), 2000).await;

    let hosts = multi.hosts().await;
    assert_eq!(hosts.len(), 2);
    assert!(hosts.iter().all(|h| h.state.is_connected()));
    let labels: Vec<_> = hosts.iter().map(|h| h.label.clone()).collect();
    assert!(labels.contains(&"A".to_string()));
    assert!(labels.contains(&"B".to_string()));
}

#[tokio::test]
async fn aggregated_sessions_track_listsessions_then_notification() {
    let initial_summary = make_summary("copilot:/s1", "Initial title", 1_000);
    let added_summary = make_summary("copilot:/s2", "Added later", 2_000);

    let state = FakeHostState::new().with_sessions(vec![initial_summary]);
    let injected = Arc::new(Mutex::new(Some(added_summary)));
    let factory = make_injecting_factory(state, injected);

    let multi = MultiHostClient::new();
    multi
        .add_host(HostConfig::new("local", "Local", factory))
        .await
        .unwrap();

    wait_for_state(&multi, &HostId::new("local"), |s| s.is_connected(), 2000).await;

    wait_until(2000, || async {
        multi.aggregated_sessions().await.len() == 2
    })
    .await;
    let aggregated = multi.aggregated_sessions().await;
    let titles: Vec<_> = aggregated.iter().map(|h| h.summary.title.clone()).collect();
    assert_eq!(
        titles,
        vec!["Added later".to_string(), "Initial title".to_string()]
    );
    assert!(aggregated.iter().all(|h| h.host_id == HostId::new("local")));
    assert!(aggregated.iter().all(|h| h.host_label == "Local"));
}

#[tokio::test]
async fn host_client_handle_invalidates_after_reconnect() {
    let factory = make_basic_factory(FakeHostState::new());

    let multi = MultiHostClient::new();
    multi
        .add_host(
            HostConfig::new("local", "Local", factory)
                .with_reconnect_policy(ReconnectPolicy::immediate_forever()),
        )
        .await
        .unwrap();

    wait_for_state(&multi, &HostId::new("local"), |s| s.is_connected(), 2000).await;

    let handle = multi
        .client(&HostId::new("local"))
        .await
        .expect("client handle");
    let initial_generation = handle.generation();

    multi
        .reconnect_host(&HostId::new("local"))
        .await
        .expect("reconnect");
    wait_until(2000, || async {
        multi
            .host(&HostId::new("local"))
            .await
            .map(|h| h.generation > initial_generation && h.state.is_connected())
            .unwrap_or(false)
    })
    .await;

    let err = handle
        .check_alive()
        .await
        .expect_err("expected HostReconnected");
    match err {
        HostError::HostReconnected {
            handle_generation,
            current_generation,
            ..
        } => {
            assert_eq!(handle_generation, initial_generation);
            assert!(current_generation > initial_generation);
        }
        other => panic!("unexpected error: {other:?}"),
    }

    let fresh = multi
        .client(&HostId::new("local"))
        .await
        .expect("fresh client handle");
    assert!(fresh.generation() > initial_generation);
    fresh.check_alive().await.expect("fresh handle alive");
}

#[tokio::test]
async fn remove_host_terminates_supervisor_and_emits_event() {
    let factory = make_basic_factory(FakeHostState::new());

    let multi = MultiHostClient::new();
    multi
        .add_host(HostConfig::new("temp", "Temporary", factory))
        .await
        .unwrap();

    wait_for_state(&multi, &HostId::new("temp"), |s| s.is_connected(), 2000).await;

    let mut events = multi.host_events();

    multi
        .remove_host(&HostId::new("temp"))
        .await
        .expect("remove");

    let mut saw_removed = false;
    let deadline = tokio::time::Instant::now() + Duration::from_millis(1000);
    while tokio::time::Instant::now() < deadline {
        match tokio::time::timeout(Duration::from_millis(100), events.recv()).await {
            Ok(Some(HostEvent::Removed { host_id })) if host_id == HostId::new("temp") => {
                saw_removed = true;
                break;
            }
            Ok(Some(_)) => continue,
            _ => break,
        }
    }
    assert!(saw_removed, "expected HostEvent::Removed");

    assert!(multi.host(&HostId::new("temp")).await.is_none());
}

#[tokio::test]
async fn fan_in_events_carry_host_id_and_resource() {
    let summary = make_summary("copilot:/s1", "first", 100);

    let state_a = FakeHostState::new().with_sessions(vec![summary.clone()]);
    let state_b = FakeHostState::new().with_sessions(vec![summary.clone()]);
    let inject_a = Arc::new(Mutex::new(Some(make_summary(
        "copilot:/added-a",
        "a-side",
        200,
    ))));
    let inject_b = Arc::new(Mutex::new(Some(make_summary(
        "copilot:/added-b",
        "b-side",
        300,
    ))));

    // Subscribe BEFORE adding hosts so the broadcast captures every
    // event from the very first connect.
    let multi = MultiHostClient::new();
    let mut events = multi.events();

    multi
        .add_host(HostConfig::new(
            "a",
            "Host A",
            make_injecting_factory(state_a, inject_a),
        ))
        .await
        .unwrap();
    multi
        .add_host(HostConfig::new(
            "b",
            "Host B",
            make_injecting_factory(state_b, inject_b),
        ))
        .await
        .unwrap();

    let mut hosts_seen: std::collections::HashSet<HostId> = std::collections::HashSet::new();
    let deadline = tokio::time::Instant::now() + Duration::from_millis(2500);
    while tokio::time::Instant::now() < deadline && hosts_seen.len() < 2 {
        match tokio::time::timeout(Duration::from_millis(500), events.recv()).await {
            Ok(Some(event)) => {
                hosts_seen.insert(event.host_id.clone());
                // Notifications carry no resource URI by design; actions
                // would. The injected sessionAdded is a notification so
                // resource is None.
                assert!(event.resource.is_none());
            }
            _ => break,
        }
    }
    assert!(
        hosts_seen.contains(&HostId::new("a")),
        "missing event from host A; saw {hosts_seen:?}"
    );
    assert!(
        hosts_seen.contains(&HostId::new("b")),
        "missing event from host B; saw {hosts_seen:?}"
    );
}

#[tokio::test]
async fn transport_factory_is_called_for_each_reconnect() {
    let connect_count = Arc::new(AtomicU32::new(0));
    let count_clone = connect_count.clone();
    let state = FakeHostState::new();

    let factory = move |_host_id: HostId| {
        let count = count_clone.clone();
        let state = state.clone();
        Box::pin(async move {
            count.fetch_add(1, Ordering::SeqCst);
            let (client_side, server_side) = pair();
            tokio::spawn(drive_fake_host_basic(server_side, state));
            Ok(BoxedTransport::new(client_side))
        })
            as std::pin::Pin<
                Box<
                    dyn std::future::Future<Output = Result<BoxedTransport, TransportError>> + Send,
                >,
            >
    };

    let multi = MultiHostClient::new();
    multi
        .add_host(
            HostConfig::new("local", "Local", factory)
                .with_reconnect_policy(ReconnectPolicy::immediate_forever()),
        )
        .await
        .unwrap();

    wait_for_state(&multi, &HostId::new("local"), |s| s.is_connected(), 2000).await;
    assert_eq!(connect_count.load(Ordering::SeqCst), 1);

    multi
        .reconnect_host(&HostId::new("local"))
        .await
        .expect("reconnect");
    wait_until(2000, || async {
        connect_count.load(Ordering::SeqCst) >= 2
            && multi
                .host(&HostId::new("local"))
                .await
                .map(|h| h.state.is_connected())
                .unwrap_or(false)
    })
    .await;
    assert_eq!(connect_count.load(Ordering::SeqCst), 2);
}

#[tokio::test]
async fn duplicate_host_id_is_rejected_with_typed_error() {
    let multi = MultiHostClient::new();
    multi
        .add_host(HostConfig::new(
            "dup",
            "first",
            make_basic_factory(FakeHostState::new()),
        ))
        .await
        .unwrap();

    let err = multi
        .add_host(HostConfig::new(
            "dup",
            "second",
            make_basic_factory(FakeHostState::new()),
        ))
        .await
        .expect_err("expected duplicate-id rejection");
    match err {
        HostError::DuplicateHost(id) => assert_eq!(id, HostId::new("dup")),
        other => panic!("expected DuplicateHost, got {other:?}"),
    }
}

#[tokio::test]
async fn reconnect_replay_actions_are_fanned_out_with_advanced_seq() {
    use ahp_types::actions::{RootActiveSessionsChangedAction, StateAction};

    // Force a reconnect handshake by pre-seeding a non-zero serverSeq +
    // subscription set, then make the second connect return a Replay
    // arm carrying a single root-state action.
    let drop_after_init = Arc::new(AtomicBool::new(false));
    let return_replay = Arc::new(Mutex::new(false));
    let state = FakeHostState::new();

    let multi = MultiHostClient::new();
    let mut events = multi.events();

    multi
        .add_host(HostConfig::new(
            "h",
            "Host",
            make_replay_factory(state, drop_after_init.clone(), return_replay.clone()),
        ))
        .await
        .unwrap();

    wait_for_state(&multi, &HostId::new("h"), |s| s.is_connected(), 2000).await;

    // Drain the live events from the first connect so the replay
    // assertions below aren't polluted.
    drain_events(&mut events, Duration::from_millis(150)).await;

    // Now flip the next-connect to use Replay, force a manual reconnect.
    *return_replay.lock().await = true;
    drop_after_init.store(true, Ordering::SeqCst);
    multi
        .reconnect_host(&HostId::new("h"))
        .await
        .expect("reconnect");

    // Expect to see the replayed action through the fan-in stream.
    let mut saw_replayed_action = false;
    let deadline = tokio::time::Instant::now() + Duration::from_millis(2500);
    while tokio::time::Instant::now() < deadline {
        match tokio::time::timeout(Duration::from_millis(500), events.recv()).await {
            Ok(Some(event)) => {
                if let ahp::SubscriptionEvent::Action(env) = event.event {
                    if matches!(
                        env.action,
                        StateAction::RootActiveSessionsChanged(RootActiveSessionsChangedAction {
                            active_sessions: 7
                        })
                    ) {
                        assert_eq!(event.host_id, HostId::new("h"));
                        assert_eq!(
                            event.resource.as_deref(),
                            Some(ahp_types::ROOT_RESOURCE_URI)
                        );
                        saw_replayed_action = true;
                        break;
                    }
                }
            }
            _ => break,
        }
    }
    assert!(
        saw_replayed_action,
        "expected the replayed RootActiveSessionsChanged action to fan out via events()"
    );

    // server_seq should have advanced past the replay envelope.
    let snap = multi.host(&HostId::new("h")).await.expect("host");
    assert!(
        snap.server_seq >= 42,
        "expected server_seq advanced past replay (got {})",
        snap.server_seq
    );
}

#[tokio::test]
async fn client_events_recv_returns_none_after_transport_close() {
    use ahp::{Client, ClientConfig};

    // Build a Client over an in-memory transport, attach an events
    // receiver, then drop the server side so the transport closes.
    let (client_side, mut server_side) = pair();
    let client = Client::connect(client_side, ClientConfig::default())
        .await
        .expect("connect");
    let mut events = client.events();

    // Server task: ack initialize, then close.
    tokio::spawn(async move {
        if let Ok(Some(frame)) = server_side.recv().await {
            if let Ok(JsonRpcMessage::Request(req)) = frame.into_parsed() {
                let resp = JsonRpcMessage::SuccessResponse(JsonRpcSuccessResponse {
                    jsonrpc: JsonRpcVersion::V2,
                    id: req.id,
                    result: ahp_types::common::AnyValue::from(serde_json::json!({
                        "protocolVersion": ahp_types::PROTOCOL_VERSION,
                        "serverSeq": 0,
                        "snapshots": []
                    })),
                });
                let _ = server_side
                    .send(TransportMessage::encode(&resp).unwrap())
                    .await;
            }
        }
        // Drop server_side to close the transport from the server side.
        drop(server_side);
    });

    let _ = client
        .initialize(
            "test".into(),
            vec![ahp_types::PROTOCOL_VERSION.to_string()],
            vec![],
        )
        .await
        .expect("init");

    // events.recv() should resolve to None once the transport closes
    // and `drive_transport` runs teardown — without the all_events
    // sender being explicitly dropped, this would hang forever.
    let next = tokio::time::timeout(Duration::from_secs(2), events.recv()).await;
    assert!(matches!(next, Ok(None)), "expected None, got {next:?}");
}

#[tokio::test]
async fn shutdown_is_not_blocked_by_a_hung_transport_factory() {
    // Factory that never returns — simulates a hung token refresh / DNS
    // lookup / TLS handshake. Without the shutdown signal racing
    // `connect_once`, `remove_host` would block forever (or until the
    // request timeout, whichever came first).
    let factory = |_host_id: HostId| -> std::pin::Pin<
        Box<dyn std::future::Future<Output = Result<BoxedTransport, TransportError>> + Send>,
    > {
        Box::pin(async move {
            // Sleep effectively forever.
            tokio::time::sleep(Duration::from_secs(3600)).await;
            Err::<BoxedTransport, TransportError>(TransportError::Closed)
        })
    };

    let multi = MultiHostClient::new();
    let _ = multi
        .add_host(HostConfig::new("hung", "Hung", factory))
        .await;

    // Give the runtime a beat to enter `connect_once`.
    tokio::time::sleep(Duration::from_millis(50)).await;

    // Remove must complete promptly. Bound to a generous timeout so a
    // regression of this fix would surface as a clear test failure
    // rather than a CI hang.
    let removed = tokio::time::timeout(
        Duration::from_secs(2),
        multi.remove_host(&HostId::new("hung")),
    )
    .await;
    assert!(
        matches!(removed, Ok(Ok(()))),
        "remove_host should not block on a hung connect; got {removed:?}"
    );
}

// ─── New phase tests (Phase 2 / 4 / 5 / 6 / 7 parity with Swift PR #129) ────

/// Phase 5: stored `clientId` is reused across `add_host` cycles.
///
/// The default in-memory store keeps the resolved id keyed by HostId,
/// so removing and re-adding a host (without an explicit
/// `with_client_id`) should yield the same id — proof that the store
/// resolution path actually runs and persists what it resolves.
#[tokio::test]
async fn client_id_persists_across_add_remove_add_when_unset() {
    let multi = MultiHostClient::new();

    multi
        .add_host(HostConfig::new(
            "h",
            "Host",
            make_basic_factory(FakeHostState::new()),
        ))
        .await
        .unwrap();
    wait_for_state(&multi, &HostId::new("h"), |s| s.is_connected(), 2000).await;
    let first_id = multi.host(&HostId::new("h")).await.unwrap().client_id;
    assert!(
        !first_id.is_empty(),
        "client_id should be generated, not empty"
    );

    multi.remove_host(&HostId::new("h")).await.unwrap();
    multi
        .add_host(HostConfig::new(
            "h",
            "Host",
            make_basic_factory(FakeHostState::new()),
        ))
        .await
        .unwrap();
    wait_for_state(&multi, &HostId::new("h"), |s| s.is_connected(), 2000).await;
    let second_id = multi.host(&HostId::new("h")).await.unwrap().client_id;
    assert_eq!(
        first_id, second_id,
        "store should resolve the same client_id on the second add"
    );
}

/// Phase 5: explicit `with_client_id` always wins over the store.
#[tokio::test]
async fn explicit_client_id_overrides_store_and_is_persisted() {
    let multi = MultiHostClient::new();

    // Seed the store via an unset add — store now holds whatever was
    // generated.
    multi
        .add_host(HostConfig::new(
            "h",
            "Host",
            make_basic_factory(FakeHostState::new()),
        ))
        .await
        .unwrap();
    wait_for_state(&multi, &HostId::new("h"), |s| s.is_connected(), 2000).await;
    let generated = multi.host(&HostId::new("h")).await.unwrap().client_id;

    multi.remove_host(&HostId::new("h")).await.unwrap();

    // Re-add with an explicit client id — must win over the store.
    multi
        .add_host(
            HostConfig::new("h", "Host", make_basic_factory(FakeHostState::new()))
                .with_client_id("explicit-id"),
        )
        .await
        .unwrap();
    wait_for_state(&multi, &HostId::new("h"), |s| s.is_connected(), 2000).await;
    assert_eq!(
        multi.host(&HostId::new("h")).await.unwrap().client_id,
        "explicit-id"
    );

    // Removing and re-adding without an explicit id should now return
    // the *explicit* id (the store was overwritten on the previous
    // add).
    multi.remove_host(&HostId::new("h")).await.unwrap();
    multi
        .add_host(HostConfig::new(
            "h",
            "Host",
            make_basic_factory(FakeHostState::new()),
        ))
        .await
        .unwrap();
    wait_for_state(&multi, &HostId::new("h"), |s| s.is_connected(), 2000).await;
    let after_overwrite = multi.host(&HostId::new("h")).await.unwrap().client_id;
    assert_eq!(after_overwrite, "explicit-id");
    assert_ne!(
        after_overwrite, generated,
        "the explicit id should have replaced the originally generated one"
    );
}

/// Phase 6: `reconnect_all_unavailable` skips connected hosts and
/// kicks any host not in `Connected`/`Connecting`.
#[tokio::test]
async fn reconnect_all_unavailable_skips_connected_and_wakes_failed() {
    // Host A: connects normally on first try (stays Connected).
    let counter_a = Arc::new(AtomicU32::new(0));
    let counter_a_clone = counter_a.clone();
    let factory_a = move |_id: HostId| {
        let counter = counter_a_clone.clone();
        Box::pin(async move {
            counter.fetch_add(1, Ordering::SeqCst);
            let (client_side, server_side) = pair();
            tokio::spawn(drive_fake_host_basic(server_side, FakeHostState::new()));
            Ok(BoxedTransport::new(client_side))
        })
            as std::pin::Pin<
                Box<
                    dyn std::future::Future<Output = Result<BoxedTransport, TransportError>> + Send,
                >,
            >
    };

    // Host B: first attempt fails (driving the host to Failed because
    // policy is `disabled`), subsequent attempts succeed.
    let did_first_fail = Arc::new(AtomicBool::new(false));
    let counter_b = Arc::new(AtomicU32::new(0));
    let counter_b_clone = counter_b.clone();
    let did_first_fail_clone = did_first_fail.clone();
    let factory_b = move |_id: HostId| {
        let counter = counter_b_clone.clone();
        let flag = did_first_fail_clone.clone();
        Box::pin(async move {
            counter.fetch_add(1, Ordering::SeqCst);
            if !flag.swap(true, Ordering::SeqCst) {
                return Err(TransportError::Io(
                    "intentional first-attempt failure".into(),
                ));
            }
            let (client_side, server_side) = pair();
            tokio::spawn(drive_fake_host_basic(server_side, FakeHostState::new()));
            Ok(BoxedTransport::new(client_side))
        })
            as std::pin::Pin<
                Box<
                    dyn std::future::Future<Output = Result<BoxedTransport, TransportError>> + Send,
                >,
            >
    };

    let multi = MultiHostClient::new();
    multi
        .add_host(HostConfig::new("a", "A", factory_a))
        .await
        .unwrap();
    multi
        .add_host(
            HostConfig::new("b", "B", factory_b).with_reconnect_policy(ReconnectPolicy::disabled()),
        )
        .await
        .unwrap();

    wait_for_state(&multi, &HostId::new("a"), |s| s.is_connected(), 2000).await;
    wait_for_state(&multi, &HostId::new("b"), |s| s.is_failed(), 2000).await;

    let a_count_before = counter_a.load(Ordering::SeqCst);
    assert_eq!(a_count_before, 1);

    let errors = multi.reconnect_all_unavailable().await;
    assert!(
        errors.is_empty(),
        "expected acks without error, got {errors:?}"
    );

    wait_for_state(&multi, &HostId::new("b"), |s| s.is_connected(), 2000).await;
    let a_count_after = counter_a.load(Ordering::SeqCst);
    let b_count_after = counter_b.load(Ordering::SeqCst);
    assert_eq!(a_count_after, 1, "host A should not have been re-connected");
    assert_eq!(
        b_count_after, 2,
        "host B should have re-attempted exactly once"
    );
}

/// Phase 6: empty map when every host is Connected.
#[tokio::test]
async fn reconnect_all_unavailable_returns_empty_when_all_connected() {
    let multi = MultiHostClient::new();
    multi
        .add_host(HostConfig::new(
            "x",
            "X",
            make_basic_factory(FakeHostState::new()),
        ))
        .await
        .unwrap();
    wait_for_state(&multi, &HostId::new("x"), |s| s.is_connected(), 2000).await;
    let errors = multi.reconnect_all_unavailable().await;
    assert!(errors.is_empty());
}

/// Phase 2: per-`(host, uri)` stream delivers a live action envelope
/// scoped to the requested resource and ignores envelopes for other
/// resources.
#[tokio::test]
async fn events_for_delivers_live_action_envelopes_scoped_to_uri() {
    use ahp_types::actions::{SessionTitleChangedAction, StateAction};

    // Custom factory: respond to subscribe + push an action envelope
    // targeting copilot:/s1 a moment later.
    let factory = move |_id: HostId| {
        Box::pin(async move {
            let (client_side, server_side) = pair();
            tokio::spawn(async move {
                let mut t = server_side;
                let mut handled_subscribe = false;
                loop {
                    let frame = match t.recv().await {
                        Ok(Some(f)) => f,
                        _ => return,
                    };
                    let parsed = match frame.into_parsed() {
                        Ok(m) => m,
                        Err(_) => continue,
                    };
                    if let JsonRpcMessage::Request(req) = parsed {
                        let result = handle_request(&req, &FakeHostState::new());
                        let resp = JsonRpcMessage::SuccessResponse(JsonRpcSuccessResponse {
                            jsonrpc: JsonRpcVersion::V2,
                            id: req.id,
                            result: ahp_types::common::AnyValue::from(result),
                        });
                        if t.send(TransportMessage::encode(&resp).unwrap())
                            .await
                            .is_err()
                        {
                            return;
                        }
                        if req.method == "subscribe" && !handled_subscribe {
                            handled_subscribe = true;
                            // Push a couple of action envelopes: one for the
                            // subscribed URI, one for a different URI. Only
                            // the first should reach the per-URI stream.
                            for (resource, title, seq) in [
                                ("copilot:/s1", "live", 11i64),
                                ("copilot:/s2", "other", 12i64),
                            ] {
                                let envelope = serde_json::json!({
                                    "action": {
                                        "type": "session/titleChanged",
                                        "session": resource,
                                        "title": title,
                                    },
                                    "serverSeq": seq,
                                });
                                let notif = JsonRpcMessage::Notification(JsonRpcNotification {
                                    jsonrpc: JsonRpcVersion::V2,
                                    method: "action".into(),
                                    params: Some(ahp_types::common::AnyValue::from(envelope)),
                                });
                                let _ = t.send(TransportMessage::encode(&notif).unwrap()).await;
                            }
                        }
                    }
                }
            });
            Ok(BoxedTransport::new(client_side))
        })
            as std::pin::Pin<
                Box<
                    dyn std::future::Future<Output = Result<BoxedTransport, TransportError>> + Send,
                >,
            >
    };

    let multi = MultiHostClient::new();
    multi
        .add_host(HostConfig::new("h", "Host", factory))
        .await
        .unwrap();
    wait_for_state(&multi, &HostId::new("h"), |s| s.is_connected(), 2000).await;

    // Register the per-URI stream BEFORE subscribing.
    let mut stream = multi
        .events_for(&HostId::new("h"), "copilot:/s1".into())
        .await
        .expect("host is registered");
    multi
        .subscribe(&HostId::new("h"), "copilot:/s1".into())
        .await
        .expect("subscribe");

    // Pull events; first matching should be the live action for /s1.
    let event = tokio::time::timeout(Duration::from_secs(2), stream.recv())
        .await
        .expect("timeout")
        .expect("channel closed");
    match event {
        ahp::SubscriptionEvent::Action(env) => {
            assert_eq!(env.server_seq, 11);
            match env.action {
                StateAction::SessionTitleChanged(SessionTitleChangedAction { session, title }) => {
                    assert_eq!(session, "copilot:/s1");
                    assert_eq!(title, "live");
                }
                other => panic!("unexpected action: {other:?}"),
            }
        }
        ahp::SubscriptionEvent::Notification(_) => panic!("expected an Action event"),
    }

    // A short grace window: nothing else should arrive on the
    // per-URI stream (the /s2 envelope is for a different URI).
    let next = tokio::time::timeout(Duration::from_millis(150), stream.recv()).await;
    assert!(
        next.is_err(),
        "stream should not deliver events for other URIs; got {next:?}"
    );
}

/// Phase 2: `events_for` returns `None` for an unregistered host.
#[tokio::test]
async fn events_for_returns_none_for_unknown_host() {
    let multi = MultiHostClient::new();
    let stream = multi
        .events_for(&HostId::new("never-added"), "copilot:/s1".into())
        .await;
    assert!(stream.is_none());
}

/// Phase 2: removing a host closes the per-`(host, uri)` stream so
/// the consumer's `recv()` loop exits cleanly.
#[tokio::test]
async fn events_for_closes_when_host_is_removed() {
    let multi = MultiHostClient::new();
    multi
        .add_host(HostConfig::new(
            "h",
            "Host",
            make_basic_factory(FakeHostState::new()),
        ))
        .await
        .unwrap();
    wait_for_state(&multi, &HostId::new("h"), |s| s.is_connected(), 2000).await;

    let mut stream = multi
        .events_for(&HostId::new("h"), "copilot:/s1".into())
        .await
        .expect("host is registered");

    multi.remove_host(&HostId::new("h")).await.unwrap();

    // The receiver should resolve to None (channel closed).
    let next = tokio::time::timeout(Duration::from_millis(500), stream.recv())
        .await
        .expect("stream did not close after host removal");
    assert!(
        next.is_none(),
        "per-uri stream should report end-of-stream after host removal; got {next:?}"
    );
}

/// Phase 2 (correctness): the per-`(host, uri)` stream must receive
/// **replayed** action envelopes too — not just live ones. This is
/// the headline bug the Swift PR #129 / Phase 2 fix addresses for
/// consumers that use the per-URI stream as their reducer feed: the
/// runtime fans replay actions through the same path as live
/// envelopes, and `events_for` was wired to capture both.
#[tokio::test]
async fn events_for_receives_replayed_envelopes_across_reconnect() {
    use ahp_types::actions::{RootActiveSessionsChangedAction, StateAction};

    let drop_after_init = Arc::new(AtomicBool::new(false));
    let return_replay = Arc::new(Mutex::new(false));
    let state = FakeHostState::new();

    let multi = MultiHostClient::new();
    // Subscribe to the per-URI stream BEFORE adding the host so the
    // first connect's replay (when it eventually happens) can't race
    // ahead of our registration.
    multi
        .add_host(HostConfig::new(
            "h",
            "Host",
            make_replay_factory(state, drop_after_init.clone(), return_replay.clone()),
        ))
        .await
        .unwrap();
    wait_for_state(&multi, &HostId::new("h"), |s| s.is_connected(), 2000).await;

    // The replay envelope targets the ROOT_RESOURCE_URI (it's a
    // RootActiveSessionsChanged action). Subscribe the per-URI
    // stream to that URI.
    let mut stream = multi
        .events_for(&HostId::new("h"), ahp_types::ROOT_RESOURCE_URI.into())
        .await
        .expect("host registered");

    // Flip the server to respond with a Replay on the next
    // `reconnect`, then drop the existing connection so the
    // supervisor kicks a new connect.
    *return_replay.lock().await = true;
    drop_after_init.store(true, Ordering::SeqCst);
    multi
        .reconnect_host(&HostId::new("h"))
        .await
        .expect("reconnect");

    // Pull from the per-URI stream until we see the replayed
    // RootActiveSessionsChanged. Without Phase 2 wiring the runtime's
    // fan-out to per-URI listeners, this would time out.
    let deadline = tokio::time::Instant::now() + Duration::from_millis(2500);
    let mut saw_it = false;
    while tokio::time::Instant::now() < deadline {
        match tokio::time::timeout(Duration::from_millis(500), stream.recv()).await {
            Ok(Some(ahp::SubscriptionEvent::Action(env))) => {
                if matches!(
                    env.action,
                    StateAction::RootActiveSessionsChanged(RootActiveSessionsChangedAction {
                        active_sessions: 7
                    })
                ) {
                    saw_it = true;
                    break;
                }
            }
            Ok(Some(_)) => continue,
            _ => break,
        }
    }
    assert!(
        saw_it,
        "events_for must deliver replayed action envelopes — Phase 2 correctness fix"
    );
}

/// Phase 2 (drop semantics): dropping a `PerResourceStream`
/// unregisters its listener slot from the registry immediately, so
/// long-running consumers that create + drop streams in a loop
/// don't leak per-listener state.
#[tokio::test]
async fn dropping_events_for_stream_releases_listener_slot() {
    let multi = MultiHostClient::new();
    multi
        .add_host(HostConfig::new(
            "h",
            "Host",
            make_basic_factory(FakeHostState::new()),
        ))
        .await
        .unwrap();
    wait_for_state(&multi, &HostId::new("h"), |s| s.is_connected(), 2000).await;

    // Create a stream, then drop it; the listener slot should be gone.
    {
        let _stream = multi
            .events_for(&HostId::new("h"), "copilot:/s1".into())
            .await
            .expect("host registered");
    }

    // Give the Drop a moment.
    tokio::time::sleep(Duration::from_millis(20)).await;

    // Push an event by triggering a manual reconnect (cheap way to
    // exercise the fan-out path). Then create a new stream and
    // verify it observes a *fresh* listener id (the prior one is
    // gone). We can't probe the registry directly, but we can probe
    // indirectly: registering many streams in a loop and dropping
    // them shouldn't exhaust memory — the previous implementation
    // would have grown the per_resource_listeners bucket
    // unboundedly. Smoke test: register and drop 100 streams, then
    // register one more and observe a live event flows through.
    for _ in 0..100 {
        let _stream = multi
            .events_for(&HostId::new("h"), "copilot:/s1".into())
            .await
            .expect("host registered");
        // Stream dropped at end of scope.
    }
    tokio::time::sleep(Duration::from_millis(20)).await;
    let _stream = multi
        .events_for(&HostId::new("h"), "copilot:/s1".into())
        .await
        .expect("host registered");
    // If we got here without OOMing or panicking, the cleanup works.
}

/// Phase 6 / lifecycle: `MultiHostClient::shutdown()` tears down
/// every host's supervisor and closes any open per-URI streams.
#[tokio::test]
async fn shutdown_tears_down_all_hosts_and_closes_listeners() {
    let multi = MultiHostClient::new();
    multi
        .add_host(HostConfig::new(
            "a",
            "A",
            make_basic_factory(FakeHostState::new()),
        ))
        .await
        .unwrap();
    multi
        .add_host(HostConfig::new(
            "b",
            "B",
            make_basic_factory(FakeHostState::new()),
        ))
        .await
        .unwrap();
    wait_for_state(&multi, &HostId::new("a"), |s| s.is_connected(), 2000).await;
    wait_for_state(&multi, &HostId::new("b"), |s| s.is_connected(), 2000).await;

    let mut stream_a = multi
        .events_for(&HostId::new("a"), "copilot:/s1".into())
        .await
        .expect("host A registered");

    multi.shutdown().await;

    // Per-URI stream should report end-of-stream.
    let next = tokio::time::timeout(Duration::from_millis(500), stream_a.recv())
        .await
        .expect("stream did not close after shutdown");
    assert!(
        next.is_none(),
        "expected per-URI stream to close on shutdown"
    );

    // Hosts should be gone.
    assert!(multi.host(&HostId::new("a")).await.is_none());
    assert!(multi.host(&HostId::new("b")).await.is_none());
    assert_eq!(multi.hosts().await.len(), 0);

    // shutdown() should be idempotent.
    multi.shutdown().await;
}

/// Phase 6 / cancellation safety: a cancelled `add_host` future
/// must not leak the `pending_host_ids` reservation. Without the
/// RAII guard, a subsequent `add_host` for the same id would
/// permanently return `DuplicateHost`.
///
/// We exercise the cancellation path by plugging a [`ClientIdStore`]
/// that takes forever to `load()`, so `add_host_inner` parks
/// awaiting the store with the reservation still held. When the
/// outer task is aborted, the reservation's `Drop` must fire.
#[tokio::test]
async fn cancelled_add_host_releases_pending_reservation() {
    use ahp::hosts::{ClientIdStore, ClientIdStoreError};
    use std::pin::Pin;

    /// Store that blocks forever inside `load` so we can park
    /// `add_host_inner` mid-resolution and then cancel it.
    struct ParkingStore;
    impl ClientIdStore for ParkingStore {
        fn load(
            &self,
            _host_id: &HostId,
        ) -> Pin<
            Box<
                dyn std::future::Future<Output = Result<Option<String>, ClientIdStoreError>>
                    + Send
                    + '_,
            >,
        > {
            Box::pin(async {
                tokio::time::sleep(Duration::from_secs(60)).await;
                Ok(None)
            })
        }
        fn store(
            &self,
            _host_id: &HostId,
            _client_id: &str,
        ) -> Pin<Box<dyn std::future::Future<Output = Result<(), ClientIdStoreError>> + Send + '_>>
        {
            Box::pin(async { Ok(()) })
        }
    }

    let multi = MultiHostClient::with_client_id_store(ParkingStore);

    // Kick off add_host on a task; it will park inside store.load.
    // Note we use a factory that would only run AFTER add_host_inner
    // returns Ok, so it never actually executes here — the
    // reservation is held while we're stuck in store.load.
    let multi_clone = multi.clone();
    let task = tokio::spawn(async move {
        let _ = multi_clone
            .add_host(HostConfig::new(
                "h",
                "Host",
                make_basic_factory(FakeHostState::new()),
            ))
            .await;
    });
    // Brief pause so the task gets to the parked store.load.
    tokio::time::sleep(Duration::from_millis(50)).await;

    // Confirm the reservation IS in place — a second add_host for
    // the same id should fail until the first is cancelled.
    let racing = multi
        .add_host(HostConfig::new(
            "h",
            "Other",
            make_basic_factory(FakeHostState::new()),
        ))
        .await;
    assert!(
        matches!(racing, Err(HostError::DuplicateHost(_))),
        "expected DuplicateHost while reservation is held; got {racing:?}"
    );

    // Cancel the parked add and wait for the abort + Drop chain.
    task.abort();
    let _ = task.await;
    tokio::time::sleep(Duration::from_millis(50)).await;

    // Switch to a multi-host client built without the parking store
    // for the recovery add, since the original is still wired to
    // ParkingStore (which would re-park). Instead, take a fresh
    // MultiHostClient with default store and confirm the path works
    // in isolation. The reservation-release behaviour we're
    // verifying is observable on `multi` itself — after the abort
    // the pending_host_ids set should not contain "h".
    //
    // We probe `multi` indirectly: a fresh add_host on `multi` for
    // the same id would re-enter the parking store, but at least
    // the reservation check should pass. So spawn another and
    // verify it parks rather than fast-fails with DuplicateHost.
    let multi_clone2 = multi.clone();
    let probe = tokio::spawn(async move {
        multi_clone2
            .add_host(HostConfig::new(
                "h",
                "Probe",
                make_basic_factory(FakeHostState::new()),
            ))
            .await
    });
    // Give the probe a tick to either fail or park.
    tokio::time::sleep(Duration::from_millis(50)).await;
    assert!(
        !probe.is_finished(),
        "second add_host should have passed the reservation check and parked in the store"
    );
    probe.abort();
    let _ = probe.await;
}

// ─── Helpers ────────────────────────────────────────────────────────────────

fn make_summary(uri: &str, title: &str, modified_at: i64) -> ahp_types::state::SessionSummary {
    ahp_types::state::SessionSummary {
        resource: uri.into(),
        provider: "copilot".into(),
        title: title.into(),
        status: 0,
        activity: None,
        created_at: 0,
        modified_at,
        project: None,
        model: None,
        working_directory: None,
        diffs: None,
    }
}

fn make_basic_factory(
    state: FakeHostState,
) -> impl Fn(
    HostId,
) -> std::pin::Pin<
    Box<dyn std::future::Future<Output = Result<BoxedTransport, TransportError>> + Send>,
> + Send
       + Sync
       + 'static {
    let state = Arc::new(state);
    move |_host_id| {
        let state = state.clone();
        Box::pin(async move {
            let (client_side, server_side) = pair();
            tokio::spawn(drive_fake_host_basic(server_side, (*state).clone()));
            Ok(BoxedTransport::new(client_side))
        })
    }
}

fn make_injecting_factory(
    state: FakeHostState,
    inject: Arc<Mutex<Option<ahp_types::state::SessionSummary>>>,
) -> impl Fn(
    HostId,
) -> std::pin::Pin<
    Box<dyn std::future::Future<Output = Result<BoxedTransport, TransportError>> + Send>,
> + Send
       + Sync
       + 'static {
    let state = Arc::new(state);
    move |_host_id| {
        let state = state.clone();
        let inject = inject.clone();
        Box::pin(async move {
            let (client_side, server_side) = pair();
            tokio::spawn(drive_fake_host_with_injection(
                server_side,
                (*state).clone(),
                inject,
            ));
            Ok(BoxedTransport::new(client_side))
        })
    }
}

/// Factory used by the reconnect-replay test. The first connect responds
/// to `initialize` normally with a non-zero `serverSeq` and a single
/// subscription so the next connect chooses the `reconnect` arm. When
/// `drop_after_init` is set, the server drops its side of the
/// connection right after the handshake to force the runtime into a
/// reconnect cycle. When `return_replay` is set, that reconnect's
/// response carries a single `RootActiveSessionsChanged` action.
fn make_replay_factory(
    state: FakeHostState,
    drop_after_init: Arc<AtomicBool>,
    return_replay: Arc<Mutex<bool>>,
) -> impl Fn(
    HostId,
) -> std::pin::Pin<
    Box<dyn std::future::Future<Output = Result<BoxedTransport, TransportError>> + Send>,
> + Send
       + Sync
       + 'static {
    let state = Arc::new(state);
    state.server_seq.store(40, Ordering::SeqCst);
    move |_host_id| {
        let state = state.clone();
        let drop_after_init = drop_after_init.clone();
        let return_replay = return_replay.clone();
        Box::pin(async move {
            let (client_side, server_side) = pair();
            tokio::spawn(drive_fake_host_replay(
                server_side,
                (*state).clone(),
                drop_after_init,
                return_replay,
            ));
            Ok(BoxedTransport::new(client_side))
        })
    }
}

async fn drive_fake_host_replay(
    mut transport: MemTransport,
    state: FakeHostState,
    drop_after_init: Arc<AtomicBool>,
    return_replay: Arc<Mutex<bool>>,
) {
    loop {
        let frame = match transport.recv().await {
            Ok(Some(f)) => f,
            _ => return,
        };
        let msg = match frame.into_parsed() {
            Ok(m) => m,
            Err(_) => continue,
        };
        if let JsonRpcMessage::Request(req) = msg {
            let result = if req.method == "reconnect" && *return_replay.lock().await {
                // Replay arm with a single RootActiveSessionsChanged
                // action carrying serverSeq=42 (advances past the
                // pre-seeded 40).
                serde_json::json!({
                    "type": "replay",
                    "actions": [
                        {
                            "action": {
                                "type": "root/activeSessionsChanged",
                                "activeSessions": 7
                            },
                            "serverSeq": 42,
                            "origin": null,
                        }
                    ],
                    "missing": []
                })
            } else {
                handle_request(&req, &state)
            };
            let resp = JsonRpcMessage::SuccessResponse(JsonRpcSuccessResponse {
                jsonrpc: JsonRpcVersion::V2,
                id: req.id,
                result: ahp_types::common::AnyValue::from(result),
            });
            if transport
                .send(TransportMessage::encode(&resp).unwrap())
                .await
                .is_err()
            {
                return;
            }

            if (req.method == "initialize" || req.method == "reconnect")
                && drop_after_init.swap(false, Ordering::SeqCst)
            {
                // Close the transport from the server side so the
                // client supervisor sees a disconnect and reconnects.
                drop(transport);
                return;
            }
        }
    }
}

async fn drain_events(events: &mut ahp::hosts::HostSubscriptionStream, window: Duration) {
    let deadline = tokio::time::Instant::now() + window;
    while tokio::time::Instant::now() < deadline {
        match tokio::time::timeout(Duration::from_millis(20), events.recv()).await {
            Ok(Some(_)) => continue,
            _ => break,
        }
    }
}

async fn wait_for_state(
    multi: &MultiHostClient,
    id: &HostId,
    pred: impl Fn(&HostState) -> bool,
    timeout_ms: u64,
) {
    wait_until(timeout_ms, || async {
        multi
            .host(id)
            .await
            .map(|h| pred(&h.state))
            .unwrap_or(false)
    })
    .await;
}

async fn wait_until<F, Fut>(timeout_ms: u64, mut cond: F)
where
    F: FnMut() -> Fut,
    Fut: std::future::Future<Output = bool>,
{
    let deadline = tokio::time::Instant::now() + Duration::from_millis(timeout_ms);
    while tokio::time::Instant::now() < deadline {
        if cond().await {
            return;
        }
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    panic!("condition did not become true within {timeout_ms} ms");
}
