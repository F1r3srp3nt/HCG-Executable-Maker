import SwiftUI
import AppKit

/// A tiny tally counter: add the configured step, reset, copy the result.
struct ContentView: View {
    @AppStorage("step") private var step = 1
    @State private var count = 0
    @State private var copied = false

    var body: some View {
        VStack(spacing: 18) {
            Text("Hello from HCG")
                .font(.title)
            Text("\(count)")
                .font(.system(size: 64, weight: .bold, design: .rounded))
                .accessibilityIdentifier("count")
            HStack(spacing: 12) {
                Button("Add \(step)") { count += step; copied = false }
                    .keyboardShortcut(.defaultAction)
                    .accessibilityIdentifier("add")
                Button("Reset") { count = 0; copied = false }
                    .accessibilityIdentifier("reset")
            }
            Button(copied ? "Copied" : "Copy result") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString("\(count)", forType: .string)
                copied = true
            }
            .accessibilityIdentifier("copy")
        }
        .padding(32)
        .onAppear { ReadinessSignal.emit(screen: "ContentView", elements: ["title": "Hello from HCG", "count": "\(count)"]) }
    }
}

/// Preferences window (⌘,).
struct SettingsView: View {
    @AppStorage("step") private var step = 1

    var body: some View {
        Form {
            Stepper("Step: \(step)", value: $step, in: 1...10)
        }
        .padding(20)
        .frame(width: 260)
    }
}

/// Test-only readiness signal for HCG's CI. When the app is launched with HCG_READY_SIGNAL=<nonce> (HCG passes it through
/// simctl), the first screen reports — once it has appeared — that it is up, with the values its key elements show, in
/// Documents/hcg-ready.json. Without the variable it does nothing. Ported along with the rest of the app.
enum ReadinessSignal {
    static func emit(screen: String, elements: [String: String]) {
        guard let nonce = ProcessInfo.processInfo.environment["HCG_READY_SIGNAL"], !nonce.isEmpty,
              let documents = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first else { return }
        let record: [String: Any] = ["nonce": nonce, "screen": screen, "elements": elements]
        guard let data = try? JSONSerialization.data(withJSONObject: record, options: [.sortedKeys]) else { return }
        try? FileManager.default.createDirectory(at: documents, withIntermediateDirectories: true)
        try? data.write(to: documents.appendingPathComponent("hcg-ready.json"), options: .atomic)
    }
}
