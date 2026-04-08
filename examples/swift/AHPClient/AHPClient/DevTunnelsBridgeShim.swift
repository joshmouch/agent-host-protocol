/// DevTunnelsBridge compatibility shim.
///
/// Maps the old DevTunnelsBridge free-function API (from UniFFI/Rust) to the new
/// pure-Swift DevTunnelsClient library. This lets existing call sites work unchanged.

import DevTunnelsClient
import Foundation

// MARK: - Re-exported Types

/// Re-export DeviceCodeResponse and DeviceCodePollResult so call sites
/// that previously used `import DevTunnelsBridge` can see these types.
public typealias DeviceCodeResponse = DevTunnelsClient.DeviceCodeResponse
public typealias DeviceCodePollResult = DevTunnelsClient.DeviceCodePollResult

// MARK: - Wrapper Structs

/// Maps the old `TunnelInfo` (flat struct from Rust FFI) to the new `Tunnel` type.
/// We re-export a lightweight wrapper so existing views compile unchanged.
public struct TunnelInfo: Identifiable {
    public var id: String { tunnelId }
    public let tunnelId: String
    public let name: String
    public let clusterId: String
    public let hasEndpoints: Bool

    init(from tunnel: Tunnel) {
        self.tunnelId = tunnel.tunnelId ?? ""
        self.name = tunnel.name ?? ""
        self.clusterId = tunnel.clusterId ?? ""
        self.hasEndpoints = TunnelConnection.isOnline(tunnel)
    }
}

/// Maps the old `TunnelDetail` (from Rust FFI) to the new `Tunnel` type with extra fields.
public struct TunnelDetail {
    public let tunnelId: String
    public let name: String
    public let clusterId: String
    public let clientRelayUri: String?
    public let ports: [UInt16]
    public let connectAccessToken: String?

    init(from tunnel: Tunnel) {
        self.tunnelId = tunnel.tunnelId ?? ""
        self.name = tunnel.name ?? ""
        self.clusterId = tunnel.clusterId ?? ""
        self.clientRelayUri = TunnelConnection.clientRelayURI(from: tunnel)
        self.ports = (tunnel.ports ?? []).map { $0.portNumber }
        self.connectAccessToken = TunnelConnection.connectToken(from: tunnel)
    }
}

// MARK: - Free Functions (matching old DevTunnelsBridge API)

/// Starts the GitHub device code OAuth flow.
public func startDeviceCodeAuth() async throws -> DeviceCodeResponse {
    try await DeviceCodeAuth().start()
}

/// Polls for device code authorization completion.
public func pollDeviceCodeAuth(deviceCode: String) async throws -> DeviceCodePollResult {
    try await DeviceCodeAuth().poll(deviceCode: deviceCode)
}

/// Lists all tunnels for the authenticated user.
public func listTunnels(accessToken: String) async throws -> [TunnelInfo] {
    let client = TunnelManagementClient(accessToken: accessToken)
    let tunnels = try await client.listTunnels()
    return tunnels.map { TunnelInfo(from: $0) }
}

/// Gets tunnel details including ports and connect access token.
public func getTunnelDetail(accessToken: String, clusterId: String, tunnelId: String) async throws -> TunnelDetail {
    let client = TunnelManagementClient(accessToken: accessToken)
    let tunnel = try await client.getTunnel(
        clusterId: clusterId,
        tunnelId: tunnelId,
        options: TunnelRequestOptions(
            includePorts: true,
            tokenScopes: [TunnelAccessScopes.connect]
        )
    )
    return TunnelDetail(from: tunnel)
}
