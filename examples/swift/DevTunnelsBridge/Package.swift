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
        // C target exposing the UniFFI-generated header + modulemap.
        // The actual symbols are provided by the Rust static library
        // linked via unsafeFlags below.
        .target(
            name: "DevTunnelsFFIFFI",
            path: "Sources/DevTunnelsFFIFFI",
            publicHeadersPath: "include"
        ),
        // Swift target with the UniFFI-generated Swift bindings and wrapper API.
        .target(
            name: "DevTunnelsBridge",
            dependencies: ["DevTunnelsFFIFFI"],
            path: "Sources/DevTunnelsBridge",
            linkerSettings: [
                .unsafeFlags([
                    "-Lrust/target/debug",
                    "-ldev_tunnels_bridge",
                ]),
            ]
        ),
        .testTarget(
            name: "DevTunnelsBridgeTests",
            dependencies: ["DevTunnelsBridge"],
            path: "Tests/DevTunnelsBridgeTests"
        ),
    ]
)
