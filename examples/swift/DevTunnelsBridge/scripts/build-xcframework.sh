#!/usr/bin/env bash
# Build the Rust library for iOS targets and package as XCFramework.
#
# Prerequisites:
#   rustup target add aarch64-apple-ios aarch64-apple-ios-sim
#   cargo install uniffi-bindgen-swift
#
# Usage:
#   cd DevTunnelsBridge/scripts
#   ./build-xcframework.sh [--release]

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
RUST_DIR="$ROOT_DIR/rust"
FRAMEWORK_DIR="$ROOT_DIR/Frameworks"
BUILD_TYPE="${1:-debug}"

if [[ "$BUILD_TYPE" == "--release" ]]; then
    CARGO_FLAGS="--release"
    TARGET_DIR="release"
else
    CARGO_FLAGS=""
    TARGET_DIR="debug"
fi

echo "==> Building Rust library for iOS..."

# Build for iOS device (ARM64)
echo "  → aarch64-apple-ios"
cd "$RUST_DIR"
cargo build $CARGO_FLAGS --target aarch64-apple-ios

# Build for iOS simulator (ARM64)
echo "  → aarch64-apple-ios-sim"
cargo build $CARGO_FLAGS --target aarch64-apple-ios-sim

echo "==> Generating Swift bindings via UniFFI..."
uniffi-bindgen-swift \
    "$RUST_DIR/src/lib.rs" \
    --out-dir "$ROOT_DIR/Sources/DevTunnelsBridge/Generated" \
    --module-name DevTunnelsFFI

echo "==> Creating XCFramework..."
rm -rf "$FRAMEWORK_DIR/DevTunnelsFFI.xcframework"

xcodebuild -create-xcframework \
    -library "$RUST_DIR/target/aarch64-apple-ios/$TARGET_DIR/libdev_tunnels_bridge.a" \
    -library "$RUST_DIR/target/aarch64-apple-ios-sim/$TARGET_DIR/libdev_tunnels_bridge.a" \
    -output "$FRAMEWORK_DIR/DevTunnelsFFI.xcframework"

echo "==> Done! XCFramework at: $FRAMEWORK_DIR/DevTunnelsFFI.xcframework"
