import XCTest
import AgentHostProtocol

final class EnumCompatibilityTests: XCTestCase {
    func testNonexhaustiveEnumAndUnionRoundTripUnknownValues() throws {
        let decoder = JSONDecoder()
        let encoder = JSONEncoder()

        let kind = try decoder.decode(ResponsePartKind.self, from: Data(#""futurePart""#.utf8))
        guard case .unknown(let rawKind) = kind else {
            return XCTFail("Expected unknown response-part kind")
        }
        XCTAssertEqual(rawKind, "futurePart")
        XCTAssertEqual(String(data: try encoder.encode(kind), encoding: .utf8), #""futurePart""#)

        let rawPart = #"{"kind":"futurePart","payload":{"preserve":true}}"#
        let part = try decoder.decode(ResponsePart.self, from: Data(rawPart.utf8))
        guard case .unknown = part else {
            return XCTFail("Expected unknown response part")
        }
        XCTAssertEqual(String(data: try encoder.encode(part), encoding: .utf8), rawPart)
    }
}
