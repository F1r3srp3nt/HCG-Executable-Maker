// swift-tools-version:5.7
import PackageDescription

// SentinelCore holds every piece of HCG Sentinel logic that does not need UIKit,
// SwiftUI, CoreBluetooth or Network.framework, so it can be unit-tested on
// macOS and Linux without a simulator.
let package = Package(
    name: "SentinelCore",
    platforms: [.iOS(.v14), .macOS(.v11)],
    products: [
        .library(name: "SentinelCore", targets: ["SentinelCore"])
    ],
    targets: [
        .target(name: "SentinelCore"),
        .testTarget(name: "SentinelCoreTests", dependencies: ["SentinelCore"])
    ]
)
