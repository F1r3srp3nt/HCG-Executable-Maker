// swift-tools-version:5.10
// StoreShapedApp: a public regression fixture with the same package shape as HCG Store Manager (a private repository),
// so HCG Executable Maker's CI proves the shape on a real Mac:
//   - a platform-neutral library, a command-line tool and a SwiftUI app as three products (two executables);
//   - a dependency used on Linux only (swift-crypto), which SwiftPM still resolves on macOS;
//   - no SwiftPM resources: the icon and the sidebar artwork come from AppPackaging.json into Contents/Resources and
//     are loaded with Bundle.main, so the app is fully sealed.
import PackageDescription

let package = Package(
    name: "StoreShapedApp",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "ShapeKit", targets: ["ShapeKit"]),
        .executable(name: "shape-tool", targets: ["shape-tool"]),
        .executable(name: "ShapeManager", targets: ["ShapeManager"]),
    ],
    dependencies: [
        .package(url: "https://github.com/apple/swift-crypto.git", "3.0.0" ..< "4.0.0"),
    ],
    targets: [
        .target(name: "ShapeKit", dependencies: [.product(name: "Crypto", package: "swift-crypto", condition: .when(platforms: [.linux]))]),
        .executableTarget(name: "shape-tool", dependencies: ["ShapeKit"]),
        .executableTarget(name: "ShapeManager", dependencies: ["ShapeKit"]),
        .testTarget(name: "ShapeKitTests", dependencies: ["ShapeKit"]),
    ]
)
