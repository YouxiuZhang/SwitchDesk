import Foundation

/// Pure status presentation: reading this value never requests a system permission.
struct InputReadiness: Equatable {
    enum EventTap: String {
        case notInstalled = "未安装"
        case installing = "安装中"
        case installed = "已安装并启用"
        case failed = "安装失败"
        case disabled = "已安装但未启用"
    }

    let accessibility: Bool?
    let inputMonitoring: Bool?
    let eventTap: EventTap

    static let unchecked = InputReadiness(accessibility: nil, inputMonitoring: nil, eventTap: .notInstalled)
    var available: Bool { accessibility == true && inputMonitoring == true && eventTap == .installed }

    static func permissionLabel(_ granted: Bool?) -> String {
        guard let granted else { return "尚未检查" }
        return granted ? "已授权" : "未授权"
    }

    var detail: String {
        guard accessibility != nil, inputMonitoring != nil else { return "键鼠权限尚未检查" }
        let missing = [("辅助功能", accessibility), ("输入监听", inputMonitoring)]
            .filter { $0.1 != true }.map { $0.0 }
        if !missing.isEmpty { return "键鼠缺少：\(missing.joined(separator: "、"))。请在系统设置中允许双栖。事件钩子：\(eventTap.rawValue)。" }
        switch eventTap {
        case .installed: return "辅助功能与输入监听已授权，事件钩子已启用。"
        case .installing: return "两项权限已授权，事件钩子正在安装。"
        case .failed: return "两项权限已授权，但事件钩子安装失败。请刷新检测；若仍失败，请退出并重新打开双栖。"
        case .disabled: return "两项权限已授权，但事件钩子未启用。请刷新检测；若仍不可用，请退出并重新打开双栖。"
        case .notInstalled: return "两项权限已授权，事件钩子尚未安装。请刷新检测；若仍不可用，请退出并重新打开双栖。"
        }
    }
}

/// Only an active operation from Windows can be confirmed; preview frames are irrelevant.
struct DisplayObservation: Equatable {
    let target: String
    let operation: Int

    init?(connected: Bool, currentTarget: String, switching: Bool, pendingTarget: String, operation: Int) {
        guard connected, currentTarget == "unknown", !switching,
              pendingTarget == "mac" || pendingTarget == "windows", operation > 0 else { return nil }
        target = pendingTarget
        self.operation = operation
    }

    var targetName: String { target == "mac" ? "Mac" : "Windows" }
    var title: String { "AOC 已显示 \(targetName)，确认画面" }
    var message: [String: Any] { ["type": "display_observed", "target": target, "operation": operation] }
}
