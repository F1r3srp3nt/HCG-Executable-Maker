import Foundation
import ProbeKit
import SwiftUI

/// Loads one resource from the app target and one from the library, shows both, and records the result in
/// $TMPDIR/hcg-resource-probe.txt so an automated check can read it.
enum Probe {
    static func run() -> (app: String, library: String) {
        let app: String
        if let url = Bundle.module.url(forResource: "banner", withExtension: "txt"), let text = try? String(contentsOf: url, encoding: .utf8) {
            app = text.trimmingCharacters(in: .whitespacesAndNewlines)
        } else {
            app = "MISSING app resource"
        }
        let library = ProbeKit.greeting()
        let report = "app=\(app)\nlibrary=\(library)\nappBundle=\(Bundle.module.bundlePath)\n"
        try? report.write(to: FileManager.default.temporaryDirectory.appendingPathComponent("hcg-resource-probe.txt"), atomically: true, encoding: .utf8)
        return (app, library)
    }
}

@main
struct ResourceProbeApp: App {
    private let result = Probe.run()

    var body: some Scene {
        WindowGroup("Resource Probe") {
            VStack(alignment: .leading, spacing: 12) {
                Text("SwiftPM resources").font(.title.bold())
                Label(result.app, systemImage: result.app.hasPrefix("MISSING") ? "xmark.octagon" : "checkmark.circle")
                Label(result.library, systemImage: result.library.hasPrefix("MISSING") ? "xmark.octagon" : "checkmark.circle")
            }
            .padding(32)
            .frame(minWidth: 420, minHeight: 200)
        }
    }
}
