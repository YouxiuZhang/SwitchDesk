import AppKit
import ApplicationServices
import CoreGraphics
import Darwin
import Foundation

private func standardError(_ message: String) {
    let bytes = Data((message + "\n").utf8)
    FileHandle.standardError.write(bytes)
}

private func usage() -> String {
    "Usage: switchdesk-probe [--output PATH] [--help]\nCollect a read-only macOS display diagnostic as JSON.\n"
}

private func outputPath(arguments: [String]) -> String? {
    if arguments == ["--help"] {
        FileHandle.standardOutput.write(Data(usage().utf8))
        exit(0)
    }
    if arguments.isEmpty { return nil }
    if arguments.count == 2 && arguments[0] == "--output" {
        guard !arguments[1].isEmpty && !arguments[1].hasPrefix("--") else {
            standardError("--output 需要一个文件路径\n" + usage())
            exit(2)
        }
        return arguments[1]
    }
    if arguments == ["--output"] {
        standardError("--output 需要一个文件路径\n" + usage())
    } else {
        standardError("未知参数或参数组合：" + arguments.joined(separator: " ") + "\n" + usage())
    }
    exit(2)
}

private func architecture() -> String {
    #if arch(arm64)
    return "arm64"
    #elseif arch(x86_64)
    return "x86_64"
    #else
    return "unknown"
    #endif
}

private func displaysAndWarnings() -> ([[String: Any]], [String]) {
    var warnings: [String] = []
    var count: UInt32 = 0
    let countError = CGGetOnlineDisplayList(0, nil, &count)
    guard countError == .success else {
        return ([], ["当前进程无法枚举显示器（CoreGraphics 错误 \(countError.rawValue)）。"])
    }
    guard count > 0 else {
        return ([], ["当前进程未枚举到显示器；这不表示没有连接显示器。"])
    }

    var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
    var returned: UInt32 = 0
    let listError = CGGetOnlineDisplayList(count, &ids, &returned)
    guard listError == .success else {
        return ([], ["当前进程无法枚举显示器（CoreGraphics 错误 \(listError.rawValue)）。"])
    }
    var screenNames: [CGDirectDisplayID: String] = [:]
    for screen in NSScreen.screens {
        guard let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else {
            continue
        }
        screenNames[number.uint32Value] = screen.localizedName
    }
    var displays: [[String: Any]] = []
    for id in ids.prefix(Int(returned)) {
        let mode = CGDisplayCopyDisplayMode(id)
        let width = mode?.pixelWidth ?? CGDisplayPixelsWide(id)
        let height = mode?.pixelHeight ?? CGDisplayPixelsHigh(id)
        guard width > 0 && height > 0 else {
            warnings.append("当前进程无法读取显示器 \(id) 的正数像素尺寸，已跳过该项。")
            continue
        }
        let rate = mode?.refreshRate ?? 0
        let refresh: Any = rate.isFinite && rate > 0 ? rate : NSNull()
        let builtin = CGDisplayIsBuiltin(id) != 0
        let name = screenNames[id].flatMap { $0.isEmpty ? nil : $0 } ?? "显示器 \(id)"
        displays.append([
            "id": String(id),
            "name": name,
            "builtin": builtin,
            "width": width,
            "height": height,
            "refresh_hz": refresh,
            "ddc_input": [
                "status": builtin ? "unsupported" : "not_tested",
                "value": NSNull(),
                "detail": builtin ? "内置显示器不适用 DDC 输入切换。" : "尚未读取 DDC 输入码。",
            ] as [String: Any],
        ])
    }
    if displays.isEmpty {
        warnings.append("当前进程未枚举到可报告的显示器；这不表示没有连接显示器。")
    }
    return (displays, warnings)
}

private func makeReport() -> [String: Any] {
    let (displays, warnings) = displaysAndWarnings()
    let formatter = ISO8601DateFormatter()
    formatter.timeZone = TimeZone(secondsFromGMT: 0)!
    formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
    return [
        "schema_version": 1,
        "platform": "macos",
        "os_version": ProcessInfo.processInfo.operatingSystemVersionString,
        "architecture": architecture(),
        "collected_at": formatter.string(from: Date()),
        "read_only": true,
        "displays": displays,
        "permissions": [
            "screen_capture": CGPreflightScreenCaptureAccess() ? "granted" : "denied",
            "input_capture": CGPreflightListenEventAccess() ? "granted" : "denied",
            "input_injection": CGPreflightPostEventAccess() ? "granted" : "denied",
        ],
        "capabilities": [
            "automatic_switch": "not_tested",
            "native_file_paste": "not_tested",
            "inactive_display_capture": "not_tested",
        ],
        "warnings": warnings,
    ]
}

private func writeExclusively(_ data: Data, to path: String) throws {
    var template = Array((path + ".tmp.XXXXXX").utf8CString)
    let descriptor = template.withUnsafeMutableBufferPointer { buffer in
        Darwin.mkstemp(buffer.baseAddress!)
    }
    guard descriptor >= 0 else {
        throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno), userInfo: nil)
    }
    let temporaryPath = template.withUnsafeBufferPointer { buffer in
        String(cString: buffer.baseAddress!)
    }
    var descriptorIsOpen = true
    var published = false
    defer {
        if descriptorIsOpen { _ = Darwin.close(descriptor) }
        if !published { temporaryPath.withCString { _ = Darwin.unlink($0) } }
    }
    try data.withUnsafeBytes { rawBuffer in
        guard let base = rawBuffer.baseAddress else { return }
        var offset = 0
        while offset < rawBuffer.count {
            let written = Darwin.write(descriptor, base.advanced(by: offset), rawBuffer.count - offset)
            if written < 0 && errno == EINTR { continue }
            guard written > 0 else {
                throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno), userInfo: nil)
            }
            offset += written
        }
    }
    let closeResult = Darwin.close(descriptor)
    descriptorIsOpen = false
    guard closeResult == 0 else {
        throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno), userInfo: nil)
    }
    let publishResult = temporaryPath.withCString { source in
        path.withCString { destination in
            Darwin.renamex_np(source, destination, UInt32(RENAME_EXCL))
        }
    }
    guard publishResult == 0 else {
        throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno), userInfo: nil)
    }
    published = true
}

let path = outputPath(arguments: Array(CommandLine.arguments.dropFirst()))
do {
    let report = makeReport()
    var data = try JSONSerialization.data(withJSONObject: report, options: [.sortedKeys])
    data.append(0x0A)
    if let path {
        try writeExclusively(data, to: path)
    } else {
        FileHandle.standardOutput.write(data)
    }
} catch {
    standardError("无法生成或写入诊断报告：\(error.localizedDescription)")
    exit(1)
}
