// ReconnectPolicy — backoff schedule for `HostRuntime`.

import Foundation

/// Backoff schedule between reconnect attempts.
public enum ReconnectBackoff: Sendable, Equatable {
    /// Retry immediately, with no delay between attempts.
    case immediate
    /// Wait a fixed amount of time between attempts.
    case constant(Duration)
    /// Exponential backoff: `delay = min(initial * multiplier^(attempt-1), max)`.
    case exponential(initial: Duration, max: Duration, multiplier: Double)

    /// Compute the delay before the `attempt`-th retry (1-based).
    public func delay(forAttempt attempt: Int) -> Duration {
        switch self {
        case .immediate:
            return .zero
        case .constant(let delay):
            return delay
        case .exponential(let initial, let max, let multiplier):
            let attempt = Swift.max(attempt, 1)
            let mult = Swift.max(multiplier, 1.0)
            let scaled = initial.seconds * pow(mult, Double(attempt - 1))
            let bounded = Swift.min(scaled, max.seconds)
            return .seconds(bounded)
        }
    }
}

/// Reconnect behaviour for a single host.
///
/// The supervisor enters a reconnect loop whenever an established connection
/// drops unexpectedly, or whenever a connect attempt fails. Use
/// `ReconnectPolicy.disabled` to opt out entirely (a single failure leaves
/// the host in `HostState.failed`).
public struct ReconnectPolicy: Sendable, Equatable {
    /// Backoff schedule between attempts.
    public var backoff: ReconnectBackoff
    /// Random jitter applied to each computed backoff. The actual delay is
    /// uniformly sampled from `[delay * (1 - jitter), delay * (1 + jitter)]`.
    /// `0.0` disables jitter; values are clamped to `[0.0, 1.0]`.
    public var jitter: Double
    /// Maximum number of attempts before giving up. `nil` retries forever.
    public var maxAttempts: Int?
    /// When `true`, the attempt counter resets to zero after a successful
    /// connection so the next reconnect starts at the initial backoff.
    public var resetOnSuccess: Bool

    public init(
        backoff: ReconnectBackoff,
        jitter: Double,
        maxAttempts: Int?,
        resetOnSuccess: Bool
    ) {
        self.backoff = backoff
        self.jitter = jitter
        self.maxAttempts = maxAttempts
        self.resetOnSuccess = resetOnSuccess
    }

    /// Disable reconnects entirely. A single failure leaves the host in
    /// `HostState.failed`; consumers can recover via manual `reconnect`.
    public static let disabled = ReconnectPolicy(
        backoff: .immediate,
        jitter: 0.0,
        maxAttempts: 0,
        resetOnSuccess: true
    )

    /// Retry forever with no backoff. Almost certainly not what you want in
    /// production — useful for tests.
    public static let immediateForever = ReconnectPolicy(
        backoff: .immediate,
        jitter: 0.0,
        maxAttempts: nil,
        resetOnSuccess: true
    )

    /// Sensible default: exponential backoff from 250 ms up to 30 s, 25 %
    /// jitter, retry forever, reset on success.
    public static let exponential = ReconnectPolicy(
        backoff: .exponential(
            initial: .milliseconds(250),
            max: .seconds(30),
            multiplier: 2.0
        ),
        jitter: 0.25,
        maxAttempts: nil,
        resetOnSuccess: true
    )

    /// Compute the delay before the `attempt`-th retry (1-based), applying
    /// jitter via the supplied random sample in `[0.0, 1.0]`.
    ///
    /// Exposed so tests can drive it deterministically; the runtime passes a
    /// real random sample.
    public func delay(forAttempt attempt: Int, sample: Double) -> Duration {
        let base = backoff.delay(forAttempt: attempt)
        if jitter <= 0.0 || base == .zero {
            return base
        }
        let bounded = max(0.0, min(jitter, 1.0))
        let s = max(0.0, min(sample, 1.0))
        // Map [0,1] -> [-jitter, +jitter]
        let factor = 1.0 + (s * 2.0 - 1.0) * bounded
        return .seconds(base.seconds * max(0.0, factor))
    }

    /// Whether `attempt` exceeds `maxAttempts`.
    public func attemptsExhausted(_ attempt: Int) -> Bool {
        guard let cap = maxAttempts else { return false }
        return attempt > cap
    }
}

// MARK: - Duration helpers

extension Duration {
    /// Convert to seconds as a Double for arithmetic. Internal helper —
    /// fractional jitter math is the only place that needs this.
    fileprivate var seconds: Double {
        let comps = components
        return Double(comps.seconds) + Double(comps.attoseconds) / 1e18
    }
}
