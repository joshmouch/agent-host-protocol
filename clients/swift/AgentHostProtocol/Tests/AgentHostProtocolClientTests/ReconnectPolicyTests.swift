// ReconnectPolicyTests — backoff + jitter unit tests, mirroring the Rust
// `policy.rs` test module.

import XCTest
@testable import AgentHostProtocolClient

final class ReconnectPolicyTests: XCTestCase {

    func testExponentialBackoffCapsAtMax() {
        let backoff: ReconnectBackoff = .exponential(
            initial: .seconds(1),
            max: .seconds(10),
            multiplier: 2.0
        )
        XCTAssertEqual(backoff.delay(forAttempt: 1), .seconds(1))
        XCTAssertEqual(backoff.delay(forAttempt: 2), .seconds(2))
        XCTAssertEqual(backoff.delay(forAttempt: 3), .seconds(4))
        XCTAssertEqual(backoff.delay(forAttempt: 4), .seconds(8))
        // Capped at max.
        XCTAssertEqual(backoff.delay(forAttempt: 5), .seconds(10))
        XCTAssertEqual(backoff.delay(forAttempt: 50), .seconds(10))
    }

    func testJitterZeroReturnsBaseDelay() {
        let policy = ReconnectPolicy(
            backoff: .constant(.seconds(5)),
            jitter: 0.0,
            maxAttempts: nil,
            resetOnSuccess: true
        )
        XCTAssertEqual(policy.delay(forAttempt: 1, sample: 0.5), .seconds(5))
    }

    func testJitterAtExtremesScalesDelay() {
        let policy = ReconnectPolicy(
            backoff: .constant(.seconds(10)),
            jitter: 0.5,
            maxAttempts: nil,
            resetOnSuccess: true
        )
        // sample 0 -> -50% -> 5s
        XCTAssertEqual(policy.delay(forAttempt: 1, sample: 0.0), .seconds(5))
        // sample 1 -> +50% -> 15s
        XCTAssertEqual(policy.delay(forAttempt: 1, sample: 1.0), .seconds(15))
        // sample 0.5 -> +0% -> 10s
        XCTAssertEqual(policy.delay(forAttempt: 1, sample: 0.5), .seconds(10))
    }

    func testDisabledPolicyExhaustsImmediately() {
        let policy = ReconnectPolicy.disabled
        XCTAssertTrue(policy.attemptsExhausted(1))
    }

    func testUnboundedPolicyNeverExhausts() {
        let policy = ReconnectPolicy.exponential
        XCTAssertFalse(policy.attemptsExhausted(1_000_000))
    }

    func testImmediateBackoffIsZero() {
        let backoff: ReconnectBackoff = .immediate
        XCTAssertEqual(backoff.delay(forAttempt: 1), .zero)
        XCTAssertEqual(backoff.delay(forAttempt: 100), .zero)
    }

    func testSampleClamping() {
        let policy = ReconnectPolicy(
            backoff: .constant(.seconds(10)),
            jitter: 0.5,
            maxAttempts: nil,
            resetOnSuccess: true
        )
        // Samples outside [0, 1] are clamped.
        XCTAssertEqual(policy.delay(forAttempt: 1, sample: -1.0), .seconds(5))
        XCTAssertEqual(policy.delay(forAttempt: 1, sample: 2.0), .seconds(15))
    }
}
