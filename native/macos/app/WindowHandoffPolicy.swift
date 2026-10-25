import Foundation
import CoreGraphics

/// Geometry uses the same top-left global coordinates as Accessibility window frames.
struct HandoffDisplay {
    let id: UInt32
    let frame: CGRect
    let visibleFrame: CGRect
    let isBuiltIn: Bool
}

enum WindowHandoffPolicy {
    static func pair(_ screens: [HandoffDisplay]) -> (external: HandoffDisplay, builtIn: HandoffDisplay)? {
        guard screens.count == 2,
              let external = screens.first(where: { !$0.isBuiltIn }),
              let builtIn = screens.first(where: { $0.isBuiltIn }),
              valid(external.visibleFrame), valid(builtIn.visibleFrame) else { return nil }
        return (external, builtIn)
    }

    static func axVisibleFrame(displayBounds: CGRect, appKitFrame: CGRect, appKitVisible: CGRect) -> CGRect {
        CGRect(x: displayBounds.minX + appKitVisible.minX - appKitFrame.minX,
               y: displayBounds.minY + appKitFrame.maxY - appKitVisible.maxY,
               width: appKitVisible.width, height: appKitVisible.height)
    }

    static func valid(_ frame: CGRect) -> Bool {
        [frame.origin.x, frame.origin.y, frame.width, frame.height].allSatisfy { $0.isFinite }
            && frame.width > 0 && frame.height > 0
    }

    static func sameFrame(_ a: CGRect, _ b: CGRect) -> Bool {
        valid(a) && valid(b) && abs(a.minX - b.minX) <= 2 && abs(a.minY - b.minY) <= 2
            && abs(a.width - b.width) <= 2 && abs(a.height - b.height) <= 2
    }

    static func gatherFrame(_ frame: CGRect, screens: [HandoffDisplay]) -> CGRect? {
        guard valid(frame), let pair = pair(screens) else { return nil }
        func area(_ screen: HandoffDisplay) -> CGFloat {
            let intersection = frame.intersection(screen.frame)
            return intersection.isNull ? 0 : intersection.width * intersection.height
        }
        guard area(pair.external) > area(pair.builtIn) else { return nil }
        let source = pair.external.visibleFrame, destination = pair.builtIn.visibleFrame
        let size = CGSize(width: min(frame.width, destination.width), height: min(frame.height, destination.height))
        let x = destination.minX + (frame.midX - source.minX) / source.width * destination.width - size.width / 2
        let y = destination.minY + (frame.midY - source.minY) / source.height * destination.height - size.height / 2
        return fit(CGRect(origin: CGPoint(x: x, y: y), size: size), into: destination)
    }

    static func fit(_ frame: CGRect, into destination: CGRect) -> CGRect {
        let width = min(frame.width, destination.width), height = min(frame.height, destination.height)
        return CGRect(x: max(destination.minX, min(frame.minX, destination.maxX - width)),
                      y: max(destination.minY, min(frame.minY, destination.maxY - height)), width: width, height: height)
    }
}

struct WindowRelocation {
    let original: CGRect
    let placed: CGRect
    let source: HandoffDisplay

    func restoreFrame(current: CGRect, screens: [HandoffDisplay]) -> CGRect? {
        guard WindowHandoffPolicy.sameFrame(current, placed),
              let pair = WindowHandoffPolicy.pair(screens), pair.external.id == source.id else { return nil }
        // Account for a changed display arrangement without returning a window off-screen.
        let translated = original.offsetBy(dx: pair.external.visibleFrame.minX - source.visibleFrame.minX,
                                           dy: pair.external.visibleFrame.minY - source.visibleFrame.minY)
        return WindowHandoffPolicy.fit(translated, into: pair.external.visibleFrame)
    }
}

struct WindowHandoffGate {
    enum Phase { case suspended, gather, restore }
    private(set) var phase = Phase.suspended
    private(set) var revision: UInt64 = 0
    private var connected = false
    private var enabled = true
    private var target = "unknown"
    private var switching = false

    mutating func setConnected(_ value: Bool) {
        guard connected != value else { return }
        connected = value
        target = "unknown"
        update()
    }

    mutating func setEnabled(_ value: Bool) {
        enabled = value
        update()
    }

    mutating func apply(target: String, switching: Bool) {
        guard connected else { return }
        self.target = target
        self.switching = switching
        update()
    }

    func permits(_ generation: UInt64, phase: Phase) -> Bool {
        revision == generation && self.phase == phase
    }

    mutating func invalidateGeometry() { revision &+= 1 }

    private mutating func update() {
        let next: Phase = !connected || !enabled || switching ? .suspended
            : target == "windows" ? .gather : target == "mac" ? .restore : .suspended
        if next != phase { phase = next; revision &+= 1 }
    }
}
