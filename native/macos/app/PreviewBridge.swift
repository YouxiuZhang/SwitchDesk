import AppKit
import AVFoundation
import CoreImage
import Foundation
import ScreenCaptureKit

/// A subscribed, paired window gets at most one compressed frame in flight.
final class PreviewBridge: NSObject, SCStreamOutput, SCStreamDelegate {
    var send: ((WireMessage) -> Void)?
    var status: ((String) -> Void)?
    var capabilityChanged: ((Bool) -> Void)?
    private let queue = DispatchQueue(label: "SwitchDesk.preview", qos: .utility)
    private let sampleQueue = DispatchQueue(label: "SwitchDesk.preview.samples", qos: .utility)
    private let frameGate = NSLock()
    private let ci = CIContext(options: [.useSoftwareRenderer: false])
    private var stream: SCStream?
    private var connected = false
    private var allowed = false
    private var subscribed = false
    private var locked = false
    private var starting = false
    private var frameBusy = false
    private var lastFrame = Date.distantPast
    private var label = "MacBook内置屏幕"
    private var observers: [NSObjectProtocol] = []

    override init() {
        super.init()
        let nc = NSWorkspace.shared.notificationCenter
        observers.append(nc.addObserver(forName: NSWorkspace.sessionDidResignActiveNotification, object: nil, queue: nil) { [weak self] _ in
            self?.queue.async { self?.locked = true; self?.reconcile() }
        })
        observers.append(nc.addObserver(forName: NSWorkspace.sessionDidBecomeActiveNotification, object: nil, queue: nil) { [weak self] _ in
            self?.queue.async { self?.locked = false; self?.reconcile() }
        })
        let distributed = DistributedNotificationCenter.default()
        observers.append(distributed.addObserver(forName: NSNotification.Name("com.apple.screenIsLocked"), object: nil, queue: nil) { [weak self] _ in
            self?.queue.async { self?.locked = true; self?.reconcile() }
        })
        observers.append(distributed.addObserver(forName: NSNotification.Name("com.apple.screenIsUnlocked"), object: nil, queue: nil) { [weak self] _ in
            self?.queue.async { self?.locked = false; self?.reconcile() }
        })
    }

    deinit {
        for observer in observers { NSWorkspace.shared.notificationCenter.removeObserver(observer); DistributedNotificationCenter.default().removeObserver(observer) }
    }

    func setConnected(_ value: Bool) {
        queue.async {
            self.connected = value
            if !value { self.subscribed = false }
            self.reconcile()
        }
    }
    func setAllowed(_ value: Bool) { queue.async { self.allowed = value; self.reconcile() } }
    func setSubscribed(_ value: Bool) { queue.async { self.subscribed = value; self.reconcile() } }
    func requestPermission() {
        _ = CGRequestScreenCaptureAccess()
        capabilityChanged?(CGPreflightScreenCaptureAccess())
        queue.async { self.reconcile() }
    }
    func stop() { queue.async { self.connected = false; self.allowed = false; self.subscribed = false; self.reconcile() } }

    private func reconcile() {
        let desired = connected && allowed && subscribed && !locked && CGPreflightScreenCaptureAccess()
        capabilityChanged?(CGPreflightScreenCaptureAccess())
        if !desired {
            if let stream { self.stream = nil; stream.stopCapture { _ in } }
            starting = false
            frameGate.lock(); frameBusy = false; frameGate.unlock()
            send?(["type": "preview_unavailable", "detail": locked ? "Mac已锁屏" : "Mac预览已暂停"])
            return
        }
        guard stream == nil && !starting else { return }
        starting = true
        SCShareableContent.getExcludingDesktopWindows(true, onScreenWindowsOnly: false) { [weak self] content, error in
            guard let self else { return }
            self.queue.async {
                guard self.connected && self.allowed && self.subscribed && !self.locked else { self.starting = false; return }
                guard let displays = content?.displays, !displays.isEmpty else {
                    self.starting = false; self.send?(["type": "preview_unavailable", "detail": error?.localizedDescription ?? "没有可预览显示器"]); return
                }
                let selected = displays.first(where: { CGDisplayIsBuiltin($0.displayID) == 0 }) ?? displays[0]
                self.label = CGDisplayIsBuiltin(selected.displayID) == 0 ? "Mac外接屏幕" : "MacBook内置屏幕"
                let filter = SCContentFilter(display: selected, excludingWindows: [])
                let configuration = SCStreamConfiguration()
                configuration.width = 640; configuration.height = 360
                configuration.pixelFormat = kCVPixelFormatType_32BGRA
                configuration.minimumFrameInterval = CMTime(value: 1, timescale: 5)
                configuration.queueDepth = 2
                configuration.showsCursor = true
                let stream = SCStream(filter: filter, configuration: configuration, delegate: self)
                do {
                    try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: self.sampleQueue)
                    self.stream = stream
                    stream.startCapture { [weak self] error in
                        self?.queue.async {
                            self?.starting = false
                            if let error { self?.stream = nil; self?.send?(["type": "preview_unavailable", "detail": error.localizedDescription]) }
                        }
                    }
                } catch {
                    self.starting = false; self.send?(["type": "preview_unavailable", "detail": error.localizedDescription])
                }
            }
        }
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, let buffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        frameGate.lock()
        let mayQueue = !frameBusy && Date().timeIntervalSince(lastFrame) >= 0.18
        if mayQueue { frameBusy = true; lastFrame = Date() }
        frameGate.unlock()
        guard mayQueue else { return }
        queue.async {
            defer { self.frameGate.lock(); self.frameBusy = false; self.frameGate.unlock() }
            guard self.connected && self.allowed && self.subscribed && !self.locked else { return }
            let image = CIImage(cvPixelBuffer: buffer)
            guard let cg = self.ci.createCGImage(image, from: image.extent) else { return }
            let rep = NSBitmapImageRep(cgImage: cg)
            var jpeg: Data?
            for quality in [0.65, 0.45, 0.3] {
                if let data = rep.representation(using: .jpeg, properties: [.compressionFactor: quality]), data.count <= 512 * 1024 { jpeg = data; break }
            }
            if let jpeg {
                self.send?(["type": "preview", "data": jpeg.base64EncodedString(), "label": self.label, "width": 640, "height": 360])
            }
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        queue.async {
            self.stream = nil; self.starting = false
            self.frameGate.lock(); self.frameBusy = false; self.frameGate.unlock()
            self.send?(["type": "preview_unavailable", "detail": error.localizedDescription])
        }
    }
}
