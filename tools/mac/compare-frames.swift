// Compares two screenshots of the same area and reports whether the picture changed (a playing video) and how much of
// it shows the sample video's fully saturated colours. Prints JSON; exit 0 always (the caller decides).
// Usage: compare-frames <a.png> <b.png>
import CoreGraphics
import Foundation
import ImageIO

func load(_ path: String) -> CGImage {
    guard let src = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil),
          let img = CGImageSourceCreateImageAtIndex(src, 0, nil) else { fatalError("cannot read \(path)") }
    return img
}

/// RGBA8 pixels of an image scaled to a fixed grid (so screenshots of different scale compare cheaply).
func pixels(_ img: CGImage, _ w: Int, _ h: Int) -> [UInt8] {
    var buf = [UInt8](repeating: 0, count: w * h * 4)
    let ctx = CGContext(data: &buf, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                        space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    ctx.interpolationQuality = .none
    ctx.draw(img, in: CGRect(x: 0, y: 0, width: w, height: h))
    return buf
}

let args = CommandLine.arguments
guard args.count == 3 else { FileHandle.standardError.write("usage: compare-frames a.png b.png\n".data(using: .utf8)!); exit(2) }
let (w, h) = (160, 100)
let a = pixels(load(args[1]), w, h), b = pixels(load(args[2]), w, h)
var changed = 0, saturatedA = 0, saturatedB = 0, total = 0
for i in stride(from: 0, to: a.count, by: 4) {
    total += 1
    let d = abs(Int(a[i]) - Int(b[i])) + abs(Int(a[i + 1]) - Int(b[i + 1])) + abs(Int(a[i + 2]) - Int(b[i + 2]))
    if d > 60 { changed += 1 }
    func sat(_ p: [UInt8]) -> Bool { // the sample's colours: one channel near max and one near zero
        let c = [Int(p[i]), Int(p[i + 1]), Int(p[i + 2])]
        return c.max()! > 200 && c.min()! < 60
    }
    if sat(a) { saturatedA += 1 }
    if sat(b) { saturatedB += 1 }
}
let result: [String: Any] = [
    "changedFraction": Double(changed) / Double(total),
    "sampleColourFractionA": Double(saturatedA) / Double(total),
    "sampleColourFractionB": Double(saturatedB) / Double(total),
]
print(String(data: try JSONSerialization.data(withJSONObject: result, options: [.sortedKeys]), encoding: .utf8)!)
