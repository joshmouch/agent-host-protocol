import SwiftUI

/// Main entry point for the AHP client app — an iOS SwiftUI application
/// that connects to an Agent Host Protocol server over WebSocket.
@main
struct AHPAppMain: App {
    @State private var store = AppStore()

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environment(store)
        }
    }
}
