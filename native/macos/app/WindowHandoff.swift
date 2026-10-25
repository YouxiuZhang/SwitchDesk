import AppKit
import ApplicationServices

/// Runs independently of keyboard, network and DDC work. Never activates an app or changes Spaces.
final class WindowHandoff {
    var onStatus: ((String) -> Void)?
    private let queue = DispatchQueue(label: "com.switchdesk.window-handoff", qos: .utility)
    private let lock = NSLock()
    private var gate = WindowHandoffGate()
    // Main-thread scheduling state.
    private var timer: Timer?
    private var screenObserver: NSObjectProtocol?
    private var inFlight = false
    private var lastDetail = ""
    // Worker-only state. Window references and geometry stay in memory.
    private struct WindowKey: Hashable {
        let pid: pid_t
        let element: AXUIElement
        static func == (lhs: Self, rhs: Self) -> Bool { lhs.pid == rhs.pid && CFEqual(lhs.element, rhs.element) }
        func hash(into hasher: inout Hasher) { hasher.combine(pid); hasher.combine(CFHash(element)) }
    }
    private var moved: [WindowKey: WindowRelocation] = [:]
    private var restoreRevision: UInt64?
    private var restoreDeadline = Date.distantPast
    private var nextApp = 0

    func start() {
        DispatchQueue.main.async { [weak self] in
            guard let self = self, self.timer == nil else { return }
            self.screenObserver = NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification,
                                                                         object: nil, queue: .main) { [weak self] _ in
                self?.change { $0.invalidateGeometry() }
            }
            self.timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in self?.scan() }
            self.scan()
        }
    }

    func stop() {
        setConnected(false)
        DispatchQueue.main.async { [weak self] in
            self?.timer?.invalidate(); self?.timer = nil
            if let observer = self?.screenObserver { NotificationCenter.default.removeObserver(observer) }
            self?.screenObserver = nil
        }
        queue.async { [weak self] in self?.moved.removeAll() }
    }

    func setConnected(_ value: Bool) { change { $0.setConnected(value) } }
    func setEnabled(_ value: Bool) { change { $0.setEnabled(value) } }
    func applyState(_ state: WireMessage) {
        change { $0.apply(target: state["target"] as? String ?? "unknown", switching: state["switching"] as? Bool ?? false) }
    }

    private func change(_ mutate: (inout WindowHandoffGate) -> Void) {
        lock.lock(); mutate(&gate); lock.unlock()
        DispatchQueue.main.async { [weak self] in self?.scan() }
    }

    private func snapshot() -> WindowHandoffGate {
        lock.lock(); defer { lock.unlock() }; return gate
    }

    private func allowed(_ state: WindowHandoffGate) -> Bool {
        snapshot().permits(state.revision, phase: state.phase) && AXIsProcessTrusted()
    }

    private func report(_ text: String, state: WindowHandoffGate) {
        DispatchQueue.main.async { [weak self] in
            guard let self = self, self.snapshot().permits(state.revision, phase: state.phase), self.lastDetail != text else { return }
            self.lastDetail = text; self.onStatus?(text)
        }
    }

    private func scan() {
        dispatchPrecondition(condition: .onQueue(.main))
        let state = snapshot()
        guard state.phase != .suspended, !inFlight else { return }
        guard AXIsProcessTrusted() else { report("需要辅助功能权限，才能收回外接屏上的窗口。", state: state); return }
        let nativeScreens = NSScreen.screens
        let unmirrored: [HandoffDisplay] = nativeScreens.compactMap { screen in
            guard let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else { return nil }
            let id = number.uint32Value
            guard CGDisplayIsInMirrorSet(id) == 0 else { return nil }
            let bounds = CGDisplayBounds(id)
            return HandoffDisplay(id: id, frame: bounds,
                                  visibleFrame: WindowHandoffPolicy.axVisibleFrame(displayBounds: bounds, appKitFrame: screen.frame, appKitVisible: screen.visibleFrame),
                                  isBuiltIn: CGDisplayIsBuiltin(id) != 0)
        }
        // Reject the entire topology when any display is mirrored or cannot be identified.
        let screens = unmirrored.count == nativeScreens.count ? unmirrored : []
        let apps = NSWorkspace.shared.runningApplications.filter { $0.activationPolicy == .regular && !$0.isHidden && !$0.isTerminated }
        let pids = apps.map { $0.processIdentifier }
        inFlight = true
        queue.async { [weak self] in
            guard let self = self else { return }
            defer { DispatchQueue.main.async { self.inFlight = false } }
            guard self.allowed(state) else { return }
            if state.phase == .gather { self.gather(pids: pids, screens: screens, state: state) }
            else { self.restore(pids: pids, screens: screens, state: state) }
        }
    }

    private func gather(pids: [pid_t], screens: [HandoffDisplay], state: WindowHandoffGate) {
        guard let pair = WindowHandoffPolicy.pair(screens) else {
            report("需要打开笔记本内屏，并连接一台使用扩展模式的外接屏。", state: state); return
        }
        guard !pids.isEmpty else { return }
        let deadline = Date().addingTimeInterval(1.5)
        // Rotate application order so a slow/unresponsive app cannot starve other apps.
        let ordered = (0..<pids.count).map { pids[(nextApp + $0) % pids.count] }
        nextApp = (nextApp + 1) % pids.count
        for pid in ordered {
            guard allowed(state), Date() < deadline else { break }
            let app = AXUIElementCreateApplication(pid)
            AXUIElementSetMessagingTimeout(app, 0.1)
            guard let windows = attribute(app, kAXWindowsAttribute) as? [AXUIElement] else { continue }
            for window in windows.prefix(128) {
                guard allowed(state), Date() < deadline else { break }
                let key = WindowKey(pid: pid, element: window)
                guard moved[key] == nil, moved.count < 1024, eligible(window), let original = frame(window),
                      let destination = WindowHandoffPolicy.gatherFrame(original, screens: screens) else { continue }
                setFrame(window, destination, state: state)
                // Read back what the app accepted, including partial moves during cancellation.
                if let actual = frame(window), !WindowHandoffPolicy.sameFrame(actual, original) {
                    moved[key] = WindowRelocation(original: original, placed: actual, source: pair.external)
                }
            }
        }
        report(moved.isEmpty ? "切到 Windows 时自动收回普通窗口；全屏和最小化窗口保持原状。"
               : "已处理 \(moved.count) 个窗口；切回 Mac 时恢复未手动移动的窗口。", state: state)
    }

    private func restore(pids: [pid_t], screens: [HandoffDisplay], state: WindowHandoffGate) {
        if restoreRevision != state.revision {
            restoreRevision = state.revision; restoreDeadline = Date().addingTimeInterval(15)
        }
        guard !moved.isEmpty else { report("切到 Windows 时，自动将外接屏窗口收回笔记本。", state: state); return }
        guard Date() < restoreDeadline else {
            moved.removeAll()
            report("部分窗口未能恢复，已保留当前位置。", state: state); return
        }
        guard WindowHandoffPolicy.pair(screens) != nil else {
            report("等待外接屏就绪后恢复窗口。", state: state); return
        }
        let visiblePids = Set(pids)
        let deadline = Date().addingTimeInterval(1.5)
        for (key, relocation) in moved {
            guard allowed(state), Date() < deadline else { break }
            guard visiblePids.contains(key.pid), eligible(key.element), let current = frame(key.element) else { continue }
            guard let destination = relocation.restoreFrame(current: current, screens: screens) else {
                // Respect the user's new position. A disconnected original display may return later.
                if !WindowHandoffPolicy.sameFrame(current, relocation.placed) { moved.removeValue(forKey: key) }
                continue
            }
            setFrame(key.element, destination, state: state)
            // A switch can cancel between position and size writes. Drop any changed placement
            // so the next Windows pass can gather a partially restored external window again.
            if let actual = frame(key.element), !WindowHandoffPolicy.sameFrame(actual, relocation.placed) || WindowHandoffPolicy.sameFrame(actual, destination) {
                moved.removeValue(forKey: key)
            }
        }
        report(moved.isEmpty ? "已恢复可还原的窗口；手动调整过的窗口保持当前位置。" : "正在恢复外接屏上的窗口。", state: state)
    }

    private func attribute(_ element: AXUIElement, _ name: String) -> CFTypeRef? {
        var value: CFTypeRef?
        return AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success ? value : nil
    }

    private func eligible(_ window: AXUIElement) -> Bool {
        AXUIElementSetMessagingTimeout(window, 0.1)
        guard attribute(window, kAXSubroleAttribute) as? String == kAXStandardWindowSubrole,
              attribute(window, kAXMinimizedAttribute) as? Bool == false,
              attribute(window, "AXFullScreen") as? Bool != true else { return false }
        var settable = DarwinBoolean(false)
        return AXUIElementIsAttributeSettable(window, kAXPositionAttribute as CFString, &settable) == .success && settable.boolValue
    }

    private func frame(_ window: AXUIElement) -> CGRect? {
        guard let p = attribute(window, kAXPositionAttribute), CFGetTypeID(p) == AXValueGetTypeID(),
              let s = attribute(window, kAXSizeAttribute), CFGetTypeID(s) == AXValueGetTypeID() else { return nil }
        var point = CGPoint.zero, size = CGSize.zero
        guard AXValueGetValue(p as! AXValue, .cgPoint, &point), AXValueGetValue(s as! AXValue, .cgSize, &size) else { return nil }
        let result = CGRect(origin: point, size: size)
        return WindowHandoffPolicy.valid(result) ? result : nil
    }

    private func setFrame(_ window: AXUIElement, _ rect: CGRect, state: WindowHandoffGate) {
        var point = rect.origin, size = rect.size
        // Moving before resizing also lets fixed-size windows bring their title bar onto the laptop.
        if allowed(state), let value = AXValueCreate(.cgPoint, &point) {
            AXUIElementSetAttributeValue(window, kAXPositionAttribute as CFString, value)
        }
        if allowed(state), let value = AXValueCreate(.cgSize, &size) {
            AXUIElementSetAttributeValue(window, kAXSizeAttribute as CFString, value)
        }
        // Some apps constrain position using the old size; place again after the resize.
        if allowed(state), let value = AXValueCreate(.cgPoint, &point) {
            AXUIElementSetAttributeValue(window, kAXPositionAttribute as CFString, value)
        }
    }
}
