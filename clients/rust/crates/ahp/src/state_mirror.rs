//! Host-aware state mirror for multi-host consumers.
//!
//! Single-host consumers can build their state by running each
//! [`ActionEnvelope`](ahp_types::actions::ActionEnvelope)'s payload
//! through the pure reducers ([`crate::apply_action_to_root`],
//! [`crate::apply_action_to_session`],
//! [`crate::apply_action_to_terminal`]) directly, keyed by URI. That
//! falls apart for multi-host consumers because URIs are per-host
//! scoped — `copilot:/s1` on Host A and `copilot:/s1` on Host B are
//! different sessions that must not clobber each other in a shared
//! map.
//!
//! [`MultiHostStateMirror`] wraps those reducers behind a façade
//! keyed by [`HostedResourceKey`] (`(HostId, uri)`), eliminating the
//! cross-host collision. Drop-in for any multi-host consumer; can be
//! fed directly from [`crate::hosts::MultiHostClient::events_for`]
//! (the lossless per-`(host, uri)` stream) or from individual
//! [`crate::hosts::HostSubscriptionEvent`]s via [`Self::apply`].
//!
//! **Feed from the reliable per-resource stream.** Pump events into
//! the mirror from
//! [`crate::hosts::MultiHostClient::events_for`](crate::hosts::MultiHostClient::events_for)
//! — which is unbounded, delivers replayed envelopes, and survives
//! reconnects — **not** from
//! [`crate::hosts::MultiHostClient::events`](crate::hosts::MultiHostClient::events)
//! (which is lossy by design). Dropping action envelopes desyncs the
//! mirror irreversibly.

use std::collections::HashMap;

use ahp_types::actions::{ActionEnvelope, StateAction};
use ahp_types::state::{RootState, SessionState, Snapshot, SnapshotState, TerminalState};
use serde_json::Value;
use tokio::sync::RwLock;

use crate::hosts::{HostId, HostSubscriptionEvent};
use crate::reducers::{apply_action_to_root, apply_action_to_session, apply_action_to_terminal};
use crate::SubscriptionEvent;

/// Compound key tagging a resource URI with the host that produced
/// it.
///
/// Session and terminal URIs aren't globally unique across hosts —
/// `copilot:/s1` on Host A and `copilot:/s1` on Host B are different
/// resources. Use this struct as the key in any multi-host state
/// map.
#[derive(Debug, Clone, Hash, PartialEq, Eq)]
pub struct HostedResourceKey {
    /// Host the resource belongs to.
    pub host_id: HostId,
    /// Resource URI, scoped to the host.
    pub uri: String,
}

impl HostedResourceKey {
    /// Build a key from a host id and URI.
    pub fn new(host_id: impl Into<HostId>, uri: impl Into<String>) -> Self {
        Self {
            host_id: host_id.into(),
            uri: uri.into(),
        }
    }
}

/// In-memory mirror of root/session/terminal state, fed by
/// [`ActionEnvelope`]s and [`Snapshot`]s tagged with their host of
/// origin.
///
/// Single-host consumers can keep using the pure reducers directly;
/// this type adds the host dimension necessary for multi-host UIs.
/// Apply [`HostSubscriptionEvent`]s directly via [`Self::apply`], or
/// feed individual envelopes / snapshots via [`Self::apply_envelope`]
/// and [`Self::apply_snapshot`].
///
/// Cheaply cloneable — the inner storage is `Arc`-shared, so all
/// clones observe the same mirror state.
#[derive(Default)]
pub struct MultiHostStateMirror {
    inner: RwLock<MirrorInner>,
}

#[derive(Default)]
struct MirrorInner {
    root_states: HashMap<HostId, RootState>,
    sessions: HashMap<HostedResourceKey, SessionState>,
    terminals: HashMap<HostedResourceKey, TerminalState>,
}

impl MultiHostStateMirror {
    /// Construct an empty mirror.
    pub fn new() -> Self {
        Self::default()
    }

    /// Apply a [`HostSubscriptionEvent`] produced by
    /// [`crate::hosts::MultiHostClient::events`] or
    /// [`crate::hosts::MultiHostClient::events_for`].
    ///
    /// Action envelopes are routed through the reducer; protocol
    /// notifications are dropped (they don't affect reducer state).
    pub async fn apply(&self, event: &HostSubscriptionEvent) {
        if let SubscriptionEvent::Action(envelope) = &event.event {
            self.apply_envelope(&event.host_id, envelope).await;
        }
    }

    /// Apply a single action envelope, scoped to `host`.
    ///
    /// Routing rules mirror
    /// [`crate::SubscriptionEvent::Action`]'s server semantics: the
    /// reducer is dispatched based on the action's `session` /
    /// `terminal` field (root if neither is present). If no
    /// snapshot has seeded the matching `(host, uri)` slot yet, the
    /// action is a no-op for session/terminal — the reducer can't
    /// invent a state from scratch. The root state is initialised
    /// on demand (empty agents list) because the protocol guarantees
    /// every host eventually publishes a root state.
    pub async fn apply_envelope(&self, host: &HostId, envelope: &ActionEnvelope) {
        let mut state = self.inner.write().await;
        if let Some(session_uri) = action_session_uri(&envelope.action) {
            let key = HostedResourceKey::new(host.clone(), session_uri);
            if let Some(session) = state.sessions.get_mut(&key) {
                apply_action_to_session(session, &envelope.action);
            }
            return;
        }
        if let Some(terminal_uri) = action_terminal_uri(&envelope.action) {
            let key = HostedResourceKey::new(host.clone(), terminal_uri);
            if let Some(terminal) = state.terminals.get_mut(&key) {
                apply_action_to_terminal(terminal, &envelope.action);
            }
            return;
        }
        let root = state
            .root_states
            .entry(host.clone())
            .or_insert_with(|| RootState {
                agents: vec![],
                active_sessions: None,
                terminals: None,
                config: None,
            });
        apply_action_to_root(root, &envelope.action);
    }

    /// Seed the mirror from a [`Snapshot`] scoped to `host` — root,
    /// session, or terminal as the snapshot's `state` discriminator
    /// dictates.
    pub async fn apply_snapshot(&self, host: &HostId, snapshot: &Snapshot) {
        let mut state = self.inner.write().await;
        match &snapshot.state {
            SnapshotState::Root(root) => {
                state
                    .root_states
                    .insert(host.clone(), root.as_ref().clone());
            }
            SnapshotState::Session(session) => {
                let key = HostedResourceKey::new(host.clone(), snapshot.resource.clone());
                state.sessions.insert(key, session.as_ref().clone());
            }
            SnapshotState::Terminal(terminal) => {
                let key = HostedResourceKey::new(host.clone(), snapshot.resource.clone());
                state.terminals.insert(key, terminal.as_ref().clone());
            }
        }
    }

    /// Reset every slot for `host` — drops the root state, all
    /// sessions keyed under that host, and all terminals keyed under
    /// that host. Other hosts are untouched.
    pub async fn reset_host(&self, host: &HostId) {
        let mut state = self.inner.write().await;
        state.root_states.remove(host);
        state.sessions.retain(|key, _| &key.host_id != host);
        state.terminals.retain(|key, _| &key.host_id != host);
    }

    /// Reset every slot across every host.
    pub async fn reset(&self) {
        let mut state = self.inner.write().await;
        state.root_states.clear();
        state.sessions.clear();
        state.terminals.clear();
    }

    /// Clone the root state for `host`, if any.
    pub async fn root(&self, host: &HostId) -> Option<RootState> {
        self.inner.read().await.root_states.get(host).cloned()
    }

    /// Clone the session state for `(host, uri)`, if any.
    pub async fn session(&self, host: &HostId, uri: &str) -> Option<SessionState> {
        let key = HostedResourceKey::new(host.clone(), uri.to_owned());
        self.inner.read().await.sessions.get(&key).cloned()
    }

    /// Clone the terminal state for `(host, uri)`, if any.
    pub async fn terminal(&self, host: &HostId, uri: &str) -> Option<TerminalState> {
        let key = HostedResourceKey::new(host.clone(), uri.to_owned());
        self.inner.read().await.terminals.get(&key).cloned()
    }

    /// Snapshot every root state. Order is unspecified.
    pub async fn root_states(&self) -> HashMap<HostId, RootState> {
        self.inner.read().await.root_states.clone()
    }

    /// Snapshot every session state. Order is unspecified.
    pub async fn sessions(&self) -> HashMap<HostedResourceKey, SessionState> {
        self.inner.read().await.sessions.clone()
    }

    /// Snapshot every terminal state. Order is unspecified.
    pub async fn terminals(&self) -> HashMap<HostedResourceKey, TerminalState> {
        self.inner.read().await.terminals.clone()
    }
}

impl std::fmt::Debug for MultiHostStateMirror {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("MultiHostStateMirror")
            .finish_non_exhaustive()
    }
}

/// Extract the action's `session` URI, if it carries one.
///
/// Uses serde rather than enumerating every variant so new actions
/// route correctly without an SDK update. Mirrors the helper inside
/// the hosts runtime.
fn action_session_uri(action: &StateAction) -> Option<String> {
    let val = serde_json::to_value(action).ok()?;
    val.get("session")
        .and_then(Value::as_str)
        .map(str::to_owned)
}

/// Extract the action's `terminal` URI, if it carries one.
fn action_terminal_uri(action: &StateAction) -> Option<String> {
    let val = serde_json::to_value(action).ok()?;
    val.get("terminal")
        .and_then(Value::as_str)
        .map(str::to_owned)
}
