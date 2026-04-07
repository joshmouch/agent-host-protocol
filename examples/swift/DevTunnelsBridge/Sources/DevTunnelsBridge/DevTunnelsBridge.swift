/// DevTunnelsBridge — Dev Tunnels connectivity for Swift.
///
/// This package wraps the Microsoft Dev Tunnels Rust SDK via UniFFI,
/// providing tunnel discovery, authentication, and port forwarding
/// for iOS and macOS apps.
///
/// ## Quick Start
///
/// ```swift
/// let client = TunnelClient()
/// try await client.authenticate()
/// let tunnels = try await client.listTunnels()
/// let stream = try await client.connect(to: tunnels[0], port: 31546)
/// ```

// MARK: - Placeholder types (to be replaced by UniFFI-generated + wrapper code)

/// Information about a Dev Tunnel.
public struct TunnelInfo: Sendable {
    /// Unique tunnel identifier.
    public let tunnelId: String
    /// Human-readable tunnel name.
    public let name: String
    /// Whether the tunnel host is currently online.
    public let isOnline: Bool
    /// Forwarded ports available on this tunnel.
    public let ports: [Int]
}

/// Result of starting a device code auth flow.
public struct DeviceCodeAuth: Sendable {
    /// The code the user must enter.
    public let userCode: String
    /// The URL the user must visit.
    public let verificationUri: String
}

/// Errors from Dev Tunnels operations.
public enum TunnelError: Error, Sendable {
    /// Authentication failed or was not completed.
    case authenticationFailed(String)
    /// No tunnels found for the authenticated user.
    case noTunnelsFound
    /// Failed to connect to the tunnel relay.
    case connectionFailed(String)
    /// The requested port is not being forwarded.
    case portNotAvailable(Int)
}
