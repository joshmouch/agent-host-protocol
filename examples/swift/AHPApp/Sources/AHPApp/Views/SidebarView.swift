import AgentHostProtocol
import SwiftUI

/// Sidebar listing sessions with a "New Session" button and connection controls.
struct SidebarView: View {
    @Environment(AppStore.self) private var store

    var body: some View {
        @Bindable var store = store

        List(selection: Binding(
            get: { store.selectedSessionURI },
            set: { uri in
                if let uri { Task { await store.selectSession(uri: uri) } }
            }
        )) {
            Section("Sessions") {
                ForEach(store.sessionSummaries, id: \.resource) { summary in
                    SessionRow(summary: summary)
                        .tag(summary.resource)
                        .contextMenu {
                            Button("Delete", role: .destructive) {
                                Task { await store.disposeSession(uri: summary.resource) }
                            }
                        }
                }
            }
        }
        .listStyle(.sidebar)
        .frame(minWidth: 220)
        .safeAreaInset(edge: .bottom) {
            VStack(spacing: 8) {
                NewSessionButton()
                ConnectionButton()
            }
            .padding()
        }
        .navigationTitle("AHP")
    }
}

// MARK: - SessionRow

struct SessionRow: View {
    let summary: SessionSummary

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack {
                Text(summary.title.isEmpty ? "New Chat" : summary.title)
                    .font(.headline)
                    .lineLimit(1)
                Spacer()
                StatusBadge(status: summary.status)
            }
            HStack {
                Text(summary.provider)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if let model = summary.model {
                    Text("·")
                        .font(.caption)
                        .foregroundStyle(.tertiary)
                    Text(model)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
        }
        .padding(.vertical, 2)
    }
}

// MARK: - StatusBadge

struct StatusBadge: View {
    let status: SessionStatus

    var body: some View {
        switch status {
        case .idle:
            Image(systemName: "circle.fill")
                .font(.system(size: 6))
                .foregroundStyle(.green)
        case .inProgress:
            ProgressView()
                .controlSize(.mini)
        case .error:
            Image(systemName: "exclamationmark.circle.fill")
                .font(.system(size: 10))
                .foregroundStyle(.red)
        }
    }
}

// MARK: - NewSessionButton

struct NewSessionButton: View {
    @Environment(AppStore.self) private var store
    @State private var showingPicker = false

    var body: some View {
        Button {
            if store.agents.count == 1, let first = store.agents.first {
                Task { await store.createSession(provider: first.provider) }
            } else if store.agents.isEmpty {
                Task { await store.createSession(provider: "copilot") }
            } else {
                showingPicker = true
            }
        } label: {
            Label("New Chat", systemImage: "plus.message")
                .frame(maxWidth: .infinity)
        }
        .buttonStyle(.borderedProminent)
        .disabled(store.connectionState != .connected)
        .sheet(isPresented: $showingPicker) {
            NavigationStack {
                AgentPicker { provider, model in
                    showingPicker = false
                    Task { await store.createSession(provider: provider, model: model) }
                }
                .navigationTitle("New Chat")
                .navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) {
                        Button("Cancel") { showingPicker = false }
                    }
                }
            }
            .presentationDetents([.medium])
        }
    }
}

// MARK: - ConnectionButton

struct ConnectionButton: View {
    @Environment(AppStore.self) private var store

    var body: some View {
        Button {
            Task {
                if store.connectionState == .connected {
                    await store.disconnect()
                } else {
                    await store.connect()
                }
            }
        } label: {
            HStack {
                Circle()
                    .fill(statusColor)
                    .frame(width: 8, height: 8)
                Text(statusLabel)
                    .font(.caption)
            }
            .frame(maxWidth: .infinity)
        }
        .buttonStyle(.bordered)
    }

    private var statusColor: Color {
        switch store.connectionState {
        case .connected: .green
        case .connecting, .reconnecting: .orange
        case .disconnected: .red
        }
    }

    private var statusLabel: String {
        switch store.connectionState {
        case .connected: "Connected"
        case .connecting: "Connecting…"
        case .reconnecting: "Reconnecting…"
        case .disconnected: "Disconnected"
        }
    }
}

// MARK: - ConnectionIndicator

struct ConnectionIndicator: View {
    @Environment(AppStore.self) private var store

    var body: some View {
        Circle()
            .fill(color)
            .frame(width: 8, height: 8)
            .help(label)
    }

    private var color: Color {
        switch store.connectionState {
        case .connected: .green
        case .connecting, .reconnecting: .orange
        case .disconnected: .red
        }
    }

    private var label: String {
        switch store.connectionState {
        case .connected: "Connected"
        case .connecting: "Connecting…"
        case .reconnecting: "Reconnecting…"
        case .disconnected: "Disconnected"
        }
    }
}
