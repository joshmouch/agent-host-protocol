import XCTest
@testable import DevTunnelsBridge

final class DevTunnelsBridgeTests: XCTestCase {
    func testTunnelInfoInit() {
        let info = TunnelInfo(
            tunnelId: "abc123",
            name: "my-machine",
            isOnline: true,
            ports: [31546, 8080]
        )
        XCTAssertEqual(info.tunnelId, "abc123")
        XCTAssertEqual(info.name, "my-machine")
        XCTAssertTrue(info.isOnline)
        XCTAssertEqual(info.ports, [31546, 8080])
    }

    func testDeviceCodeAuthInit() {
        let auth = DeviceCodeAuth(
            userCode: "ABCD-1234",
            verificationUri: "https://github.com/login/device"
        )
        XCTAssertEqual(auth.userCode, "ABCD-1234")
        XCTAssertEqual(auth.verificationUri, "https://github.com/login/device")
    }

    func testTunnelErrorCases() {
        let authError = TunnelError.authenticationFailed("token expired")
        let portError = TunnelError.portNotAvailable(31546)

        switch authError {
        case .authenticationFailed(let msg):
            XCTAssertEqual(msg, "token expired")
        default:
            XCTFail("Expected authenticationFailed")
        }

        switch portError {
        case .portNotAvailable(let port):
            XCTAssertEqual(port, 31546)
        default:
            XCTFail("Expected portNotAvailable")
        }
    }
}
