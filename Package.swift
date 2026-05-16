// swift-tools-version: 5.9

import PackageDescription

// AgentHostProtocol is the official Swift client for the Agent Host Protocol.
//
// Although this repository is polyglot (TypeScript-first, with Rust and Swift
// clients), Package.swift lives at the repo root because Swift Package Manager
// only resolves remote packages whose manifest is at the repository root.
// The actual Swift sources live under clients/swift/AgentHostProtocol/.

let package = Package(
    name: "AgentHostProtocol",
    platforms: [
        .iOS(.v16),
        .macOS(.v13),
        .tvOS(.v16),
        .watchOS(.v9),
    ],
    products: [
        .library(
            name: "AgentHostProtocol",
            targets: ["AgentHostProtocol"]
        ),
        .library(
            name: "AgentHostProtocolClient",
            targets: ["AgentHostProtocolClient"]
        ),
    ],
    targets: [
        .target(
            name: "AgentHostProtocol",
            path: "clients/swift/AgentHostProtocol/Sources/AgentHostProtocol"
        ),
        .target(
            name: "AgentHostProtocolClient",
            dependencies: ["AgentHostProtocol"],
            path: "clients/swift/AgentHostProtocol/Sources/AgentHostProtocolClient"
        ),
        .testTarget(
            name: "AgentHostProtocolTests",
            dependencies: ["AgentHostProtocol"],
            path: "clients/swift/AgentHostProtocol/Tests/AgentHostProtocolTests"
        ),
        .testTarget(
            name: "AgentHostProtocolClientTests",
            dependencies: ["AgentHostProtocolClient"],
            path: "clients/swift/AgentHostProtocol/Tests/AgentHostProtocolClientTests"
        ),
    ]
)
