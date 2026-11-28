import ShapeKit

// The command-line product. HCG Executable Maker must not put this into the app bundle.
print("shape-tool \(ShapeKit.sha256(CommandLine.arguments.dropFirst().joined(separator: " ")))")
