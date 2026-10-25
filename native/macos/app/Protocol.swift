import Foundation
import CoreFoundation

typealias WireMessage = [String: Any]

func wireString(_ message: WireMessage, _ key: String, _ fallback: String = "") -> String {
    message[key] as? String ?? fallback
}
func wireInt(_ message: WireMessage, _ key: String, _ fallback: Int = 0) -> Int {
    guard let n = message[key] as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID() else { return fallback }
    return n.intValue
}
func wireBool(_ message: WireMessage, _ key: String, _ fallback: Bool = false) -> Bool {
    message[key] as? Bool ?? fallback
}

func validRelativePath(_ path: String) -> Bool {
    guard !path.isEmpty, path.utf8.count <= 4096, !path.contains("\\"), !path.contains(":"),
          !path.hasPrefix("/"), !path.contains("\0") else { return false }
    let reserved = Set(["CON", "PRN", "AUX", "NUL"] + (1...9).map { "COM\($0)" } + (1...9).map { "LPT\($0)" })
    for part in path.components(separatedBy: "/") {
        if part.isEmpty || part == "." || part == ".." || part.utf8.count > 255 || part.hasSuffix(".") || part.hasSuffix(" ") { return false }
        if part.unicodeScalars.contains(where: { $0.value < 32 || "<>\"|?*".unicodeScalars.contains($0) }) { return false }
        if reserved.contains(part.components(separatedBy: ".")[0].uppercased()) { return false }
    }
    return true
}

enum DesktopWire {
    static let version = 1
    static let controlLimit = 65_536
    static let mediaLimit = 12 * 1024 * 1024
    static func frame(_ message: WireMessage, limit: Int) throws -> Data {
        let body = try JSONSerialization.data(withJSONObject: message, options: [.sortedKeys])
        guard !body.isEmpty, body.count <= limit else { throw NSError(domain: "SwitchDesk", code: 1, userInfo: [NSLocalizedDescriptionKey: "消息超过允许大小"]) }
        var length = UInt32(body.count).bigEndian
        var output = withUnsafeBytes(of: &length) { Data($0) }
        output.append(body)
        return output
    }
}
