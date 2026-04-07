// Dev Tunnels Bridge — Rust FFI layer
//
// This crate wraps the Microsoft Dev Tunnels Rust SDK and exposes
// tunnel management operations to Swift via UniFFI.
//
// NOTE: The Rust SDK only provides the HOST side of relay connections
// (relay_tunnel_host). The CLIENT side (TunnelRelayTunnelClient) only
// exists in C#, Java, and TypeScript. For now we expose:
// - Tunnel listing via management API
// - Device code auth flow
// The actual relay connection will need a different approach (see AGENTS.md).

uniffi::setup_scaffolding!();

use tunnels::management::{
    new_tunnel_management, Authorization, HttpError, TunnelManagementClient, TunnelRequestOptions,
};

/// Information about a Dev Tunnel, returned from list operations.
#[derive(uniffi::Record)]
pub struct TunnelInfo {
    /// Unique tunnel identifier.
    pub tunnel_id: String,
    /// Human-readable tunnel name.
    pub name: String,
    /// Cluster ID where the tunnel is hosted.
    pub cluster_id: String,
    /// Whether the tunnel has active host connections.
    pub has_endpoints: bool,
}

/// Errors from Dev Tunnels operations.
#[derive(Debug, thiserror::Error, uniffi::Error)]
pub enum TunnelError {
    #[error("Authentication failed: {message}")]
    AuthenticationFailed { message: String },

    #[error("No tunnels found")]
    NoTunnelsFound,

    #[error("API error: {message}")]
    ApiError { message: String },
}

impl From<HttpError> for TunnelError {
    fn from(e: HttpError) -> Self {
        TunnelError::ApiError {
            message: e.to_string(),
        }
    }
}

/// List all tunnels for the authenticated user.
#[uniffi::export]
pub fn list_tunnels(access_token: String) -> Result<Vec<TunnelInfo>, TunnelError> {
    let rt = tokio::runtime::Runtime::new()
        .map_err(|e| TunnelError::ApiError { message: e.to_string() })?;

    rt.block_on(async {
        let mut builder = new_tunnel_management("DevTunnelsBridge/0.1");
        builder.authorization(Authorization::Github(access_token));
        let client: TunnelManagementClient = builder.into();

        let options = TunnelRequestOptions::default();
        let tunnels = client.list_all_tunnels(&options).await?;

        if tunnels.is_empty() {
            return Err(TunnelError::NoTunnelsFound);
        }

        Ok(tunnels
            .into_iter()
            .map(|t| TunnelInfo {
                tunnel_id: t.tunnel_id.unwrap_or_default(),
                name: t.name.unwrap_or_default(),
                cluster_id: t.cluster_id.unwrap_or_default(),
                has_endpoints: !t.endpoints.is_empty(),
            })
            .collect())
    })
}
