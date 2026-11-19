// Lists the windows a process owns (on screen or not) as JSON lines: id, layer, bounds, on-screen, and whether the layer
// is the desktop layer (CGWindowLevelForKey(.desktopWindow)) or just above it, where desktop-wallpaper renderers live.
// Usage: window-report <pid>
import CoreGraphics
import Foundation

guard CommandLine.arguments.count == 2, let pid = Int32(CommandLine.arguments[1]) else {
    FileHandle.standardError.write("usage: window-report <pid>\n".data(using: .utf8)!)
    exit(2)
}
let desktop = Int(CGWindowLevelForKey(.desktopWindow))
let all = (CGWindowListCopyWindowInfo([.optionAll], kCGNullWindowID) as? [[String: Any]]) ?? []
for w in all where (w[kCGWindowOwnerPID as String] as? Int32) == pid {
    let layer = w[kCGWindowLayer as String] as? Int ?? 0
    let b = w[kCGWindowBounds as String] as? [String: Double] ?? [:]
    let row: [String: Any] = [
        "id": w[kCGWindowNumber as String] as? Int ?? 0,
        "layer": layer,
        "desktopLayer": layer == desktop || layer == desktop + 1,
        "onScreen": w[kCGWindowIsOnscreen as String] as? Bool ?? false,
        "x": b["X"] ?? 0, "y": b["Y"] ?? 0, "width": b["Width"] ?? 0, "height": b["Height"] ?? 0,
        "name": w[kCGWindowName as String] as? String ?? "",
    ]
    let data = try JSONSerialization.data(withJSONObject: row, options: [.sortedKeys])
    print(String(data: data, encoding: .utf8)!)
}
