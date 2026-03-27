import AgentHostProtocol
import SwiftUI

/// Main chat view showing the conversation with the agent.
struct ChatView: View {
    @Environment(AppStore.self) private var store
    @State private var inputText = ""
    @FocusState private var inputFocused: Bool

    var body: some View {
        VStack(spacing: 0) {
            // Session header
            if let session = store.currentSession {
                SessionHeader(session: session)
                Divider()
            }

            // Message list
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 16) {
                        if let session = store.currentSession {
                            // Completed turns
                            ForEach(session.turns, id: \.id) { turn in
                                TurnView(turn: turn, activeTurnId: nil)
                                    .id(turn.id)
                            }

                            // Active turn (streaming)
                            if let activeTurn = session.activeTurn {
                                ActiveTurnView(turn: activeTurn)
                                    .id("active-\(activeTurn.id)")
                            }
                        }
                    }
                    .padding()
                }
                .onChange(of: store.currentSession?.activeTurn?.responseParts.count) {
                    // Auto-scroll to bottom when new content arrives
                    if let active = store.currentSession?.activeTurn {
                        withAnimation(.easeOut(duration: 0.15)) {
                            proxy.scrollTo("active-\(active.id)", anchor: .bottom)
                        }
                    }
                }
            }

            Divider()

            // Input area
            InputBar(text: $inputText, isFocused: $inputFocused) {
                guard !inputText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
                let message = inputText
                inputText = ""
                Task { await store.sendMessage(message) }
            }
        }
    }
}

// MARK: - SessionHeader

struct SessionHeader: View {
    let session: SessionState

    var body: some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(session.summary.title.isEmpty ? "New Chat" : session.summary.title)
                    .font(.headline)
                HStack(spacing: 4) {
                    Text(session.summary.provider)
                        .font(.caption)
                    if let model = session.summary.model {
                        Text("·")
                        Text(model)
                            .font(.caption)
                    }
                }
                .foregroundStyle(.secondary)
            }
            Spacer()
            if session.lifecycle == .creating {
                ProgressView()
                    .controlSize(.small)
                Text("Setting up…")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal)
        .padding(.vertical, 8)
    }
}

// MARK: - InputBar

struct InputBar: View {
    @Binding var text: String
    var isFocused: FocusState<Bool>.Binding
    let onSubmit: () -> Void

    @Environment(AppStore.self) private var store

    var body: some View {
        HStack(alignment: .bottom, spacing: 8) {
            TextField("Message…", text: $text, axis: .vertical)
                .textFieldStyle(.roundedBorder)
                .lineLimit(1...8)
                .focused(isFocused)
                .onSubmit {
                    onSubmit()
                }
                .submitLabel(.send)

            if store.currentSession?.activeTurn != nil {
                // Cancel button while a turn is active
                Button {
                    Task { await store.cancelTurn() }
                } label: {
                    Image(systemName: "stop.circle.fill")
                        .font(.title2)
                        .foregroundStyle(.red)
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Cancel turn")
            } else {
                // Send button
                Button(action: onSubmit) {
                    Image(systemName: "arrow.up.circle.fill")
                        .font(.title2)
                        .foregroundStyle(
                            text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                                ? .gray : .blue
                        )
                }
                .buttonStyle(.plain)
                .disabled(text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                .accessibilityLabel("Send message")
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
    }
}

// MARK: - TurnView (completed)

struct TurnView: View {
    let turn: Turn
    let activeTurnId: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            // User message
            UserBubble(text: turn.userMessage.text, attachments: turn.userMessage.attachments)

            // Response parts
            ForEach(Array(turn.responseParts.enumerated()), id: \.offset) { _, part in
                ResponsePartView(part: part)
            }

            // Turn status footer
            if turn.state == .error, let error = turn.error {
                Label(error.message, systemImage: "exclamationmark.triangle.fill")
                    .font(.caption)
                    .foregroundStyle(.red)
            }

            if turn.state == .cancelled {
                Label("Cancelled", systemImage: "xmark.circle")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            // Usage info
            if let usage = turn.usage {
                UsageBadge(usage: usage)
            }
        }
    }
}

// MARK: - ActiveTurnView (streaming)

struct ActiveTurnView: View {
    let turn: ActiveTurn

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            UserBubble(text: turn.userMessage.text, attachments: turn.userMessage.attachments)

            ForEach(Array(turn.responseParts.enumerated()), id: \.offset) { _, part in
                ResponsePartView(part: part)
            }

            // Streaming indicator
            HStack(spacing: 6) {
                ProgressView()
                    .controlSize(.small)
                Text("Thinking…")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            if let usage = turn.usage {
                UsageBadge(usage: usage)
            }
        }
    }
}

// MARK: - UserBubble

struct UserBubble: View {
    let text: String
    let attachments: [MessageAttachment]?

    var body: some View {
        HStack {
            Spacer(minLength: 60)
            VStack(alignment: .trailing, spacing: 4) {
                Text(text)
                    .padding(.horizontal, 12)
                    .padding(.vertical, 8)
                    .background(.blue.opacity(0.15), in: RoundedRectangle(cornerRadius: 12))

                if let attachments, !attachments.isEmpty {
                    ForEach(Array(attachments.enumerated()), id: \.offset) { _, attachment in
                        Label(
                            attachment.displayName ?? attachment.path,
                            systemImage: attachment.type == .directory ? "folder" : "doc"
                        )
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    }
                }
            }
        }
    }
}

// MARK: - UsageBadge

struct UsageBadge: View {
    let usage: UsageInfo

    var body: some View {
        HStack(spacing: 8) {
            if let input = usage.inputTokens {
                Label("\(input) in", systemImage: "arrow.down.circle")
                    .font(.caption2)
            }
            if let output = usage.outputTokens {
                Label("\(output) out", systemImage: "arrow.up.circle")
                    .font(.caption2)
            }
        }
        .foregroundStyle(.tertiary)
    }
}
