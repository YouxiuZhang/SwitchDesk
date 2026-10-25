import Foundation

@main
struct ProtocolChecks {
    static func main() throws {
        let valid = ["hello.txt", "中文目录/图片.png", "a/.hidden", "root/zero", "AUXiliary.txt", "x/file with spaces.txt"]
        let invalid = ["", "/etc/passwd", "../file", "root/../file", "./a", "a//b", "a/", "C:/x", "\\server\\share", "x\\y", "nul.txt", "x/COM1.log", "x/NUL", "x/a.", "x/a ", "x/?.txt", "x/a\0b", "x/a\nb"]
        for path in valid { precondition(validRelativePath(path), "valid path rejected: \(path)") }
        for path in invalid { precondition(!validRelativePath(path), "unsafe path accepted") }
        let message: WireMessage = ["type": "clipboard", "text": "中文\n🙂", "revision": 12]
        let framed = try DesktopWire.frame(message, limit: 65536)
        let bytes = [UInt8](framed.prefix(4))
        let length = bytes.reduce(0) { ($0 << 8) | Int($1) }
        precondition(length == framed.count - 4)
        let decoded = try JSONSerialization.jsonObject(with: framed.dropFirst(4)) as! WireMessage
        precondition(wireString(decoded,"text") == "中文\n🙂")
        precondition(wireInt(decoded,"revision") == 12)
        precondition(wireInt(["x":true],"x",-1) == -1)
        do {
            _ = try DesktopWire.frame(["data":String(repeating:"x",count:100)],limit:10)
            fatalError("oversized frame accepted")
        } catch { }
        print("PASS: 24 path cases, UTF-8 frame length/roundtrip, size limit, boolean rejection")
    }
}
