import SwiftUI

// MARK: - HostPickerView

/// Home screen that presents two options for connecting to an agent host:
/// 1. **My Hosts** — List running hosts from the Codamente registry.
/// 2. **New Codespace** — Provision a Codespace with an agent host.
///
/// Also shows the manual "Add Server" option as a fallback for direct connections.
struct HostPickerView: View {
    @Environment(AppStore.self) private var store
    @State private var remoteHosts: [RemoteHost] = []
    @State private var isLoadingHosts = false
    @State private var hostError: String?
    @State private var showCodespaceSetup = false
    @State private var showAddServer = false
    @State private var showSettings = false

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 24) {
                    headerSection
                    remoteHostsSection
                    codespaceSection
                    manualServerSection
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 12)
            }
            .navigationTitle("Connect")
            .navigationBarTitleDisplayMode(.large)
            .toolbar {
                ToolbarItem(placement: .navigationBarTrailing) {
                    Menu {
                        if let user = store.authManager.user {
                            Section {
                                Label(user.login, systemImage: "person.circle")
                            }
                        }
                        Button {
                            showSettings = true
                        } label: {
                            Label("Settings", systemImage: "gear")
                        }
                        Button(role: .destructive) {
                            store.authManager.signOut()
                        } label: {
                            Label("Sign Out", systemImage: "rectangle.portrait.and.arrow.right")
                        }
                    } label: {
                        if let user = store.authManager.user {
                            AsyncImage(url: URL(string: user.avatarUrl ?? "")) { image in
                                image.resizable()
                            } placeholder: {
                                Image(systemName: "person.circle.fill")
                                    .resizable()
                            }
                            .frame(width: 28, height: 28)
                            .clipShape(Circle())
                        } else {
                            Image(systemName: "person.circle")
                        }
                    }
                }
            }
            .refreshable {
                await loadRemoteHosts()
            }
            .task {
                await loadRemoteHosts()
            }
            .sheet(isPresented: $showCodespaceSetup) {
                CodespaceSetupView()
                    .environment(store)
            }
            .sheet(isPresented: $showAddServer) {
                AddServerView { server in
                    store.addServer(server)
                    store.selectServer(server.id)
                    Task { await store.connect() }
                }
                .environment(store)
            }
            .sheet(isPresented: $showSettings) {
                SettingsView()
                    .environment(store)
            }
        }
    }

    // MARK: - Sections

    @ViewBuilder
    private var headerSection: some View {
        VStack(spacing: 8) {
            Image(systemName: "bubble.left.and.bubble.right")
                .font(.system(size: 40))
                .foregroundStyle(.blue)

            Text("AHP Client")
                .font(.title2.bold())

            Text("Choose how to connect to an agent host")
                .font(.subheadline)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 8)
    }

    @ViewBuilder
    private var remoteHostsSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Label("My Hosts", systemImage: "server.rack")
                    .font(.headline)
                Spacer()
                if isLoadingHosts {
                    ProgressView()
                        .controlSize(.small)
                }
            }

            Text("Agent hosts registered with the Codamente service.")
                .font(.caption)
                .foregroundStyle(.secondary)

            if let hostError {
                Label(hostError, systemImage: "exclamationmark.triangle")
                    .font(.caption)
                    .foregroundStyle(.orange)
            }

            if remoteHosts.isEmpty && !isLoadingHosts {
                VStack(spacing: 8) {
                    Text("No hosts available")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                    Text("Start the Codamente extension in VS Code to share an agent host.")
                        .font(.caption)
                        .foregroundStyle(.tertiary)
                        .multilineTextAlignment(.center)
                }
                .frame(maxWidth: .infinity)
                .padding(.vertical, 16)
                .modifier(PickerCardStyle())
            } else {
                ForEach(remoteHosts) { host in
                    Button {
                        Task { await connectToRemoteHost(host) }
                    } label: {
                        HStack(spacing: 12) {
                            Image(systemName: "desktopcomputer")
                                .font(.title3)
                                .foregroundStyle(.blue)
                                .frame(width: 36)

                            VStack(alignment: .leading, spacing: 2) {
                                Text(host.hostName)
                                    .font(.body.weight(.medium))
                                    .foregroundStyle(.primary)
                                Text(host.tunnelUrl)
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                                    .lineLimit(1)
                                    .truncationMode(.middle)
                            }

                            Spacer()

                            Image(systemName: "chevron.right")
                                .font(.footnote.weight(.semibold))
                                .foregroundStyle(.tertiary)
                        }
                        .padding(.horizontal, 16)
                        .padding(.vertical, 14)
                        .modifier(PickerCardStyle())
                    }
                    .buttonStyle(.plain)
                }
            }
        }
    }

    @ViewBuilder
    private var codespaceSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            Label("New Codespace", systemImage: "cloud")
                .font(.headline)

            Text("Provision a GitHub Codespace with an agent host for any repository.")
                .font(.caption)
                .foregroundStyle(.secondary)

            Button {
                showCodespaceSetup = true
            } label: {
                HStack(spacing: 12) {
                    Image(systemName: "plus.circle.fill")
                        .font(.title3)
                        .foregroundStyle(.green)
                        .frame(width: 36)

                    VStack(alignment: .leading, spacing: 2) {
                        Text("Create Codespace")
                            .font(.body.weight(.medium))
                            .foregroundStyle(.primary)
                        Text("Pick a repository and start an agent host in the cloud")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }

                    Spacer()

                    Image(systemName: "chevron.right")
                        .font(.footnote.weight(.semibold))
                        .foregroundStyle(.tertiary)
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 14)
                .modifier(PickerCardStyle())
            }
            .buttonStyle(.plain)
        }
    }

    @ViewBuilder
    private var manualServerSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            Label("Manual Connection", systemImage: "link")
                .font(.headline)

            Text("Connect directly to a local or remote AHP server.")
                .font(.caption)
                .foregroundStyle(.secondary)

            // Show existing saved servers
            ForEach(store.servers) { server in
                Button {
                    store.selectServer(server.id)
                    Task { await store.connect() }
                } label: {
                    HStack(spacing: 12) {
                        Image(systemName: "antenna.radiowaves.left.and.right")
                            .font(.title3)
                            .foregroundStyle(.purple)
                            .frame(width: 36)

                        VStack(alignment: .leading, spacing: 2) {
                            Text(server.name)
                                .font(.body.weight(.medium))
                                .foregroundStyle(.primary)
                            Text("\(server.scheme)://\(server.host)")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }

                        Spacer()

                        if server.id == store.selectedServerId {
                            Image(systemName: "checkmark.circle.fill")
                                .foregroundStyle(.blue)
                        }

                        Image(systemName: "chevron.right")
                            .font(.footnote.weight(.semibold))
                            .foregroundStyle(.tertiary)
                    }
                    .padding(.horizontal, 16)
                    .padding(.vertical, 14)
                    .modifier(PickerCardStyle())
                }
                .buttonStyle(.plain)
            }

            Button {
                showAddServer = true
            } label: {
                HStack(spacing: 12) {
                    Image(systemName: "plus")
                        .font(.title3)
                        .foregroundStyle(.secondary)
                        .frame(width: 36)

                    Text("Add Server Manually")
                        .font(.body.weight(.medium))
                        .foregroundStyle(.primary)

                    Spacer()
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 14)
                .modifier(PickerCardStyle())
            }
            .buttonStyle(.plain)
        }
    }

    // MARK: - Actions

    private func loadRemoteHosts() async {
        guard let token = store.authManager.token else { return }

        isLoadingHosts = true
        hostError = nil

        do {
            remoteHosts = try await store.hostDiscovery.listHosts(token: token)
        } catch {
            hostError = error.localizedDescription
        }

        isLoadingHosts = false
    }

    private func connectToRemoteHost(_ host: RemoteHost) async {
        guard let token = store.authManager.token else { return }

        do {
            let connectInfo = try await store.hostDiscovery.getConnectInfo(
                hostId: host.id,
                token: token
            )
            store.connectToRemoteHost(
                tunnelUrl: connectInfo.tunnelUrl,
                connectionToken: connectInfo.connectionToken,
                name: host.hostName
            )
        } catch {
            store.errorMessage = error.localizedDescription
        }
    }
}

// MARK: - PickerCardStyle

struct PickerCardStyle: ViewModifier {
    @Environment(\.colorScheme) private var colorScheme

    func body(content: Content) -> some View {
        content
            .background(
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .fill(colorScheme == .dark ? Color(.secondarySystemBackground) : Color.white)
            )
            .shadow(color: Color.black.opacity(colorScheme == .dark ? 0 : 0.04), radius: 8, y: 4)
            .overlay(
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .stroke(
                        Color(.systemGray5).opacity(colorScheme == .dark ? 0.4 : 1),
                        lineWidth: colorScheme == .dark ? 0.5 : 1
                    )
            )
    }
}
