// Prints the CGWindowID of the largest on-screen window owned by the given process ID (for `screencapture -l`).
// Usage: window-id <pid>   — exits 1 when the process has no on-screen window yet.
import CoreGraphics
import Foundation

guard CommandLine.arguments.count == 2, let pid = Int32(CommandLine.arguments[1]) else {
    FileHandle.standardError.write("usage: window-id <pid>\n".data(using: .utf8)!)
    exit(2)
}
let windows = (CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]]) ?? []
let mine = windows.filter { ($0[kCGWindowOwnerPID as String] as? Int32) == pid && ($0[kCGWindowLayer as String] as? Int) == 0 }
func area(_ w: [String: Any]) -> Double {
    guard let b = w[kCGWindowBounds as String] as? [String: Double] else { return 0 }
    return (b["Width"] ?? 0) * (b["Height"] ?? 0)
}
guard let best = mine.max(by: { area($0) < area($1) }), let id = best[kCGWindowNumber as String] as? Int else { exit(1) }
print(id)
