// swift-tools-version: 5.9

import PackageDescription

let package = Package(
    name: "AHPApp",
    platforms: [
        .iOS(.v17),
    ],
    dependencies: [
        .package(path: "../AgentHostProtocol"),
    ],
    targets: [
        .executableTarget(
            name: "AHPApp",
            dependencies: ["AgentHostProtocol"],
            path: "Sources/AHPApp"
        ),
    ]
)
