// Writes a short H.264 MP4 whose picture changes every frame (a hue that cycles and a bar that sweeps across), so a
// playback test can tell a playing video from a still frame by comparing two screenshots.
// Usage: swift make-sample-video.swift <out.mp4> [seconds=6] [width=1280] [height=720]
import AVFoundation
import CoreVideo
import Foundation

let args = CommandLine.arguments
guard args.count >= 2 else {
    FileHandle.standardError.write("usage: make-sample-video <out.mp4> [seconds] [width] [height]\n".data(using: .utf8)!)
    exit(2)
}
let out = URL(fileURLWithPath: args[1])
let seconds = args.count > 2 ? Int(args[2]) ?? 6 : 6
let width = args.count > 3 ? Int(args[3]) ?? 1280 : 1280
let height = args.count > 4 ? Int(args[4]) ?? 720 : 720
let fps: Int32 = 30
try? FileManager.default.removeItem(at: out)

let writer = try AVAssetWriter(outputURL: out, fileType: .mp4)
let input = AVAssetWriterInput(mediaType: .video, outputSettings: [
    AVVideoCodecKey: AVVideoCodecType.h264, AVVideoWidthKey: width, AVVideoHeightKey: height,
])
input.expectsMediaDataInRealTime = false
let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: [
    kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
    kCVPixelBufferWidthKey as String: width, kCVPixelBufferHeightKey as String: height,
])
writer.add(input)
guard writer.startWriting() else { fatalError("cannot write: \(String(describing: writer.error))") }
writer.startSession(atSourceTime: .zero)

func hsv(_ h: Double) -> (UInt8, UInt8, UInt8) { // full saturation and value
    let i = Int(h * 6) % 6, f = h * 6 - Double(Int(h * 6))
    let q = UInt8((1 - f) * 255), t = UInt8(f * 255)
    switch i { case 0: return (255, t, 0); case 1: return (q, 255, 0); case 2: return (0, 255, t)
    case 3: return (0, q, 255); case 4: return (t, 0, 255); default: return (255, 0, q) }
}

let frames = seconds * Int(fps)
for n in 0..<frames {
    while !input.isReadyForMoreMediaData { usleep(1000) }
    var buffer: CVPixelBuffer?
    CVPixelBufferPoolCreatePixelBuffer(nil, adaptor.pixelBufferPool!, &buffer)
    guard let pb = buffer else { fatalError("no pixel buffer") }
    CVPixelBufferLockBaseAddress(pb, [])
    let base = CVPixelBufferGetBaseAddress(pb)!.assumingMemoryBound(to: UInt8.self)
    let stride = CVPixelBufferGetBytesPerRow(pb)
    let (r, g, b) = hsv(Double(n % 90) / 90)
    let barX = (n * 23) % width, barW = width / 8
    for y in 0..<height {
        let row = base + y * stride
        for x in 0..<width {
            let inBar = x >= barX && x < barX + barW
            row[x * 4 + 0] = inBar ? 255 : b
            row[x * 4 + 1] = inBar ? 255 : g
            row[x * 4 + 2] = inBar ? 255 : r
            row[x * 4 + 3] = 255
        }
    }
    CVPixelBufferUnlockBaseAddress(pb, [])
    adaptor.append(pb, withPresentationTime: CMTime(value: CMTimeValue(n), timescale: fps))
}
input.markAsFinished()
let done = DispatchSemaphore(value: 0)
writer.finishWriting { done.signal() }
done.wait()
guard writer.status == .completed else { fatalError("writing failed: \(String(describing: writer.error))") }
print("Wrote \(out.path): \(seconds) s, \(width)x\(height), \(fps) fps, H.264")
