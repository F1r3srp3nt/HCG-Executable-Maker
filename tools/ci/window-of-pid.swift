// Prints "<window id> <width>x<height>" for each on-screen window owned by the given process ID (largest first).
// Used by CI to screenshot exactly the app it started (screencapture -l <id>). Needs no Accessibility permission.
import CoreGraphics
import Foundation

guard CommandLine.arguments.count == 2, let pid = Int32(CommandLine.arguments[1]) else {
    FileHandle.standardError.write("usage: window-of-pid <pid>\n".data(using: .utf8)!); exit(2)
}
let windows = (CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]]) ?? []
let mine = windows.filter { ($0[kCGWindowOwnerPID as String] as? Int32) == pid && ($0[kCGWindowLayer as String] as? Int) == 0 }
    .compactMap { w -> (Int, Int, Int)? in
        guard let id = w[kCGWindowNumber as String] as? Int, let b = w[kCGWindowBounds as String] as? [String: Any],
              let width = b["Width"] as? Int, let height = b["Height"] as? Int else { return nil }
        return (id, width, height)
    }
    .sorted { $0.1 * $0.2 > $1.1 * $1.2 }
for (id, width, height) in mine { print("\(id) \(width)x\(height)") }
exit(mine.isEmpty ? 1 : 0)
