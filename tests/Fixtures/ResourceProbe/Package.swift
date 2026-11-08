// swift-tools-version: 5.9
import PackageDescription

// ResourceProbe — proves that SwiftPM resources load (through Bundle.module) from the packaged .app.
// Both the library and the app target declare resources, so both resource bundles must be found at run time.
let package = Package(
    name: "ResourceProbe",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "ProbeKit", targets: ["ProbeKit"]),
        .executable(name: "ResourceProbe", targets: ["ResourceProbeApp"]),
    ],
    targets: [
        .target(name: "ProbeKit", resources: [.process("Resources")]),
        .executableTarget(name: "ResourceProbeApp", dependencies: ["ProbeKit"], resources: [.copy("Resources/banner.txt")]),
    ]
)
