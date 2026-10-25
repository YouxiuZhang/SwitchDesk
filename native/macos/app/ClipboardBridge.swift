import AppKit
import Foundation

/// The pasteboard is touched only by this serial queue, except for AppKit's synchronous
/// data-provider callback. That callback waits on a separate FileTransfer queue.
final class ClipboardBridge {
    var send: ((WireMessage) -> Void)?
    var status: ((String) -> Void)?
    private let queue = DispatchQueue(label: "SwitchDesk.clipboard", qos: .utility)
    private let pasteboard = NSPasteboard.general
    private let transfer: FileTransfer
    private var connected = false
    private var enabled = false
    private var timer: DispatchSourceTimer?
    private var observedChange = 0
    private var ownChange = -1
    private var pending: [String: Int] = [:]
    private var deferred: [WireMessage] = []
    private var revision = 0
    private var provider: RemoteFileProvider?
    private var activeProviders: [RemoteFileProvider] = []

    init(transfer: FileTransfer) { self.transfer = transfer }

    func start() {
        queue.async {
            guard self.timer == nil else { return }
            let timer = DispatchSource.makeTimerSource(queue: self.queue)
            timer.schedule(deadline: .now() + .milliseconds(400), repeating: .milliseconds(400))
            timer.setEventHandler { [weak self] in self?.poll() }
            self.timer = timer; timer.resume()
        }
    }

    func setAvailable(connected: Bool, enabled: Bool) {
        queue.async {
            self.connected = connected; self.enabled = enabled
            self.transfer.setAvailable(connected: connected, enabled: enabled)
            if !connected || !enabled {
                self.pending.removeAll(); self.deferred.removeAll()
                if !connected { self.revision = 0 }
                let hadProvider = self.provider != nil
                self.provider?.cancel(); self.provider = nil
                self.activeProviders.forEach { $0.cancel() }; self.activeProviders.removeAll()
                if hadProvider && self.pasteboard.changeCount == self.ownChange {
                    self.pasteboard.clearContents()
                    self.ownChange = self.pasteboard.changeCount
                    self.observedChange = self.ownChange
                }
            } else { self.observedChange = self.pasteboard.changeCount }
        }
    }

    func receive(_ message: WireMessage) {
        queue.async { self.apply(message) }
    }

    func stop() {
        queue.async {
            self.timer?.cancel(); self.timer = nil
            self.connected = false; self.enabled = false
            self.pending.removeAll(); self.deferred.removeAll(); self.revision = 0
            self.provider?.cancel(); self.provider = nil
            self.activeProviders.forEach { $0.cancel() }; self.activeProviders.removeAll()
            self.transfer.cancelAll()
        }
    }

    private func poll() {
        guard connected && enabled else { return }
        let change = pasteboard.changeCount
        guard change != observedChange else { return }
        observedChange = change
        if change == ownChange { return }
        retireCurrentProvider()
        let items = pasteboard.pasteboardItems ?? []
        let fileURLs = items.compactMap { item -> URL? in
            guard let text = item.string(forType: .fileURL), let url = URL(string: text), url.isFileURL else { return nil }
            return url
        }
        if !fileURLs.isEmpty {
            transfer.makeOffer(urls: fileURLs) { [weak self] result in
                guard let self else { return }
                self.queue.async {
                    guard self.connected && self.enabled, self.pasteboard.changeCount == change else { return }
                    switch result {
                    case .success(let message): self.sendLocal(message, change: change)
                    case .failure(let error): self.status?(error.localizedDescription)
                    }
                }
            }
            return
        }
        if let text = pasteboard.string(forType: .string) {
            guard !text.hasPrefix("switchdesk1:") else { return }
            guard text.utf8.count <= 1024 * 1024 else { status?("剪贴板文字超过1MiB"); return }
            sendLocal(["type": "clipboard", "id": UUID().uuidString, "origin": "mac", "kind": "text", "text": text], change: change)
            return
        }
        let png: Data?
        if let native = pasteboard.data(forType: .png) { png = native }
        else if let tiff = pasteboard.data(forType: .tiff), let image = NSBitmapImageRep(data: tiff) { png = image.representation(using: .png, properties: [:]) }
        else { png = nil }
        if let png {
            guard png.count <= 8 * 1024 * 1024 else { status?("剪贴板图片超过8MiB"); return }
            sendLocal(["type": "clipboard", "id": UUID().uuidString, "origin": "mac", "kind": "image", "data": png.base64EncodedString()], change: change)
        }
    }

    private func sendLocal(_ message: WireMessage, change: Int) {
        guard let id = message["id"] as? String else { return }
        pending[id] = change
        send?(message)
    }

    private func apply(_ message: WireMessage) {
        guard connected && enabled, wireString(message, "type") == "clipboard" else { return }
        let rev = wireInt(message, "revision", -1)
        guard rev > 0 else { status?("剪贴板版本无效"); return }
        let id = wireString(message, "id")
        if wireString(message, "origin") == "mac" {
            guard pending.removeValue(forKey: id) != nil else { return }
            revision = max(revision, rev)
            if pending.isEmpty { drainDeferred() }
            return
        }
        guard rev > revision else { return }
        if !pending.isEmpty {
            if deferred.first.map({ wireInt($0, "revision") < rev }) ?? true { deferred = [message] }
            return
        }
        // A local copy may have happened since the last timer tick. Observe and send it first.
        if pasteboard.changeCount != observedChange {
            poll()
            // A newer local clipboard always wins this race, including unsupported types.
            return
        }
        switch wireString(message, "kind") {
        case "text":
            guard let value = message["text"] as? String, value.utf8.count <= 1024 * 1024 else { status?("收到的文字超限"); return }
            write([value as NSString]);
        case "image":
            guard let encoded = message["data"] as? String, encoded.count <= 11 * 1024 * 1024,
                  let data = Data(base64Encoded: encoded), data.count <= 8 * 1024 * 1024,
                  data.starts(with: [137, 80, 78, 71, 13, 10, 26, 10]) else { status?("收到的PNG无效"); return }
            let item = NSPasteboardItem(); item.setData(data, forType: .png)
            write([item])
        case "files":
            guard let raw = message["entries"] as? [WireMessage], let roots = message["roots"] as? [String] else { status?("文件清单格式错误"); return }
            do {
                let entries = try FileTransfer.parse(entries: raw, roots: roots)
                retireCurrentProvider()
                let remote = RemoteFileProvider(offerID: id, entries: entries, roots: roots, transfer: transfer, changeCheck: { [weak self] in
                    guard let self else { return false }
                    return self.connected && self.enabled && self.pasteboard.changeCount == self.ownChange
                }, status: { [weak self] text in self?.status?(text) }, onSettled: { [weak self] settled in
                    self?.queue.async { self?.activeProviders.removeAll(where: { $0 === settled }) }
                })
                provider = remote
                let items = roots.map { _ -> NSPasteboardItem in
                    let item = NSPasteboardItem()
                    item.setDataProvider(remote, forTypes: [.fileURL])
                    return item
                }
                write(items)
                remote.bind(items: items)
            } catch { status?(error.localizedDescription) }
        default: status?("未知剪贴板格式"); return
        }
        revision = rev
    }

    private func drainDeferred() {
        let messages = deferred; deferred.removeAll()
        for message in messages { apply(message) }
    }

    private func retireCurrentProvider() {
        guard let old = provider else { return }
        provider = nil
        if old.retireIfStarted() { activeProviders.append(old) }
    }

    private func write(_ objects: [NSPasteboardWriting]) {
        pasteboard.clearContents()
        guard pasteboard.writeObjects(objects) else { status?("写入剪贴板失败"); return }
        ownChange = pasteboard.changeCount
        observedChange = ownChange
    }
}

/// One remote offer exposes one fileURL item per top-level copied file or directory.
/// Finder requests a URL only when paste is attempted; the URL appears after all entries
/// have been downloaded and checked. The callback is synchronous by AppKit contract.
private final class RemoteFileProvider: NSObject, NSPasteboardItemDataProvider {
    private let offerID: String
    private let entries: [FileEntry]
    private let roots: [String]
    private let transfer: FileTransfer
    private let changeCheck: () -> Bool
    private let status: (String) -> Void
    private let onSettled: (RemoteFileProvider) -> Void
    private let lock = NSLock()
    private var items: [NSPasteboardItem] = []
    private var result: Result<[URL], Error>?
    private var waiters: [DispatchSemaphore] = []
    private var requested = false
    private var cancelled = false

    init(offerID: String, entries: [FileEntry], roots: [String], transfer: FileTransfer, changeCheck: @escaping () -> Bool, status: @escaping (String) -> Void, onSettled: @escaping (RemoteFileProvider) -> Void) {
        self.offerID = offerID; self.entries = entries; self.roots = roots; self.transfer = transfer; self.changeCheck = changeCheck; self.status = status; self.onSettled = onSettled
    }
    func bind(items: [NSPasteboardItem]) { lock.lock(); self.items = items; lock.unlock() }
    func cancel() {
        lock.lock(); cancelled = true; let signals = waiters; waiters.removeAll(); lock.unlock()
        signals.forEach { $0.signal() }
    }

    /// A replaced clipboard revokes an unused offer; an in-progress paste keeps running.
    func retireIfStarted() -> Bool {
        lock.lock(); let started = requested, active = requested && result == nil; lock.unlock()
        if !started { cancel() }
        return active
    }

    func pasteboard(_ pasteboard: NSPasteboard?, item: NSPasteboardItem, provideDataForType type: NSPasteboard.PasteboardType) {
        guard type == .fileURL else { return }
        lock.lock(); let alreadyStarted = requested; lock.unlock()
        guard alreadyStarted || changeCheck() else { return }
        let signal = DispatchSemaphore(value: 0)
        lock.lock()
        if !requested {
            requested = true
            transfer.fetchRemote(id: offerID, entries: entries, roots: roots) { [weak self] result in
                guard let self else { return }
                self.lock.lock(); self.result = result; let signals = self.waiters; self.waiters.removeAll(); self.lock.unlock()
                signals.forEach { $0.signal() }
                self.onSettled(self)
            }
        }
        if result == nil && !cancelled { waiters.append(signal) }
        let shouldWait = result == nil && !cancelled
        lock.unlock()
        if shouldWait && signal.wait(timeout: .now() + 75) == .timedOut { status("Finder粘贴等待文件超时"); return }
        lock.lock(); let outcome = result; let blocked = cancelled; let index = items.firstIndex(where: { $0 === item }); lock.unlock()
        guard !blocked, let index else { return }
        switch outcome {
        case .success(let urls):
            guard urls.indices.contains(index) else { return }
            lock.lock(); let allItems = items; lock.unlock()
            for (position, element) in allItems.enumerated() where urls.indices.contains(position) {
                element.setString(urls[position].absoluteString, forType: .fileURL)
            }
        case .failure(let error): status(error.localizedDescription)
        case .none: break
        }
    }

    func pasteboardFinishedWithDataProvider(_ pasteboard: NSPasteboard) { _ = retireIfStarted() }
}
