import AgentHostProtocol
import SwiftUI

/// Agent/model picker for creating a new session.
struct AgentPicker: View {
    @Environment(AppStore.self) private var store
    @State private var selectedProvider: String?
    @State private var selectedModel: String?

    let onSelect: (_ provider: String, _ model: String?) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            List {
                ForEach(store.agents, id: \.provider) { agent in
                    Section {
                        ForEach(agent.models, id: \.id) { model in
                            Button {
                                selectedProvider = agent.provider
                                selectedModel = model.id
                            } label: {
                                HStack {
                                    VStack(alignment: .leading) {
                                        Text(model.name)
                                            .font(.body)
                                        if let ctx = model.maxContextWindow {
                                            Text("\(ctx / 1000)k context")
                                                .font(.caption)
                                                .foregroundStyle(.secondary)
                                        }
                                    }
                                    Spacer()
                                    if selectedProvider == agent.provider && selectedModel == model.id {
                                        Image(systemName: "checkmark")
                                            .foregroundStyle(.blue)
                                    }
                                    if model.supportsVision == true {
                                        Image(systemName: "eye")
                                            .font(.caption)
                                            .foregroundStyle(.secondary)
                                    }
                                }
                            }
                            .buttonStyle(.plain)
                        }
                    } header: {
                        HStack {
                            Text(agent.displayName)
                            Text("(\(agent.provider))")
                                .foregroundStyle(.secondary)
                        }
                    }
                }
            }
            .listStyle(.inset)

            HStack {
                Spacer()
                Button("Create") {
                    guard let provider = selectedProvider else { return }
                    onSelect(provider, selectedModel)
                }
                .buttonStyle(.borderedProminent)
                .disabled(selectedProvider == nil)
            }
            .padding(.horizontal)
            .padding(.bottom, 8)
        }
    }
}
