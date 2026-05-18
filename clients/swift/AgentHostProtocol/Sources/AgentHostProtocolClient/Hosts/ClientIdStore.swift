// ClientIdStore — pluggable persistence for stable per-host `clientId`s.

import Foundation

/// Persistence hook for stable `clientId`s per host.
///
/// On `MultiHostClient.add(_:)`, the multi-host client looks up `hostId`
/// in this store. If the store returns a value, that id is reused — letting
/// the server treat successive launches as the same client (which the AHP
/// `reconnect` flow needs to replay missed actions). If the store returns
/// `nil`, the multi-host client generates a fresh UUID and stores it.
///
/// The default `InMemoryClientIdStore` is **session-stable only** — it does
/// not survive process restarts. Production multi-host apps should plug a
/// keychain/file-backed implementation in so reconnects keep working across
/// launches.
public protocol ClientIdStore: AnyObject, Sendable {
    /// Look up the previously stored `clientId` for `hostId`, if any.
    func load(_ hostId: HostId) async -> String?

    /// Persist `clientId` for `hostId`. Implementations should overwrite any
    /// previous value.
    func store(_ hostId: HostId, clientId: String) async
}

/// In-process `ClientIdStore` backed by an actor-protected dictionary.
///
/// Keeps assigned ids in memory. Survives reconnects within the same process
/// but **not** restarts. Fine for tests, ephemeral CLIs, and as a starting
/// point — production apps should provide a persistent implementation
/// (filesystem, keychain, secure enclave, …).
public final class InMemoryClientIdStore: ClientIdStore {
    private let storage: Storage

    public init() {
        self.storage = Storage()
    }

    public func load(_ hostId: HostId) async -> String? {
        await storage.load(hostId)
    }

    public func store(_ hostId: HostId, clientId: String) async {
        await storage.store(hostId, clientId: clientId)
    }

    private actor Storage {
        private var entries: [HostId: String] = [:]

        func load(_ hostId: HostId) -> String? { entries[hostId] }

        func store(_ hostId: HostId, clientId: String) {
            entries[hostId] = clientId
        }
    }
}
