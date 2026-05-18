// HostState — connection state for a single host.

import Foundation

/// Connection state for a single host.
public enum HostState: Sendable, Equatable {
    /// The host has been added but no transport is open.
    case disconnected
    /// A transport is being opened or the `initialize` handshake is in flight.
    case connecting
    /// The host is fully connected and serving subscriptions.
    case connected
    /// A previous connection dropped; the supervisor is retrying with backoff.
    ///
    /// `attempt` is one-based and resets after a successful connect when the
    /// host's `ReconnectPolicy.resetOnSuccess` is `true`.
    case reconnecting(attempt: Int)
    /// Reconnect attempts were exhausted (or `ReconnectPolicy.disabled` was
    /// configured) and the host is no longer trying. The supervisor still
    /// services `snapshot`, manual `reconnect`, and `shutdown` commands while
    /// in this state.
    case failed(reason: String)

    /// Convenience: is the host currently `.connected`?
    public var isConnected: Bool {
        if case .connected = self { return true }
        return false
    }

    /// Convenience: is the host in a terminal failure state?
    public var isFailed: Bool {
        if case .failed = self { return true }
        return false
    }
}
