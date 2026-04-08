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

use serde::Deserialize;
use tunnels::contracts::TunnelRelayTunnelEndpoint;
use tunnels::management::{
    new_tunnel_management, Authorization, HttpError, TunnelLocator, TunnelManagementClient,
    TunnelRequestOptions,
};

// ─── GitHub OAuth Constants ──────────────────────────────────────────────────

/// GitHub OAuth App client ID used by Dev Tunnels.
/// Same as the VS Code tunnel extension uses.
const GITHUB_CLIENT_ID: &str = "01ab8ac9400c4e429b23";

/// OAuth scopes needed by the Dev Tunnels management API.
const GITHUB_SCOPES: &str = "user:email read:org";

// ─── FFI Types ───────────────────────────────────────────────────────────────

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

/// Detailed tunnel info including relay URI for connection.
#[derive(uniffi::Record)]
pub struct TunnelDetail {
    /// Unique tunnel identifier.
    pub tunnel_id: String,
    /// Human-readable tunnel name.
    pub name: String,
    /// Cluster ID where the tunnel is hosted.
    pub cluster_id: String,
    /// Client relay URI (wss://...) for connecting to this tunnel.
    pub client_relay_uri: Option<String>,
    /// Forwarded port numbers available on this tunnel.
    pub ports: Vec<u16>,
    /// Tunnel access token with "connect" scope, for authenticating
    /// to the devtunnels.ms forwarded port endpoint.
    pub connect_access_token: Option<String>,
}

/// Result of starting a GitHub device code auth flow.
/// Display `user_code` and `verification_uri` to the user, then poll
/// with `poll_device_code_auth`.
#[derive(uniffi::Record)]
pub struct DeviceCodeResponse {
    /// The device code (used for polling, not shown to user).
    pub device_code: String,
    /// The code the user enters at the verification URI.
    pub user_code: String,
    /// The URL the user visits to enter the code.
    pub verification_uri: String,
    /// Seconds until the device code expires.
    pub expires_in: u32,
    /// Minimum seconds between poll attempts.
    pub interval: u32,
}

/// Result of polling for device code completion.
#[derive(uniffi::Enum)]
pub enum DeviceCodePollResult {
    /// User completed authorization. Contains the access token.
    AccessToken { token: String },
    /// Authorization is still pending — poll again after `interval` seconds.
    Pending,
    /// The device code expired. Start a new flow.
    Expired,
    /// The flow was denied or encountered an error.
    Error { message: String },
}

/// Errors from Dev Tunnels operations.
#[derive(Debug, thiserror::Error, uniffi::Error)]
pub enum TunnelError {
    #[error("Authentication failed: {message}")]
    AuthenticationFailed { message: String },

    #[error("No tunnels found")]
    NoTunnelsFound,

    #[error("Tunnel not found: {message}")]
    TunnelNotFound { message: String },

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

// ─── Helpers ─────────────────────────────────────────────────────────────────

fn make_client(access_token: &str) -> TunnelManagementClient {
    let mut builder = new_tunnel_management("DevTunnelsBridge/0.1");
    builder.authorization(Authorization::Github(access_token.to_string()));
    builder.into()
}

fn make_runtime() -> Result<tokio::runtime::Runtime, TunnelError> {
    tokio::runtime::Runtime::new().map_err(|e| TunnelError::ApiError {
        message: e.to_string(),
    })
}

// ─── Tunnel Management ──────────────────────────────────────────────────────

/// List all tunnels for the authenticated user.
#[uniffi::export]
pub fn list_tunnels(access_token: String) -> Result<Vec<TunnelInfo>, TunnelError> {
    let rt = make_runtime()?;
    rt.block_on(async {
        let client = make_client(&access_token);
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

/// Get detailed info about a specific tunnel, including relay URI and ports.
///
/// The relay URI is extracted by re-deserializing the endpoint JSON as
/// TunnelRelayTunnelEndpoint, since `Tunnel.endpoints` is typed as
/// `Vec<TunnelEndpoint>` which doesn't include relay-specific fields.
#[uniffi::export]
pub fn get_tunnel_detail(
    access_token: String,
    cluster_id: String,
    tunnel_id: String,
) -> Result<TunnelDetail, TunnelError> {
    let rt = make_runtime()?;
    rt.block_on(async {
        let client = make_client(&access_token);
        let locator = TunnelLocator::ID {
            cluster: cluster_id,
            id: tunnel_id,
        };
        let mut options = TunnelRequestOptions::default();
        options.include_ports = true;
        options.token_scopes = vec!["connect".to_string()];

        let tunnel = client.get_tunnel(&locator, &options).await?;

        // Extract relay URI: re-serialize the endpoint and try parsing as relay type
        let client_relay_uri = tunnel
            .endpoints
            .iter()
            .filter(|ep| matches!(ep.connection_mode, tunnels::contracts::TunnelConnectionMode::TunnelRelay))
            .find_map(|ep| {
                let json = serde_json::to_value(ep).ok()?;
                let relay: TunnelRelayTunnelEndpoint = serde_json::from_value(json).ok()?;
                relay.client_relay_uri
            });

        let connect_access_token = tunnel
            .access_tokens
            .as_ref()
            .and_then(|tokens| tokens.get("connect").cloned());

        let ports = tunnel.ports.iter().map(|p| p.port_number).collect();

        Ok(TunnelDetail {
            tunnel_id: tunnel.tunnel_id.unwrap_or_default(),
            name: tunnel.name.unwrap_or_default(),
            cluster_id: tunnel.cluster_id.unwrap_or_default(),
            client_relay_uri,
            ports,
            connect_access_token,
        })
    })
}

// ─── Device Code Auth ───────────────────────────────────────────────────────

#[derive(Deserialize)]
struct GithubDeviceCodeResponse {
    device_code: String,
    user_code: String,
    verification_uri: String,
    expires_in: u32,
    interval: u32,
}

#[derive(Deserialize)]
struct GithubTokenResponse {
    access_token: Option<String>,
    error: Option<String>,
    error_description: Option<String>,
}

/// Start a GitHub device code authentication flow.
///
/// Returns a `DeviceCodeResponse` containing the `user_code` and
/// `verification_uri` to show to the user. After the user authorizes,
/// poll with `poll_device_code_auth` using the returned `device_code`.
#[uniffi::export]
pub fn start_device_code_auth() -> Result<DeviceCodeResponse, TunnelError> {
    let rt = make_runtime()?;
    rt.block_on(async {
        let client = reqwest::Client::new();
        let res = client
            .post("https://github.com/login/device/code")
            .header("Accept", "application/json")
            .form(&[
                ("client_id", GITHUB_CLIENT_ID),
                ("scope", GITHUB_SCOPES),
            ])
            .send()
            .await
            .map_err(|e| TunnelError::AuthenticationFailed {
                message: e.to_string(),
            })?;

        if !res.status().is_success() {
            return Err(TunnelError::AuthenticationFailed {
                message: format!("GitHub returned {}", res.status()),
            });
        }

        let body: GithubDeviceCodeResponse =
            res.json().await.map_err(|e| TunnelError::AuthenticationFailed {
                message: e.to_string(),
            })?;

        Ok(DeviceCodeResponse {
            device_code: body.device_code,
            user_code: body.user_code,
            verification_uri: body.verification_uri,
            expires_in: body.expires_in,
            interval: body.interval,
        })
    })
}

/// Poll GitHub for device code authorization completion.
///
/// Call this repeatedly with the `device_code` from `start_device_code_auth`,
/// waiting at least `interval` seconds between calls.
#[uniffi::export]
pub fn poll_device_code_auth(device_code: String) -> Result<DeviceCodePollResult, TunnelError> {
    let rt = make_runtime()?;
    rt.block_on(async {
        let client = reqwest::Client::new();
        let res = client
            .post("https://github.com/login/oauth/access_token")
            .header("Accept", "application/json")
            .form(&[
                ("client_id", GITHUB_CLIENT_ID),
                ("device_code", device_code.as_str()),
                (
                    "grant_type",
                    "urn:ietf:params:oauth:grant-type:device_code",
                ),
            ])
            .send()
            .await
            .map_err(|e| TunnelError::AuthenticationFailed {
                message: e.to_string(),
            })?;

        let body: GithubTokenResponse =
            res.json().await.map_err(|e| TunnelError::AuthenticationFailed {
                message: e.to_string(),
            })?;

        if let Some(token) = body.access_token {
            return Ok(DeviceCodePollResult::AccessToken { token });
        }

        match body.error.as_deref() {
            Some("authorization_pending") => Ok(DeviceCodePollResult::Pending),
            Some("slow_down") => Ok(DeviceCodePollResult::Pending),
            Some("expired_token") => Ok(DeviceCodePollResult::Expired),
            Some(err) => Ok(DeviceCodePollResult::Error {
                message: body
                    .error_description
                    .unwrap_or_else(|| err.to_string()),
            }),
            None => Ok(DeviceCodePollResult::Error {
                message: "Unknown error".to_string(),
            }),
        }
    })
}
