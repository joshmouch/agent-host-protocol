import SwiftUI

/// Root view: routes between sign-in, host picker, and the main chat experience.
///
/// Flow:
/// 1. If not signed in and auth not skipped → `SignInView`
/// 2. If signed in but no server selected → `HostPickerView`
/// 3. If server selected → sidebar + chat (existing flow)
struct ContentView: View {
    @Environment(AppStore.self) private var store
    @State private var showSettings = false
    @State private var navigationPath: [String] = []

    var body: some View {
        Group {
            if !store.authManager.isSignedIn && !store.authSkipped {
                // Step 1: Sign in with GitHub
                SignInView()
            } else if store.selectedServer == nil && store.authManager.isSignedIn {
                // Step 2: Pick a host (remote, codespace, or manual)
                HostPickerView()
            } else {
                // Step 3: Main chat experience
                mainChatView
            }
        }
        .environment(store)
        .alert("Error", isPresented: .init(
            get: { store.errorMessage != nil },
            set: { if !$0 { store.errorMessage = nil } }
        )) {
            Button("OK") { store.errorMessage = nil }
        } message: {
            Text(store.errorMessage ?? "")
        }
    }

    @ViewBuilder
    private var mainChatView: some View {
        NavigationStack(path: $navigationPath) {
            SidebarView(navigationPath: $navigationPath)
                .navigationDestination(for: String.self) { value in
                    if value.hasPrefix("folder:") {
                        let path = String(value.dropFirst("folder:".count))
                        FolderSessionsView(folderPath: path, navigationPath: $navigationPath)
                    } else {
                        ChatView()
                    }
                }
        }
        .onChange(of: store.selectedSessionURI) { _, newValue in
            if let uri = newValue {
                if navigationPath.last != uri {
                    navigationPath = [uri]
                }
            } else {
                navigationPath = []
            }
        }
        .sheet(isPresented: $showSettings) {
            SettingsView()
                .environment(store)
        }
    }
}
