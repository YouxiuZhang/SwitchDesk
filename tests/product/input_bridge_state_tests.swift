import Foundation

@main struct InputBridgeStateTests {
    static func main() {
        // Never start the bridge: no event tap, connection, or injected input is created.
        let bridge = InputBridge()
        var failures = [String]()
        func check(_ name: String, ready: Bool, armed: Bool?, switching: Bool, expected: Bool) {
            var state: WireMessage = ["epoch": 0, "target": "mac", "source": "windows",
                                      "ready": ready, "switching": switching]
            if let armed { state["armed"] = armed }
            bridge.applyState(state)
            let actual = Mirror(reflecting: bridge).children.first { $0.label == "ready" }?.value as? Bool
            if actual != expected { failures.append("\(name): expected input ready=\(expected), got \(String(describing: actual))") }
        }
        check("visual confirmation alone is disarmed", ready: true, armed: false, switching: false, expected: false)
        check("missing armed fails closed", ready: true, armed: nil, switching: false, expected: false)
        check("explicitly armed and ready permits input", ready: true, armed: true, switching: false, expected: true)
        check("switching closes input", ready: true, armed: true, switching: true, expected: false)
        check("not ready closes input", ready: false, armed: true, switching: false, expected: false)
        check("rearming permits input", ready: true, armed: true, switching: false, expected: true)
        check("removing armed revokes input", ready: true, armed: nil, switching: false, expected: false)
        if !failures.isEmpty {
            failures.forEach { print("FAIL: \($0)") }
            exit(1)
        }
        print("InputBridge armed-state tests passed (7 checks)")
    }
}
