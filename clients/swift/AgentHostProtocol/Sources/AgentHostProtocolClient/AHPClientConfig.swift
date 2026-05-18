// AHPClientConfig — knobs for an `AHPClient` instance.

import Foundation

/// Tunable settings for an `AHPClient` instance.
public struct AHPClientConfig: Sendable {
    /// Maximum time a `request` will wait for its response before failing
    /// with `AHPClientError.requestTimeout`. Defaults to 30 seconds.
    public var requestTimeout: Duration

    /// Buffer size for the multicast `events` and `stateChanges` streams.
    /// When a consumer falls behind by more than this many items, the oldest
    /// items are dropped (`.bufferingNewest`). Per-URI subscription streams
    /// are *unbounded* regardless of this value, since dropping action
    /// envelopes desyncs the consumer's reducer mirror.
    ///
    /// Values less than 1 are silently clamped to 1 by the initializer so
    /// the buffering policy always retains at least the most recent item.
    ///
    /// Defaults to 256.
    public var subscriptionBufferSize: Int

    public init(
        requestTimeout: Duration = .seconds(30),
        subscriptionBufferSize: Int = 256
    ) {
        self.requestTimeout = requestTimeout
        // `.bufferingNewest(0)` means "buffer nothing" and `.bufferingNewest`
        // with a negative count is undefined; clamp to 1 so the public API
        // never lets the consumer reach those edges by accident.
        self.subscriptionBufferSize = max(1, subscriptionBufferSize)
    }

    public static let `default` = AHPClientConfig()
}
