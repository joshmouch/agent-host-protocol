// swift-tools-version: 5.9
// AHP Swift Conformance Runner — B5
// Depends on the root-level AgentHostProtocol package (local path).

import PackageDescription

let package = Package(
    name: "AHPConformanceSwift",
    platforms: [
        .macOS(.v13),
    ],
    dependencies: [
        .package(path: "../.."),  // root Package.swift → AgentHostProtocol
    ],
    targets: [
        .executableTarget(
            name: "ConformanceRunner",
            dependencies: [
                .product(name: "AgentHostProtocol", package: "agent-host-protocol"),
            ],
            path: "Sources/ConformanceRunner"
        ),
    ]
)
