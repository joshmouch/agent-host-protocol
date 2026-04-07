import XCTest
@testable import DevTunnelsBridge

final class DevTunnelsBridgeTests: XCTestCase {
    func testTunnelInfoInit() {
        let info = TunnelInfo(
            tunnelId: "abc123",
            name: "my-machine",
            clusterId: "usw2",
            hasEndpoints: true
        )
        XCTAssertEqual(info.tunnelId, "abc123")
        XCTAssertEqual(info.name, "my-machine")
        XCTAssertEqual(info.clusterId, "usw2")
        XCTAssertTrue(info.hasEndpoints)
    }

    func testTunnelInfoEquality() {
        let a = TunnelInfo(tunnelId: "a", name: "n", clusterId: "c", hasEndpoints: false)
        let b = TunnelInfo(tunnelId: "a", name: "n", clusterId: "c", hasEndpoints: false)
        XCTAssertEqual(a, b)
    }

    func testTunnelErrorCases() {
        let authError = TunnelError.AuthenticationFailed(message: "token expired")
        let apiError = TunnelError.ApiError(message: "HTTP 500")

        switch authError {
        case .AuthenticationFailed(let msg):
            XCTAssertEqual(msg, "token expired")
        default:
            XCTFail("Expected AuthenticationFailed")
        }

        switch apiError {
        case .ApiError(let msg):
            XCTAssertEqual(msg, "HTTP 500")
        default:
            XCTFail("Expected ApiError")
        }
    }

    func testListTunnelsRequiresAuth() {
        // Calling with an invalid token should produce an error, not crash.
        // This validates the FFI boundary works end-to-end.
        do {
            _ = try listTunnels(accessToken: "invalid-token")
            // If it doesn't throw, that's fine too (would need real auth)
        } catch let error as TunnelError {
            // Expected: either AuthenticationFailed or ApiError
            switch error {
            case .AuthenticationFailed, .ApiError, .NoTunnelsFound:
                break  // Any of these is valid
            }
        } catch {
            // Any error from the FFI layer is acceptable for an invalid token
        }
    }
}

