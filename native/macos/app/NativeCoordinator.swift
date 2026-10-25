import AppKit
import ApplicationServices
import Foundation

/// Facade used by Session and the UI. Its adapters keep hooks, media and disk off the UI queue.
final class NativeCoordinator {
    var onWindowHandoffStatus: ((String) -> Void)?
    private let windowHandoff = WindowHandoff()
    var sendControl: ((WireMessage) -> Void)?
    var sendMedia: ((WireMessage) -> Void)?
    var onStatus: ((String) -> Void)?
    var onCapabilities: ((Bool, Bool) -> Void)?
    var onInputReadiness: ((InputReadiness) -> Void)?
    var onSwitch: (() -> Void)?
    var onEmergency: (() -> Void)?
    var onTransfer: ((String, Double) -> Void)?

    private let input = InputBridge()
    private let transfer = FileTransfer()
    private let clipboard: ClipboardBridge
    private let preview = PreviewBridge()
    private var connected = false
    private var clipboardEnabled = false
    private var captureAllowed = false
    private var inputCapability = false
    private var captureCapability = false

    init() {
        clipboard = ClipboardBridge(transfer: transfer)
        input.send = { [weak self] message in self?.sendControl?(message) }
        input.switchHotkey = { [weak self] in
            guard let self else { return }
            self.input.beginSwitch()
            DispatchQueue.main.async { self.onSwitch?() }
        }
        input.emergencyHotkey = { [weak self] in
            guard let self else { return }
            DispatchQueue.main.async { self.onEmergency?() }
        }
        input.capabilityChanged = { [weak self] available in
            DispatchQueue.main.async {
                guard let self else { return }
                self.inputCapability = available && self.input.inputAvailable
                self.publishCapabilities()
            }
        }
        transfer.send = { [weak self] message in self?.sendMedia?(message) }
        transfer.status = { [weak self] text in self?.report(text) }
        transfer.progress = { [weak self] title, fraction in self?.reportTransfer(title, fraction) }
        clipboard.send = { [weak self] message in self?.sendMedia?(message) }
        clipboard.status = { [weak self] text in self?.report(text) }
        preview.send = { [weak self] message in self?.sendMedia?(message) }
        preview.status = { [weak self] text in self?.report(text) }
        preview.capabilityChanged = { [weak self] available in
            DispatchQueue.main.async {
                guard let self else { return }
                self.captureCapability = available
                self.publishCapabilities()
            }
        }
    }

    func start() {
        windowHandoff.onStatus = { [weak self] text in self?.onWindowHandoffStatus?(text) }
        windowHandoff.start()
        input.start() // Preflight only; prompts are exclusively in the explicit permission actions.
        clipboard.start()
        inputCapability = input.inputAvailable
        captureCapability = CGPreflightScreenCaptureAccess()
        publishCapabilities()
    }

    func setConnected(_ connected: Bool) {
        windowHandoff.setConnected(connected)
        self.connected = connected
        input.setConnected(connected)
        clipboard.setAvailable(connected: connected, enabled: clipboardEnabled)
        preview.setConnected(connected)
        report(connected ? "已连接 Windows；\(input.readiness.detail)" : "连接已断开，输入和传输已释放")
        publishCapabilities()
    }

    func applyState(_ state: WireMessage) {
        input.applyState(state)
        windowHandoff.applyState(state)
    }

    func setWindowHandoffEnabled(_ value: Bool) { windowHandoff.setEnabled(value) }

    func receiveControl(_ message: WireMessage) {
        switch wireString(message, "type") {
        case "input": input.receive(message)
        case "preview_subscribe": preview.setSubscribed(wireBool(message, "enabled"))
        case "emergency": input.remoteEmergency()
        default: break
        }
    }

    func receiveMedia(_ message: WireMessage) {
        // Called from Session's media queue. Neither branch synchronously dispatches to main.
        switch wireString(message, "type") {
        case "clipboard": clipboard.receive(message)
        case "file_request", "file_begin", "file_chunk", "file_end", "file_done", "file_error", "file_cancel": transfer.receive(message)
        default: break
        }
    }

    func setClipboardEnabled(_ enabled: Bool) {
        clipboardEnabled = enabled
        clipboard.setAvailable(connected: connected, enabled: enabled)
    }
    func setShortcutMapping(_ enabled: Bool) { input.setShortcutMapping(enabled) }
    func setCaptureAllowed(_ allowed: Bool) {
        captureAllowed = allowed
        preview.setAllowed(allowed)
    }
    func requestInputPermissions() {
        input.requestPermissions()
        refreshPermissionsSoon()
    }
    func requestPreviewPermission() {
        preview.requestPermission()
        refreshPermissionsSoon()
    }
    private func refreshPermissionsSoon() {
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in
            self?.refreshPermissions()
        }
    }
    func refreshPermissions() {
        // Recheck after the user returns from System Settings; never opens a permission prompt.
        input.start()
        inputCapability = input.inputAvailable
        captureCapability = CGPreflightScreenCaptureAccess()
        publishCapabilities()
    }
    func cancelTransfer() { transfer.cancelAll() }
    func heartbeat() { input.heartbeat() }
    func stop() {
        windowHandoff.stop()
        connected = false
        input.stop(); clipboard.stop(); preview.stop(); transfer.cancelAll()
        publishCapabilities()
    }

    private func publishCapabilities() {
        let readiness = input.readiness
        let inputAvailable = inputCapability && readiness.available
        let captureAvailable = captureCapability && CGPreflightScreenCaptureAccess()
        if connected { sendControl?(["type": "capabilities", "input": inputAvailable, "capture": captureAvailable]) }
        onCapabilities?(inputAvailable, captureAvailable)
        onInputReadiness?(readiness)
    }
    private func report(_ text: String) { DispatchQueue.main.async { [weak self] in self?.onStatus?(text) } }
    private func reportTransfer(_ title: String, _ fraction: Double) { DispatchQueue.main.async { [weak self] in self?.onTransfer?(title, fraction) } }
}
