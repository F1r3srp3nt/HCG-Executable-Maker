// Composes screenshots side by side into one JPEG (CI helper; runs on the macOS runner).
// usage: swift compose.swift <out.jpg> <height> <image>...
import AppKit

let args = CommandLine.arguments
let out = args[1]
let targetHeight = CGFloat(Double(args[2])!)
let images = args.dropFirst(3).compactMap { NSImage(contentsOfFile: $0)?.cgImage(forProposedRect: nil, context: nil, hints: nil) }
let widths = images.map { CGFloat($0.width) * targetHeight / CGFloat($0.height) }
let gap: CGFloat = 24
let width = Int(widths.reduce(0, +) + gap * CGFloat(images.count + 1))
let height = Int(targetHeight + gap * 2)
let ctx = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                    space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)!
ctx.setFillColor(CGColor(gray: 0.13, alpha: 1))
ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
ctx.interpolationQuality = .high
var x = gap
for (image, w) in zip(images, widths) {
    ctx.draw(image, in: CGRect(x: x, y: gap, width: w, height: targetHeight))
    x += w + gap
}
let data = NSBitmapImageRep(cgImage: ctx.makeImage()!).representation(using: .jpeg, properties: [.compressionFactor: 0.8])!
try! data.write(to: URL(fileURLWithPath: out))
print("Composed \(images.count) screenshots into \(out) (\(width)x\(height), \(data.count) bytes)")
