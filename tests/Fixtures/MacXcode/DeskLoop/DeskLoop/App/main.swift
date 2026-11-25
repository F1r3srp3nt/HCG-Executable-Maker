import AppKit
import AVFoundation

// DeskLoop: a minimal AppKit app used as an HCG Executable Maker test fixture (native macOS app built with Xcode).
let app = NSApplication.shared
final class Delegate: NSObject, NSApplicationDelegate {
    var window: NSWindow?
    func applicationDidFinishLaunching(_ notification: Notification) {
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 420, height: 240), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        w.title = "DeskLoop"
        w.contentView = NSTextField(labelWithString: "DeskLoop \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") ?? "")")
        w.center()
        w.makeKeyAndOrderFront(nil)
        window = w
        NSApp.activate(ignoringOtherApps: true)
    }
}
let delegate = Delegate()
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()
