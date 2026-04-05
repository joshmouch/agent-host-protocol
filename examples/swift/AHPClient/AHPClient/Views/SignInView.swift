import SwiftUI

// MARK: - SignInView

/// Full-screen GitHub sign-in view shown when the user is not authenticated.
struct SignInView: View {
    @Environment(AppStore.self) private var store

    var body: some View {
        VStack(spacing: 32) {
            Spacer()

            Image(systemName: "bubble.left.and.bubble.right")
                .font(.system(size: 60))
                .foregroundStyle(.blue)

            VStack(spacing: 8) {
                Text("AHP Client")
                    .font(.largeTitle.bold())

                Text("Agent Host Protocol Client")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }

            VStack(spacing: 16) {
                Text("Sign in with GitHub to discover remote agent hosts or provision Codespaces.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 40)

                Button {
                    Task { await store.authManager.signIn() }
                } label: {
                    HStack(spacing: 8) {
                        if store.authManager.isAuthenticating {
                            ProgressView()
                                .controlSize(.small)
                                .tint(.white)
                        }
                        Image(systemName: "person.badge.key")
                        Text("Sign in with GitHub")
                    }
                    .frame(maxWidth: 280)
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .disabled(store.authManager.isAuthenticating)

                if let error = store.authManager.authError {
                    Label(error, systemImage: "exclamationmark.triangle")
                        .font(.caption)
                        .foregroundStyle(.red)
                        .padding(.horizontal, 32)
                }
            }

            Spacer()

            // Skip option — allows manual server connection without GitHub auth
            Button {
                store.skipAuth()
            } label: {
                Text("Skip — connect manually")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }
            .padding(.bottom, 24)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
