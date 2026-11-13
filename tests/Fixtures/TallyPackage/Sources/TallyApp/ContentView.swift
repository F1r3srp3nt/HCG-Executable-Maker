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
