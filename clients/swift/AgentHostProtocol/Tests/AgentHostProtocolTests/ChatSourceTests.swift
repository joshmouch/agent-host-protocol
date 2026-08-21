import XCTest
import AgentHostProtocol

final class ChatSourceTests: XCTestCase {

    func testChatSourceRoutesByKind() throws {
        let decoder = JSONDecoder()

        let fork = try decoder.decode(
            ChatSource.self,
            from: Data(#"{"kind":"fork","chat":"ahp-chat:/main","turnId":"turn-12"}"#.utf8)
        )
        let sideChat = try decoder.decode(
            ChatSource.self,
            from: Data(#"{"kind":"sideChat","chat":"ahp-chat:/main","turnId":"turn-active","selection":{"text":"const value = compute()","responsePartId":"part-7"}}"#.utf8)
        )

        switch fork {
        case .fork(let value):
            XCTAssertEqual(value.kind, .fork)
            XCTAssertEqual(value.turnId, "turn-12")
        default:
            XCTFail("Expected fork variant")
        }

        switch sideChat {
        case .sideChat(let value):
            XCTAssertEqual(value.kind, .sideChat)
            XCTAssertEqual(value.turnId, "turn-active")
            XCTAssertEqual(value.selection?.text, "const value = compute()")
            XCTAssertEqual(value.selection?.responsePartId, "part-7")
        default:
            XCTFail("Expected sideChat variant")
        }
    }

    func testChatSourcePreservesMissingOrUnknownKind() throws {
        let decoder = JSONDecoder()
        let encoder = JSONEncoder()
        let payloads = [
            #"{"chat":"ahp-chat:/main","turnId":"turn-12"}"#,
            #"{"kind":"future","chat":"ahp-chat:/main","turnId":"turn-12"}"#,
        ]

        for payload in payloads {
            let source = try decoder.decode(ChatSource.self, from: Data(payload.utf8))
            guard case .unknown = source else {
                return XCTFail("Expected unknown ChatSource for \(payload)")
            }
            let encoded = try encoder.encode(source)
            XCTAssertEqual(
                try JSONSerialization.jsonObject(with: encoded) as? NSDictionary,
                try JSONSerialization.jsonObject(with: Data(payload.utf8)) as? NSDictionary
            )
        }
    }

    func testChatSourceBranchesEncodeFixedKinds() throws {
        let encoder = JSONEncoder()

        let fork = ForkChatSource(chat: "ahp-chat:/main", turnId: "turn-12")
        let sideChat = SideChatSource(
            chat: "ahp-chat:/main",
            turnId: "turn-active",
            selection: SideChatSelection(text: "const value = compute()", responsePartId: "part-7")
        )

        XCTAssertEqual(fork.kind, .fork)
        XCTAssertEqual(sideChat.kind, .sideChat)

        let forkBranch = try JSONSerialization.jsonObject(with: encoder.encode(fork)) as? [String: Any]
        let sideChatBranch = try JSONSerialization.jsonObject(with: encoder.encode(sideChat)) as? [String: Any]
        let forkUnion = try JSONSerialization.jsonObject(with: encoder.encode(ChatSource.fork(fork))) as? [String: Any]
        let sideChatUnion = try JSONSerialization.jsonObject(with: encoder.encode(ChatSource.sideChat(sideChat))) as? [String: Any]

        XCTAssertEqual(forkBranch?["kind"] as? String, "fork")
        XCTAssertEqual(sideChatBranch?["kind"] as? String, "sideChat")
        XCTAssertEqual(forkUnion?["kind"] as? String, "fork")
        XCTAssertEqual(sideChatUnion?["kind"] as? String, "sideChat")
        XCTAssertEqual((sideChatBranch?["selection"] as? [String: Any])?["text"] as? String, "const value = compute()")
        XCTAssertEqual((sideChatUnion?["selection"] as? [String: Any])?["responsePartId"] as? String, "part-7")
    }
}
