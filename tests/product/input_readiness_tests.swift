import Foundation

@main
struct InputReadinessTests {
    static func main() {
        let missingAccessibility = InputReadiness(accessibility: false, inputMonitoring: true, eventTap: .notInstalled)
        precondition(!missingAccessibility.available)
        precondition(missingAccessibility.detail.contains("辅助功能"))
        precondition(!missingAccessibility.detail.contains("缺少：输入监听"))
        let missingMonitoring = InputReadiness(accessibility: true, inputMonitoring: false, eventTap: .notInstalled)
        precondition(missingMonitoring.detail.contains("输入监听"))
        let missingBoth = InputReadiness(accessibility: false, inputMonitoring: false, eventTap: .notInstalled)
        precondition(missingBoth.detail.contains("辅助功能、输入监听"))
        for state in [InputReadiness.EventTap.notInstalled, .installing, .failed, .disabled] {
            let status = InputReadiness(accessibility: true, inputMonitoring: true, eventTap: state)
            precondition(!status.available, "Only an enabled, installed hook is ready")
            precondition(status.detail.contains("事件钩子"))
        }
        precondition(InputReadiness(accessibility: true, inputMonitoring: true, eventTap: .installed).available)
        precondition(!InputReadiness.unchecked.available)
        precondition(InputReadiness.unchecked.detail.contains("尚未检查"))

        let allowed = DisplayObservation(connected: true, currentTarget: "unknown", switching: false,
                                         pendingTarget: "mac", operation: 7)
        precondition(allowed?.title == "AOC 已显示 Mac，确认画面")
        precondition(allowed?.message["type"] as? String == "display_observed")
        precondition(allowed?.message["target"] as? String == "mac")
        precondition(allowed?.message["operation"] as? Int == 7)
        let windows = DisplayObservation(connected: true, currentTarget: "unknown", switching: false,
                                         pendingTarget: "windows", operation: 8)
        precondition(windows?.title == "AOC 已显示 Windows，确认画面")
        for pending in ["", "unknown", "MAC", "preview"] {
            precondition(DisplayObservation(connected: true, currentTarget: "unknown", switching: false,
                                            pendingTarget: pending, operation: 7) == nil)
        }
        for operation in [0, -1] {
            precondition(DisplayObservation(connected: true, currentTarget: "unknown", switching: false,
                                            pendingTarget: "mac", operation: operation) == nil)
        }
        precondition(DisplayObservation(connected: false, currentTarget: "unknown", switching: false,
                                        pendingTarget: "mac", operation: 7) == nil)
        precondition(DisplayObservation(connected: true, currentTarget: "mac", switching: false,
                                        pendingTarget: "mac", operation: 7) == nil)
        precondition(DisplayObservation(connected: true, currentTarget: "unknown", switching: true,
                                        pendingTarget: "mac", operation: 7) == nil)
        print("Input readiness and display observation tests passed")
    }
}
