import DevTunnelsClient
import Security
import SwiftUI

// MARK: - Tunnel + Identifiable

extension Tunnel: @retroactive Identifiable {
    public var id: String { tunnelId ?? UUID().uuidString }

    /// Tunnel labels used by VS Code's CLI to mark service-owned tunnels.
    /// These are infrastructure tags, not user-visible names, so we filter
    /// them out when picking a friendly label for display.
    /// Source: `cli/src/tunnels/dev_tunnels.rs` in microsoft/vscode.
    private static let vscodeReservedLabels: Set<String> = [
        "vscode-server-launcher",
        "vscode-port-forward",
    ]

    /// Human-readable display name. Prefers (in order): the tunnel's `name`
    /// field, the first non-reserved entry in `labels` (VS Code's CLI stores
    /// the user-facing machine name there), and finally the tunnel ID.
    var displayName: String {
        if let name, !name.isEmpty { return name }
        if let labels {
            if let friendly = labels.first(where: { !$0.isEmpty && !Self.vscodeReservedLabels.contains($0) }) {
                return friendly
            }
        }
        return tunnelId ?? ""
    }
}

// MARK: - Token Storage

/// Persists the GitHub access token in the iOS Keychain so it survives
/// across sheet presentations and app launches.
enum TunnelTokenStore {
    private static let service = "com.rebornix.AHPClient.DevTunnels"
    private static let account = "github-token"

    static func save(_ token: String) {
        let data = Data(token.utf8)
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(query as CFDictionary)
        var addQuery = query
        addQuery[kSecValueData as String] = data
        SecItemAdd(addQuery as CFDictionary, nil)
    }

    static func load() -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data,
              let token = String(data: data, encoding: .utf8)
        else { return nil }
        return token
    }

    static func delete() {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(query as CFDictionary)
    }
}

// MARK: - TunnelListView

/// View for browsing Dev Tunnels and initiating device code authentication.
struct TunnelListView: View {
    /// Called when the user selects a tunnel port to use as an AHP server.
    var onConnectToTunnel: ((ServerConfiguration) -> Void)?

    @State private var tunnels: [Tunnel] = []
    @State private var accessToken: String = ""
    @State private var isLoading = false
    @State private var errorMessage: String?

    // Device code auth state
    @State private var deviceCodeResponse: DeviceCodeResponse?
    @State private var isPolling = false
    @State private var authMessage: String?

    var body: some View {
        List {
            authSection
            if !accessToken.isEmpty {
                tunnelListSection
            }
        }
        .navigationTitle("Dev Tunnels")
        .navigationBarTitleDisplayMode(.inline)
        .refreshable {
            await loadTunnels()
        }
        .task {
            if let saved = TunnelTokenStore.load() {
                accessToken = saved
                await loadTunnels()
            }
        }
    }

    // MARK: - Auth Section

    @ViewBuilder
    private var authSection: some View {
        Section {
            if accessToken.isEmpty {
                if let dcr = deviceCodeResponse {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Sign in with GitHub")
                            .font(.headline)
                        Text("Go to **\(dcr.verificationUri)** and enter:")
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                        Text(dcr.userCode)
                            .font(.system(.title, design: .monospaced, weight: .bold))
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .center)
                            .padding(.vertical, 4)

                        if isPolling {
                            HStack {
                                ProgressView()
                                    .controlSize(.small)
                                Text("Waiting for authorization…")
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                        }

                        if let msg = authMessage {
                            Text(msg)
                                .font(.caption)
                                .foregroundStyle(.orange)
                        }
                    }
                } else {
                    Button {
                        Task { await startAuth() }
                    } label: {
                        Label("Sign in with GitHub", systemImage: "person.badge.key")
                    }
                }
            } else {
                HStack {
                    Image(systemName: "checkmark.circle.fill")
                        .foregroundStyle(.green)
                    Text("Authenticated")
                    Spacer()
                    Button("Sign Out") {
                        TunnelTokenStore.delete()
                        accessToken = ""
                        tunnels = []
                        deviceCodeResponse = nil
                    }
                    .font(.caption)
                    .buttonStyle(.borderless)
                }
            }
        } header: {
            Text("Authentication")
        }
    }

    // MARK: - Tunnel List Section

    @ViewBuilder
    private var tunnelListSection: some View {
        Section {
            if isLoading {
                HStack {
                    ProgressView()
                        .controlSize(.small)
                    Text("Loading tunnels…")
                        .foregroundStyle(.secondary)
                }
            } else if tunnels.isEmpty {
                Text("No tunnels found")
                    .foregroundStyle(.secondary)
            } else {
                ForEach(tunnels) { tunnel in
                    NavigationLink {
                        TunnelDetailView(
                            tunnel: tunnel,
                            accessToken: accessToken,
                            onConnectToTunnel: onConnectToTunnel
                        )
                    } label: {
                        HStack {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(tunnel.displayName)
                                    .font(.body)
                                Text("\(tunnel.clusterId ?? "") · \(tunnel.tunnelId ?? "")")
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                            Spacer()
                            if onConnectToTunnel != nil {
                                Button("Connect") {
                                    connectTunnel(tunnel)
                                }
                                .buttonStyle(.borderedProminent)
                                .controlSize(.small)
                            }
                        }
                    }
                }
            }

            if let err = errorMessage {
                Label(err, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .font(.caption)
            }
        } header: {
            Text("Tunnels")
        }
    }

    // MARK: - Actions

    private func startAuth() async {
        do {
            let response = try await DeviceCodeAuth().start()
            deviceCodeResponse = response
            isPolling = true
            authMessage = nil
            await pollForToken(deviceCode: response.deviceCode)
        } catch {
            authMessage = error.localizedDescription
        }
    }

    private func pollForToken(deviceCode: String) async {
        let interval: UInt64 = 5_000_000_000 // 5 seconds
        while isPolling {
            try? await Task.sleep(nanoseconds: interval)
            do {
                let result = try await DeviceCodeAuth().poll(deviceCode: deviceCode)
                switch result {
                case .accessToken(let token):
                    accessToken = token
                    TunnelTokenStore.save(token)
                    isPolling = false
                    deviceCodeResponse = nil
                    await loadTunnels()
                case .pending:
                    continue
                case .expired:
                    authMessage = "Code expired. Please try again."
                    isPolling = false
                    deviceCodeResponse = nil
                case .error(let message):
                    authMessage = message
                    isPolling = false
                }
            } catch {
                authMessage = error.localizedDescription
                isPolling = false
            }
        }
    }

    private func loadTunnels() async {
        guard !accessToken.isEmpty else { return }
        isLoading = true
        errorMessage = nil
        do {
            let client = TunnelManagementClient(accessToken: accessToken)
            tunnels = try await client.listTunnels()
            isLoading = false
        } catch {
            errorMessage = error.localizedDescription
            isLoading = false
        }
    }

    /// Connect directly to a tunnel's AHP port (31546), fetching the connect
    /// access token in the background.
    private func connectTunnel(_ tunnel: Tunnel) {
        Task {
            let port = TunnelDetailView.agentHostPort
            let tunnelId = tunnel.tunnelId ?? ""
            let clusterId = tunnel.clusterId ?? ""
            var connectToken: String?
            do {
                let client = TunnelManagementClient(accessToken: accessToken)
                let detail = try await client.getTunnel(
                    clusterId: clusterId,
                    tunnelId: tunnelId,
                    options: TunnelRequestOptions(
                        includePorts: true,
                        tokenScopes: [TunnelAccessScopes.connect]
                    )
                )
                connectToken = TunnelConnection.connectToken(from: detail)
            } catch {
                print("[AHP] Warning: failed to fetch connect token: \(error)")
            }
            let host = "\(tunnelId)-\(port).\(clusterId).devtunnels.ms"
            let server = ServerConfiguration(
                name: tunnel.displayName,
                scheme: "wss",
                host: host,
                token: accessToken,
                tunnelId: tunnelId,
                clusterId: clusterId,
                connectAccessToken: connectToken
            )
            onConnectToTunnel?(server)
        }
    }
}

// MARK: - Tunnel Detail

struct TunnelDetailView: View {
    /// Agent Host Protocol port, matching VS Code's convention.
    static let agentHostPort = 31546

    let tunnel: Tunnel
    let accessToken: String
    var onConnectToTunnel: ((ServerConfiguration) -> Void)?

    @State private var detail: Tunnel?
    @State private var isLoading = true
    @State private var errorMessage: String?

    var body: some View {
        List {
            Section("Info") {
                LabeledContent("Name", value: tunnel.displayName)
                LabeledContent("Tunnel ID", value: tunnel.tunnelId ?? "")
                LabeledContent("Cluster", value: tunnel.clusterId ?? "")
            }

            if isLoading {
                Section {
                    HStack {
                        ProgressView().controlSize(.small)
                        Text("Loading details…").foregroundStyle(.secondary)
                    }
                }
            } else if let detail {
                if let relayUri = TunnelConnection.clientRelayURI(from: detail) {
                    Section("Relay") {
                        Text(relayUri)
                            .font(.caption)
                            .textSelection(.enabled)
                    }
                }

                Section("Ports") {
                    let ports = detail.ports ?? []
                    if ports.isEmpty {
                        Text("No ports configured")
                            .foregroundStyle(.secondary)
                    } else {
                        ForEach(sortedPorts(ports), id: \.self) { port in
                            HStack {
                                VStack(alignment: .leading, spacing: 2) {
                                    HStack(spacing: 6) {
                                        Text("Port \(port)")
                                            .font(.body)
                                        if port == TunnelDetailView.agentHostPort {
                                            Text("AHP")
                                                .font(.caption2.weight(.semibold))
                                                .padding(.horizontal, 5)
                                                .padding(.vertical, 1)
                                                .background(.blue.opacity(0.15))
                                                .foregroundStyle(.blue)
                                                .clipShape(Capsule())
                                        }
                                    }
                                    Text("\(tunnel.tunnelId ?? "")-\(port).\(tunnel.clusterId ?? "").devtunnels.ms")
                                        .font(.caption)
                                        .foregroundStyle(.secondary)
                                }
                                Spacer()
                                if onConnectToTunnel != nil {
                                    Button("Connect") {
                                        connectToPort(port)
                                    }
                                    .buttonStyle(.borderedProminent)
                                    .controlSize(.small)
                                }
                            }
                        }
                    }
                }
            }

            if let err = errorMessage {
                Section {
                    Label(err, systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.red)
                        .font(.caption)
                }
            }
        }
        .navigationTitle(tunnel.displayName)
        .task {
            await loadDetail()
        }
    }

    /// Sort ports so the AHP port (31546) appears first.
    private func sortedPorts(_ ports: [TunnelPort]) -> [Int] {
        ports.map { Int($0.portNumber) }.sorted { a, b in
            if a == Self.agentHostPort { return true }
            if b == Self.agentHostPort { return false }
            return a < b
        }
    }

    private func connectToPort(_ port: Int) {
        let tunnelId = tunnel.tunnelId ?? ""
        let clusterId = tunnel.clusterId ?? ""
        let host = "\(tunnelId)-\(port).\(clusterId).devtunnels.ms"
        let connectToken: String? = detail.flatMap { TunnelConnection.connectToken(from: $0) }
        let server = ServerConfiguration(
            name: tunnel.displayName,
            scheme: "wss",
            host: host,
            token: accessToken,
            tunnelId: tunnelId,
            clusterId: clusterId,
            connectAccessToken: connectToken
        )
        onConnectToTunnel?(server)
    }

    private func loadDetail() async {
        do {
            let client = TunnelManagementClient(accessToken: accessToken)
            detail = try await client.getTunnel(
                clusterId: tunnel.clusterId ?? "",
                tunnelId: tunnel.tunnelId ?? "",
                options: TunnelRequestOptions(
                    includePorts: true,
                    tokenScopes: [TunnelAccessScopes.connect]
                )
            )
            isLoading = false
        } catch {
            errorMessage = error.localizedDescription
            isLoading = false
        }
    }
}
