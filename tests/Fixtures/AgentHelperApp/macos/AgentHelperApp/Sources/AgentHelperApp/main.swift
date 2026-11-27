import AppKit
import Foundation

/// Runs the bundled host agent (Contents/Helpers/hcg-agent version) at launch, shows what it printed and writes it to
/// $TMPDIR/hcg-agent-helper-probe.txt so a test can prove the packaged app found and ran its helper.
final class AppDelegate: NSObject, NSApplicationDelegate {
    var window: NSWindow?

    func applicationDidFinishLaunching(_ notification: Notification) {
        let helper = Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/hcg-agent")
        var text: String
        let process = Process()
        process.executableURL = helper
        process.arguments = ["version"]
        let pipe = Pipe()
        process.standardOutput = pipe
        do {
            try process.run()
            process.waitUntilExit()
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            text = String(data: data, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            if process.terminationStatus != 0 { text = "helper exited with \(process.terminationStatus): \(text)" }
        } catch {
            text = "helper could not run: \(error.localizedDescription)"
        }
        let report = FileManager.default.temporaryDirectory.appendingPathComponent("hcg-agent-helper-probe.txt")
        try? "helper=\(text)\n".write(to: report, atomically: true, encoding: .utf8)

        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 560, height: 180), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.title = "Agent Helper"
        let label = NSTextField(labelWithString: "Host agent: \(text)")
        label.frame = NSRect(x: 24, y: 70, width: 512, height: 40)
        window.contentView?.addSubview(label)
        window.center()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        self.window = window
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()
