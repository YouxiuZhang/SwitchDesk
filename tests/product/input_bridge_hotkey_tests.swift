// Concatenate this file after InputBridge.swift for compilation: the extension can
// exercise the real private callback without installing a tap or posting events.
import CoreGraphics
import Foundation

extension InputBridge {
    static func runHotkeyTests() {
        var permissionsGranted = true
        let bridge = InputBridge(hotkeyPermissionsGranted: { permissionsGranted })
        var switches = 0
        var emergencies = 0
        var failures = [String]()
        var checks = 0
        bridge.switchHotkey = { switches += 1 }
        bridge.emergencyHotkey = { emergencies += 1 }
        func check(_ condition: Bool, _ description: String) {
            checks += 1
            if !condition { failures.append(description) }
        }
        func key(_ code: CGKeyCode, down: Bool, flags: CGEventFlags = [], repeatKey: Bool = false,
                 injected: Bool = false) -> Bool {
            let event = CGEvent(source: nil)!
            event.type = down ? .keyDown : .keyUp
            event.setIntegerValueField(.keyboardEventKeycode, value: Int64(code))
            event.flags = flags
            event.setIntegerValueField(.keyboardEventAutorepeat, value: repeatKey ? 1 : 0)
            if injected { event.setIntegerValueField(.eventSourceUserData, value: bridge.marker) }
            return bridge.handle(down ? .keyDown : .keyUp, event) == nil
        }

        // Default bridge is disconnected, disarmed, uninstalled, and lease-expired.
        check(key(111, down: true), "F12 down is consumed before the forwarding gate")
        check(switches == 1, "F12 requests a switch even while normal input is disarmed")
        check(key(111, down: true, repeatKey: true), "F12 repeat is consumed")
        check(key(111, down: true), "duplicate F12 down is consumed")
        check(switches == 1, "held F12 requests only one switch")
        check(key(111, down: false), "F12 up is consumed")
        check(key(111, down: true, flags: [.maskSecondaryFn]), "Fn+F12 is recognized as F12")
        check(switches == 2, "a fresh F12 press requests the next switch")
        check(key(111, down: false), "Fn+F12 up is consumed even without the Fn flag")
        check(key(111, down: false), "orphan F12 up never reaches local or remote apps")
        check(key(111, down: true, repeatKey: true), "orphan F12 repeat is consumed")
        check(switches == 2, "orphan repeat never triggers a switch")
        _ = key(111, down: false)

        check(!key(111, down: true, injected: true), "injected F12 down passes through")
        check(switches == 2, "injected F12 never switches")
        _ = key(111, down: true)
        check(!key(111, down: false, injected: true), "injected F12 up passes through")
        _ = key(111, down: true)
        check(switches == 3, "injected up cannot reset a physical key's debounce")
        _ = key(111, down: false)

        let legacy: CGEventFlags = [.maskControl, .maskAlternate]
        check(!key(49, down: true, flags: legacy), "Ctrl+Option+Space is an ordinary key")
        check(!key(49, down: false, flags: legacy), "old switch shortcut release is ordinary input")
        check(switches == 3, "old shortcut no longer switches")
        check(key(53, down: true, flags: legacy), "Ctrl+Option+Esc remains the emergency shortcut")
        check(key(53, down: true, flags: legacy, repeatKey: true), "held emergency shortcut is consumed")
        check(emergencies == 1, "emergency action runs once per physical press")
        check(key(53, down: false), "emergency up is consumed after modifiers are released")
        check(!key(53, down: true), "plain Escape is ordinary input")
        _ = key(53, down: false)
        check(!key(0, down: true), "disarmed ordinary input stays local")

        // A global shortcut is independent of input consent, but never of TCC.
        permissionsGranted = false
        check(!key(111, down: true), "F12 stays local when system permissions are missing")
        check(!key(111, down: false), "F12 up stays local without system permissions")
        check(!key(53, down: true, flags: legacy), "emergency shortcut also respects system permissions")
        _ = key(53, down: false)
        check(switches == 3 && emergencies == 1, "denied permissions prevent hotkey actions")
        permissionsGranted = true
        _ = key(111, down: true)
        permissionsGranted = false
        check(!key(111, down: true, repeatKey: true), "permission revocation leaves F12 repeats local")
        permissionsGranted = true
        _ = key(111, down: true, repeatKey: true)
        check(switches == 4, "permission refresh cannot retrigger a held F12")
        _ = key(111, down: false)

        // Simulate only forwarding state; the event tap itself remains uninstalled.
        bridge.installed = true
        bridge.connected = true
        bridge.ready = true
        bridge.source = "mac"
        bridge.target = "windows"
        bridge.suspended = false
        bridge.leaseUntil = Date().addingTimeInterval(30)
        var packets = [WireMessage]()
        bridge.sendQueue.sync {}
        bridge.send = { packets.append($0) }
        check(key(111, down: true), "F12 is consumed with input forwarding armed")
        check(key(111, down: false), "F12 up cannot be forwarded to Windows")
        bridge.sendQueue.sync {}
        check(packets.isEmpty, "switch hotkey sends no keyboard input")
        check(switches == 5, "armed F12 still uses the same switch callback")
        permissionsGranted = false
        check(!key(111, down: true), "forwarding readiness cannot bypass hotkey permissions")
        _ = key(111, down: false)
        bridge.sendQueue.sync {}
        check(packets.isEmpty && switches == 5, "denied F12 is neither switched nor forwarded")
        permissionsGranted = true
        check(key(0, down: true), "ordinary armed input still forwards")
        check(key(49, down: true, flags: legacy), "old shortcut can be forwarded as ordinary input")
        bridge.sendQueue.sync {}
        let forwardedCodes = packets.compactMap { ($0["event"] as? WireMessage)?["code"] as? Int }
        check(forwardedCodes == [65, 32], "normal A and old shortcut Space retain normal forwarding")
        bridge.installed = false

        // The bridge has never started: no system hook, network, or injected key exists.
        bridge.sendQueue.sync {}
        bridge.injectQueue.sync {}
        check(bridge.tap == nil && bridge.tapThread == nil, "test never installs a system hook")
        check(bridge.injectedKeys.isEmpty && bridge.injectedButtons.isEmpty, "test never injects input")
        if !failures.isEmpty {
            failures.forEach { print("FAIL: \($0)") }
            exit(1)
        }
        print("InputBridge hotkey tests passed (\(checks) checks)")
    }
}

@main struct InputBridgeHotkeyTests {
    static func main() { InputBridge.runHotkeyTests() }
}
