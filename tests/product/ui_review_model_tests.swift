// Compile with the DesktopModel prefix of Main.swift, Protocol.swift and InputReadiness.swift.
// These service substitutes fail immediately if UI review creates a native or network service.
import AppKit
import Foundation

final class NativeCoordinator {
    init() { fatalError("UI review must not construct NativeCoordinator") }
    var sendControl: ((WireMessage) -> Void)?
    var sendMedia: ((WireMessage) -> Void)?
    var onStatus: ((String) -> Void)?
    var onWindowHandoffStatus: ((String) -> Void)?
    var onCapabilities: ((Bool, Bool) -> Void)?
    var onInputReadiness: ((InputReadiness) -> Void)?
    var onSwitch: (() -> Void)?
    var onEmergency: (() -> Void)?
    var onTransfer: ((String, Double) -> Void)?
    func start() {}
    func stop() {}
    func heartbeat() {}
    func setConnected(_ value: Bool) {}
    func applyState(_ value: WireMessage) {}
    func receiveControl(_ value: WireMessage) {}
    func receiveMedia(_ value: WireMessage) {}
    func setClipboardEnabled(_ value: Bool) {}
    func setShortcutMapping(_ value: Bool) {}
    func setWindowHandoffEnabled(_ value: Bool) {}
    func setCaptureAllowed(_ value: Bool) {}
    func cancelTransfer() {}
    func requestInputPermissions() {}
    func requestPreviewPermission() {}
    func refreshPermissions() {}
}

enum SessionError: Error { case invalidInvitation }
final class DesktopSession {
    init() { fatalError("UI review must not construct DesktopSession") }
    var pairedHost = ""
    var hasPairing = false
    var onConnection: ((Bool, String) -> Void)?
    var onState: ((WireMessage) -> Void)?
    var onControl: ((WireMessage) -> Void)?
    var onMedia: ((WireMessage) -> Void)?
    var onHeartbeat: (() -> Void)?
    var onPreview: ((Data, String) -> Void)?
    var onPreviewUnavailable: ((String) -> Void)?
    func start() {}
    func stop() {}
    func saveInvitation(_ value: String) throws {}
    func sendControl(_ value: WireMessage) {}
    func sendMedia(_ value: WireMessage) {}
}

@main struct UIReviewModelTests {
    static func main() {
        let model = DesktopModel(uiReview: true)
        model.connected = true
        model.ready = true
        model.canSwitch = true
        model.canRecoverWindows = true
        model.pendingVisualTarget = "mac"
        model.visualConfirmationID = 3
        precondition(model.pendingObservation != nil)
        let originalDetail = model.stateDetail
        model.confirmObservedDisplay()
        precondition(model.stateDetail == originalDetail)
        model.switchTarget()
        model.restoreWindows()
        model.target = "mac"
        model.switchTarget()
        model.chooseSource("windows")
        model.emergencyRecovery()
        model.requestInputPermissions()
        model.requestPreviewPermission()
        model.openInputSettings(accessibility: true)
        model.openInputSettings(accessibility: false)
        model.refreshPermissions()
        model.setClipboard(false)
        model.setShortcutMapping(false)
        model.setWindowHandoffEnabled(false)
        precondition(!model.windowHandoffEnabled)
        model.setPreview(false)
        model.cancelTransfer()
        model.saveInvitation()
        model.openInvitationFile()
        model.loadInvitationFile(URL(fileURLWithPath: "/does-not-exist.switchdesk"))
        model.updateSubscription()
        model.windowHidden()
        model.stop()
        precondition(model.inputReadiness == .unchecked)
        print("UI review action isolation tests passed")
    }
}
