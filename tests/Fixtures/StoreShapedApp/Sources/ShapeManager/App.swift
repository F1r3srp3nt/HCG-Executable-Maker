#if os(macOS)
import AppKit
import ShapeKit
import SwiftUI

/// The SwiftUI app (as HCGStoreManager is): a sidebar with the app's artwork and a few sections.
@main
struct ShapeManagerApp: App {
    @State private var section: String? = "Command Center"
    var body: some Scene {
        WindowGroup("Store-shaped Manager") {
            NavigationSplitView {
                VStack(alignment: .leading) {
                    HStack {
                        if let img = Artwork.image("Brand-512", "png") {
                            Image(nsImage: img).resizable().frame(width: 40, height: 40).clipShape(RoundedRectangle(cornerRadius: 9))
                        }
                        Text("STORE-SHAPED MANAGER").font(.caption.bold())
                    }.padding()
                    List(["Command Center", "Upload Center", "Settings"], id: \.self, selection: $section) { Text($0) }
                }
            } detail: {
                Text(section ?? "").font(.largeTitle).frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            .frame(minWidth: 800, minHeight: 500)
            .onAppear { LaunchProbe.write() }
        }
    }
}

/// Artwork from Contents/Resources (Bundle.main); the same loader HCG Store Manager uses.
enum Artwork {
    static func image(_ name: String, _ ext: String) -> NSImage? {
        guard let url = Bundle.main.url(forResource: name, withExtension: ext) else { return nil }
        return NSImage(contentsOf: url)
    }
}

/// Written when the window appears, so CI can prove the packaged app launched its GUI and found its artwork:
/// $TMPDIR/hcg-store-shaped-probe.txt.
enum LaunchProbe {
    static func write() {
        let icon = Bundle.main.object(forInfoDictionaryKey: "CFBundleIconFile") as? String ?? "(none)"
        let iconFile = Bundle.main.url(forResource: "AppIcon", withExtension: "icns") != nil
        let lines = [
            "window=ShapeManager",
            "bundle=\(Bundle.main.bundleIdentifier ?? "(none)")",
            "artwork=\(Artwork.image("Brand-512", "png") != nil ? "loaded" : "missing")",
            "icon=\(icon) file=\(iconFile ? "present" : "missing")",
            "core=\(ShapeKit.sha256("store").prefix(12))",
        ]
        let report = FileManager.default.temporaryDirectory.appendingPathComponent("hcg-store-shaped-probe.txt")
        try? (lines.joined(separator: "\n") + "\n").write(to: report, atomically: true, encoding: .utf8)
    }
}
#else
@main
enum ShapeManagerUnsupported { static func main() { print("macOS only") } }
#endif
