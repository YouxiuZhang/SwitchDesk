import Foundation
import Network
import Security
import CryptoKit

struct PairingInvitation: Codable {
    let host: String
    let port: Int
    let pin: String
    let token: String

    static func parse(_ input: String) throws -> PairingInvitation {
        let trimmed = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.hasPrefix("switchdesk1:"),
              let data = Data(base64Encoded: String(trimmed.dropFirst("switchdesk1:".count))),
              let value = try? JSONDecoder().decode(Self.self, from: data),
              (1...65535).contains(value.port),
              value.pin.count == 64,
              value.pin.utf8.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) }),
              Data(base64Encoded: value.token)?.count == 32,
              !value.host.isEmpty, value.host.count <= 255,
              !value.host.contains(where: { $0.isWhitespace || $0 == "/" || $0 == "@" }) else {
            throw SessionError.invalidInvitation
        }
        return value
    }
}

enum SessionError: LocalizedError {
    case invalidInvitation
    case invalidFrame
    case authentication
    var errorDescription: String? {
        switch self {
        case .invalidInvitation: return "配对邀请无效，请重新从 Windows 复制完整邀请。"
        case .invalidFrame: return "收到格式错误或过大的消息。"
        case .authentication: return "配对认证失败，请在 Windows 上确认授权或重新配对。"
        }
    }
}

private enum PairingStore {
    static let service = "com.switchdesk.desktop.pairing.v1"
    static func read(_ account: String) -> String? {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                    kSecAttrService as String: service,
                                    kSecAttrAccount as String: account,
                                    kSecReturnData as String: true,
                                    kSecMatchLimit as String: kSecMatchLimitOne]
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }
    static func write(_ string: String, account: String) -> Bool {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                    kSecAttrService as String: service,
                                    kSecAttrAccount as String: account]
        let attrs: [String: Any] = [kSecValueData as String: Data(string.utf8),
                                    kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly]
        let status = SecItemAdd(query.merging(attrs) { _, new in new } as CFDictionary, nil)
        if status == errSecDuplicateItem {
            return SecItemUpdate(query as CFDictionary, attrs as CFDictionary) == errSecSuccess
        }
        return status == errSecSuccess
    }
}

/// Public entry points and callbacks are used on the main thread. All socket state lives on queue.
final class DesktopSession {
    var onConnection: ((Bool, String) -> Void)?
    var onState: ((WireMessage) -> Void)?
    var onControl: ((WireMessage) -> Void)?
    var onMedia: ((WireMessage) -> Void)? // background file queue, ordered
    var onHeartbeat: (() -> Void)?
    var onPreview: ((Data, String) -> Void)?
    var onPreviewUnavailable: ((String) -> Void)?

    private let queue = DispatchQueue(label: "switchdesk.session")
    private let fileQueue = DispatchQueue(label: "switchdesk.media.files", qos: .utility)
    private let persistPairing: Bool
    private var invitation: PairingInvitation?
    private var deviceID: String
    private var control: NWConnection?
    private var media: NWConnection?
    private var controlReady = false
    private var mediaReady = false
    private var generation = 0
    private var stopped = false
    private var reconnectPending = false
    private var heartbeatTimer: DispatchSourceTimer?
    private var lastPong = Date.distantPast
    private var previewPending = false
    private var controlWriter: FrameWriter?
    private var mediaWriter: FrameWriter?
    private let mediaSlots = DispatchSemaphore(value: 8)

    init(persistPairing: Bool = true, testInvitation: PairingInvitation? = nil) {
        self.persistPairing = persistPairing
        if !persistPairing || testInvitation != nil {
            // A local simulated peer can exercise networking without touching the user's pairing.
            invitation = testInvitation
            deviceID = UUID().uuidString
        } else {
            if let saved = PairingStore.read("invitation") { invitation = try? PairingInvitation.parse(saved) }
            if let saved = PairingStore.read("device"), UUID(uuidString: saved) != nil {
                deviceID = saved
            } else {
                let newID = UUID().uuidString
                deviceID = newID
                _ = PairingStore.write(newID, account: "device")
            }
        }
    }

    var hasPairing: Bool { invitation != nil }
    var pairedHost: String { invitation?.host ?? "" }

    func saveInvitation(_ string: String) throws {
        let parsed = try PairingInvitation.parse(string)
        guard !persistPairing || PairingStore.write(string.trimmingCharacters(in: .whitespacesAndNewlines), account: "invitation") else {
            throw NSError(domain: "SwitchDesk", code: 10, userInfo: [NSLocalizedDescriptionKey: "无法将配对信息保存到钥匙串。"])
        }
        invitation = parsed
        queue.async { self.invitation = parsed; self.disconnect("正在连接新配对设备", retry: false); self.connect() }
    }

    func start() { queue.async { self.stopped = false; self.connect() } }
    func stop() { queue.async { self.stopped = true; self.disconnect("已退出", retry: false) } }
    func sendControl(_ message: WireMessage) { queue.async { self.controlWriter?.enqueue(message) } }
    func sendMedia(_ message: WireMessage) {
        let isChunk = wireString(message, "type") == "file_chunk"
        if isChunk { mediaSlots.wait() } // FileTransfer calls from its background producer.
        queue.async {
            guard let writer = self.mediaWriter else { if isChunk { self.mediaSlots.signal() }; return }
            writer.enqueue(message, finished: isChunk ? { self.mediaSlots.signal() } : nil)
        }
    }

    private func connect() {
        guard !stopped, let invite = invitation, control == nil else { return }
        generation += 1
        let current = generation
        dispatchConnection(false, "正在连接 Windows · \(invite.host)")
        let conn = makeConnection(invite)
        control = conn
        conn.stateUpdateHandler = { [weak self] state in
            guard let self = self, current == self.generation else { return }
            switch state {
            case .ready:
                self.controlWriter = FrameWriter(connection: conn, limit: DesktopWire.controlLimit, queue: self.queue) { [weak self] error in
                    guard let self = self, current == self.generation else { return }
                    self.disconnect("控制发送失败：\(error.localizedDescription)", retry: true)
                }
                self.controlWriter?.enqueue(self.hello("control", token: invite.token))
                self.receiveFrame(on: conn, limit: DesktopWire.controlLimit, generation: current, channel: "control")
                self.queue.asyncAfter(deadline: .now() + 90) { [weak self] in
                    guard let self = self, current == self.generation, !self.controlReady else { return }
                    self.disconnect("等待 Windows 配对授权超时", retry: true)
                }
            case .failed(let error): self.disconnect("控制连接中断：\(error.localizedDescription)", retry: true)
            case .waiting(let error): self.disconnect("等待网络：\(error.localizedDescription)", retry: true)
            case .cancelled: break
            default: break
            }
        }
        conn.start(queue: queue)
    }

    private func makeConnection(_ invite: PairingInvitation) -> NWConnection {
        let tls = NWProtocolTLS.Options()
        sec_protocol_options_set_min_tls_protocol_version(tls.securityProtocolOptions, .TLSv12)
        sec_protocol_options_set_verify_block(tls.securityProtocolOptions, { _, trust, complete in
            let secTrust = sec_trust_copy_ref(trust).takeRetainedValue()
            guard let chain = SecTrustCopyCertificateChain(secTrust) as? [SecCertificate],
                  let cert = chain.first else { complete(false); return }
            let der = SecCertificateCopyData(cert) as Data
            let actual = SHA256.hash(data: der).map { String(format: "%02x", $0) }.joined()
            complete(actual == invite.pin)
        }, queue)
        let params = NWParameters(tls: tls, tcp: NWProtocolTCP.Options())
        return NWConnection(host: NWEndpoint.Host(invite.host), port: NWEndpoint.Port(rawValue: UInt16(invite.port))!, using: params)
    }

    private func hello(_ channel: String, token: String) -> WireMessage {
        ["type": "hello", "version": DesktopWire.version, "channel": channel,
         "token": token, "device": deviceID, "name": Host.current().localizedName ?? "Mac"]
    }

    private func startMedia(generation current: Int) {
        guard let invite = invitation, controlReady, media == nil else { return }
        let conn = makeConnection(invite)
        media = conn
        conn.stateUpdateHandler = { [weak self] state in
            guard let self = self, current == self.generation else { return }
            switch state {
            case .ready:
                self.mediaWriter = FrameWriter(connection: conn, limit: DesktopWire.mediaLimit, queue: self.queue) { [weak self] error in
                    guard let self = self, current == self.generation else { return }
                    self.disconnect("媒体发送失败：\(error.localizedDescription)", retry: true)
                }
                self.mediaWriter?.enqueue(self.hello("media", token: invite.token))
                self.receiveFrame(on: conn, limit: DesktopWire.mediaLimit, generation: current, channel: "media")
                self.queue.asyncAfter(deadline: .now() + 15) { [weak self] in
                    guard let self = self, current == self.generation, !self.mediaReady else { return }
                    self.disconnect("媒体连接认证超时", retry: true)
                }
            case .failed(let error): self.disconnect("媒体连接中断：\(error.localizedDescription)", retry: true)
            case .waiting(let error): self.disconnect("媒体等待网络：\(error.localizedDescription)", retry: true)
            case .cancelled: break
            default: break
            }
        }
        conn.start(queue: queue)
    }

    private func receiveFrame(on conn: NWConnection, limit: Int, generation current: Int, channel: String) {
        readExactly(4, from: conn) { [weak self] header in
            guard let self = self, current == self.generation else { return }
            guard let header = header else { self.disconnect("连接已关闭", retry: true); return }
            let length = header.reduce(UInt32(0)) { ($0 << 8) | UInt32($1) }
            guard length > 0, length <= limit else { self.disconnect(SessionError.invalidFrame.localizedDescription, retry: true); return }
            self.readExactly(Int(length), from: conn) { [weak self] body in
                guard let self = self, current == self.generation else { return }
                guard let body = body,
                      let object = try? JSONSerialization.jsonObject(with: body),
                      let message = object as? WireMessage,
                      let type = message["type"] as? String else {
                    self.disconnect(SessionError.invalidFrame.localizedDescription, retry: true); return
                }
                self.process(message, type: type, channel: channel, generation: current) {
                    self.receiveFrame(on: conn, limit: limit, generation: current, channel: channel)
                }
            }
        }
    }

    private func readExactly(_ count: Int, from conn: NWConnection, accumulated: Data = Data(), done: @escaping (Data?) -> Void) {
        conn.receive(minimumIncompleteLength: 1, maximumLength: count - accumulated.count) { [weak self] data, _, complete, error in
            guard let self = self else { return }
            if error != nil || complete { done(nil); return }
            var next = accumulated
            guard let data = data, !data.isEmpty else { done(nil); return }
            next.append(data)
            if next.count == count { done(next) }
            else { self.readExactly(count, from: conn, accumulated: next, done: done) }
        }
    }

    private func process(_ message: WireMessage, type: String, channel: String, generation current: Int, resume: @escaping () -> Void) {
        if channel == "control" && !controlReady {
            guard type == "welcome", wireInt(message, "version") == DesktopWire.version,
                  wireString(message, "channel") == "control" else { disconnect(SessionError.authentication.localizedDescription, retry: true); return }
            controlReady = true
            lastPong = Date()
            startHeartbeat(generation: current)
            startMedia(generation: current)
            resume()
            return
        }
        if channel == "media" && !mediaReady {
            guard type == "welcome", wireInt(message, "version") == DesktopWire.version,
                  wireString(message, "channel") == "media" else { disconnect(SessionError.authentication.localizedDescription, retry: true); return }
            mediaReady = true
            dispatchConnection(true, "已连接 Windows")
            resume()
            return
        }
        if channel == "control" {
            switch type {
            case "ping":
                controlWriter?.enqueue(["type": "pong"])
                DispatchQueue.main.async { [weak self] in self?.onHeartbeat?() }
            case "pong":
                lastPong = Date()
                DispatchQueue.main.async { [weak self] in self?.onHeartbeat?() }
            case "state": DispatchQueue.main.async { [weak self] in self?.onState?(message) }
            default: DispatchQueue.main.async { [weak self] in self?.onControl?(message) }
            }
            resume()
            return
        }
        guard mediaReady else { disconnect(SessionError.authentication.localizedDescription, retry: true); return }
        if type == "preview" {
            if !previewPending,
               let encoded = message["data"] as? String, encoded.utf8.count <= 700_000,
               let data = Data(base64Encoded: encoded), data.count <= 512 * 1024,
               (1...4096).contains(wireInt(message, "width")),
               (1...4096).contains(wireInt(message, "height")) {
                previewPending = true
                let label = wireString(message, "label", "Windows 共享屏幕")
                DispatchQueue.main.async { [weak self] in
                    guard let self = self else { return }
                    self.onPreview?(data, label)
                    self.queue.async { self.previewPending = false }
                }
            }
            resume()
        } else if type == "preview_unavailable" {
            DispatchQueue.main.async { [weak self] in self?.onPreviewUnavailable?(wireString(message, "detail", "画面暂不可用")) }
            resume()
        } else {
            // Waiting here only pauses media reading; control and heartbeats keep flowing.
            fileQueue.async { [weak self] in
                guard let self = self else { return }
                let valid = self.queue.sync { current == self.generation && self.mediaReady }
                if valid { self.onMedia?(message) }
                self.queue.async { if current == self.generation { resume() } }
            }
        }
    }

    private func startHeartbeat(generation current: Int) {
        heartbeatTimer?.cancel()
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 1, repeating: 1)
        timer.setEventHandler { [weak self] in
            guard let self = self, current == self.generation else { return }
            if Date().timeIntervalSince(self.lastPong) > 4 {
                self.disconnect("心跳超时，已释放远控", retry: true)
            } else {
                self.controlWriter?.enqueue(["type": "ping"])
            }
        }
        heartbeatTimer = timer
        timer.resume()
    }

    private func disconnect(_ detail: String, retry: Bool) {
        generation += 1
        heartbeatTimer?.cancel(); heartbeatTimer = nil
        controlWriter?.cancel(); mediaWriter?.cancel()
        controlWriter = nil; mediaWriter = nil
        control?.cancel(); media?.cancel()
        control = nil; media = nil
        controlReady = false; mediaReady = false
        previewPending = false
        dispatchConnection(false, detail)
        if retry && !stopped && invitation != nil && !reconnectPending {
            reconnectPending = true
            queue.asyncAfter(deadline: .now() + 2) { [weak self] in
                guard let self = self else { return }
                self.reconnectPending = false
                self.connect()
            }
        }
    }

    private func dispatchConnection(_ connected: Bool, _ detail: String) {
        DispatchQueue.main.async { [weak self] in self?.onConnection?(connected, detail) }
    }
}

private final class FrameWriter {
    private final class Packet {
        let data: Data
        let type: String
        let eventKind: String
        let finished: (() -> Void)?
        var didFinish = false
        init(data: Data, type: String, eventKind: String, finished: (() -> Void)?) {
            self.data = data; self.type = type; self.eventKind = eventKind; self.finished = finished
        }
        func finish() {
            guard !didFinish else { return }
            didFinish = true
            finished?()
        }
    }
    private let connection: NWConnection
    private let limit: Int
    private let queue: DispatchQueue
    private let onFailure: (Error) -> Void
    private var pending: [Packet] = []
    private var active: Packet?
    private var writing = false
    private var cancelled = false

    init(connection: NWConnection, limit: Int, queue: DispatchQueue, onFailure: @escaping (Error) -> Void) {
        self.connection = connection; self.limit = limit; self.queue = queue; self.onFailure = onFailure
    }
    func enqueue(_ message: WireMessage, finished: (() -> Void)? = nil) {
        guard !cancelled, let data = try? DesktopWire.frame(message, limit: limit) else { finished?(); return }
        let type = wireString(message, "type")
        let eventKind = (message["event"] as? WireMessage).map { wireString($0, "kind") } ?? ""
        // A preview is only useful when the lane is idle; never build a stale queue.
        if type == "preview" && (writing || !pending.isEmpty) { finished?(); return }
        if type == "input" && eventKind == "move" {
            pending.removeAll { packet in
                if packet.type == "input" && packet.eventKind == "move" { packet.finish(); return true }
                return false
            }
        }
        pending.append(Packet(data: data, type: type, eventKind: eventKind, finished: finished))
        writeNext()
    }
    func cancel() {
        cancelled = true
        active?.finish()
        pending.forEach { $0.finish() }
        pending.removeAll()
    }
    private func writeNext() {
        guard !writing, !cancelled, !pending.isEmpty else { return }
        writing = true
        let packet = pending.removeFirst()
        active = packet
        connection.send(content: packet.data, completion: .contentProcessed { [weak self] error in
            guard let self = self else { return }
            self.queue.async {
                packet.finish()
                self.active = nil
                self.writing = false
                if let error = error { self.onFailure(error) }
                else { self.writeNext() }
            }
        })
    }
}
