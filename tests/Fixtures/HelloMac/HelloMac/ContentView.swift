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
