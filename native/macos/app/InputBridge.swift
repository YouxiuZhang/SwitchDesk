import AppKit
import ApplicationServices
import CoreGraphics
import Foundation

/// The event tap owns its own run loop. No clipboard, file or network wait runs on it.
final class InputBridge {
    var send: ((WireMessage) -> Void)?
    var switchHotkey: (() -> Void)?
    var emergencyHotkey: (() -> Void)?
    var capabilityChanged: ((Bool) -> Void)?

    private let lock = NSLock()
    private let hotkeyPermissionsGranted: () -> Bool
    private let sendQueue = DispatchQueue(label: "SwitchDesk.input.send", qos: .userInteractive)
    private let injectQueue = DispatchQueue(label: "SwitchDesk.input.inject", qos: .userInteractive)
    private var tap: CFMachPort?
    private var tapRunLoop: CFRunLoop?
    private var tapThread: Thread?
    private var installed = false
    private var tapInstallFailed = false
    private var reportedReadiness = InputReadiness.unchecked
    private var connected = false
    private var ready = false
    private var source = "windows"
    private var target = "windows"
    private var epoch = 0
    private var leaseUntil = Date.distantPast
    private var suspended = false
    private var shortcutMapping = false
    private var consumedHotkeys = Set<CGKeyCode>()
    private var consumedShortcuts = Set<CGKeyCode>()
    private var sentModifiers = Set<Int>()
    private var injectedKeys = Set<Int>()
    private var injectedButtons = Set<Int>()
    private var watchdog: DispatchSourceTimer?

    // Private marker is checked before any hotkey or forwarding decision.
    private let marker: Int64 = 0x53574445534B_0001

    init(hotkeyPermissionsGranted: @escaping () -> Bool = {
        AXIsProcessTrusted() && CGPreflightListenEventAccess()
    }) {
        self.hotkeyPermissionsGranted = hotkeyPermissionsGranted
    }

    var readiness: InputReadiness {
        let accessibility = AXIsProcessTrusted()
        let inputMonitoring = CGPreflightListenEventAccess()
        lock.lock()
        let port = tap, isInstalled = installed, isInstalling = tapThread != nil, failed = tapInstallFailed
        lock.unlock()
        let tapState: InputReadiness.EventTap
        if isInstalled, let port { tapState = CGEvent.tapIsEnabled(tap: port) ? .installed : .disabled }
        else if isInstalling { tapState = .installing }
        else { tapState = failed ? .failed : .notInstalled }
        return InputReadiness(accessibility: accessibility, inputMonitoring: inputMonitoring, eventTap: tapState)
    }

    var inputAvailable: Bool { readiness.available }

    func start() {
        // Keep diagnosing individual permission changes even while the combined capability is false.
        if watchdog == nil {
            let timer = DispatchSource.makeTimerSource(queue: injectQueue)
            timer.schedule(deadline: .now() + .milliseconds(250), repeating: .milliseconds(250))
            timer.setEventHandler { [weak self] in self?.checkLease() }
            watchdog = timer
            timer.resume()
        }
        guard AXIsProcessTrusted(), CGPreflightListenEventAccess() else { capabilityChanged?(false); return }
        lock.lock()
        if installed || tapThread != nil {
            let port = tap
            lock.unlock()
            if let port { CGEvent.tapEnable(tap: port, enable: true) }
            capabilityChanged?(inputAvailable)
            return
        }
        tapInstallFailed = false
        let thread = Thread { [weak self] in self?.runTap() }
        thread.name = "SwitchDesk event tap"
        tapThread = thread
        lock.unlock()
        thread.start()
    }

    func requestPermissions() {
        _ = AXIsProcessTrustedWithOptions([kAXTrustedCheckOptionPrompt.takeUnretainedValue(): true] as CFDictionary)
        _ = CGRequestListenEventAccess()
        start()
    }

    func setConnected(_ value: Bool) {
        lock.lock()
        connected = value
        if !value { leaseUntil = .distantPast; ready = false; suspended = false }
        lock.unlock()
        if !value { releaseInjected(); sendRelease() }
    }

    func applyState(_ state: WireMessage) {
        lock.lock()
        let oldEpoch = epoch
        epoch = wireInt(state, "epoch")
        target = wireString(state, "target")
        source = wireString(state, "source")
        // Display confirmation is not input consent; absent armed must also fail closed.
        ready = wireBool(state, "ready") && wireBool(state, "armed") && !wireBool(state, "switching")
        if oldEpoch != epoch || !ready { leaseUntil = .distantPast; suspended = false }
        let shouldRelease = oldEpoch != epoch || !ready || target != "mac"
        lock.unlock()
        if shouldRelease { releaseInjected() }
    }

    func heartbeat() {
        lock.lock()
        if connected { leaseUntil = Date().addingTimeInterval(3) }
        lock.unlock()
    }

    func setShortcutMapping(_ value: Bool) { lock.lock(); shortcutMapping = value; lock.unlock() }

    func receive(_ message: WireMessage) {
        guard wireString(message, "type") == "input", let event = message["event"] as? WireMessage else { return }
        lock.lock()
        let allowed = connected && ready && target == "mac" && source == "windows" && !suspended && Date() < leaseUntil && wireInt(message, "epoch", -1) == epoch
        let packetEpoch = epoch
        lock.unlock()
        guard allowed else { return }
        injectQueue.async { [weak self] in self?.inject(event, epoch: packetEpoch) }
    }

    func emergency() {
        lock.lock(); suspended = true; leaseUntil = .distantPast; lock.unlock()
        releaseInjected()
        sendRelease()
        emergencyHotkey?()
    }

    func beginSwitch() {
        lock.lock(); suspended = true; leaseUntil = .distantPast; lock.unlock()
        releaseInjected()
        sendRelease()
    }

    func remoteEmergency() {
        lock.lock(); suspended = true; leaseUntil = .distantPast; lock.unlock()
        releaseInjected()
    }

    func stop() {
        watchdog?.cancel(); watchdog = nil
        setConnected(false)
        lock.lock()
        let loop = tapRunLoop
        lock.unlock()
        if let loop { CFRunLoopStop(loop) }
    }

    private func runTap() {
        let types: [CGEventType] = [.keyDown, .keyUp, .flagsChanged, .mouseMoved, .leftMouseDown, .leftMouseUp, .rightMouseDown, .rightMouseUp, .otherMouseDown, .otherMouseUp, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged, .scrollWheel]
        let mask = types.reduce(CGEventMask(0)) { $0 | (CGEventMask(1) << $1.rawValue) }
        let callback: CGEventTapCallBack = { _, type, event, userInfo in
            guard let userInfo else { return Unmanaged.passUnretained(event) }
            let owner = Unmanaged<InputBridge>.fromOpaque(userInfo).takeUnretainedValue()
            return owner.handle(type, event)
        }
        let info = UnsafeMutableRawPointer(Unmanaged.passUnretained(self).toOpaque())
        guard let port = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap, eventsOfInterest: mask, callback: callback, userInfo: info),
              let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, port, 0) else {
            lock.lock(); tapThread = nil; tapInstallFailed = true; lock.unlock()
            capabilityChanged?(false)
            return
        }
        lock.lock(); tap = port; tapRunLoop = CFRunLoopGetCurrent(); installed = true; tapInstallFailed = false; lock.unlock()
        CFRunLoopAddSource(CFRunLoopGetCurrent(), source, .commonModes)
        CGEvent.tapEnable(tap: port, enable: true)
        capabilityChanged?(inputAvailable)
        CFRunLoopRun()
        CFRunLoopRemoveSource(CFRunLoopGetCurrent(), source, .commonModes)
        lock.lock(); tap = nil; tapRunLoop = nil; tapThread = nil; installed = false; lock.unlock()
        capabilityChanged?(false)
    }

    private func handle(_ type: CGEventType, _ event: CGEvent) -> Unmanaged<CGEvent>? {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            lock.lock(); let port = tap; lock.unlock()
            if let port { CGEvent.tapEnable(tap: port, enable: true) }
            return Unmanaged.passUnretained(event)
        }
        if event.getIntegerValueField(.eventSourceUserData) == marker { return Unmanaged.passUnretained(event) }
        let code = CGKeyCode(event.getIntegerValueField(.keyboardEventKeycode))
        let isDown = type == .keyDown
        if type == .keyDown || type == .keyUp {
            let isSwitch = code == 111 // kVK_F12; Fn+F12 has the same keycode.
            let isEmergency = code == 53 && event.flags.contains(.maskControl) && event.flags.contains(.maskAlternate)
            lock.lock()
            let consumed = consumedHotkeys.contains(code)
            if !isDown { consumedHotkeys.remove(code) }
            lock.unlock()
            if isSwitch || isEmergency || consumed {
                // Global switching does not require remote-input consent, but it
                // still requires both macOS permissions, including after revocation.
                guard hotkeyPermissionsGranted() else { return Unmanaged.passUnretained(event) }
                if !isDown { return isSwitch || consumed ? nil : Unmanaged.passUnretained(event) }
                lock.lock(); let firstDown = consumedHotkeys.insert(code).inserted; lock.unlock()
                if firstDown && event.getIntegerValueField(.keyboardEventAutorepeat) == 0 {
                    if isSwitch { switchHotkey?() } else { emergency() }
                }
                return nil
            }
        }
        lock.lock()
        let allowed = installed && connected && ready && source == "mac" && target == "windows" && !suspended && Date() < leaseUntil
        let currentEpoch = epoch
        let mapping = shortcutMapping
        lock.unlock()
        guard allowed else { return Unmanaged.passUnretained(event) }
        if type == .keyDown || type == .keyUp {
            if mapping, let action = shortcutAction(code, flags: event.flags) {
                lock.lock()
                if isDown { consumedShortcuts.insert(code) }
                else { consumedShortcuts.remove(code) }
                let modifiers = sentModifiers
                sentModifiers.removeAll()
                lock.unlock()
                if isDown {
                    for vk in modifiers { emit(["kind": "key", "code": vk, "down": false], epoch: currentEpoch) }
                    emit(["kind": "shortcut", "action": action], epoch: currentEpoch)
                }
                return nil
            }
            lock.lock(); let consumed = consumedShortcuts.contains(code); if !isDown { consumedShortcuts.remove(code) }; lock.unlock()
            if consumed { return nil }
            if let vk = MacKeyMap.toVK[code] {
                emit(["kind": "key", "code": vk, "down": isDown], epoch: currentEpoch)
                return nil
            }
            return Unmanaged.passUnretained(event)
        }
        if type == .flagsChanged {
            guard let vk = MacKeyMap.toVK[code] else { return Unmanaged.passUnretained(event) }
            let flag: CGEventFlags
            switch vk { case 160, 161: flag = .maskShift; case 162, 163: flag = .maskControl; case 164, 165: flag = .maskAlternate; case 91, 92: flag = .maskCommand; default: return Unmanaged.passUnretained(event) }
            lock.lock()
            // CGEventFlags names a modifier family, not its left/right member.
            let down = event.flags.contains(flag) && !sentModifiers.contains(vk)
            if down { sentModifiers.insert(vk) } else { sentModifiers.remove(vk) }
            lock.unlock()
            emit(["kind": "key", "code": vk, "down": down], epoch: currentEpoch)
            return nil
        }
        if [.mouseMoved, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged].contains(type) {
            let dx = event.getIntegerValueField(.mouseEventDeltaX), dy = event.getIntegerValueField(.mouseEventDeltaY)
            var remainingX = dx, remainingY = dy
            var pieces = 0
            while (remainingX != 0 || remainingY != 0) && pieces < 64 {
                let x = max(-2048, min(2048, remainingX)), y = max(-2048, min(2048, remainingY))
                emit(["kind": "move", "dx": x, "dy": y], epoch: currentEpoch)
                remainingX -= x; remainingY -= y
                pieces += 1
            }
            return nil
        }
        if type == .scrollWheel {
            let dx = Double(event.getIntegerValueField(.scrollWheelEventDeltaAxis2)) / 3.0
            let dy = Double(event.getIntegerValueField(.scrollWheelEventDeltaAxis1)) / 3.0
            var remainingX = dx, remainingY = dy
            var pieces = 0
            while (abs(remainingX) > 0.0001 || abs(remainingY) > 0.0001) && pieces < 64 {
                let x = max(-20, min(20, remainingX)), y = max(-20, min(20, remainingY))
                emit(["kind": "scroll", "dx": x, "dy": y], epoch: currentEpoch)
                remainingX -= x; remainingY -= y
                pieces += 1
            }
            return nil
        }
        let button: Int
        switch type { case .leftMouseDown, .leftMouseUp: button = 0; case .rightMouseDown, .rightMouseUp: button = 1; default: button = 2 }
        if button == 2 && event.getIntegerValueField(.mouseEventButtonNumber) != 2 { return Unmanaged.passUnretained(event) }
        emit(["kind": "button", "button": button, "down": [.leftMouseDown, .rightMouseDown, .otherMouseDown].contains(type)], epoch: currentEpoch)
        return nil
    }

    private func shortcutAction(_ code: CGKeyCode, flags: CGEventFlags) -> String? {
        guard flags.contains(.maskCommand) else { return nil }
        switch code {
        case 8: return "copy"
        case 9: return "paste"
        case 7: return "cut"
        case 0: return "selectAll"
        case 6: return flags.contains(.maskShift) ? "redo" : "undo"
        default: return nil
        }
    }

    private func emit(_ event: WireMessage, epoch: Int) {
        let message: WireMessage = ["type": "input", "epoch": epoch, "event": event]
        sendQueue.async { [weak self] in self?.send?(message) }
    }
    private func sendRelease() {
        lock.lock(); let currentEpoch = epoch; lock.unlock()
        sendQueue.async { [weak self] in self?.send?(["type": "input", "epoch": currentEpoch, "event": ["kind": "release"]]) }
    }

    private func checkLease() {
        let currentReadiness = readiness
        lock.lock()
        let expired = connected && Date() >= leaseUntil
        let available = currentReadiness.available
        let changed = currentReadiness != reportedReadiness
        if changed { reportedReadiness = currentReadiness }
        if !available { ready = false }
        lock.unlock()
        if expired { releaseInjected() }
        if changed { if !available { releaseInjected() }; capabilityChanged?(available) }
    }

    private func inject(_ event: WireMessage, epoch packetEpoch: Int) {
        let kind = wireString(event, "kind")
        lock.lock()
        let active = connected && ready && target == "mac" && source == "windows" && !suspended && Date() < leaseUntil && epoch == packetEpoch
        lock.unlock()
        guard active else { return }
        if kind == "release" { releaseInjected(); return }
        guard let source = CGEventSource(stateID: .privateState) else { return }
        switch kind {
        case "key":
            let vk = wireInt(event, "code", -1)
            guard let key = MacKeyMap.toMac[vk] else { return }
            let down = wireBool(event, "down")
            guard let cg = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: down) else { return }
            lock.lock()
            var next = injectedKeys
            if down { next.insert(vk) } else { next.remove(vk) }
            cg.flags = InjectedModifierState.flags(for: next)
            injectedKeys = next
            lock.unlock()
            cg.setIntegerValueField(.eventSourceUserData, value: marker); cg.post(tap: .cghidEventTap)
        case "move":
            let dx = wireInt(event, "dx"), dy = wireInt(event, "dy")
            guard (-2048...2048).contains(dx), (-2048...2048).contains(dy), let here = CGEvent(source: source) else { return }
            let point = CGPoint(x: here.location.x + CGFloat(dx), y: here.location.y + CGFloat(dy))
            lock.lock(); let buttons = injectedButtons, flags = InjectedModifierState.flags(for: injectedKeys); lock.unlock()
            let dragButton: CGMouseButton = buttons.contains(0) ? .left : buttons.contains(1) ? .right : .center
            let moveType: CGEventType = buttons.contains(0) ? .leftMouseDragged : buttons.contains(1) ? .rightMouseDragged : buttons.contains(2) ? .otherMouseDragged : .mouseMoved
            guard let cg = CGEvent(mouseEventSource: source, mouseType: moveType, mouseCursorPosition: point, mouseButton: dragButton) else { return }
            cg.flags = flags
            cg.setIntegerValueField(.eventSourceUserData, value: marker); cg.post(tap: .cghidEventTap)
        case "button":
            let button = wireInt(event, "button", -1)
            guard (0...2).contains(button), let here = CGEvent(source: source) else { return }
            let down = wireBool(event, "down")
            let mouseButton: CGMouseButton = button == 0 ? .left : button == 1 ? .right : .center
            let mouseType: CGEventType = button == 0 ? (down ? .leftMouseDown : .leftMouseUp) : button == 1 ? (down ? .rightMouseDown : .rightMouseUp) : (down ? .otherMouseDown : .otherMouseUp)
            guard let cg = CGEvent(mouseEventSource: source, mouseType: mouseType, mouseCursorPosition: here.location, mouseButton: mouseButton) else { return }
            lock.lock(); cg.flags = InjectedModifierState.flags(for: injectedKeys); lock.unlock()
            cg.setIntegerValueField(.eventSourceUserData, value: marker); cg.post(tap: .cghidEventTap)
            lock.lock(); if down { injectedButtons.insert(button) } else { injectedButtons.remove(button) }; lock.unlock()
        case "scroll":
            let dx = (event["dx"] as? NSNumber)?.doubleValue ?? 0, dy = (event["dy"] as? NSNumber)?.doubleValue ?? 0
            guard dx.isFinite, dy.isFinite, abs(dx) <= 20, abs(dy) <= 20 else { return }
            guard let cg = CGEvent(scrollWheelEvent2Source: source, units: .line, wheelCount: 2, wheel1: Int32((dy * 3).rounded()), wheel2: Int32((dx * 3).rounded()), wheel3: 0) else { return }
            lock.lock(); cg.flags = InjectedModifierState.flags(for: injectedKeys); lock.unlock()
            cg.setIntegerValueField(.eventSourceUserData, value: marker); cg.post(tap: .cghidEventTap)
        case "shortcut":
            let action = wireString(event, "action")
            let key: CGKeyCode
            let shift: Bool
            switch action { case "copy": key = 8; shift = false; case "paste": key = 9; shift = false; case "cut": key = 7; shift = false; case "undo": key = 6; shift = false; case "redo": key = 6; shift = true; case "selectAll": key = 0; shift = false; default: return }
            for down in [true, false] {
                if let cg = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: down) {
                    cg.flags = shift ? [.maskCommand, .maskShift] : [.maskCommand]
                    cg.setIntegerValueField(.eventSourceUserData, value: marker); cg.post(tap: .cghidEventTap)
                }
            }
        default: return
        }
    }

    private func releaseInjected() {
        injectQueue.async { [weak self] in
            guard let self, let source = CGEventSource(stateID: .privateState) else { return }
            self.lock.lock()
            let keys = self.injectedKeys, buttons = self.injectedButtons
            self.injectedKeys.removeAll(); self.injectedButtons.removeAll()
            self.lock.unlock()
            for vk in keys {
                guard let code = MacKeyMap.toMac[vk], let cg = CGEvent(keyboardEventSource: source, virtualKey: code, keyDown: false) else { continue }
                cg.setIntegerValueField(.eventSourceUserData, value: self.marker); cg.post(tap: .cghidEventTap)
            }
            for button in buttons {
                guard let here = CGEvent(source: source) else { continue }
                let mouseButton: CGMouseButton = button == 0 ? .left : button == 1 ? .right : .center
                let type: CGEventType = button == 0 ? .leftMouseUp : button == 1 ? .rightMouseUp : .otherMouseUp
                if let cg = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: here.location, mouseButton: mouseButton) {
                    cg.setIntegerValueField(.eventSourceUserData, value: self.marker); cg.post(tap: .cghidEventTap)
                }
            }
        }
    }
}

/// Pure mapping used for every posted event, since CGEventSource(.privateState) is new each time.
enum InjectedModifierState {
    static func flags(for pressed: Set<Int>) -> CGEventFlags {
        var flags: CGEventFlags = []
        if pressed.contains(160) || pressed.contains(161) { flags.insert(.maskShift) }
        if pressed.contains(162) || pressed.contains(163) { flags.insert(.maskControl) }
        if pressed.contains(164) || pressed.contains(165) { flags.insert(.maskAlternate) }
        if pressed.contains(91) || pressed.contains(92) { flags.insert(.maskCommand) }
        return flags
    }
}

private enum MacKeyMap {
    // Apple virtual keycodes -> Windows VK. Physical US-layout positions, including both-side modifiers.
    static let toVK: [CGKeyCode: Int] = [
        0:65, 1:83, 2:68, 3:70, 4:72, 5:71, 6:90, 7:88, 8:67, 9:86, 11:66, 12:81, 13:87, 14:69, 15:82, 16:89, 17:84, 31:79, 32:85, 34:73, 35:80, 37:76, 38:74, 40:75, 45:78, 46:77,
        18:49, 19:50, 20:51, 21:52, 23:53, 22:54, 26:55, 28:56, 25:57, 29:48,
        24:187, 27:189, 30:221, 33:219, 39:222, 41:186, 42:220, 43:188, 44:191, 47:190, 50:192,
        36:13, 48:9, 49:32, 51:8, 53:27, 117:46, 115:36, 119:35, 116:33, 121:34,
        123:37, 124:39, 125:40, 126:38,
        55:91, 54:92, 56:160, 60:161, 59:162, 62:163, 58:164, 61:165,
        122:112, 120:113, 99:114, 118:115, 96:116, 97:117, 98:118, 100:119, 101:120, 109:121, 103:122, 111:123,
        82:96, 83:97, 84:98, 85:99, 86:100, 87:101, 88:102, 89:103, 91:104, 92:105, 65:110, 67:106, 69:107, 75:111, 78:109, 76:13
    ]
    static let toMac: [Int: CGKeyCode] = {
        var inverse: [Int: CGKeyCode] = [:]
        for (key, vk) in toVK where inverse[vk] == nil { inverse[vk] = key }
        return inverse
    }()
}
