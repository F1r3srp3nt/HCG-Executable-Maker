// swift-tools-version: 5.9
import PackageDescription

// Tally — a small SwiftUI Mac app in a Swift package (the layout of Oslo-Sprint's Distil and Relay).
// The core library builds anywhere; the SwiftUI app target is declared only on macOS.
var products: [Product] = [.library(name: "TallyCore", targets: ["TallyCore"])]
var targets: [Target] = [
    .target(name: "TallyCore"),
    .testTarget(name: "TallyCoreTests", dependencies: ["TallyCore"]),
]
#if os(macOS)
products.append(.executable(name: "Tally", targets: ["TallyApp"]))
targets.append(.executableTarget(name: "TallyApp", dependencies: ["TallyCore"]))
#endif

let package = Package(
    name: "Tally",
    platforms: [.macOS(.v14)],
    products: products,
    targets: targets
)
