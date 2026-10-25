import CryptoKit
import Darwin
import Foundation

enum TransferFailure: LocalizedError {
    case invalid(String)
    case cancelled
    case timeout
    var errorDescription: String? {
        switch self { case .invalid(let text): return text; case .cancelled: return "传输已取消"; case .timeout: return "文件传输超时" }
    }
}

struct FileEntry: Equatable {
    let path: String
    let directory: Bool
    let size: Int64
    var wire: WireMessage { ["path": path, "dir": directory, "size": size] }
}

/// All disk and hashing work lives on `queue`; receiveMedia never waits for the UI.
final class FileTransfer {
    var send: ((WireMessage) -> Void)?
    var status: ((String) -> Void)?
    var progress: ((String, Double) -> Void)?

    private struct SourceFile {
        let url: URL
        let size: Int64
        let device: dev_t
        let inode: ino_t
        let modified: timespec
    }
    private struct Offer {
        let id: String
        let entries: [FileEntry]
        let roots: [String]
        let files: [String: SourceFile]
    }
    private final class Download {
        let id: String
        let offerID: String
        let entries: [FileEntry]
        let roots: [String]
        let staging: URL
        let final: URL
        let completion: (Result<[URL], Error>) -> Void
        var begun = false
        var current: FileHandle?
        var currentPath = ""
        var currentSize: Int64 = 0
        var hash = SHA256()
        var complete = Set<String>()
        var received: Int64 = 0
        var lastActivity = Date()
        init(id: String, offerID: String, entries: [FileEntry], roots: [String], staging: URL, final: URL, completion: @escaping (Result<[URL], Error>) -> Void) {
            self.id = id; self.offerID = offerID; self.entries = entries; self.roots = roots; self.staging = staging; self.final = final; self.completion = completion
        }
    }

    private let queue = DispatchQueue(label: "SwitchDesk.file.transfer", qos: .utility)
    private let outgoingQueue = DispatchQueue(label: "SwitchDesk.file.outgoing", qos: .utility)
    private let sendGate = NSLock()
    private let fm = FileManager.default
    private var offers: [String: Offer] = [:]
    private var offerOrder: [String] = []
    private var downloads: [String: Download] = [:]
    private var activeSends: [String: Int] = [:]
    private var cancelled = Set<String>()
    private var sendGeneration = 0
    private var enabled = false
    private var connected = false
    private let cacheRoot: URL

    init(cacheDirectory: URL? = nil) {
        let base = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        cacheRoot = cacheDirectory ?? base.appendingPathComponent("SwitchDesk/Transfers", isDirectory: true)
        try? fm.createDirectory(at: cacheRoot, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        // A new process has no pasteboard provider for previous sessions' cache URLs.
        if let old = try? fm.contentsOfDirectory(at: cacheRoot, includingPropertiesForKeys: nil) {
            for url in old {
                let name = url.lastPathComponent.replacingOccurrences(of: ".partial", with: "")
                if UUID(uuidString: name) != nil { try? fm.removeItem(at: url) }
            }
        }
    }

    func setAvailable(connected: Bool, enabled: Bool) {
        queue.async {
            self.connected = connected; self.enabled = enabled
            if !connected || !enabled { self.cancelAllInternal() }
        }
    }

    func makeOffer(urls: [URL], completion: @escaping (Result<WireMessage, Error>) -> Void) {
        queue.async {
            do {
                guard self.connected && self.enabled else { throw TransferFailure.cancelled }
                let offer = try self.buildOffer(urls: urls)
                self.offers[offer.id] = offer
                self.offerOrder.append(offer.id)
                while self.offerOrder.count > 8 {
                    guard let index = self.offerOrder.firstIndex(where: { self.activeSends[$0, default: 0] == 0 }) else { break }
                    let oldest = self.offerOrder.remove(at: index)
                    self.offers.removeValue(forKey: oldest)
                }
                completion(.success(["type": "clipboard", "id": offer.id, "origin": "mac", "kind": "files", "entries": offer.entries.map(\.wire), "roots": offer.roots]))
            } catch { completion(.failure(error)) }
        }
    }

    func fetchRemote(id: String, entries: [FileEntry], roots: [String], completion: @escaping (Result<[URL], Error>) -> Void) {
        queue.async {
            do {
                guard self.connected && self.enabled else { throw TransferFailure.cancelled }
                try Self.validate(entries: entries, roots: roots)
                let transfer = UUID().uuidString
                let staging = self.cacheRoot.appendingPathComponent("\(transfer).partial", isDirectory: true)
                let final = self.cacheRoot.appendingPathComponent(transfer, isDirectory: true)
                let total = entries.reduce(Int64(0)) { $0 + $1.size }
                let free = (try self.fm.attributesOfFileSystem(forPath: self.cacheRoot.path)[.systemFreeSize] as? NSNumber)?.int64Value ?? 0
                guard free > total + 64 * 1024 * 1024 else { throw TransferFailure.invalid("缓存空间不足") }
                try self.fm.createDirectory(at: staging, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
                let download = Download(id: transfer, offerID: id, entries: entries, roots: roots, staging: staging, final: final, completion: completion)
                self.downloads[transfer] = download
                self.send?(["type": "file_request", "id": id, "transfer": transfer])
                self.scheduleTimeout(transfer)
            } catch { completion(.failure(error)) }
        }
    }

    func receive(_ message: WireMessage) {
        // Session calls from its media worker, never main. Completing each chunk here
        // preserves NW receive backpressure and bounds decoded data in memory.
        queue.sync { self.handle(message) }
    }

    func cancelAll() { queue.async { self.cancelAllInternal() } }

    private func handle(_ message: WireMessage) {
        let type = wireString(message, "type")
        let transfer = wireString(message, "transfer")
        if type == "file_request" {
            let id = wireString(message, "id")
            guard connected && enabled, let offer = offers[id], UUID(uuidString: transfer) != nil,
                  activeSends.values.reduce(0, +) < 4 else {
                send?(["type": "file_error", "transfer": transfer, "detail": "文件清单已失效"])
                return
            }
            activeSends[id, default: 0] += 1
            sendGate.lock(); let generation = sendGeneration; sendGate.unlock()
            outgoingQueue.async {
                self.sendOffer(offer, transfer: transfer, generation: generation)
                self.queue.async {
                    self.activeSends[id, default: 1] -= 1
                    if self.activeSends[id] == 0 { self.activeSends.removeValue(forKey: id) }
                    if !self.offerOrder.contains(id) { self.offers.removeValue(forKey: id) }
                }
            }
            return
        }
        if type == "file_cancel" {
            sendGate.lock(); cancelled.insert(transfer); sendGate.unlock()
            if let d = downloads[transfer] { fail(d, TransferFailure.cancelled, notifyPeer: false) }
            return
        }
        guard let d = downloads[transfer] else { return }
        d.lastActivity = Date()
        do {
            switch type {
            case "file_begin":
                guard !d.begun, wireString(message, "id") == d.offerID,
                      let raw = message["entries"] as? [WireMessage], let roots = message["roots"] as? [String] else { throw TransferFailure.invalid("文件清单不匹配") }
                let parsed = try Self.parse(entries: raw, roots: roots)
                guard parsed == d.entries, roots == d.roots else { throw TransferFailure.invalid("文件清单不匹配") }
                for entry in d.entries where entry.directory {
                    try fm.createDirectory(at: safeURL(entry.path, below: d.staging), withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
                }
                d.begun = true
            case "file_chunk":
                guard d.begun else { throw TransferFailure.invalid("未开始传输") }
                let path = wireString(message, "path")
                guard let entry = d.entries.first(where: { $0.path == path && !$0.directory }), !d.complete.contains(path),
                      d.entries.first(where: { !$0.directory && !d.complete.contains($0.path) })?.path == path,
                      let data64 = message["data"] as? String, let data = Data(base64Encoded: data64), data.count <= 32768,
                      wireInt(message, "offset", -1) == d.currentSize || d.currentPath != path && wireInt(message, "offset", -1) == 0 else { throw TransferFailure.invalid("文件块无效") }
                if d.currentPath != path {
                    guard d.current == nil else { throw TransferFailure.invalid("文件顺序错误") }
                    let url = safeURL(path, below: d.staging)
                    try fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
                    guard fm.createFile(atPath: url.path, contents: nil, attributes: [.posixPermissions: 0o600]) else { throw TransferFailure.invalid("无法创建缓存文件") }
                    d.current = try FileHandle(forWritingTo: url)
                    d.currentPath = path; d.currentSize = 0; d.hash = SHA256()
                }
                guard d.currentSize + Int64(data.count) <= entry.size else { throw TransferFailure.invalid("文件长度超限") }
                try d.current?.write(contentsOf: data)
                d.hash.update(data: data); d.currentSize += Int64(data.count); d.received += Int64(data.count)
                let total = d.entries.reduce(Int64(0)) { $0 + $1.size }
                progress?("接收文件", total == 0 ? 1 : Double(d.received) / Double(total))
            case "file_end":
                guard d.begun else { throw TransferFailure.invalid("未开始传输") }
                let path = wireString(message, "path")
                guard let entry = d.entries.first(where: { $0.path == path && !$0.directory }), !d.complete.contains(path),
                      d.entries.first(where: { !$0.directory && !d.complete.contains($0.path) })?.path == path else { throw TransferFailure.invalid("文件结尾无效") }
                if d.current == nil && entry.size == 0 {
                    let url = safeURL(path, below: d.staging)
                    try fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
                    guard fm.createFile(atPath: url.path, contents: nil, attributes: [.posixPermissions: 0o600]) else { throw TransferFailure.invalid("无法创建空文件") }
                    d.currentPath = path; d.currentSize = 0; d.hash = SHA256()
                }
                guard d.currentPath == path, d.currentSize == entry.size else { throw TransferFailure.invalid("文件大小不符") }
                try d.current?.close(); d.current = nil
                let digest = d.hash.finalize().map { String(format: "%02x", $0) }.joined()
                guard digest == wireString(message, "sha256") else { throw TransferFailure.invalid("文件校验失败") }
                d.complete.insert(path); d.currentPath = ""; d.currentSize = 0
            case "file_done":
                guard d.begun, d.current == nil, d.complete.count == d.entries.filter({ !$0.directory }).count else { throw TransferFailure.invalid("文件传输不完整") }
                try fm.moveItem(at: d.staging, to: d.final)
                downloads.removeValue(forKey: transfer)
                d.completion(.success(d.roots.map { safeURL($0, below: d.final) }))
                progress?("文件已准备好", 1)
            case "file_error": throw TransferFailure.invalid(wireString(message, "detail", "对方文件传输失败"))
            default: break
            }
        } catch { fail(d, error, notifyPeer: type != "file_error") }
    }

    private func sendOffer(_ offer: Offer, transfer: String, generation: Int) {
        do {
            guard maySend(transfer, generation: generation) else { throw TransferFailure.cancelled }
            send?(["type": "file_begin", "id": offer.id, "transfer": transfer, "entries": offer.entries.map(\.wire), "roots": offer.roots])
            let total = offer.entries.reduce(Int64(0)) { $0 + $1.size }
            var sent: Int64 = 0
            for entry in offer.entries where !entry.directory {
                guard maySend(transfer, generation: generation), let file = offer.files[entry.path], try sameIdentity(file) else { throw TransferFailure.invalid("源文件已更改") }
                let fd = open(file.url.path, O_RDONLY | O_NOFOLLOW)
                guard fd >= 0 else { throw TransferFailure.invalid("无法安全打开源文件") }
                var opened = stat()
                guard fstat(fd, &opened) == 0, (opened.st_mode & S_IFMT) == S_IFREG,
                      opened.st_dev == file.device, opened.st_ino == file.inode,
                      opened.st_size == file.size, opened.st_mtimespec.tv_sec == file.modified.tv_sec,
                      opened.st_mtimespec.tv_nsec == file.modified.tv_nsec else {
                    close(fd); throw TransferFailure.invalid("源文件身份已变更")
                }
                let handle = FileHandle(fileDescriptor: fd, closeOnDealloc: true)
                var hash = SHA256(); var offset: Int64 = 0
                defer { try? handle.close() }
                while let data = try handle.read(upToCount: 32768), !data.isEmpty {
                    guard maySend(transfer, generation: generation) else { throw TransferFailure.cancelled }
                    send?(["type": "file_chunk", "transfer": transfer, "path": entry.path, "offset": offset, "data": data.base64EncodedString()])
                    hash.update(data: data); offset += Int64(data.count); sent += Int64(data.count)
                    progress?("发送文件", total == 0 ? 1 : Double(sent) / Double(total))
                }
                guard offset == entry.size, try sameIdentity(file) else { throw TransferFailure.invalid("源文件传输中已更改") }
                send?(["type": "file_end", "transfer": transfer, "path": entry.path, "sha256": hash.finalize().map { String(format: "%02x", $0) }.joined()])
            }
            guard maySend(transfer, generation: generation) else { throw TransferFailure.cancelled }
            send?(["type": "file_done", "transfer": transfer])
        } catch {
            send?(["type": "file_error", "transfer": transfer, "detail": error.localizedDescription])
            status?(error.localizedDescription)
        }
        sendGate.lock(); cancelled.remove(transfer); sendGate.unlock()
    }

    private func maySend(_ transfer: String, generation: Int) -> Bool {
        sendGate.lock(); defer { sendGate.unlock() }
        return sendGeneration == generation && !cancelled.contains(transfer)
    }

    private func scheduleTimeout(_ transfer: String) {
        queue.asyncAfter(deadline: .now() + 10) { [weak self] in
            guard let self, let pending = self.downloads[transfer] else { return }
            if Date().timeIntervalSince(pending.lastActivity) > 60 { self.fail(pending, TransferFailure.timeout, notifyPeer: true) }
            else { self.scheduleTimeout(transfer) }
        }
    }

    private func buildOffer(urls: [URL]) throws -> Offer {
        guard !urls.isEmpty else { throw TransferFailure.invalid("没有可复制文件") }
        let id = UUID().uuidString
        var entries: [FileEntry] = [], roots: [String] = [], files: [String: SourceFile] = [:]
        var folded = Set<String>(), total: Int64 = 0
        func visit(_ url: URL, _ path: String) throws {
            guard validRelativePath(path), folded.insert(path.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: nil)).inserted else { throw TransferFailure.invalid("文件名无效或大小写重复") }
            var st = stat()
            guard lstat(url.path, &st) == 0 else { throw TransferFailure.invalid("无法读取文件") }
            guard (st.st_mode & S_IFMT) != S_IFLNK else { throw TransferFailure.invalid("不允许符号链接") }
            if (st.st_mode & S_IFMT) == S_IFDIR {
                entries.append(FileEntry(path: path, directory: true, size: 0))
                for child in try fm.contentsOfDirectory(at: url, includingPropertiesForKeys: nil).sorted(by: { $0.lastPathComponent < $1.lastPathComponent }) {
                    try visit(child, path + "/" + child.lastPathComponent)
                }
            } else if (st.st_mode & S_IFMT) == S_IFREG {
                let size = Int64(st.st_size)
                guard size >= 0, total <= 16 * 1024 * 1024 * 1024 - size else { throw TransferFailure.invalid("文件总量超过16GiB") }
                total += size
                entries.append(FileEntry(path: path, directory: false, size: size))
                files[path] = SourceFile(url: url, size: size, device: st.st_dev, inode: st.st_ino, modified: st.st_mtimespec)
            } else { throw TransferFailure.invalid("只支持普通文件和目录") }
            guard entries.count <= 4096 else { throw TransferFailure.invalid("文件数量超过4096") }
        }
        for url in urls {
            let name = url.lastPathComponent
            guard !roots.contains(where: { $0.caseInsensitiveCompare(name) == .orderedSame }) else { throw TransferFailure.invalid("顶层文件名重复") }
            try rejectSymlinkAncestors(url)
            roots.append(name); try visit(url, name)
        }
        try Self.validate(entries: entries, roots: roots)
        return Offer(id: id, entries: entries, roots: roots, files: files)
    }

    private func sameIdentity(_ file: SourceFile) throws -> Bool {
        var st = stat()
        guard lstat(file.url.path, &st) == 0 else { return false }
        return (st.st_mode & S_IFMT) == S_IFREG && st.st_dev == file.device && st.st_ino == file.inode && st.st_size == file.size && st.st_mtimespec.tv_sec == file.modified.tv_sec && st.st_mtimespec.tv_nsec == file.modified.tv_nsec
    }

    private func rejectSymlinkAncestors(_ url: URL) throws {
        var path = ""
        for component in url.standardizedFileURL.pathComponents where component != "/" {
            path += "/" + component
            var st = stat()
            guard lstat(path, &st) == 0, (st.st_mode & S_IFMT) != S_IFLNK else { throw TransferFailure.invalid("路径包含符号链接") }
        }
    }

    static func parse(entries raw: [WireMessage], roots: [String]) throws -> [FileEntry] {
        let entries = try raw.map { item -> FileEntry in
            guard let path = item["path"] as? String, let dir = item["dir"] as? Bool,
                  let number = item["size"] as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() else { throw TransferFailure.invalid("文件清单格式错误") }
            return FileEntry(path: path, directory: dir, size: number.int64Value)
        }
        try validate(entries: entries, roots: roots)
        return entries
    }

    static func validate(entries: [FileEntry], roots: [String]) throws {
        guard !entries.isEmpty, entries.count <= 4096, !roots.isEmpty else { throw TransferFailure.invalid("文件清单为空或过大") }
        var paths = Set<String>(), folded = Set<String>(), total: Int64 = 0
        for e in entries {
            guard validRelativePath(e.path), e.size >= 0, (!e.directory || e.size == 0),
                  paths.insert(e.path).inserted, folded.insert(e.path.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: nil)).inserted else { throw TransferFailure.invalid("文件路径无效或重复") }
            guard total <= 16 * 1024 * 1024 * 1024 - e.size else { throw TransferFailure.invalid("文件总量超过16GiB") }
            total += e.size
        }
        var rootSet = Set<String>()
        for root in roots {
            guard validRelativePath(root), !root.contains("/"), rootSet.insert(root.lowercased()).inserted,
                  paths.contains(root) else { throw TransferFailure.invalid("根文件名无效") }
        }
        let directoryPaths = Set(entries.filter(\.directory).map(\.path))
        for e in entries {
            let parts = e.path.split(separator: "/")
            guard rootSet.contains(String(parts[0]).lowercased()) else { throw TransferFailure.invalid("路径不在根目录") }
            if parts.count > 1 {
                for count in 1..<parts.count {
                    guard directoryPaths.contains(parts.prefix(count).joined(separator: "/")) else { throw TransferFailure.invalid("缺少上级目录") }
                }
            }
        }
    }

    private func safeURL(_ relative: String, below root: URL) -> URL { root.appendingPathComponent(relative, isDirectory: false) }
    private func fail(_ d: Download, _ error: Error, notifyPeer: Bool) {
        try? d.current?.close(); d.current = nil
        downloads.removeValue(forKey: d.id)
        try? fm.removeItem(at: d.staging)
        if notifyPeer { send?(["type": "file_cancel", "transfer": d.id]) }
        d.completion(.failure(error)); status?(error.localizedDescription)
    }
    private func cancelAllInternal() {
        sendGate.lock(); sendGeneration += 1; sendGate.unlock()
        for d in Array(downloads.values) { fail(d, TransferFailure.cancelled, notifyPeer: true) }
        offers.removeAll(); offerOrder.removeAll()
    }
}
