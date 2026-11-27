// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "FieldKitCore",
    platforms: [.macOS(.v14)],
    products: [.library(name: "FieldKitCore", targets: ["FieldKitCore"])],
    targets: [
        .target(name: "FieldKitCore"),
        .testTarget(name: "FieldKitCoreTests", dependencies: ["FieldKitCore"]),
    ]
)
