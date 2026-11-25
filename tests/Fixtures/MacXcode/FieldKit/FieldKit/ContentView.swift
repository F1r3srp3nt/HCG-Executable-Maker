import SwiftUI
import FieldKitCore

struct ContentView: View {
    private let summary = FieldSummary(readings: [12.5, 13.0, 11.75])

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("FieldKit").font(.title)
            Text("Readings: \(summary.count)")
            Text("Average: \(summary.average, specifier: "%.2f")")
            #if FIELDKIT_DEV
            Text("Development build").foregroundStyle(.secondary)
            #endif
        }
        .padding(24)
        .frame(minWidth: 320, minHeight: 160)
    }
}
