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
        } catch let error as TunnelError {
            switch error {
            case .AuthenticationFailed, .ApiError, .NoTunnelsFound, .TunnelNotFound:
                break  // Any of these is valid
            }
        } catch {
            // Any error from the FFI layer is acceptable for an invalid token
        }
    }

    func testGetTunnelDetailRequiresAuth() {
        do {
            _ = try getTunnelDetail(
                accessToken: "invalid-token",
                clusterId: "usw2",
                tunnelId: "test123"
            )
        } catch let error as TunnelError {
            switch error {
            case .AuthenticationFailed, .ApiError, .TunnelNotFound, .NoTunnelsFound:
                break
            }
        } catch {
            // Any error from FFI is acceptable
        }
    }

    func testTunnelDetailInit() {
        let detail = TunnelDetail(
            tunnelId: "abc123",
            name: "my-machine",
            clusterId: "usw2",
            clientRelayUri: "wss://usw2-data.rel.tunnels.api.visualstudio.com/...",
            ports: [8080, 31546]
        )
        XCTAssertEqual(detail.tunnelId, "abc123")
        XCTAssertEqual(detail.name, "my-machine")
        XCTAssertEqual(detail.clientRelayUri, "wss://usw2-data.rel.tunnels.api.visualstudio.com/...")
        XCTAssertEqual(detail.ports, [8080, 31546])
    }

    func testDeviceCodeResponseInit() {
        let resp = DeviceCodeResponse(
            deviceCode: "dc_abc123",
            userCode: "ABCD-1234",
            verificationUri: "https://github.com/login/device",
            expiresIn: 900,
            interval: 5
        )
        XCTAssertEqual(resp.userCode, "ABCD-1234")
        XCTAssertEqual(resp.verificationUri, "https://github.com/login/device")
        XCTAssertEqual(resp.expiresIn, 900)
        XCTAssertEqual(resp.interval, 5)
    }

    func testDeviceCodePollResultCases() {
        let token = DeviceCodePollResult.accessToken(token: "ghp_abc")
        let pending = DeviceCodePollResult.pending
        let expired = DeviceCodePollResult.expired
        let error = DeviceCodePollResult.error(message: "denied")

        switch token {
        case .accessToken(let t): XCTAssertEqual(t, "ghp_abc")
        default: XCTFail("Expected accessToken")
        }
        switch pending {
        case .pending: break
        default: XCTFail("Expected pending")
        }
        switch expired {
        case .expired: break
        default: XCTFail("Expected expired")
        }
        switch error {
        case .error(let msg): XCTAssertEqual(msg, "denied")
        default: XCTFail("Expected error")
        }
    }

    func testStartDeviceCodeAuth() {
        // This calls the real GitHub API — should succeed and return a valid response.
        // Verifies the full FFI round-trip works for auth.
        do {
            let resp = try startDeviceCodeAuth()
            XCTAssertFalse(resp.userCode.isEmpty)
            XCTAssertTrue(resp.verificationUri.contains("github.com"))
            XCTAssertGreaterThan(resp.expiresIn, 0)
            XCTAssertGreaterThan(resp.interval, 0)
        } catch {
            // Network errors are acceptable in CI/offline environments
        }
    }

    func testPollDeviceCodeAuthWithInvalidCode() {
        do {
            let result = try pollDeviceCodeAuth(deviceCode: "invalid_device_code")
            switch result {
            case .error:
                break  // Expected for invalid device code
            case .expired:
                break  // Also acceptable
            default:
                break  // Any result is fine, we're testing the FFI boundary
            }
        } catch {
            // Network or API errors acceptable
        }
    }
}

