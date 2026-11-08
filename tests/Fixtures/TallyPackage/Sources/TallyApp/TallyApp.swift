import AppKit
import SwiftUI
import TallyCore

final class TallyAppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        // Needed when launched with `swift run` (no app bundle): become a regular foreground app.
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

@main
struct TallyApp: App {
    @NSApplicationDelegateAdaptor(TallyAppDelegate.self) private var delegate

    var body: some Scene {
        WindowGroup("Tally") {
            ContentView()
                .frame(minWidth: 360, minHeight: 320)
        }
        .windowStyle(.hiddenTitleBar)
        Settings {
            SettingsView()
        }
    }
}
