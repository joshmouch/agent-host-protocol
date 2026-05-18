// HostError — errors specific to the multi-host SDK layer.

import Foundation

/// Errors specific to the multi-host SDK layer.
///
/// Errors from the underlying single-host `AHPClient` are carried through
/// `client(AHPClientError)`.
public enum HostError: Error, Sendable {
    /// No host with this id is currently registered.
    case unknownHost(HostId)

    /// The `HostClientHandle` was issued for a connection that has since
    /// been replaced by a reconnect. Acquire a fresh handle via
    /// `MultiHostClient.client(for:)`.
    case hostReconnected(host: HostId, handleGeneration: UInt64, currentGeneration: UInt64)

    /// The host's runtime task has been torn down (e.g. the host was removed
    /// or the multi-host client was shut down).
    case hostShutDown(HostId)

    /// `MultiHostClient.add` was called with an id that is already registered.
    case duplicateHost(HostId)

    /// A request bubbled up an error from the underlying `AHPClient`.
    case client(AHPClientError)
}

extension HostError: LocalizedError {
    public var errorDescription: String? {
        switch self {
        case .unknownHost(let id):
            return "no host registered with id \(id)"
        case .hostReconnected(let host, let handleGeneration, let currentGeneration):
            return "host \(host) reconnected (generation \(handleGeneration) -> \(currentGeneration)); request a fresh client handle"
        case .hostShutDown(let id):
            return "host \(id) runtime is no longer active"
        case .duplicateHost(let id):
            return "host \(id) is already registered; remove it first"
        case .client(let error):
            return error.errorDescription
        }
    }
}
