// AnyCodableTests.swift — Tests for AnyCodable encode/decode correctness.
//
// Focused on the NSNumber-bridging bug: when a [String: Any] dictionary is
// produced by JSONSerialization (which boxes numeric/bool values as NSNumber),
// AnyCodable must re-encode each value to its original JSON type rather than
// the first matching Swift pattern-match arm.

import XCTest
@testable import AgentHostProtocol

final class AnyCodableTests: XCTestCase {

    func testAnyCodableEncodePreservesIntFromNSNumber() throws {
        let object = try JSONSerialization.jsonObject(
            with: #"{"x":1}"#.data(using: .utf8)!
        )
        let wrapped = AnyCodable(object)
        let bytes = try JSONEncoder().encode(wrapped)
        XCTAssertEqual(String(data: bytes, encoding: .utf8), #"{"x":1}"#)
    }

    func testAnyCodableEncodePreservesBoolFromNSNumber() throws {
        let object = try JSONSerialization.jsonObject(
            with: #"{"x":true}"#.data(using: .utf8)!
        )
        let wrapped = AnyCodable(object)
        let bytes = try JSONEncoder().encode(wrapped)
        XCTAssertEqual(String(data: bytes, encoding: .utf8), #"{"x":true}"#)
    }

    func testAnyCodableEncodePreservesDoubleFromNSNumber() throws {
        let object = try JSONSerialization.jsonObject(
            with: #"{"x":1.5}"#.data(using: .utf8)!
        )
        let wrapped = AnyCodable(object)
        let bytes = try JSONEncoder().encode(wrapped)
        XCTAssertEqual(String(data: bytes, encoding: .utf8), #"{"x":1.5}"#)
    }

    func testAnyCodableEncodePreservesNativeSwiftBool() throws {
        // A native Swift Bool (NOT NSNumber-backed) must encode as `true`, not `1`.
        // Exercises the `type(of:) != Bool.self` guard, which routes a native Swift
        // Bool past the objCType dispatch to the `case let bool as Bool` arm.
        let wrapped = AnyCodable(["x": true] as [String: Any])
        let bytes = try JSONEncoder().encode(wrapped)
        XCTAssertEqual(String(data: bytes, encoding: .utf8), #"{"x":true}"#)
    }

    func testAnyCodableEncodePreservesFloatBackedNSNumber() throws {
        // A Float-backed NSNumber (objCType 'f') must encode as a decimal, not an
        // integer. This exercises the 'f' dispatch arm; the JSONSerialization path
        // boxes JSON numbers as 'd'/'q' and never produces it.
        let wrapped = AnyCodable(["x": NSNumber(value: Float(1.5))] as [String: Any])
        let bytes = try JSONEncoder().encode(wrapped)
        XCTAssertEqual(String(data: bytes, encoding: .utf8), #"{"x":1.5}"#)
    }

    func testAnyCodableEncodePreservesUnsignedNSNumberAboveInt64Max() throws {
        // A JSON integer above Int64.max is boxed by JSONSerialization as an
        // unsigned 'Q' NSNumber. The int64Value fallback would corrupt it (it does
        // not round-trip); the 'Q' dispatch arm encodes via uint64Value so the
        // value survives.
        let big = UInt64(Int64.max) + 1  // 9223372036854775808
        let wrapped = AnyCodable(["x": NSNumber(value: big)] as [String: Any])
        let bytes = try JSONEncoder().encode(wrapped)
        XCTAssertEqual(String(data: bytes, encoding: .utf8), #"{"x":9223372036854775808}"#)
    }
}
