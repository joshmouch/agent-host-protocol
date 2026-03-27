import AgentHostProtocol
import SwiftUI
import UIKit

/// Renders a single response part: markdown text, reasoning, tool call, or content ref.
struct ResponsePartView: View {
    let part: ResponsePart

    var body: some View {
        switch part {
        case .markdown(let md):
            MarkdownPartView(part: md)
        case .reasoning(let r):
            ReasoningPartView(part: r)
        case .toolCall(let tc):
            ToolCallPartView(toolCall: tc.toolCall)
        case .contentRef(let ref):
            ContentRefView(ref: ref)
        }
    }
}

// MARK: - MarkdownPartView

struct MarkdownPartView: View {
    let part: MarkdownResponsePart

    var body: some View {
        Text(LocalizedStringKey(part.content))
            .textSelection(.enabled)
            .frame(maxWidth: .infinity, alignment: .leading)
    }
}

// MARK: - ReasoningPartView

struct ReasoningPartView: View {
    let part: ReasoningResponsePart
    @State private var isExpanded = false

    var body: some View {
        DisclosureGroup(isExpanded: $isExpanded) {
            Text(part.content)
                .font(.caption)
                .foregroundStyle(.secondary)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
        } label: {
            Label("Reasoning", systemImage: "brain")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .padding(8)
        .background(.quaternary.opacity(0.3), in: RoundedRectangle(cornerRadius: 8))
    }
}

// MARK: - ToolCallPartView

struct ToolCallPartView: View {
    let toolCall: ToolCallState
    @Environment(AppStore.self) private var store

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            // Header
            HStack {
                Image(systemName: toolIcon)
                    .foregroundStyle(toolColor)
                Text(displayName)
                    .font(.subheadline.bold())
                Spacer()
                statusView
            }

            // Invocation message
            if let msg = invocationMessage {
                Text(stringOrMarkdownText(msg))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            // Tool input (collapsible)
            if let input = toolInput, !input.isEmpty {
                DisclosureGroup("Parameters") {
                    Text(input)
                        .font(.system(.caption, design: .monospaced))
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .font(.caption)
            }

            // Tool result content
            toolResultView

            // Action buttons
            actionButtons
        }
        .padding(10)
        .background(
            RoundedRectangle(cornerRadius: 10)
                .fill(.background)
                .shadow(color: .primary.opacity(0.1), radius: 2, y: 1)
        )
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .stroke(borderColor, lineWidth: 1)
        )
    }

    // MARK: - Status

    @ViewBuilder
    private var statusView: some View {
        switch toolCall {
        case .streaming:
            ProgressView().controlSize(.mini)
        case .pendingConfirmation:
            Image(systemName: "questionmark.circle.fill")
                .foregroundStyle(.orange)
        case .running:
            ProgressView().controlSize(.mini)
        case .pendingResultConfirmation:
            Image(systemName: "checkmark.circle.badge.questionmark")
                .foregroundStyle(.orange)
        case .completed(let s):
            Image(systemName: s.success ? "checkmark.circle.fill" : "xmark.circle.fill")
                .foregroundStyle(s.success ? .green : .red)
        case .cancelled:
            Image(systemName: "slash.circle.fill")
                .foregroundStyle(.secondary)
        }
    }

    // MARK: - Action Buttons

    @ViewBuilder
    private var actionButtons: some View {
        switch toolCall {
        case .pendingConfirmation:
            HStack {
                Button("Deny", role: .destructive) {
                    if let ids = turnAndToolId {
                        Task { await store.denyToolCall(toolCallId: ids.toolCallId, turnId: ids.turnId) }
                    }
                }
                .buttonStyle(.bordered)

                Button("Approve") {
                    if let ids = turnAndToolId {
                        Task { await store.approveToolCall(toolCallId: ids.toolCallId, turnId: ids.turnId) }
                    }
                }
                .buttonStyle(.borderedProminent)
            }
        case .pendingResultConfirmation:
            HStack {
                Button("Reject", role: .destructive) {
                    // Result denial not exposed yet
                }
                .buttonStyle(.bordered)

                Button("Accept") {
                    if let ids = turnAndToolId {
                        Task { await store.approveToolCallResult(toolCallId: ids.toolCallId, turnId: ids.turnId) }
                    }
                }
                .buttonStyle(.borderedProminent)
            }
        default:
            EmptyView()
        }
    }

    // MARK: - Tool Result Content

    @ViewBuilder
    private var toolResultView: some View {
        switch toolCall {
        case .completed(let s):
            if let content = s.content {
                ForEach(Array(content.enumerated()), id: \.offset) { _, item in
                    ToolResultContentView(content: item)
                }
            }
            if !s.success {
                Text("Tool failed")
                    .font(.caption)
                    .foregroundStyle(.red)
            }
        case .pendingResultConfirmation(let s):
            if let content = s.content {
                ForEach(Array(content.enumerated()), id: \.offset) { _, item in
                    ToolResultContentView(content: item)
                }
            }
        default:
            EmptyView()
        }
    }

    // MARK: - Helpers

    private var displayName: String {
        toolCall.baseFields.displayName
    }

    private var toolIcon: String {
        let name = toolCall.baseFields.toolName
        switch name {
        case "bash", "terminal", "runCommand": return "terminal"
        case "readFile", "read_file": return "doc.text"
        case "writeFile", "write_file", "editFile", "edit_file": return "doc.badge.plus"
        case "listDirectory", "list_directory": return "folder"
        default: return "wrench"
        }
    }

    private var toolColor: Color {
        switch toolCall {
        case .pendingConfirmation: .orange
        case .running, .streaming: .blue
        case .completed(let s): s.success ? .green : .red
        case .cancelled: .secondary
        case .pendingResultConfirmation: .orange
        }
    }

    private var borderColor: Color {
        switch toolCall {
        case .pendingConfirmation, .pendingResultConfirmation: .orange.opacity(0.5)
        case .completed(let s): s.success ? .green.opacity(0.3) : .red.opacity(0.3)
        default: .secondary.opacity(0.2)
        }
    }

    private var invocationMessage: StringOrMarkdown? {
        switch toolCall {
        case .streaming(let s): return s.invocationMessage
        case .pendingConfirmation(let s): return s.invocationMessage
        case .running(let s): return s.invocationMessage
        case .pendingResultConfirmation(let s): return s.invocationMessage
        case .completed(let s): return s.invocationMessage
        case .cancelled(let s): return s.invocationMessage
        }
    }

    private var toolInput: String? {
        switch toolCall {
        case .streaming(let s): return s.partialInput
        case .pendingConfirmation(let s): return s.toolInput
        case .running(let s): return s.toolInput
        case .pendingResultConfirmation(let s): return s.toolInput
        case .completed(let s): return s.toolInput
        default: return nil
        }
    }

    /// Get turnId + toolCallId for dispatching actions.
    /// The turnId comes from the current active turn in the store.
    private var turnAndToolId: (turnId: String, toolCallId: String)? {
        let tcId = toolCall.toolCallId
        if let activeTurn = store.currentSession?.activeTurn {
            return (activeTurn.id, tcId)
        }
        return nil
    }

    private func stringOrMarkdownText(_ value: StringOrMarkdown) -> String {
        switch value {
        case .string(let s): return s
        case .markdown(let m): return m
        }
    }
}

// MARK: - ToolResultContentView

struct ToolResultContentView: View {
    let content: ToolResultContent

    var body: some View {
        switch content {
        case .text(let t):
            Text(t.text)
                .font(.system(.caption, design: .monospaced))
                .textSelection(.enabled)
                .padding(6)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(.quaternary.opacity(0.3), in: RoundedRectangle(cornerRadius: 6))
        case .binary(let b):
            if b.contentType?.hasPrefix("image/") == true,
               let data = Data(base64Encoded: b.data),
               let uiImage = UIImage(data: data) {
                Image(uiImage: uiImage)
                    .resizable()
                    .scaledToFit()
                    .frame(maxHeight: 300)
                    .clipShape(RoundedRectangle(cornerRadius: 8))
            } else {
                Label("Binary content (\(b.contentType ?? "unknown"))", systemImage: "doc.zipper")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        case .fileEdit(let edit):
            HStack {
                Image(systemName: "doc.badge.gearshape")
                VStack(alignment: .leading) {
                    Text("File edit")
                        .font(.caption.bold())
                    if let diff = edit.diff {
                        HStack(spacing: 4) {
                            Text("+\(diff.added ?? 0)")
                                .foregroundStyle(.green)
                            Text("-\(diff.removed ?? 0)")
                                .foregroundStyle(.red)
                        }
                        .font(.caption)
                    }
                }
            }
            .padding(6)
            .background(.quaternary.opacity(0.3), in: RoundedRectangle(cornerRadius: 6))
        case .contentRef(let ref):
            ContentRefView(ref: ref)
        }
    }
}

// MARK: - ContentRefView

struct ContentRefView: View {
    let ref: ContentRef

    var body: some View {
        HStack {
            Image(systemName: contentIcon)
            VStack(alignment: .leading) {
                Text(ref.uri)
                    .font(.caption)
                    .lineLimit(1)
                if let type = ref.contentType {
                    Text(type)
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            }
        }
        .padding(6)
        .background(.quaternary.opacity(0.3), in: RoundedRectangle(cornerRadius: 6))
    }

    private var contentIcon: String {
        if ref.contentType?.hasPrefix("image/") == true { return "photo" }
        if ref.contentType?.hasPrefix("text/") == true { return "doc.text" }
        return "doc"
    }
}
