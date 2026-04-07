// Dev Tunnels Bridge — Rust FFI layer
//
// This crate wraps the Dev Tunnels Rust SDK and exposes operations
// to Swift via UniFFI. The generated bindings provide async-compatible
// functions for tunnel discovery, authentication, and connection.

uniffi::setup_scaffolding!();

/// Information about a Dev Tunnel, returned from list operations.
#[derive(uniffi::Record)]
pub struct TunnelInfo {
    /// Unique tunnel identifier.
    pub tunnel_id: String,
    /// Human-readable tunnel name.
    pub name: String,
    /// Whether the tunnel host is currently online.
    pub is_online: bool,
    /// Forwarded ports available on this tunnel.
    pub ports: Vec<i32>,
}

/// Result of starting a device code authentication flow.
#[derive(uniffi::Record)]
pub struct DeviceCodeResult {
    /// The code the user must enter at the verification URI.
    pub user_code: String,
    /// The URL the user must visit to enter the code.
    pub verification_uri: String,
}

/// Errors from Dev Tunnels operations.
#[derive(Debug, thiserror::Error, uniffi::Error)]
pub enum TunnelError {
    #[error("Authentication failed: {message}")]
    AuthenticationFailed { message: String },

    #[error("No tunnels found")]
    NoTunnelsFound,

    #[error("Connection failed: {message}")]
    ConnectionFailed { message: String },

    #[error("Port {port} is not available")]
    PortNotAvailable { port: i32 },
}

// ─── Placeholder implementations ─────────────────────────────────────────────
// These will be replaced with real Dev Tunnels SDK calls.

/// List all tunnels for the authenticated user.
#[uniffi::export]
pub fn list_tunnels(_access_token: String) -> Result<Vec<TunnelInfo>, TunnelError> {
    // TODO: Call dev-tunnels management API
    Err(TunnelError::AuthenticationFailed {
        message: "Not yet implemented".to_string(),
    })
}

/// Start a GitHub device code authentication flow.
#[uniffi::export]
pub fn start_device_code_auth(_client_id: String) -> Result<DeviceCodeResult, TunnelError> {
    // TODO: POST to https://github.com/login/device/code
    Err(TunnelError::AuthenticationFailed {
        message: "Not yet implemented".to_string(),
    })
}
