//! Client self-instrumentation metrics.
//!
//! Emits the cross-client telemetry metrics named by [`ahp_types::telemetry`]
//! through the [`metrics`] facade. The facade is a no-op until the host
//! application installs a recorder, so instrumentation is effectively
//! zero-cost when unobserved — the Rust analog of the .NET client's `Meter`
//! gated on `HasListeners()`.
//!
//! Only the metric / attribute *names* are shared across language clients (they
//! are generated from `types/telemetry/registry.ts` into [`ahp_types::telemetry`]); the
//! wiring below is hand-written and idiomatic to Rust, exactly as the telemetry
//! contract intends ("only the NAMES are shared").
//!
//! This is client SELF-instrumentation (how the client reports on its own
//! operation) and is distinct from the protocol's "OpenTelemetry over AHP"
//! channel (server → client OTLP delivery).

use ahp_types::telemetry as names;
use metrics::{counter, gauge, histogram};
use std::time::{Duration, Instant};

/// One JSON-RPC message (`kind`: [`names::MESSAGE_KIND_REQUEST`] or
/// [`names::MESSAGE_KIND_NOTIFICATION`]) was written to the transport.
pub(crate) fn message_sent(method: &str, kind: &'static str) {
    counter!(
        names::MESSAGES_SENT,
        names::ATTR_RPC_SYSTEM => names::RPC_SYSTEM_JSONRPC,
        names::ATTR_MESSAGE_KIND => kind,
        names::ATTR_RPC_METHOD => method.to_owned(),
    )
    .increment(1);
}

/// One JSON-RPC message was read and parsed from the transport.
pub(crate) fn message_received() {
    counter!(names::MESSAGES_RECEIVED).increment(1);
}

/// A frame arrived that could not be parsed as a JSON-RPC message.
pub(crate) fn frame_malformed() {
    counter!(names::FRAMES_MALFORMED).increment(1);
}

/// A request settled; decrements the in-flight gauge and records its duration
/// against the given `outcome` (one of the [`names`] `OUTCOME_*` values).
///
/// Paired with the gauge increment in [`RequestSpan::started`]; callers should
/// prefer the [`RequestSpan`] guard so cancellation is accounted for.
fn request_finished(method: &str, outcome: &'static str, elapsed: Duration) {
    gauge!(names::REQUESTS_IN_FLIGHT).decrement(1.0);
    histogram!(
        names::REQUEST_DURATION,
        names::ATTR_RPC_METHOD => method.to_owned(),
        names::ATTR_OUTCOME => outcome,
    )
    .record(elapsed.as_secs_f64() * 1_000.0);
}

/// RAII guard spanning one in-flight request.
///
/// Construction ([`RequestSpan::started`]) increments the in-flight gauge; the
/// matching decrement + duration record ([`request_finished`]) happens exactly
/// once — either via [`RequestSpan::settle`] with the real outcome, or, if the
/// span is dropped without settling (the caller cancelled the request future),
/// via `Drop` tagged [`names::OUTCOME_CANCELLED`].
///
/// This guarantees the in-flight gauge is balanced and a `request.duration`
/// sample is recorded even on cancellation, which a plain
/// increment/`request_finished` pair around an `.await` cannot do (a dropped
/// future runs no further statements, leaking the gauge and emitting no
/// duration).
pub(crate) struct RequestSpan {
    method: String,
    started: Instant,
    settled: bool,
}

impl RequestSpan {
    /// Begin a span: increments the in-flight gauge and starts the clock.
    pub(crate) fn started(method: &str) -> Self {
        gauge!(names::REQUESTS_IN_FLIGHT).increment(1.0);
        Self {
            method: method.to_owned(),
            started: Instant::now(),
            settled: false,
        }
    }

    /// Record the request as finished with `outcome` (one of the `OUTCOME_*`
    /// values). Idempotent guard: subsequent calls and the eventual `Drop` are
    /// no-ops so the duration is recorded exactly once.
    pub(crate) fn settle(&mut self, outcome: &'static str) {
        if self.settled {
            return;
        }
        self.settled = true;
        request_finished(&self.method, outcome, self.started.elapsed());
    }
}

impl Drop for RequestSpan {
    fn drop(&mut self) {
        // Unsettled at drop ⇒ the request future was cancelled before
        // producing an outcome. Record it as such so the gauge stays balanced
        // and a `cancelled` duration sample is emitted.
        self.settle(names::OUTCOME_CANCELLED);
    }
}

/// A reconnect attempt settled with the given `outcome`.
pub(crate) fn reconnect(outcome: &'static str) {
    counter!(names::RECONNECTS, names::ATTR_OUTCOME => outcome).increment(1);
}

/// A subscription fan-out was opened; increments the active-subscriptions gauge.
pub(crate) fn subscription_opened() {
    gauge!(names::SUBSCRIPTIONS_ACTIVE).increment(1.0);
}

/// A subscription fan-out was closed; decrements the active-subscriptions gauge.
pub(crate) fn subscription_closed() {
    gauge!(names::SUBSCRIPTIONS_ACTIVE).decrement(1.0);
}

/// `dropped` events were skipped on the given `stream` (one of the [`names`]
/// `STREAM_*` values) because a consumer fell behind the broadcast buffer.
pub(crate) fn events_dropped(stream: &'static str, dropped: u64) {
    counter!(names::EVENTS_DROPPED, names::ATTR_STREAM => stream).increment(dropped);
}

#[cfg(test)]
mod tests {
    use super::names;

    /// The metric/attribute name *constants* this module emits come straight
    /// from the generated contract — assert a representative sample matches the
    /// canonical `types/telemetry/registry.ts` values so a drift in either is
    /// caught here. This pins the NAME constants only; that the metrics
    /// actually *emit* (and carry the right attributes) is proven by the
    /// `tests/telemetry_emission.rs` integration test.
    #[test]
    fn contract_name_constants_match() {
        assert_eq!(names::MESSAGES_SENT, "ahp.client.messages.sent");
        assert_eq!(names::REQUEST_DURATION, "ahp.client.request.duration");
        assert_eq!(names::REQUESTS_IN_FLIGHT, "ahp.client.requests.in_flight");
        assert_eq!(names::FRAMES_MALFORMED, "ahp.client.frames.malformed");
        assert_eq!(names::ATTR_RPC_METHOD, "rpc.method");
        assert_eq!(names::ATTR_OUTCOME, "ahp.outcome");
        assert_eq!(names::OUTCOME_OK, "ok");
        assert_eq!(names::MESSAGE_KIND_REQUEST, "request");
    }
}
