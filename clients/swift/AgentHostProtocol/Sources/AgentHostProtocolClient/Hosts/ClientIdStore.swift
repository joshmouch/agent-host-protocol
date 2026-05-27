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

/// Filesystem-backed `ClientIdStore` that survives process restarts.
///
/// Stores one file per host id under a configurable directory; writes are
/// atomic (`.atomic` Data option) and best-effort restrict file permissions
/// to owner-read/write on POSIX platforms so the persisted ids aren't
/// world-readable. Per-store mutations are serialised through an internal
/// actor so concurrent `load`/`store` calls from different hosts don't race
/// on the directory's contents.
///
/// **iOS Keychain note:** for the highest-security profile on Apple
/// platforms, wrap a Keychain-backed implementation of `ClientIdStore`
/// in your app (the SDK doesn't ship one to keep this product
/// dependency-free across SwiftPM-supported platforms). `FileClientIdStore`
/// is a reasonable default for desktops, command-line tools, and
/// development builds; it provides persistence without coupling
/// `AgentHostProtocolClient` to `Security.framework`.
///
/// The directory is created on first write if it doesn't already exist.
/// Filenames are derived from each host id via a percent-encoding helper
/// so arbitrary `HostId` strings (including `:`, `/`, etc.) map to safe
/// filesystem paths.
public final class FileClientIdStore: ClientIdStore {
    private let storage: Storage

    /// Build a store rooted at `directory`. The directory will be created
    /// when needed; the caller is responsible for picking a location that
    /// the process can write to (e.g. `Application Support` on Apple
    /// platforms, `XDG_DATA_HOME` / `~/.local/share` on Linux).
    public init(directory: URL) {
        self.storage = Storage(directory: directory)
    }

    public func load(_ hostId: HostId) async -> String? {
        await storage.load(hostId)
    }

    public func store(_ hostId: HostId, clientId: String) async {
        await storage.store(hostId, clientId: clientId)
    }

    private actor Storage {
        private let directory: URL
        private let fm = FileManager.default

        init(directory: URL) {
            self.directory = directory
        }

        func load(_ hostId: HostId) -> String? {
            let url = fileURL(for: hostId)
            guard let data = try? Data(contentsOf: url),
                  let text = String(data: data, encoding: .utf8)
            else {
                return nil
            }
            let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
            return trimmed.isEmpty ? nil : trimmed
        }

        func store(_ hostId: HostId, clientId: String) {
            ensureDirectory()
            let url = fileURL(for: hostId)
            guard let data = clientId.data(using: .utf8) else { return }
            do {
                try data.write(to: url, options: [.atomic])
                // Best-effort restrict permissions to owner-only on POSIX
                // platforms. Silently ignore on platforms where this
                // attribute isn't applicable.
                try? fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
            } catch {
                #if DEBUG
                print("[FileClientIdStore] failed to persist id for \(hostId): \(error)")
                #endif
            }
        }

        private func ensureDirectory() {
            if !fm.fileExists(atPath: directory.path) {
                try? fm.createDirectory(at: directory, withIntermediateDirectories: true)
                // Best-effort restrict the directory too.
                try? fm.setAttributes(
                    [.posixPermissions: 0o700],
                    ofItemAtPath: directory.path
                )
            }
        }

        private func fileURL(for hostId: HostId) -> URL {
            let safe = Self.encode(hostId)
            return directory.appendingPathComponent("\(safe).clientid")
        }

        /// Percent-encode characters that aren't safe in filesystem
        /// paths, including the URL path separator and any control
        /// characters. The reverse direction isn't needed because we
        /// only read files we wrote, by the same key.
        static func encode(_ hostId: HostId) -> String {
            var allowed = CharacterSet.alphanumerics
            allowed.insert(charactersIn: "-._~")
            return hostId.value.addingPercentEncoding(withAllowedCharacters: allowed) ?? hostId.value
        }
    }
}
