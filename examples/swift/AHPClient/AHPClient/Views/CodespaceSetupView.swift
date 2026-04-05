import SwiftUI

// MARK: - CodespaceSetupView

/// View for provisioning a new GitHub Codespace with an agent host.
///
/// Flow:
/// 1. User searches/selects a repository.
/// 2. App creates a Codespace via the GitHub API.
/// 3. App polls until the Codespace is available.
/// 4. App polls the host registry until the agent host registers itself.
/// 5. App connects to the agent host.
struct CodespaceSetupView: View {
    @Environment(AppStore.self) private var store
    @Environment(\.dismiss) private var dismiss

    @State private var searchQuery = ""
    @State private var repositories: [GitHubRepository] = []
    @State private var isSearching = false
    @State private var selectedRepo: GitHubRepository?

    @State private var provisioningState: ProvisioningState = .idle
    @State private var provisioningMessage = ""
    @State private var codespace: Codespace?
    @State private var errorMessage: String?

    @State private var searchTask: Task<Void, Never>?

    /// Debounce delay for repository search (nanoseconds).
    private static let searchDebounceNs: UInt64 = 400_000_000

    private enum ProvisioningState {
        case idle
        case creating
        case waitingForCodespace
        case waitingForHost
        case connecting
        case done
        case failed
    }

    var body: some View {
        NavigationStack {
            Group {
                switch provisioningState {
                case .idle:
                    repositoryPickerContent
                case .creating, .waitingForCodespace, .waitingForHost, .connecting:
                    provisioningProgressContent
                case .done:
                    successContent
                case .failed:
                    failedContent
                }
            }
            .navigationTitle("New Codespace")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") {
                        dismiss()
                    }
                    .disabled(isProvisioning)
                }
            }
        }
    }

    private var isProvisioning: Bool {
        switch provisioningState {
        case .creating, .waitingForCodespace, .waitingForHost, .connecting:
            return true
        default:
            return false
        }
    }

    // MARK: - Repository Picker

    @ViewBuilder
    private var repositoryPickerContent: some View {
        VStack(spacing: 0) {
            // Search bar
            HStack(spacing: 12) {
                Image(systemName: "magnifyingglass")
                    .foregroundStyle(.secondary)
                TextField("Search repositories…", text: $searchQuery)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                if isSearching {
                    ProgressView()
                        .controlSize(.small)
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            .background(Color(.systemGray6))
            .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
            .padding(.horizontal, 16)
            .padding(.top, 8)

            if repositories.isEmpty && !isSearching && !searchQuery.isEmpty {
                ContentUnavailableView.search(text: searchQuery)
            } else if repositories.isEmpty && !isSearching {
                VStack(spacing: 12) {
                    Spacer()
                    Image(systemName: "magnifyingglass")
                        .font(.system(size: 36))
                        .foregroundStyle(.secondary)
                    Text("Search for a repository")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                    Text("Type a name to find repositories where you can create a Codespace.")
                        .font(.caption)
                        .foregroundStyle(.tertiary)
                        .multilineTextAlignment(.center)
                        .padding(.horizontal, 32)
                    Spacer()
                }
            } else {
                List {
                    ForEach(repositories) { repo in
                        Button {
                            selectedRepo = repo
                        } label: {
                            repositoryRow(repo)
                        }
                        .buttonStyle(.plain)
                    }
                }
                .listStyle(.plain)
            }
        }
        .onChange(of: searchQuery) { _, newValue in
            searchTask?.cancel()
            searchTask = Task {
                try? await Task.sleep(nanoseconds: Self.searchDebounceNs)
                guard !Task.isCancelled else { return }
                await searchRepositories(query: newValue)
            }
        }
        .task {
            // Load user's recent repos on appear
            await loadRecentRepos()
        }
        .alert("Create Codespace", isPresented: .init(
            get: { selectedRepo != nil },
            set: { if !$0 { selectedRepo = nil } }
        )) {
            Button("Create") {
                if let repo = selectedRepo {
                    Task { await provisionCodespace(repo: repo) }
                }
            }
            Button("Cancel", role: .cancel) {
                selectedRepo = nil
            }
        } message: {
            if let repo = selectedRepo {
                Text("Create a Codespace for \(repo.fullName) and start an agent host?")
            }
        }
    }

    @ViewBuilder
    private func repositoryRow(_ repo: GitHubRepository) -> some View {
        HStack(spacing: 12) {
            Image(systemName: repo.isPrivate ? "lock" : "globe")
                .foregroundStyle(repo.isPrivate ? .orange : .secondary)
                .frame(width: 24)

            VStack(alignment: .leading, spacing: 2) {
                Text(repo.fullName)
                    .font(.body.weight(.medium))
                    .lineLimit(1)

                HStack(spacing: 8) {
                    if let language = repo.language {
                        Text(language)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    if let description = repo.description {
                        Text(description)
                            .font(.caption)
                            .foregroundStyle(.tertiary)
                            .lineLimit(1)
                    }
                }
            }

            Spacer()

            Image(systemName: "chevron.right")
                .font(.footnote)
                .foregroundStyle(.tertiary)
        }
        .padding(.vertical, 4)
    }

    // MARK: - Provisioning Progress

    @ViewBuilder
    private var provisioningProgressContent: some View {
        VStack(spacing: 24) {
            Spacer()

            ProgressView()
                .scaleEffect(1.5)

            Text(provisioningMessage)
                .font(.headline)
                .multilineTextAlignment(.center)

            Text(provisioningDetail)
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 32)

            if let codespace {
                Text(codespace.name)
                    .font(.caption.monospaced())
                    .foregroundStyle(.tertiary)
            }

            Spacer()
        }
    }

    private var provisioningDetail: String {
        switch provisioningState {
        case .creating:
            return "Sending request to GitHub…"
        case .waitingForCodespace:
            return "This may take a few minutes for the first build."
        case .waitingForHost:
            return "The agent host should start automatically via postStartCommand."
        case .connecting:
            return "Establishing WebSocket connection…"
        default:
            return ""
        }
    }

    // MARK: - Success

    @ViewBuilder
    private var successContent: some View {
        VStack(spacing: 24) {
            Spacer()

            Image(systemName: "checkmark.circle.fill")
                .font(.system(size: 56))
                .foregroundStyle(.green)

            Text("Connected!")
                .font(.title2.bold())

            Text("Your Codespace is ready and the agent host is running.")
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 32)

            Button("Start Chatting") {
                dismiss()
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)

            Spacer()
        }
    }

    // MARK: - Failed

    @ViewBuilder
    private var failedContent: some View {
        VStack(spacing: 24) {
            Spacer()

            Image(systemName: "exclamationmark.triangle.fill")
                .font(.system(size: 56))
                .foregroundStyle(.orange)

            Text("Provisioning Failed")
                .font(.title2.bold())

            if let errorMessage {
                Text(errorMessage)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 32)
            }

            HStack(spacing: 16) {
                Button("Try Again") {
                    provisioningState = .idle
                    errorMessage = nil
                }
                .buttonStyle(.borderedProminent)

                Button("Cancel") {
                    dismiss()
                }
                .buttonStyle(.bordered)
            }

            Spacer()
        }
    }

    // MARK: - Actions

    private func loadRecentRepos() async {
        guard let token = store.authManager.token else { return }

        isSearching = true
        do {
            repositories = try await store.codespaceService.listUserRepos(token: token)
        } catch {
            // Non-fatal
            print("[Codespace] Failed to load repos: \(error)")
        }
        isSearching = false
    }

    private func searchRepositories(query: String) async {
        guard let token = store.authManager.token, !query.isEmpty else {
            if query.isEmpty {
                await loadRecentRepos()
            }
            return
        }

        isSearching = true
        do {
            repositories = try await store.codespaceService.searchRepositories(
                query: query,
                token: token
            )
        } catch {
            // Non-fatal
            print("[Codespace] Search failed: \(error)")
        }
        isSearching = false
    }

    private func provisionCodespace(repo: GitHubRepository) async {
        guard let token = store.authManager.token else { return }

        provisioningState = .creating
        provisioningMessage = "Creating Codespace…"

        do {
            // Step 1: Create the Codespace
            let created = try await store.codespaceService.createCodespace(
                owner: repo.owner.login,
                repo: repo.name,
                ref: repo.defaultBranch,
                token: token
            )
            codespace = created

            // Step 2: Wait for it to be available
            provisioningState = .waitingForCodespace
            provisioningMessage = "Provisioning Codespace…"

            let ready = try await store.codespaceService.waitForCodespace(
                name: created.name,
                token: token
            )
            codespace = ready

            // Step 3: Wait for the agent host to register with the registry
            provisioningState = .waitingForHost
            provisioningMessage = "Waiting for Agent Host…"

            let host = try await waitForHostRegistration(
                codespace: ready,
                token: token
            )

            // Step 4: Connect
            provisioningState = .connecting
            provisioningMessage = "Connecting…"

            let connectInfo = try await store.hostDiscovery.getConnectInfo(
                hostId: host.id,
                token: token
            )

            store.connectToRemoteHost(
                tunnelUrl: connectInfo.tunnelUrl,
                connectionToken: connectInfo.connectionToken,
                name: host.hostName
            )

            provisioningState = .done

        } catch is CancellationError {
            provisioningState = .idle
        } catch {
            provisioningState = .failed
            errorMessage = error.localizedDescription
        }
    }

    /// Poll the host registry until a host matching the Codespace name appears.
    private func waitForHostRegistration(
        codespace: Codespace,
        token: String,
        timeout: TimeInterval = 120
    ) async throws -> RemoteHost {
        let deadline = Date().addingTimeInterval(timeout)

        while Date() < deadline {
            let hosts = try await store.hostDiscovery.listHosts(token: token)

            // Look for a host whose name contains the codespace name
            if let match = hosts.first(where: { $0.hostName.contains(codespace.name) }) {
                return match
            }

            try await Task.sleep(nanoseconds: 5_000_000_000) // 5 seconds
            try Task.checkCancellation()
        }

        throw CodespaceError.timeout
    }
}
