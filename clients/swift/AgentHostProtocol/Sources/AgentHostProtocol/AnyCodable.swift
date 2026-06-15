// AnyCodable — type-erased Codable wrapper for unknown/Record<string, unknown> values.

import Foundation

/// A type-erased `Codable` value for handling `unknown` and `Record<string, unknown>` types.
///
/// Marked `@unchecked Sendable` because the stored `Any` is only ever set to
/// immutable, `Sendable`-safe types during decoding (Bool, Int, Double, String,
/// NSNull, and recursive `[Any]`/`[String: Any]` of those). The value is `let`,
/// so it cannot be mutated after initialization.
public struct AnyCodable: Codable, @unchecked Sendable, Equatable {
    public let value: Any

    public init(_ value: Any) {
        self.value = value
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() {
            self.value = NSNull()
        } else if let bool = try? container.decode(Bool.self) {
            self.value = bool
        } else if let int = try? container.decode(Int.self) {
            self.value = int
        } else if let double = try? container.decode(Double.self) {
            self.value = double
        } else if let string = try? container.decode(String.self) {
            self.value = string
        } else if let array = try? container.decode([AnyCodable].self) {
            self.value = array.map { $0.value }
        } else if let dict = try? container.decode([String: AnyCodable].self) {
            self.value = dict.mapValues { $0.value }
        } else {
            throw DecodingError.dataCorruptedError(
                in: container,
                debugDescription: "AnyCodable cannot decode value"
            )
        }
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()

        // NSNumber bridges promiscuously to Bool/Int/Double — pattern matching
        // alone can't distinguish a Bool-backed NSNumber from an Int-backed one.
        // Inspect objCType to dispatch faithfully to the underlying type.
        // ('c' is also Int8's encoding, but JSONSerialization only ever produces
        // 'c' for a Bool, so the JSON-decode path this type serves is unambiguous.)
        // Unsigned integer types ('C'/'I'/'S'/'L'/'Q') encode via uint64Value: a
        // JSON integer above Int64.max is boxed as an unsigned 'Q' NSNumber, and
        // int64Value would silently corrupt it (it does not round-trip).
        if let number = value as? NSNumber, type(of: value) != Bool.self {
            let objCType = number.objCType[0]
            switch objCType {
            case 0x63 /* 'c' */, 0x42 /* 'B' */:
                try container.encode(number.boolValue)
                return
            case 0x66 /* 'f' */, 0x64 /* 'd' */:
                try container.encode(number.doubleValue)
                return
            case 0x43 /* 'C' */, 0x49 /* 'I' */, 0x53 /* 'S' */, 0x4C /* 'L' */, 0x51 /* 'Q' */:
                try container.encode(number.uint64Value)
                return
            default:
                try container.encode(number.int64Value)
                return
            }
        }

        switch value {
        case is NSNull:
            try container.encodeNil()
        case let bool as Bool:
            try container.encode(bool)
        case let int as Int:
            try container.encode(int)
        case let double as Double:
            try container.encode(double)
        case let string as String:
            try container.encode(string)
        case let array as [Any]:
            try container.encode(array.map { AnyCodable($0) })
        case let dict as [String: Any]:
            try container.encode(dict.mapValues { AnyCodable($0) })
        default:
            throw EncodingError.invalidValue(
                value,
                EncodingError.Context(codingPath: encoder.codingPath,
                    debugDescription: "AnyCodable cannot encode value of type \(type(of: value))")
            )
        }
    }

    public static func == (lhs: AnyCodable, rhs: AnyCodable) -> Bool {
        switch (lhs.value, rhs.value) {
        case is (NSNull, NSNull):
            return true
        case let (lhs as Bool, rhs as Bool):
            return lhs == rhs
        case let (lhs as Int, rhs as Int):
            return lhs == rhs
        case let (lhs as Double, rhs as Double):
            return lhs == rhs
        case let (lhs as String, rhs as String):
            return lhs == rhs
        case let (lhs as [Any], rhs as [Any]):
            guard lhs.count == rhs.count else { return false }
            return zip(lhs, rhs).allSatisfy { AnyCodable($0) == AnyCodable($1) }
        case let (lhs as [String: Any], rhs as [String: Any]):
            guard lhs.count == rhs.count else { return false }
            return lhs.allSatisfy { key, val in
                guard let other = rhs[key] else { return false }
                return AnyCodable(val) == AnyCodable(other)
            }
        default:
            return false
        }
    }
}
