// Run HCG Executable Maker — the target Xcode's Run button starts.
//
// HCG Executable Maker is a .NET / Avalonia application. Xcode cannot compile it, so the "HCG Executable Maker (.NET)"
// External Build System target builds the real app bundle with the repository's own pipeline
// (Scripts/build-app.sh → tools/self-package). This small native tool is built after it and, when Xcode runs it,
// replaces itself (execv) with the real app's executable inside "HCG Executable Maker.app". The process Xcode watches
// is then the actual application: its output appears in Xcode's console, and Stop quits it.
//
// The bundle is found next to this tool in the build products folder, or at HCG_APP_BUNDLE if that is set.
import Foundation

let appName = "HCG Executable Maker.app"
let executableName = "HCGExecutableMaker"

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(("error: " + message + "\n").data(using: .utf8)!)
    exit(1)
}

let selfURL = URL(fileURLWithPath: CommandLine.arguments[0]).resolvingSymlinksInPath()
var bundleURL = selfURL.deletingLastPathComponent().appendingPathComponent(appName)
if let override = ProcessInfo.processInfo.environment["HCG_APP_BUNDLE"], !override.isEmpty, !override.contains("$(") {
    bundleURL = URL(fileURLWithPath: override)
}

let executableURL = bundleURL.appendingPathComponent("Contents/MacOS").appendingPathComponent(executableName)
guard FileManager.default.isExecutableFile(atPath: executableURL.path) else {
    fail("\(executableURL.path) was not found. Build the \"HCG Executable Maker\" scheme first (⌘B); the build log is in "
        + "\(selfURL.deletingLastPathComponent().appendingPathComponent("HCGExecutableMaker-build-logs").path).")
}

print("Starting \(bundleURL.path)")
fflush(stdout)

// argv[0] is the real executable's path, so the app finds its bundle (Info.plist, Resources, icon) as when Finder opens it.
let arguments = [executableURL.path] + CommandLine.arguments.dropFirst()
var cArguments = arguments.map { strdup($0) } + [nil]
execv(executableURL.path, &cArguments)
fail("Could not start \(executableURL.path): \(String(cString: strerror(errno))).")
