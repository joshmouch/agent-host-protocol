import DevTunnelsBridge
import Security
import SwiftUI

// MARK: - Token Storage

/// Persists the GitHub access token in the iOS Keychain so it survives
/// across sheet presentations and app launches.
private enum TokenStore {
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

    @State private var tunnels: [TunnelInfo] = []
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
            if let saved = TokenStore.load() {
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
                        TokenStore.delete()
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
                ForEach(tunnels, id: \.tunnelId) { tunnel in
                    NavigationLink {
                        TunnelDetailView(
                            tunnel: tunnel,
                            accessToken: accessToken,
                            onConnectToTunnel: onConnectToTunnel
                        )
                    } label: {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(tunnel.name)
                                .font(.body)
                            Text("\(tunnel.clusterId) · \(tunnel.tunnelId)")
                                .font(.caption)
                                .foregroundStyle(.secondary)
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
            let response = try startDeviceCodeAuth()
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
                let result = try pollDeviceCodeAuth(deviceCode: deviceCode)
                switch result {
                case .accessToken(let token):
                    accessToken = token
                    TokenStore.save(token)
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
            tunnels = try listTunnels(accessToken: accessToken)
            isLoading = false
        } catch {
            errorMessage = error.localizedDescription
            isLoading = false
        }
    }
}

// MARK: - Tunnel Detail

struct TunnelDetailView: View {
    let tunnel: TunnelInfo
    let accessToken: String
    var onConnectToTunnel: ((ServerConfiguration) -> Void)?

    @State private var detail: TunnelDetail?
    @State private var isLoading = true
    @State private var errorMessage: String?

    var body: some View {
        List {
            Section("Info") {
                LabeledContent("Name", value: tunnel.name)
                LabeledContent("Tunnel ID", value: tunnel.tunnelId)
                LabeledContent("Cluster", value: tunnel.clusterId)
            }

            if isLoading {
                Section {
                    HStack {
                        ProgressView().controlSize(.small)
                        Text("Loading details…").foregroundStyle(.secondary)
                    }
                }
            } else if let detail {
                if let relayUri = detail.clientRelayUri {
                    Section("Relay") {
                        Text(relayUri)
                            .font(.caption)
                            .textSelection(.enabled)
                    }
                }

                Section("Ports") {
                    if detail.ports.isEmpty {
                        Text("No ports configured")
                            .foregroundStyle(.secondary)
                    } else {
                        ForEach(detail.ports.map { Int($0) }, id: \.self) { port in
                            HStack {
                                VStack(alignment: .leading, spacing: 2) {
                                    Text("Port \(port)")
                                        .font(.body)
                                    Text("\(tunnel.tunnelId)-\(port).\(tunnel.clusterId).devtunnels.ms")
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
        .navigationTitle(tunnel.name)
        .task {
            await loadDetail()
        }
    }

    private func connectToPort(_ port: Int) {
        let host = "\(tunnel.tunnelId)-\(port).\(tunnel.clusterId).devtunnels.ms"
        let server = ServerConfiguration(
            name: "\(tunnel.name) :\(port)",
            scheme: "wss",
            host: host,
            token: accessToken
        )
        onConnectToTunnel?(server)
    }

    private func loadDetail() async {
        do {
            detail = try getTunnelDetail(
                accessToken: accessToken,
                clusterId: tunnel.clusterId,
                tunnelId: tunnel.tunnelId
            )
            isLoading = false
        } catch {
            errorMessage = error.localizedDescription
            isLoading = false
        }
    }
}
