// swift-tools-version: 5.9

import PackageDescription

let package = Package(
    name: "DevTunnelsBridge",
    platforms: [
        .iOS(.v16),
        .macOS(.v13),
    ],
    products: [
        .library(
            name: "DevTunnelsBridge",
            targets: ["DevTunnelsBridge"]
        ),
    ],
    targets: [
        .target(
            name: "DevTunnelsBridge",
            path: "Sources/DevTunnelsBridge"
        ),
        .testTarget(
            name: "DevTunnelsBridgeTests",
            dependencies: ["DevTunnelsBridge"],
            path: "Tests/DevTunnelsBridgeTests"
        ),
    ]
)
