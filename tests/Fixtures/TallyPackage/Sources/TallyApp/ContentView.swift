import AppKit
import SwiftUI
import TallyCore

struct ContentView: View {
    @AppStorage("step") private var step = 1
    @State private var tally = Tally()
    @State private var copied = false
    @StateObject private var mic = MicrophoneCheck()

    var body: some View {
        VStack(spacing: 16) {
            Text("Tally")
                .font(.largeTitle.bold())
            Text("\(tally.total)")
                .font(.system(size: 72, weight: .bold, design: .rounded))
                .accessibilityIdentifier("total")
            Text(tally.summary)
                .foregroundStyle(.secondary)
            HStack(spacing: 12) {
                Button("Add \(step)") {
                    tally.step = step
                    tally.add()
                    copied = false
                }
                .keyboardShortcut(.defaultAction)
                .help("Adds the step to the total")
                Button("Reset") { tally.reset() }
            }
            .buttonStyle(.borderedProminent)
            Button(copied ? "Copied" : "Copy total") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString("\(tally.total)", forType: .string)
                copied = true
            }
            Text(mic.status)
                .font(.footnote)
                .foregroundStyle(.secondary)
                .textSelection(.enabled)
        }
        .padding(32)
        .task { await mic.refresh() }
        .onAppear { ReadinessSignal.emit(screen: "ContentView", elements: ["title": "Tally", "total": "\(tally.total)", "summary": tally.summary]) }
    }
}

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
