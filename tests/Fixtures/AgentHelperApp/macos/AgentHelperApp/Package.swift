// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "AgentHelperApp",
    platforms: [.macOS(.v13)],
    products: [.executable(name: "AgentHelperApp", targets: ["AgentHelperApp"])],
    targets: [.executableTarget(name: "AgentHelperApp")]
)
