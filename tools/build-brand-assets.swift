import AppKit
import Foundation

// Format conversion only: the approved imagegen master is the single source of artwork.
let root = URL(fileURLWithPath: CommandLine.arguments[1])
let master = root.appendingPathComponent("design/brand/switchdesk-silver.png")
guard let source = NSImage(contentsOf: master), let original = source.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
    fatalError("Missing approved icon master")
}
let fm = FileManager.default
let iconset = root.appendingPathComponent(".build/brand/SwitchDesk.iconset")
let windows = root.appendingPathComponent("native/windows/desktop-assets")
let mac = root.appendingPathComponent("native/macos")
try fm.createDirectory(at: iconset, withIntermediateDirectories: true)

func bitmap(_ size: Int) -> NSBitmapImageRep {
    let color = CGColorSpace(name: CGColorSpace.sRGB)!
    let context = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8,
                            bytesPerRow: size * 4, space: color,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    context.interpolationQuality = .high
    context.draw(original, in: CGRect(x: 0, y: 0, width: size, height: size))
    return NSBitmapImageRep(cgImage: context.makeImage()!)
}
func png(_ size: Int, _ url: URL) throws {
    try bitmap(size).representation(using: .png, properties: [:])!.write(to: url)
}
for size in [16, 32, 128, 256, 512] {
    try png(size, iconset.appendingPathComponent("icon_\(size)x\(size).png"))
    try png(size * 2, iconset.appendingPathComponent("icon_\(size)x\(size)@2x.png"))
}
try png(256, windows.appendingPathComponent("SwitchDesk.png"))
try png(512, mac.appendingPathComponent("SwitchDesk.png"))

extension Data {
    mutating func u16(_ number: UInt16) { var value = number.littleEndian; Swift.withUnsafeBytes(of: &value) { append(contentsOf: $0) } }
    mutating func u32(_ number: UInt32) { var value = number.littleEndian; Swift.withUnsafeBytes(of: &value) { append(contentsOf: $0) } }
}
// Uncompressed BGRA DIB frames work with NSIS, Explorer and .NET Framework Icon.
let sizes = [16, 24, 32, 48, 64, 128, 256]
var frames: [Data] = []
for size in sizes {
    let image = bitmap(size), maskStride = ((size + 31) / 32) * 4
    var frame = Data()
    frame.u32(40); frame.u32(UInt32(size)); frame.u32(UInt32(size * 2))
    frame.u16(1); frame.u16(32); frame.u32(0)
    frame.u32(UInt32(size * size * 4 + maskStride * size))
    for _ in 0..<4 { frame.u32(0) }
    var mask = [UInt8](repeating: 0, count: maskStride * size)
    for row in 0..<size {
        for x in 0..<size {
            let color = image.colorAt(x: x, y: size - 1 - row)!.usingColorSpace(.deviceRGB)!
            func byte(_ value: CGFloat) -> UInt8 { UInt8(max(0, min(255, (value * 255).rounded()))) }
            let alpha = byte(color.alphaComponent)
            frame.append(contentsOf: [byte(color.blueComponent), byte(color.greenComponent), byte(color.redComponent), alpha])
            if alpha == 0 { mask[row * maskStride + x / 8] |= UInt8(0x80 >> (x % 8)) }
        }
    }
    frame.append(contentsOf: mask); frames.append(frame)
}
var ico = Data()
ico.u16(0); ico.u16(1); ico.u16(UInt16(sizes.count))
var offset = 6 + sizes.count * 16
for (index, size) in sizes.enumerated() {
    let edge = size == 256 ? 0 : UInt8(size)
    ico.append(contentsOf: [edge, edge, 0, 0]); ico.u16(1); ico.u16(32)
    ico.u32(UInt32(frames[index].count)); ico.u32(UInt32(offset)); offset += frames[index].count
}
for frame in frames { ico.append(frame) }
try ico.write(to: windows.appendingPathComponent("SwitchDesk.ico"))
let task = Process()
task.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
task.arguments = ["-c", "icns", iconset.path, "-o", mac.appendingPathComponent("SwitchDesk.icns").path]
try task.run(); task.waitUntilExit()
guard task.terminationStatus == 0 else { fatalError("iconutil failed") }
print("Generated PNG, multi-size ICO and ICNS from approved master")
