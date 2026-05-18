//! `MultiHostClient` facade.

use std::collections::{HashMap, HashSet};
use std::sync::{Arc, RwLock as StdRwLock};

use tokio::sync::{broadcast, mpsc, RwLock};

use super::client_id_store::{ClientIdStore, InMemoryClientIdStore};
use super::runtime::{spawn, EventSink, HostHandleTx};
use super::types::{
    generate_client_id, HostClientHandle, HostConfig, HostError, HostEvent, HostEventStream,
    HostHandle, HostId, HostState, HostSubscriptionEvent, HostSubscriptionStream, HostedAgent,
    HostedSessionSummary, PerResourceStream,
};
use crate::SubscriptionEvent;

/// Buffer size for the cross-host fan-in broadcasts.
const DEFAULT_EVENT_BUFFER: usize = 1024;

/// Concurrent registry of [`HostHandle`]s plus the supervisor tasks
/// that drive them.
///
/// Cheap to clone — the inner state is reference-counted, so multiple
/// UI layers can hold their own clone and observe the same hosts.
///
/// See the module docs for the full surface; common entry points:
///
/// - [`MultiHostClient::single`] — one-line single-host constructor.
/// - [`MultiHostClient::add_host`] / [`MultiHostClient::remove_host`].
/// - [`MultiHostClient::events`] / [`MultiHostClient::host_events`].
/// - [`MultiHostClient::events_for`] — reliable per-`(host, uri)`
///   event stream that survives reconnects.
/// - [`MultiHostClient::reconnect_all_unavailable`] — bulk wake
///   helper.
/// - [`MultiHostClient::aggregated_sessions`] /
///   [`MultiHostClient::aggregated_agents`].
#[derive(Clone)]
pub struct MultiHostClient {
    inner: Arc<MultiInner>,
}

pub(super) struct MultiInner {
    pub(super) hosts: RwLock<HashMap<HostId, HostHandleTx>>,
    /// Insertion order of hosts. Used by aggregated views for
    /// deterministic tie-breaking when sorting by `modified_at` (or
    /// for stable agent ordering across calls). Held in an
    /// `StdRwLock` because mutations are tiny vec ops with no `await`
    /// points in scope.
    pub(super) host_order: StdRwLock<Vec<HostId>>,
    /// Host ids reserved by an in-flight `add_host` call. Held
    /// synchronously across the async `ClientIdStore` lookup so two
    /// concurrent `add_host` calls for the same id can't both pass
    /// the duplicate check. Wrapped in an RAII guard so a cancelled
    /// `add_host` future doesn't leak the reservation.
    pub(super) pending_host_ids: StdRwLock<HashSet<HostId>>,
    pub(super) fan_out: broadcast::Sender<HostSubscriptionEvent>,
    pub(super) host_events: broadcast::Sender<HostEvent>,
    /// Per-`(HostId, listener_id)` registry of unbounded mpsc senders
    /// that back [`MultiHostClient::events_for`]. Lossless by
    /// construction — slow consumers retain an unbounded backlog
    /// rather than dropping events, mirroring the Swift SDK's
    /// `events(host:uri:)` semantics.
    pub(super) per_resource_listeners:
        StdRwLock<HashMap<HostId, HashMap<u64, PerResourceListener>>>,
    pub(super) next_listener_id: std::sync::atomic::AtomicU64,
    pub(super) client_id_store: Arc<dyn ClientIdStore>,
}

pub(super) struct PerResourceListener {
    pub(super) uri: String,
    pub(super) tx: mpsc::UnboundedSender<SubscriptionEvent>,
}

/// RAII guard that holds the `pending_host_ids` reservation for a
/// host id and releases it on drop. Without this, a cancelled
/// `add_host` future would leak the reservation and permanently lock
/// out future adds for the same id.
struct PendingReservation {
    inner: Arc<MultiInner>,
    id: HostId,
}

impl PendingReservation {
    fn new(inner: Arc<MultiInner>, id: HostId) -> Self {
        Self { inner, id }
    }
}

impl Drop for PendingReservation {
    fn drop(&mut self) {
        if let Ok(mut pending) = self.inner.pending_host_ids.write() {
            pending.remove(&self.id);
        }
    }
}

impl MultiInner {
    /// Synchronous fan-out: invoked from the per-host runtime on the
    /// hot event path. Writes the event to the lossy broadcast (which
    /// backs [`MultiHostClient::events`]) AND to every matching
    /// per-`(host, uri)` listener (which backs
    /// [`MultiHostClient::events_for`]).
    ///
    /// Protocol notifications (events with `resource: None`) fan to
    /// every listener for the host because they're cross-resource by
    /// design (auth required, session added/removed/changed).
    pub(super) fn fan_event(&self, event: HostSubscriptionEvent) {
        // 1. Lossy broadcast fan-in.
        let _ = self.fan_out.send(event.clone());

        // 2. Lossless per-`(host, uri)` fan-out. We take a read lock
        //    on the registry; per-listener `mpsc::UnboundedSender::send`
        //    is cheap and never blocks. Dead listeners (receiver
        //    dropped without unregistering — shouldn't happen because
        //    `PerResourceStream::Drop` unregisters, but defence in
        //    depth) are collected and pruned under the write lock so
        //    `per_resource_listeners` doesn't grow indefinitely.
        let mut dead: Vec<(HostId, u64)> = Vec::new();
        {
            let listeners = self
                .per_resource_listeners
                .read()
                .expect("poisoned listener lock");
            let Some(bucket) = listeners.get(&event.host_id) else {
                return;
            };
            for (id, listener) in bucket {
                let deliver = match &event.resource {
                    Some(uri) => listener.uri == *uri,
                    None => true,
                };
                if deliver && listener.tx.send(event.event.clone()).is_err() {
                    dead.push((event.host_id.clone(), *id));
                }
            }
        }
        if !dead.is_empty() {
            let mut listeners = self
                .per_resource_listeners
                .write()
                .expect("poisoned listener lock");
            for (host_id, listener_id) in dead {
                if let Some(bucket) = listeners.get_mut(&host_id) {
                    bucket.remove(&listener_id);
                    if bucket.is_empty() {
                        listeners.remove(&host_id);
                    }
                }
            }
        }
    }

    /// Remove a single per-`(host, uri)` listener from the registry.
    /// Called from [`PerResourceStream`]'s `Drop` so dropping a stream
    /// releases its registry slot promptly rather than waiting for
    /// the host to be removed.
    ///
    /// Recovers gracefully on a poisoned lock: a panic inside `Drop`
    /// during unwinding would abort the process, and a leaked listener
    /// slot is strictly less bad than that. The next event for the
    /// host with no live listener has its dead sender pruned by
    /// [`MultiInner::fan_event`] anyway.
    pub(super) fn remove_per_resource_listener(&self, host: &HostId, listener_id: u64) {
        let Ok(mut listeners) = self.per_resource_listeners.write() else {
            return;
        };
        if let Some(bucket) = listeners.get_mut(host) {
            bucket.remove(&listener_id);
            if bucket.is_empty() {
                listeners.remove(host);
            }
        }
    }

    /// Drop every per-`(host, uri)` listener registered for `host` and
    /// remove the bucket. Called on `remove_host` / `shutdown` so any
    /// consumer holding the matching [`PerResourceStream`] observes a
    /// clean close on its next `recv()`.
    fn drop_per_resource_listeners_for(&self, host: &HostId) {
        let mut listeners = self
            .per_resource_listeners
            .write()
            .expect("poisoned listener lock");
        listeners.remove(host);
    }
}

impl MultiHostClient {
    /// Build an empty multi-host client backed by an
    /// [`InMemoryClientIdStore`].
    ///
    /// In-memory client ids are session-stable but lost on process
    /// restart. For cross-launch reconnect identity, use
    /// [`MultiHostClient::with_client_id_store`] with a persistent
    /// store like [`super::FileClientIdStore`].
    pub fn new() -> Self {
        Self::with_client_id_store(InMemoryClientIdStore::new())
    }

    /// Build an empty multi-host client wired to a custom
    /// [`ClientIdStore`].
    ///
    /// The store is consulted whenever a host is added without an
    /// explicit [`HostConfig::client_id`]: a stored value is reused;
    /// a miss generates a fresh id which is then written back through
    /// [`ClientIdStore::store`].
    pub fn with_client_id_store(store: impl ClientIdStore) -> Self {
        let (fan_out, _) = broadcast::channel(DEFAULT_EVENT_BUFFER);
        let (host_events, _) = broadcast::channel(DEFAULT_EVENT_BUFFER);
        Self {
            inner: Arc::new(MultiInner {
                hosts: RwLock::new(HashMap::new()),
                host_order: StdRwLock::new(Vec::new()),
                pending_host_ids: StdRwLock::new(HashSet::new()),
                fan_out,
                host_events,
                per_resource_listeners: StdRwLock::new(HashMap::new()),
                next_listener_id: std::sync::atomic::AtomicU64::new(1),
                client_id_store: Arc::new(store),
            }),
        }
    }

    /// Convenience: construct a multi-host client with a single host
    /// already registered, and wait for the first connection attempt to
    /// either succeed or fail.
    ///
    /// Returns the (`MultiHostClient`, [`HostHandle`]) pair. If the host
    /// fails to connect within the configured reconnect policy, the
    /// returned handle will reflect [`HostState::Failed`] and the
    /// connection error is surfaced via `last_error`.
    ///
    /// Designed so single-host consumers don't have to think about
    /// "registry" concepts — `let (client, host) = MultiHostClient::single(config).await?;`
    /// is the whole onboarding.
    pub async fn single(config: HostConfig) -> Result<(Self, HostHandle), HostError> {
        let multi = Self::new();
        let handle = multi.add_host(config).await?;
        Ok((multi, handle))
    }

    /// Like [`MultiHostClient::single`] but lets the caller plug a
    /// custom [`ClientIdStore`] in the same call, which is the
    /// natural shape for desktop apps wiring a persistent file-backed
    /// store at startup.
    pub async fn single_with_client_id_store(
        config: HostConfig,
        store: impl ClientIdStore,
    ) -> Result<(Self, HostHandle), HostError> {
        let multi = Self::with_client_id_store(store);
        let handle = multi.add_host(config).await?;
        Ok((multi, handle))
    }

    /// Register a new host and start its supervisor task.
    ///
    /// The supervisor immediately attempts to open a transport via
    /// [`HostConfig::transport_factory`], complete the `initialize`
    /// handshake, and start fanning events. The returned handle is the
    /// snapshot at this moment — by the time the call returns the host
    /// may already be `Connected` (if connect succeeds quickly), still
    /// `Connecting`, or already `Reconnecting` if the first attempt
    /// failed.
    ///
    /// This call is non-blocking with respect to the host's own
    /// supervisor: the snapshot is read directly from per-host shared
    /// state, never via the supervisor's command channel. A slow or
    /// hung transport factory therefore cannot block other registry
    /// operations.
    ///
    /// # `clientId` resolution
    ///
    /// If `config.client_id` is `Some(_)`, that value is used and
    /// also written back to the configured [`ClientIdStore`] so the
    /// store reflects the host's current identity. If `None`, the
    /// store is consulted; on miss, a fresh id is generated and
    /// stored. A failure to read or write the store is surfaced as
    /// [`HostError::ClientIdStore`] without registering the host.
    ///
    /// Returns [`HostError::DuplicateHost`] if the host id is already in
    /// use or a concurrent `add_host` is mid-flight for the same id;
    /// remove the existing host first.
    pub async fn add_host(&self, config: HostConfig) -> Result<HostHandle, HostError> {
        let id = config.id.clone();

        // Reserve the id synchronously so two concurrent `add_host`
        // calls for the same id can't both pass the duplicate check
        // across the awaited store lookups. Held in an RAII guard so
        // that a cancelled `add_host` future doesn't leak the
        // reservation and permanently lock out future adds for `id`.
        let _reservation = {
            let hosts = self.inner.hosts.read().await;
            if hosts.contains_key(&id) {
                return Err(HostError::DuplicateHost(id));
            }
            let mut pending = self
                .inner
                .pending_host_ids
                .write()
                .expect("poisoned pending lock");
            if pending.contains(&id) {
                return Err(HostError::DuplicateHost(id));
            }
            pending.insert(id.clone());
            PendingReservation::new(self.inner.clone(), id.clone())
        };

        self.add_host_inner(config).await
    }

    async fn add_host_inner(&self, mut config: HostConfig) -> Result<HostHandle, HostError> {
        let id = config.id.clone();

        // Resolve the clientId: explicit > stored > freshly generated.
        // Always write the resolved value back to the store so the
        // next launch sees it.
        let client_id = match config.client_id.take() {
            Some(explicit) => {
                self.inner
                    .client_id_store
                    .store(&id, &explicit)
                    .await
                    .map_err(HostError::ClientIdStore)?;
                explicit
            }
            None => {
                let stored = self
                    .inner
                    .client_id_store
                    .load(&id)
                    .await
                    .map_err(HostError::ClientIdStore)?;
                let resolved = stored.unwrap_or_else(generate_client_id);
                self.inner
                    .client_id_store
                    .store(&id, &resolved)
                    .await
                    .map_err(HostError::ClientIdStore)?;
                resolved
            }
        };

        // Construct the per-runtime event sink. The closure holds a
        // `Weak<MultiInner>` so it doesn't keep `MultiHostClient`
        // alive past the consumer dropping every clone — a drop is
        // observable via `Weak::upgrade` returning `None`.
        let inner_weak = Arc::downgrade(&self.inner);
        let event_sink: EventSink = Arc::new(move |event: HostSubscriptionEvent| {
            if let Some(inner) = inner_weak.upgrade() {
                inner.fan_event(event);
            }
        });

        let shared = {
            let mut hosts = self.inner.hosts.write().await;
            // Re-check duplicate inside the write lock in case a
            // racing `remove_host` left a stale `pending_host_ids`
            // entry plus a fresh real host.
            if hosts.contains_key(&id) {
                return Err(HostError::DuplicateHost(id));
            }
            let tx = spawn(
                config,
                client_id,
                event_sink,
                self.inner.host_events.clone(),
            );
            let shared = tx.shared.clone();
            hosts.insert(id.clone(), tx);
            // Append to insertion-order list for deterministic
            // aggregated views.
            self.inner
                .host_order
                .write()
                .expect("poisoned host_order lock")
                .push(id.clone());
            shared
        };

        // Snapshot directly from per-host shared state. Doing this via
        // the supervisor's command channel would block here when the
        // supervisor is busy in `connect_once` (e.g. a hung transport
        // factory) — see Copilot review on #121 for the bug this avoids.
        let snapshot = shared.lock().await.snapshot();
        Ok(snapshot)
    }

    /// Remove a host, cancelling its supervisor task and dropping its
    /// current connection.
    ///
    /// Any outstanding [`HostClientHandle`]s for this host become stale
    /// and will return [`HostError::HostShutDown`] from subsequent
    /// dispatches. Any [`PerResourceStream`]s for this host are closed
    /// so consumers' `recv()` loops exit cleanly.
    pub async fn remove_host(&self, id: &HostId) -> Result<(), HostError> {
        let entry = {
            let mut hosts = self.inner.hosts.write().await;
            hosts.remove(id)
        };
        match entry {
            Some(tx) => {
                self.inner
                    .host_order
                    .write()
                    .expect("poisoned host_order lock")
                    .retain(|h| h != id);
                self.inner.drop_per_resource_listeners_for(id);
                tx.shutdown().await;
                let _ = self.inner.host_events.send(HostEvent::Removed {
                    host_id: id.clone(),
                });
                Ok(())
            }
            None => Err(HostError::UnknownHost(id.clone())),
        }
    }

    /// Tear down every registered host's supervisor and drop every
    /// per-`(host, uri)` listener.
    ///
    /// Safe to call multiple times. The receiving half of any
    /// existing [`PerResourceStream`] will resolve to `None` on its
    /// next `recv()`. The cross-host broadcast streams returned by
    /// [`MultiHostClient::events`] and
    /// [`MultiHostClient::host_events`] are kept alive by their
    /// senders inside the still-held [`MultiInner`]; they close
    /// naturally when the last [`MultiHostClient`] clone is dropped.
    ///
    /// Mirrors Swift's `MultiHostClient.shutdown()` so consumers have
    /// a deterministic lifecycle anchor for app teardown / sign-out.
    pub async fn shutdown(&self) {
        // Drain the host map so concurrent callers can't see the
        // hosts in a partially-torn-down state.
        let entries: Vec<HostHandleTx> = {
            let mut hosts = self.inner.hosts.write().await;
            self.inner
                .host_order
                .write()
                .expect("poisoned host_order lock")
                .clear();
            hosts.drain().map(|(_, tx)| tx).collect()
        };
        // Drop every per-URI listener so consumers exit their loops.
        {
            let mut listeners = self
                .inner
                .per_resource_listeners
                .write()
                .expect("poisoned listener lock");
            listeners.clear();
        }
        // Shut down each supervisor. Concurrent shutdowns let a slow
        // transport teardown not block the others.
        let mut tasks = tokio::task::JoinSet::new();
        for tx in entries {
            tasks.spawn(async move {
                tx.shutdown().await;
            });
        }
        while tasks.join_next().await.is_some() {}
    }

    /// Trigger a manual reconnect for `id`. Cancels the current
    /// connection (or pending backoff sleep) and immediately attempts a
    /// fresh connect.
    pub async fn reconnect_host(&self, id: &HostId) -> Result<(), HostError> {
        let tx = self
            .inner
            .hosts
            .read()
            .await
            .get(id)
            .map(|entry| entry.cmd_tx.clone())
            .ok_or_else(|| HostError::UnknownHost(id.clone()))?;
        let (reply_tx, reply_rx) = tokio::sync::oneshot::channel();
        tx.send(super::runtime::HostCommand::Reconnect { reply: reply_tx })
            .await
            .map_err(|_| HostError::HostShutDown(id.clone()))?;
        reply_rx
            .await
            .map_err(|_| HostError::HostShutDown(id.clone()))?;
        Ok(())
    }

    /// Trigger a manual reconnect on every registered host that is
    /// **not** currently `Connected` or `Connecting` — i.e., hosts in
    /// `Disconnected`, `Reconnecting`, or `Failed`. Hosts already
    /// connected (or actively connecting) are skipped.
    ///
    /// Designed for the scene-phase / network-change pattern: on
    /// `ScenePhase.active` (or a network reachability flip), call
    /// this to wake every host the user has been away from instead
    /// of writing the loop in every consumer. Particularly useful
    /// for `Failed` hosts whose reconnect policy is exhausted — a
    /// manual reconnect bypasses the policy and starts a fresh
    /// attempt.
    ///
    /// Per-host reconnect requests are dispatched concurrently; the
    /// returned map contains one entry per host whose reconnect
    /// failed. An empty map means every selected host acknowledged
    /// its reconnect request.
    pub async fn reconnect_all_unavailable(&self) -> HashMap<HostId, HostError> {
        // Snapshot the candidate list under the read lock to avoid
        // holding it across the per-host snapshot / reconnect awaits.
        let candidates: Vec<(HostId, mpsc::Sender<super::runtime::HostCommand>)> = {
            let hosts = self.inner.hosts.read().await;
            hosts
                .iter()
                .map(|(id, tx)| (id.clone(), tx.cmd_tx.clone()))
                .collect()
        };

        let mut pending: Vec<(HostId, mpsc::Sender<super::runtime::HostCommand>)> = Vec::new();
        for (id, cmd_tx) in candidates {
            let snap = match self.host(&id).await {
                Some(s) => s,
                None => continue, // removed between read and snapshot
            };
            match snap.state {
                HostState::Connected | HostState::Connecting => continue,
                HostState::Disconnected
                | HostState::Reconnecting { .. }
                | HostState::Failed { .. } => pending.push((id, cmd_tx)),
            }
        }

        // Fire all reconnects concurrently.
        let mut tasks = tokio::task::JoinSet::new();
        for (id, cmd_tx) in pending {
            tasks.spawn(async move {
                let (reply_tx, reply_rx) = tokio::sync::oneshot::channel();
                let send_result = cmd_tx
                    .send(super::runtime::HostCommand::Reconnect { reply: reply_tx })
                    .await;
                let outcome = match send_result {
                    Ok(()) => match reply_rx.await {
                        Ok(()) => Ok(()),
                        Err(_) => Err(HostError::HostShutDown(id.clone())),
                    },
                    Err(_) => Err(HostError::HostShutDown(id.clone())),
                };
                (id, outcome)
            });
        }

        let mut errors = HashMap::new();
        while let Some(join) = tasks.join_next().await {
            // A `JoinError` here means the task itself panicked; we
            // shouldn't reach this since the task only sends/recvs.
            // Surface it as a `HostShutDown` error rather than panic.
            if let Ok((id, Err(err))) = join {
                errors.insert(id, err);
            }
        }
        errors
    }

    /// Snapshot the current state of `id`.
    ///
    /// Reads from per-host shared state directly; safe to call even if
    /// the host's supervisor is mid-`connect_once` and unable to
    /// process commands.
    pub async fn host(&self, id: &HostId) -> Option<HostHandle> {
        let shared = self
            .inner
            .hosts
            .read()
            .await
            .get(id)
            .map(|e| e.shared.clone())?;
        let snapshot = shared.lock().await.snapshot();
        Some(snapshot)
    }

    /// Snapshot every registered host. Order is unspecified.
    ///
    /// Reads from per-host shared state directly; safe to call even if
    /// some hosts' supervisors are mid-`connect_once`.
    pub async fn hosts(&self) -> Vec<HostHandle> {
        let shareds: Vec<_> = self
            .inner
            .hosts
            .read()
            .await
            .values()
            .map(|e| e.shared.clone())
            .collect();
        let mut out = Vec::with_capacity(shareds.len());
        for shared in shareds {
            out.push(shared.lock().await.snapshot());
        }
        out
    }

    /// Acquire a generation-checked client handle for `id`.
    ///
    /// Returns `None` if the host is not registered or has no live
    /// connection. The returned handle refuses to dispatch through a
    /// connection that has been replaced by a reconnect — request a
    /// fresh handle in that case.
    pub async fn client(&self, id: &HostId) -> Option<HostClientHandle> {
        // Read the current generation + Client clone directly from
        // shared state. No need to round-trip through the supervisor.
        let shared = self
            .inner
            .hosts
            .read()
            .await
            .get(id)
            .map(|e| e.shared.clone())?;
        let state = shared.lock().await;
        let client = state.current_client.clone()?;
        Some(HostClientHandle {
            host_id: id.clone(),
            generation: state.generation,
            client,
            shared: shared.clone(),
        })
    }

    /// Convenience: subscribe to `uri` on `host_id`.
    ///
    /// Returns the server's [`ahp_types::commands::SubscribeResult`]
    /// (the initial snapshot the server pushes back). To consume the
    /// live stream of action envelopes for `uri`, call
    /// [`MultiHostClient::events_for`] separately — typically
    /// **before** this call, so envelopes the server pushes between
    /// the subscribe response and the consumer's first `recv()`
    /// aren't dropped on the floor:
    ///
    /// ```ignore
    /// let mut stream = multi.events_for(&host_id, uri.clone()).await
    ///     .expect("host registered");
    /// let snapshot = multi.subscribe(&host_id, uri.clone()).await?;
    /// while let Some(event) = stream.recv().await { /* ... */ }
    /// ```
    pub async fn subscribe(
        &self,
        host_id: &HostId,
        uri: String,
    ) -> Result<ahp_types::commands::SubscribeResult, HostError> {
        let entry = self.host_entry(host_id).await?;
        entry.subscribe(uri).await
    }

    /// Convenience: unsubscribe from `uri` on `host_id`.
    pub async fn unsubscribe(&self, host_id: &HostId, uri: String) -> Result<(), HostError> {
        let entry = self.host_entry(host_id).await?;
        entry.unsubscribe(uri).await
    }

    /// Convenience: dispatch `action` on `host_id`.
    pub async fn dispatch(
        &self,
        host_id: &HostId,
        action: ahp_types::actions::StateAction,
    ) -> Result<crate::DispatchHandle, HostError> {
        let entry = self.host_entry(host_id).await?;
        entry.dispatch(action).await
    }

    /// Subscribe to a fan-in stream of every inbound event from every
    /// registered host. Each call returns a fresh receiver — multiple
    /// consumers can listen independently.
    ///
    /// **This stream is lossy by design.** Slow consumers see
    /// `broadcast::error::RecvError::Lagged` and skip ahead, dropping
    /// older events. Use it for advisory / notification-style
    /// consumption (e.g. counters, UI badges). For reducer-critical
    /// per-URI action streams use [`MultiHostClient::events_for`]
    /// instead — that surface uses unbounded per-listener buffering
    /// and survives reconnects.
    pub fn events(&self) -> HostSubscriptionStream {
        HostSubscriptionStream {
            rx: self.inner.fan_out.subscribe(),
        }
    }

    /// Subscribe to connection-state events for UX. Each call returns a
    /// fresh receiver.
    pub fn host_events(&self) -> HostEventStream {
        HostEventStream {
            rx: self.inner.host_events.subscribe(),
        }
    }

    /// Per-`(host, uri)` event stream — the reliable channel for
    /// reducer-critical action envelopes.
    ///
    /// Returns a [`PerResourceStream`] that delivers every event
    /// scoped to `uri` on `host` — both live envelopes and envelopes
    /// replayed during reconnect via `apply_reconnect_result`. The
    /// stream is unbounded (no buffer drop) because losing an action
    /// envelope desyncs downstream state mirrors irreversibly.
    ///
    /// Unlike [`crate::SessionSubscription`] (bound to one underlying
    /// [`crate::Client`] generation), this stream is owned by
    /// [`MultiHostClient`] and **survives reconnects** — replayed
    /// envelopes from the per-host supervisor's reconnect path reach
    /// this stream too. Protocol-level notifications (auth required,
    /// session added/removed/changed) are also fanned to every active
    /// per-URI listener for the host because they're cross-resource
    /// by design.
    ///
    /// **Subscription registration is independent from subscribing
    /// on the server.** Calling `events_for` does NOT send a
    /// `subscribe` request. Attach the listener **before** calling
    /// [`MultiHostClient::subscribe`] to avoid missing the initial
    /// post-subscribe events:
    ///
    /// ```ignore
    /// let mut stream = multi.events_for(&host_id, uri.clone()).await
    ///     .expect("host registered");
    /// multi.subscribe(&host_id, uri.clone()).await?;
    /// while let Some(event) = stream.recv().await { /* ... */ }
    /// ```
    ///
    /// **Consume promptly.** The stream is unbounded — a stalled
    /// consumer retains an unbounded backlog. Process events on a
    /// fast loop and dispatch reducer work asynchronously if needed.
    ///
    /// Returns `None` if `host` is not registered.
    pub async fn events_for(&self, host: &HostId, uri: String) -> Option<PerResourceStream> {
        // Hold the hosts `read()` lock across listener registration
        // so a concurrent `remove_host` (which takes the `write()`
        // lock and then calls `drop_per_resource_listeners_for(host)`)
        // can't slip between our presence check and our insert,
        // leaving a listener registered against a removed host.
        let listener_id = self
            .inner
            .next_listener_id
            .fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        let (tx, rx) = mpsc::unbounded_channel();
        {
            let hosts = self.inner.hosts.read().await;
            if !hosts.contains_key(host) {
                return None;
            }
            let mut listeners = self
                .inner
                .per_resource_listeners
                .write()
                .expect("poisoned listener lock");
            let bucket = listeners.entry(host.clone()).or_default();
            bucket.insert(listener_id, PerResourceListener { uri, tx });
        }
        Some(PerResourceStream {
            rx,
            // Hold a Weak reference so dropping the stream doesn't
            // keep `MultiInner` alive past its last clone.
            registry: Arc::downgrade(&self.inner),
            host: host.clone(),
            listener_id,
        })
    }

    /// Aggregated session summaries across every registered host,
    /// sorted by `summary.modified_at` descending. Includes both the
    /// host id and label so consumers can render a unified inbox
    /// without losing host attribution.
    ///
    /// **Tie-breaking:** for equal `modified_at`, summaries are
    /// ordered by host registration order, then by
    /// `summary.resource`, so the result is deterministic across
    /// calls (matches the Swift SDK).
    pub async fn aggregated_sessions(&self) -> Vec<HostedSessionSummary> {
        let order = self
            .inner
            .host_order
            .read()
            .expect("poisoned host_order lock")
            .clone();
        let index: HashMap<HostId, usize> = order
            .iter()
            .enumerate()
            .map(|(i, id)| (id.clone(), i))
            .collect();
        let mut out = Vec::new();
        for id in &order {
            let shared = match self
                .inner
                .hosts
                .read()
                .await
                .get(id)
                .map(|e| e.shared.clone())
            {
                Some(s) => s,
                None => continue,
            };
            let snap = shared.lock().await.snapshot();
            for summary in snap.session_summaries.iter().cloned() {
                out.push(HostedSessionSummary {
                    host_id: snap.id.clone(),
                    host_label: snap.label.clone(),
                    summary,
                });
            }
        }
        out.sort_by(|lhs, rhs| {
            rhs.summary
                .modified_at
                .cmp(&lhs.summary.modified_at)
                .then_with(|| {
                    let li = index.get(&lhs.host_id).copied().unwrap_or(usize::MAX);
                    let ri = index.get(&rhs.host_id).copied().unwrap_or(usize::MAX);
                    li.cmp(&ri)
                })
                .then_with(|| lhs.summary.resource.cmp(&rhs.summary.resource))
        });
        out
    }

    /// Aggregated agents across every registered host, in
    /// registration order per host. Order is deterministic across
    /// calls.
    pub async fn aggregated_agents(&self) -> Vec<HostedAgent> {
        let order = self
            .inner
            .host_order
            .read()
            .expect("poisoned host_order lock")
            .clone();
        let mut out = Vec::new();
        for id in order {
            let shared = match self
                .inner
                .hosts
                .read()
                .await
                .get(&id)
                .map(|e| e.shared.clone())
            {
                Some(s) => s,
                None => continue,
            };
            let snap = shared.lock().await.snapshot();
            for agent in snap.agents.iter().cloned() {
                out.push(HostedAgent {
                    host_id: snap.id.clone(),
                    host_label: snap.label.clone(),
                    agent,
                });
            }
        }
        out
    }

    async fn host_entry(&self, id: &HostId) -> Result<HostHandleTxRef, HostError> {
        self.inner
            .hosts
            .read()
            .await
            .get(id)
            .map(|e| HostHandleTxRef {
                cmd_tx: e.cmd_tx.clone(),
                shared: e.shared.clone(),
            })
            .ok_or_else(|| HostError::UnknownHost(id.clone()))
    }
}

impl Default for MultiHostClient {
    fn default() -> Self {
        Self::new()
    }
}

impl std::fmt::Debug for MultiHostClient {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("MultiHostClient").finish_non_exhaustive()
    }
}

/// Handle on a host's command sender that doesn't block the registry
/// `RwLock` while we await on the host's runtime.
struct HostHandleTxRef {
    cmd_tx: tokio::sync::mpsc::Sender<super::runtime::HostCommand>,
    shared: Arc<super::types::HostShared>,
}

impl HostHandleTxRef {
    async fn subscribe(
        &self,
        uri: String,
    ) -> Result<ahp_types::commands::SubscribeResult, HostError> {
        let (tx, rx) = tokio::sync::oneshot::channel();
        let host_id = self.host_id().await;
        self.cmd_tx
            .send(super::runtime::HostCommand::Subscribe { uri, reply: tx })
            .await
            .map_err(|_| HostError::HostShutDown(host_id.clone()))?;
        rx.await.map_err(|_| HostError::HostShutDown(host_id))?
    }

    async fn unsubscribe(&self, uri: String) -> Result<(), HostError> {
        let (tx, rx) = tokio::sync::oneshot::channel();
        let host_id = self.host_id().await;
        self.cmd_tx
            .send(super::runtime::HostCommand::Unsubscribe { uri, reply: tx })
            .await
            .map_err(|_| HostError::HostShutDown(host_id.clone()))?;
        rx.await.map_err(|_| HostError::HostShutDown(host_id))?
    }

    async fn dispatch(
        &self,
        action: ahp_types::actions::StateAction,
    ) -> Result<crate::DispatchHandle, HostError> {
        let (tx, rx) = tokio::sync::oneshot::channel();
        let host_id = self.host_id().await;
        self.cmd_tx
            .send(super::runtime::HostCommand::Dispatch {
                action: Box::new(action),
                reply: tx,
            })
            .await
            .map_err(|_| HostError::HostShutDown(host_id.clone()))?;
        rx.await.map_err(|_| HostError::HostShutDown(host_id))?
    }

    async fn host_id(&self) -> HostId {
        self.shared.lock().await.id.clone()
    }
}

// Convenience predicates for tests/consumers.
impl HostState {
    /// Convenience: is the host currently connected?
    pub fn is_connected(&self) -> bool {
        matches!(self, HostState::Connected)
    }

    /// Convenience: is the host in a terminal failure state?
    pub fn is_failed(&self) -> bool {
        matches!(self, HostState::Failed { .. })
    }
}
