import Foundation
import CoreGraphics

@main struct WindowHandoffTests {
    static func near(_ a: CGRect, _ b: CGRect) -> Bool { WindowHandoffPolicy.sameFrame(a, b) }
    static func main() {
        let external = HandoffDisplay(id: 3, frame: CGRect(x: 0, y: 0, width: 1920, height: 1080),
                                      visibleFrame: CGRect(x: 0, y: 25, width: 1920, height: 1020), isBuiltIn: false)
        let laptop = HandoffDisplay(id: 1, frame: CGRect(x: 204, y: 1080, width: 1512, height: 982),
                                    visibleFrame: CGRect(x: 204, y: 1118, width: 1512, height: 920), isBuiltIn: true)
        let screens = [external, laptop]
        precondition(WindowHandoffPolicy.pair(screens)?.external.id == 3)
        precondition(WindowHandoffPolicy.pair([external]) == nil)
        precondition(WindowHandoffPolicy.pair([laptop]) == nil)
        let third = HandoffDisplay(id: 4, frame: CGRect(x: -1920, y: 0, width: 1920, height: 1080),
                                   visibleFrame: CGRect(x: -1920, y: 25, width: 1920, height: 1020), isBuiltIn: false)
        precondition(WindowHandoffPolicy.pair(screens + [third]) == nil)

        // AppKit's bottom-left coordinate system, even when the laptop is below the main display.
        let visible = WindowHandoffPolicy.axVisibleFrame(
            displayBounds: laptop.frame,
            appKitFrame: CGRect(x: 204, y: -982, width: 1512, height: 982),
            appKitVisible: CGRect(x: 204, y: -958, width: 1512, height: 920))
        precondition(near(visible, CGRect(x: 204, y: 1118, width: 1512, height: 920)))

        let original = CGRect(x: 300, y: 180, width: 900, height: 600)
        let moved = WindowHandoffPolicy.gatherFrame(original, screens: screens)!
        precondition(moved.size == original.size)
        precondition(laptop.visibleFrame.contains(moved))
        precondition(WindowHandoffPolicy.gatherFrame(moved, screens: screens) == nil)
        let huge = CGRect(x: 0, y: 25, width: 1900, height: 1000)
        let shrunk = WindowHandoffPolicy.gatherFrame(huge, screens: screens)!
        precondition(shrunk.width == 1512 && shrunk.height == 920)
        precondition(laptop.visibleFrame.contains(shrunk))
        let leftLaptop = HandoffDisplay(id: 1, frame: CGRect(x: -1512, y: -180, width: 1512, height: 982),
                                        visibleFrame: CGRect(x: -1512, y: -142, width: 1512, height: 920), isBuiltIn: true)
        precondition(leftLaptop.visibleFrame.contains(WindowHandoffPolicy.gatherFrame(original, screens: [external, leftLaptop])!))
        precondition(WindowHandoffPolicy.gatherFrame(CGRect(x: 0, y: 0, width: 0, height: 0), screens: screens) == nil)
        precondition(WindowHandoffPolicy.gatherFrame(CGRect(x: Double.nan, y: 0, width: 100, height: 100), screens: screens) == nil)
        precondition(WindowHandoffPolicy.gatherFrame(CGRect(x: 9000, y: 0, width: 100, height: 100), screens: screens) == nil)

        // A restore is conditional on the actual frame that the application accepted.
        let record = WindowRelocation(original: original, placed: moved, source: external)
        precondition(near(record.restoreFrame(current: moved, screens: screens)!, original))
        precondition(record.restoreFrame(current: moved.offsetBy(dx: 40, dy: 0), screens: screens) == nil)
        precondition(record.restoreFrame(current: CGRect(origin: moved.origin, size: CGSize(width: 800, height: 500)), screens: screens) == nil)
        precondition(record.restoreFrame(current: moved, screens: [laptop]) == nil)
        precondition(record.restoreFrame(current: moved, screens: [third, laptop]) == nil)
        let shiftedExternal = HandoffDisplay(id: 3, frame: external.frame.offsetBy(dx: 150, dy: -50),
                                            visibleFrame: external.visibleFrame.offsetBy(dx: 150, dy: -50), isBuiltIn: false)
        precondition(near(record.restoreFrame(current: moved, screens: [shiftedExternal, laptop])!, original.offsetBy(dx: 150, dy: -50)))

        var gate = WindowHandoffGate()
        gate.apply(target: "windows", switching: false)
        precondition(gate.phase == .suspended)
        gate.setConnected(true)
        // Reconnection needs a fresh authoritative state, not a stale pre-disconnect target.
        precondition(gate.phase == .suspended)
        gate.apply(target: "windows", switching: false)
        precondition(gate.phase == .gather)
        let first = gate.revision
        gate.apply(target: "windows", switching: false)
        precondition(gate.revision == first)
        gate.invalidateGeometry()
        precondition(gate.phase == .gather && !gate.permits(first, phase: .gather))
        gate.apply(target: "windows", switching: true)
        precondition(!gate.permits(first, phase: .gather))
        gate.apply(target: "mac", switching: false)
        precondition(gate.phase == .restore)
        let restoring = gate.revision
        gate.setConnected(false)
        precondition(!gate.permits(restoring, phase: .restore))
        gate.setConnected(true)
        precondition(gate.phase == .suspended)
        gate.apply(target: "windows", switching: false)
        let beforeDisable = gate.revision
        gate.setEnabled(false)
        precondition(!gate.permits(beforeDisable, phase: .gather))
        gate.setEnabled(true)
        precondition(gate.phase == .gather)
        gate.apply(target: "unknown", switching: false)
        precondition(gate.phase == .suspended)
        print("Window handoff policy tests passed")
    }
}
