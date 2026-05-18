//! Tests for [`ahp::MultiHostStateMirror`].
//!
//! The mirror is the host-aware reducer façade — its core invariant
//! is that two hosts can legitimately advertise the same resource URI
//! without clobbering each other. These tests pin down the
//! reducer-routing behaviour, the per-host snapshot semantics, and
//! the reset helpers.

use ahp::hosts::HostId;
use ahp::{HostedResourceKey, MultiHostStateMirror};
use ahp_types::actions::{
    ActionEnvelope, RootAgentsChangedAction, SessionTitleChangedAction, StateAction,
};
use ahp_types::state::{
    AgentInfo, RootState, SessionLifecycle, SessionState, SessionSummary, Snapshot, SnapshotState,
};

fn agent(provider: &str) -> AgentInfo {
    AgentInfo {
        provider: provider.into(),
        display_name: provider.into(),
        description: String::new(),
        models: vec![],
        protected_resources: None,
        customizations: None,
    }
}

fn empty_root() -> RootState {
    RootState {
        agents: vec![],
        active_sessions: None,
        terminals: None,
        config: None,
    }
}

fn session_state(uri: &str, title: &str) -> SessionState {
    SessionState {
        summary: SessionSummary {
            resource: uri.into(),
            provider: "copilot".into(),
            title: title.into(),
            status: 0,
            activity: None,
            created_at: 1,
            modified_at: 1,
            project: None,
            model: None,
            working_directory: None,
            diffs: None,
        },
        lifecycle: SessionLifecycle::Ready,
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

#[tokio::test]
async fn root_states_are_isolated_per_host() {
    let mirror = MultiHostStateMirror::new();
    mirror
        .apply_snapshot(
            &HostId::new("alpha"),
            &Snapshot {
                resource: ahp_types::ROOT_RESOURCE_URI.into(),
                state: SnapshotState::Root(Box::new(RootState {
                    agents: vec![agent("a")],
                    ..empty_root()
                })),
                from_seq: 0,
            },
        )
        .await;
    mirror
        .apply_snapshot(
            &HostId::new("beta"),
            &Snapshot {
                resource: ahp_types::ROOT_RESOURCE_URI.into(),
                state: SnapshotState::Root(Box::new(RootState {
                    agents: vec![agent("b")],
                    ..empty_root()
                })),
                from_seq: 0,
            },
        )
        .await;

    let alpha = mirror.root(&HostId::new("alpha")).await.unwrap();
    let beta = mirror.root(&HostId::new("beta")).await.unwrap();
    assert_eq!(alpha.agents.first().map(|a| a.provider.as_str()), Some("a"));
    assert_eq!(beta.agents.first().map(|a| a.provider.as_str()), Some("b"));
}

#[tokio::test]
async fn session_uri_collision_across_hosts_does_not_clobber() {
    // The core multi-host invariant: two hosts can legitimately
    // advertise the same session URI; the mirror MUST key by
    // (hostId, uri) so they don't overwrite each other.
    let mirror = MultiHostStateMirror::new();
    let uri = "copilot:/s1";

    mirror
        .apply_snapshot(
            &HostId::new("alpha"),
            &Snapshot {
                resource: uri.into(),
                state: SnapshotState::Session(Box::new(session_state(uri, "A title"))),
                from_seq: 0,
            },
        )
        .await;
    mirror
        .apply_snapshot(
            &HostId::new("beta"),
            &Snapshot {
                resource: uri.into(),
                state: SnapshotState::Session(Box::new(session_state(uri, "B title"))),
                from_seq: 0,
            },
        )
        .await;

    let alpha_session = mirror.session(&HostId::new("alpha"), uri).await.unwrap();
    let beta_session = mirror.session(&HostId::new("beta"), uri).await.unwrap();
    assert_eq!(alpha_session.summary.title, "A title");
    assert_eq!(beta_session.summary.title, "B title");

    let key_alpha = HostedResourceKey::new("alpha", uri);
    let key_beta = HostedResourceKey::new("beta", uri);
    assert_ne!(key_alpha, key_beta);
}

#[tokio::test]
async fn apply_root_action_updates_only_target_host() {
    let mirror = MultiHostStateMirror::new();
    mirror
        .apply_snapshot(
            &HostId::new("alpha"),
            &Snapshot {
                resource: ahp_types::ROOT_RESOURCE_URI.into(),
                state: SnapshotState::Root(Box::new(empty_root())),
                from_seq: 0,
            },
        )
        .await;
    mirror
        .apply_snapshot(
            &HostId::new("beta"),
            &Snapshot {
                resource: ahp_types::ROOT_RESOURCE_URI.into(),
                state: SnapshotState::Root(Box::new(empty_root())),
                from_seq: 0,
            },
        )
        .await;

    let envelope = ActionEnvelope {
        action: StateAction::RootAgentsChanged(RootAgentsChangedAction {
            agents: vec![agent("new")],
        }),
        server_seq: 5,
        origin: None,
        rejection_reason: None,
    };
    mirror
        .apply_envelope(&HostId::new("alpha"), &envelope)
        .await;

    let alpha = mirror.root(&HostId::new("alpha")).await.unwrap();
    let beta = mirror.root(&HostId::new("beta")).await.unwrap();
    assert_eq!(alpha.agents.len(), 1);
    assert_eq!(alpha.agents[0].provider, "new");
    assert_eq!(
        beta.agents.len(),
        0,
        "applying a root action to alpha must not touch beta's root state"
    );
}

#[tokio::test]
async fn apply_session_action_updates_only_target_session() {
    let mirror = MultiHostStateMirror::new();
    let uri = "copilot:/s1";
    mirror
        .apply_snapshot(
            &HostId::new("alpha"),
            &Snapshot {
                resource: uri.into(),
                state: SnapshotState::Session(Box::new(session_state(uri, "Old"))),
                from_seq: 0,
            },
        )
        .await;
    mirror
        .apply_snapshot(
            &HostId::new("beta"),
            &Snapshot {
                resource: uri.into(),
                state: SnapshotState::Session(Box::new(session_state(uri, "Old"))),
                from_seq: 0,
            },
        )
        .await;

    let envelope = ActionEnvelope {
        action: StateAction::SessionTitleChanged(SessionTitleChangedAction {
            session: uri.into(),
            title: "New on alpha".into(),
        }),
        server_seq: 7,
        origin: None,
        rejection_reason: None,
    };
    mirror
        .apply_envelope(&HostId::new("alpha"), &envelope)
        .await;

    let alpha = mirror.session(&HostId::new("alpha"), uri).await.unwrap();
    let beta = mirror.session(&HostId::new("beta"), uri).await.unwrap();
    assert_eq!(alpha.summary.title, "New on alpha");
    assert_eq!(
        beta.summary.title, "Old",
        "applying a session action to alpha must not touch beta's session state"
    );
}

#[tokio::test]
async fn session_action_without_prior_snapshot_is_noop() {
    // The reducers can't invent a SessionState from scratch — only
    // applySnapshot can seed one. Confirm an action that arrives
    // before any snapshot is silently dropped rather than panicking
    // or creating a placeholder.
    let mirror = MultiHostStateMirror::new();
    let envelope = ActionEnvelope {
        action: StateAction::SessionTitleChanged(SessionTitleChangedAction {
            session: "copilot:/missing".into(),
            title: "anything".into(),
        }),
        server_seq: 1,
        origin: None,
        rejection_reason: None,
    };
    mirror
        .apply_envelope(&HostId::new("alpha"), &envelope)
        .await;
    assert!(mirror
        .session(&HostId::new("alpha"), "copilot:/missing")
        .await
        .is_none());
}

#[tokio::test]
async fn root_action_initialises_empty_root_when_no_snapshot_seen() {
    // Root actions land on every host eventually — the protocol
    // guarantees an initial root snapshot, but if a consumer wires
    // the mirror up partway through (or before the snapshot lands),
    // a root action should still update the in-memory root rather
    // than be lost. Matches the runtime's behaviour where root
    // state is held with default values until a snapshot replaces
    // them.
    let mirror = MultiHostStateMirror::new();
    let envelope = ActionEnvelope {
        action: StateAction::RootAgentsChanged(RootAgentsChangedAction {
            agents: vec![agent("a")],
        }),
        server_seq: 1,
        origin: None,
        rejection_reason: None,
    };
    mirror.apply_envelope(&HostId::new("h"), &envelope).await;
    let root = mirror.root(&HostId::new("h")).await.unwrap();
    assert_eq!(root.agents.len(), 1);
}

#[tokio::test]
async fn reset_host_drops_only_that_hosts_slots() {
    let mirror = MultiHostStateMirror::new();
    let uri = "copilot:/s1";
    for host in ["alpha", "beta"] {
        mirror
            .apply_snapshot(
                &HostId::new(host),
                &Snapshot {
                    resource: ahp_types::ROOT_RESOURCE_URI.into(),
                    state: SnapshotState::Root(Box::new(empty_root())),
                    from_seq: 0,
                },
            )
            .await;
        mirror
            .apply_snapshot(
                &HostId::new(host),
                &Snapshot {
                    resource: uri.into(),
                    state: SnapshotState::Session(Box::new(session_state(uri, host))),
                    from_seq: 0,
                },
            )
            .await;
    }

    mirror.reset_host(&HostId::new("alpha")).await;

    assert!(mirror.root(&HostId::new("alpha")).await.is_none());
    assert!(mirror.session(&HostId::new("alpha"), uri).await.is_none());
    assert!(mirror.root(&HostId::new("beta")).await.is_some());
    assert!(mirror.session(&HostId::new("beta"), uri).await.is_some());
}

#[tokio::test]
async fn reset_clears_every_host() {
    let mirror = MultiHostStateMirror::new();
    let uri = "copilot:/s1";
    mirror
        .apply_snapshot(
            &HostId::new("alpha"),
            &Snapshot {
                resource: uri.into(),
                state: SnapshotState::Session(Box::new(session_state(uri, "A"))),
                from_seq: 0,
            },
        )
        .await;
    mirror
        .apply_snapshot(
            &HostId::new("beta"),
            &Snapshot {
                resource: ahp_types::ROOT_RESOURCE_URI.into(),
                state: SnapshotState::Root(Box::new(empty_root())),
                from_seq: 0,
            },
        )
        .await;
    mirror.reset().await;
    assert!(mirror.sessions().await.is_empty());
    assert!(mirror.root_states().await.is_empty());
}

/// `MultiHostStateMirror` is `Clone` and all clones share the same
/// underlying storage — proving the docstring's "Cheaply cloneable —
/// inner storage is `Arc`-shared" claim. A consumer can hand a clone
/// to a reducer task and to a UI store and have both observe the
/// same writes.
#[tokio::test]
async fn clones_share_inner_storage() {
    let mirror = MultiHostStateMirror::new();
    let clone = mirror.clone();

    clone
        .apply_snapshot(
            &HostId::new("alpha"),
            &Snapshot {
                resource: ahp_types::ROOT_RESOURCE_URI.into(),
                state: SnapshotState::Root(Box::new(RootState {
                    agents: vec![agent("from-clone")],
                    active_sessions: None,
                    terminals: None,
                    config: None,
                })),
                from_seq: 0,
            },
        )
        .await;

    let observed = mirror.root(&HostId::new("alpha")).await.unwrap();
    assert_eq!(observed.agents.len(), 1);
    assert_eq!(observed.agents[0].provider, "from-clone");
}
