// HostId — stable, opaque identifier for a host registered with `MultiHostClient`.

import Foundation

/// Stable identifier for a host registered with `MultiHostClient`.
///
/// Opaque to the SDK — consumers pick the format. It's used as the persistence
/// key for `ClientIdStore`, the routing key for commands on `MultiHostClient`,
/// and the tag on every `HostSubscriptionEvent`.
public struct HostId: Hashable, Sendable, CustomStringConvertible {
    public let value: String

    public init(_ value: String) {
        self.value = value
    }

    public var description: String { value }
}

extension HostId: ExpressibleByStringLiteral {
    public init(stringLiteral value: StringLiteralType) {
        self.value = value
    }
}
