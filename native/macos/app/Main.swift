import AppKit
import SwiftUI
import UniformTypeIdentifiers

private enum Palette {
    static let ink = Color(red: 36 / 255, green: 41 / 255, blue: 51 / 255)
    static let muted = Color(red: 123 / 255, green: 129 / 255, blue: 141 / 255)
    static let purple = Color(red: 101 / 255, green: 84 / 255, blue: 217 / 255)
    static let pale = Color(red: 240 / 255, green: 237 / 255, blue: 255 / 255)
    static let paper = Color(red: 251 / 255, green: 252 / 255, blue: 253 / 255)
    static let sidebar = Color(red: 246 / 255, green: 247 / 255, blue: 249 / 255)
    static let line = Color(red: 231 / 255, green: 233 / 255, blue: 238 / 255)
    static let hero = Color(red: 241 / 255, green: 239 / 255, blue: 251 / 255)
    static let heroLine = Color(red: 228 / 255, green: 223 / 255, blue: 245 / 255)
    static let green = Color(red: 39 / 255, green: 130 / 255, blue: 112 / 255)
}

final class DesktopModel: ObservableObject {
    @Published var connected = false
    @Published var connectionDetail = "尚未配对 Windows"
    @Published var pairedHost = ""
    @Published var target = "unknown"
    @Published var source = "windows"
    @Published var ready = false
    @Published var canSwitch = false
    @Published var canRecoverWindows = false
    @Published var recoveryDetail = ""
    @Published var pendingVisualTarget = ""
    @Published var visualConfirmationID = 0
    @Published var switchTimingDetail = ""
    @Published var inputDetail = ""
    @Published var remoteAppVersion = ""
    @Published var switching = false
    @Published var remoteControlArmed = false
    @Published var stateDetail = "等待 Windows 连接与显示器校准"
    @Published var previewImage: NSImage?
    @Published var previewLabel = "Windows 共享屏幕"
    @Published var previewDetail = "连接后显示另一台电脑的画面"
    @Published var previewLive = false
    @Published var previewEnabled = UserDefaults.standard.object(forKey: "previewEnabled") as? Bool ?? true
    @Published var clipboardEnabled = UserDefaults.standard.object(forKey: "clipboardEnabled") as? Bool ?? true
    @Published var shortcutMapping = UserDefaults.standard.object(forKey: "shortcutMapping") as? Bool ?? true
    @Published var capabilityInput = false
    @Published var capabilityCapture = false
    @Published var inputReadiness = InputReadiness.unchecked
    @Published var nativeStatus = "尚未检查系统权限"
    @Published var transferName = ""
    @Published var transferProgress = 0.0
    @Published var invitationInput = ""
    @Published var settingsMessage = ""
    @Published var page = 0

    // The isolated UI review never constructs services that read pairing or user data.
    let isUIReview: Bool
    lazy var coordinator = NativeCoordinator()
    lazy var session = DesktopSession()
    weak var window: NSWindow?
    private var lastPreview = Date.distantPast
    private var latestState: WireMessage = [:]
    private var emergencyEpoch: Int?
    private var subscribed = false
    private var timer: Timer?

    init(uiReview: Bool = false) {
        isUIReview = uiReview
        guard !uiReview else { return }
        pairedHost = session.pairedHost
        if session.hasPairing { connectionDetail = "已保存配对，等待连接 Windows" }
        session.onConnection = { [weak self] connected, detail in self?.connectionChanged(connected, detail: detail) }
        session.onState = { [weak self] state in self?.applyState(state) }
        session.onControl = { [weak self] message in self?.coordinator.receiveControl(message) }
        session.onMedia = { [weak self] message in self?.coordinator.receiveMedia(message) }
        session.onHeartbeat = { [weak self] in self?.coordinator.heartbeat() }
        session.onPreview = { [weak self] data, label in self?.receivePreview(data, label: label) }
        session.onPreviewUnavailable = { [weak self] detail in self?.clearPreview(detail) }
        coordinator.sendControl = { [weak self] message in self?.session.sendControl(message) }
        coordinator.sendMedia = { [weak self] message in self?.session.sendMedia(message) }
        coordinator.onStatus = { [weak self] text in DispatchQueue.main.async { self?.nativeStatus = text } }
        coordinator.onCapabilities = { [weak self] input, capture in
            DispatchQueue.main.async {
                guard let self = self else { return }
                self.capabilityInput = input; self.capabilityCapture = capture
                if self.connected { self.session.sendControl(["type": "capabilities", "input": input, "capture": capture]) }
            }
        }
        coordinator.onInputReadiness = { [weak self] readiness in
            DispatchQueue.main.async { self?.inputReadiness = readiness }
        }
        coordinator.onSwitch = { [weak self] in DispatchQueue.main.async { self?.switchTarget() } }
        coordinator.onEmergency = { [weak self] in DispatchQueue.main.async { self?.emergencyRecovery() } }
        coordinator.onTransfer = { [weak self] name, progress in
            DispatchQueue.main.async { self?.transferName = name; self?.transferProgress = max(0, min(1, progress)) }
        }
        coordinator.setClipboardEnabled(clipboardEnabled)
        coordinator.setShortcutMapping(shortcutMapping)
        coordinator.setCaptureAllowed(previewEnabled)
        coordinator.start()
        session.start()
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in self?.tick() }
    }

    func stop() {
        guard !isUIReview else { return }
        timer?.invalidate(); timer = nil
        coordinator.stop()
        session.stop()
    }

    private func connectionChanged(_ value: Bool, detail: String) {
        connected = value
        connectionDetail = detail
        if value {
            settingsMessage = "配对成功；在 Windows 确认显示器后即可切屏。键鼠共享单独授权。"
            coordinator.setConnected(true)
            coordinator.applyState(effectiveState(latestState))
            session.sendControl(["type": "capabilities", "input": capabilityInput, "capture": capabilityCapture])
            updateSubscription()
        } else {
            coordinator.setConnected(false)
            remoteControlArmed = false; emergencyEpoch = nil
            latestState = [:]
            target = "unknown"; source = "windows"; ready = false; canSwitch = false; canRecoverWindows = false; switching = false
            stateDetail = "连接中断，键鼠已收回本机"
            recoveryDetail = ""; remoteAppVersion = ""
            pendingVisualTarget = ""; visualConfirmationID = 0; switchTimingDetail = ""; inputDetail = ""
            clearPreview("连接后恢复实时预览")
            subscribed = false
        }
    }

    private func applyState(_ state: WireMessage) {
        latestState = state
        target = wireString(state, "target", "unknown")
        source = wireString(state, "source", "windows")
        ready = wireBool(state, "ready")
        canSwitch = wireBool(state, "can_switch", ready)
        canRecoverWindows = wireBool(state, "can_recover_windows")
        recoveryDetail = wireString(state, "recovery_detail").trimmingCharacters(in: .whitespacesAndNewlines)
        pendingVisualTarget = wireString(state, "pending_visual_target")
        visualConfirmationID = wireInt(state, "visual_confirmation_id")
        switchTimingDetail = wireString(state, "switch_timing_detail").trimmingCharacters(in: .whitespacesAndNewlines)
        inputDetail = wireString(state, "input_detail").trimmingCharacters(in: .whitespacesAndNewlines)
        remoteAppVersion = wireString(state, "app_version").trimmingCharacters(in: .whitespacesAndNewlines)
        switching = wireBool(state, "switching")
        stateDetail = wireString(state, "detail", ready ? "显示器与输入已就绪" : "等待 Windows 完成校准")
        let epoch = wireInt(state, "epoch")
        // Keep the emergency barrier even if an old disarmed state arrives late.
        remoteControlArmed = wireBool(state, "armed") && ready && !switching &&
            (emergencyEpoch.map { epoch > $0 } ?? true)
        coordinator.applyState(effectiveState(state))
    }

    private func disarmedState(_ state: WireMessage) -> WireMessage {
        var copy = state
        copy["ready"] = false
        return copy
    }

    private func effectiveState(_ state: WireMessage) -> WireMessage {
        remoteControlArmed ? state : disarmedState(state)
    }

    func switchTarget() {
        guard !isUIReview else { return }
        if target == "unknown", canRecoverWindows { restoreWindows(); return }
        guard connected, canSwitch, !switching else { return }
        session.sendControl(["type": "switch_request"])
    }

    func restoreWindows() {
        guard !isUIReview, connected, canRecoverWindows, !switching else { return }
        remoteControlArmed = false
        emergencyEpoch = wireInt(latestState, "epoch")
        coordinator.applyState(disarmedState(latestState))
        session.sendControl(["type": "restore_windows_request"])
        stateDetail = "已请求恢复 Windows 画面，键鼠保持本地"
    }

    func chooseSource(_ value: String) {
        guard !isUIReview, connected, ready, !switching, (value == "mac" || value == "windows") else { return }
        session.sendControl(["type": "source_request", "source": value])
    }

    var pendingObservation: DisplayObservation? {
        DisplayObservation(connected: connected, currentTarget: target, switching: switching,
                           pendingTarget: pendingVisualTarget, operation: visualConfirmationID)
    }

    func confirmObservedDisplay() {
        guard !isUIReview, let observation = pendingObservation else { return }
        session.sendControl(observation.message)
        stateDetail = "已提交 AOC 画面确认，等待 Windows 校验；确认后请选择键鼠来源。"
    }

    func emergencyRecovery() {
        guard !isUIReview else { return }
        remoteControlArmed = false
        emergencyEpoch = wireInt(latestState, "epoch")
        coordinator.receiveControl(["type": "emergency"])
        coordinator.applyState(disarmedState(latestState))
        session.sendControl(["type": "emergency", "source": "mac"])
        stateDetail = "紧急恢复已发出，键鼠已收回本机"
    }

    func saveInvitation() {
        guard !isUIReview else { return }
        do {
            try session.saveInvitation(invitationInput)
            pairedHost = session.pairedHost
            invitationInput = ""
            settingsMessage = "配对邀请已保存，正在连接 Windows。首次连接请在 Windows 窗口允许此 Mac。"
        } catch { settingsMessage = error.localizedDescription }
    }

    func openInvitationFile() {
        guard !isUIReview else { return }
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [UTType(filenameExtension: "switchdesk") ?? .plainText]
        panel.allowsOtherFileTypes = false
        panel.canChooseDirectories = false
        panel.begin { [weak self] result in
            guard result == .OK, let url = panel.url else { return }
            self?.loadInvitationFile(url)
        }
    }

    func loadInvitationFile(_ url: URL) {
        guard !isUIReview else { return }
        do {
            let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
            guard size > 0 && size <= 8192 else { throw SessionError.invalidInvitation }
            invitationInput = try String(contentsOf: url, encoding: .utf8)
            saveInvitation()
        } catch { settingsMessage = "无法读取邀请文件：\(error.localizedDescription)" }
    }

    func setClipboard(_ value: Bool) {
        clipboardEnabled = value
        guard !isUIReview else { return }
        UserDefaults.standard.set(value, forKey: "clipboardEnabled")
        coordinator.setClipboardEnabled(value)
        if !value { coordinator.cancelTransfer() }
    }
    func setShortcutMapping(_ value: Bool) {
        shortcutMapping = value
        guard !isUIReview else { return }
        UserDefaults.standard.set(value, forKey: "shortcutMapping")
        coordinator.setShortcutMapping(value)
    }
    func setPreview(_ value: Bool) {
        previewEnabled = value
        guard !isUIReview else { return }
        UserDefaults.standard.set(value, forKey: "previewEnabled")
        coordinator.setCaptureAllowed(value)
        if !value { clearPreview("预览已关闭") }
        updateSubscription()
    }
    func cancelTransfer() { if !isUIReview { coordinator.cancelTransfer() } }
    func requestInputPermissions() { if !isUIReview { coordinator.requestInputPermissions() } }
    func requestPreviewPermission() { if !isUIReview { coordinator.requestPreviewPermission() } }
    func openInputSettings(accessibility: Bool) {
        guard !isUIReview else { return }
        let pane = accessibility ? "Privacy_Accessibility" : "Privacy_ListenEvent"
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(pane)") {
            NSWorkspace.shared.open(url)
        }
    }
    func refreshPermissions() { if !isUIReview { coordinator.refreshPermissions() } }

    func updateSubscription() {
        guard !isUIReview else { return }
        let wants = connected && previewEnabled && window?.isVisible == true && window?.isMiniaturized == false && NSApp.isActive
        guard wants != subscribed else { return }
        subscribed = wants
        if connected { session.sendControl(["type": "preview_subscribe", "enabled": wants]) }
        if !wants { clearPreview(previewEnabled ? "打开窗口后恢复预览" : "预览已关闭") }
    }
    func windowHidden() {
        guard !isUIReview else { return }
        if subscribed && connected { session.sendControl(["type": "preview_subscribe", "enabled": false]) }
        subscribed = false
        clearPreview(previewEnabled ? "打开窗口后恢复预览" : "预览已关闭")
    }

    private func receivePreview(_ data: Data, label: String) {
        guard connected, subscribed, let image = NSImage(data: data) else { return }
        previewImage = image
        previewLabel = label
        previewDetail = "实时预览"
        lastPreview = Date()
        previewLive = true
    }
    private func clearPreview(_ detail: String) {
        previewImage = nil
        previewLive = false
        previewDetail = detail
        lastPreview = .distantPast
    }
    private func tick() {
        updateSubscription()
        if previewLive && Date().timeIntervalSince(lastPreview) > 2 {
            clearPreview("2 秒未收到新画面，预览已暂停")
        }
    }
}

private struct DeskCard<Content: View>: View {
    let content: Content
    var padding: CGFloat
    init(padding: CGFloat = 16, @ViewBuilder content: () -> Content) {
        self.padding = padding
        self.content = content()
    }
    var body: some View {
        content.padding(padding).frame(maxWidth: .infinity, alignment: .leading)
            .background(.white, in: RoundedRectangle(cornerRadius: 11))
            .overlay(RoundedRectangle(cornerRadius: 11).stroke(Palette.line, lineWidth: 1))
    }
}

private struct PrimaryButton: ViewModifier {
    func body(content: Content) -> some View {
        content.font(.system(size: 12, weight: .medium)).foregroundColor(.white)
            .padding(.horizontal, 15).padding(.vertical, 10)
            .background(Palette.purple, in: RoundedRectangle(cornerRadius: 8))
    }
}

private struct SecondaryButton: ViewModifier {
    func body(content: Content) -> some View {
        content.font(.system(size: 11)).foregroundColor(Palette.muted)
            .padding(.horizontal, 12).padding(.vertical, 8)
            .background(.white, in: RoundedRectangle(cornerRadius: 7))
            .overlay(RoundedRectangle(cornerRadius: 7).stroke(Palette.line))
    }
}

private struct DeskKey: View {
    let label: String
    var body: some View {
        Text(label).font(.system(size: 10)).foregroundStyle(Palette.muted)
            .padding(.horizontal, 5).padding(.vertical, 3)
            .background(.white.opacity(0.65), in: RoundedRectangle(cornerRadius: 4))
            .overlay(RoundedRectangle(cornerRadius: 4).stroke(Palette.heroLine))
    }
}

struct DesktopView: View {
    @ObservedObject var model: DesktopModel
    @State private var previewExpanded = false
    @State private var connectionExpanded = false

    var body: some View {
        HStack(spacing: 0) {
            sidebar
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    if model.page == 0 { dashboard }
                    else if model.page == 1 { clipboardPage }
                    else { settings }
                }
                .padding(.horizontal, 31).padding(.top, 27).padding(.bottom, 22)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .background(Palette.paper)
        }
        .foregroundStyle(Palette.ink)
        .frame(minWidth: 900, minHeight: 600)
        .preferredColorScheme(.light)
        .sheet(isPresented: $previewExpanded) { enlargedPreview }
    }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 11) {
                Image("SwitchDesk").resizable().scaledToFit().frame(width: 40, height: 40)
                VStack(alignment: .leading, spacing: 3) {
                    Text("双栖").font(.system(size: 18, weight: .bold)).tracking(2)
                    Text("SWITCHDESK").font(.system(size: 9)).tracking(1.7).foregroundStyle(Palette.muted)
                }
            }.padding(.horizontal, 10).padding(.bottom, 30)
            Text("工作空间").font(.system(size: 10)).tracking(1.4)
                .foregroundStyle(Palette.muted.opacity(0.7)).padding(.leading, 12).padding(.bottom, 11)
            VStack(spacing: 6) {
                nav("控制台", icon: "square.grid.2x2", page: 0)
                nav("剪贴板与文件", icon: "doc.on.clipboard", page: 1)
                nav("设置", icon: "gearshape", page: 2)
            }
            Spacer(minLength: 30)
            VStack(alignment: .leading, spacing: 7) {
                HStack(spacing: 8) {
                    Circle().fill(Palette.green).frame(width: 6, height: 6)
                    Text("本机 · MacBook").font(.system(size: 11))
                }.foregroundStyle(Palette.muted)
                Text("两台电脑，一个工作空间。")
                    .font(.system(size: 10)).foregroundStyle(Palette.muted.opacity(0.65))
            }.padding(.horizontal, 10)
        }
        .padding(.horizontal, 15).padding(.top, 25).padding(.bottom, 18)
        .frame(width: 188).frame(maxHeight: .infinity)
        .background(Palette.sidebar)
        .overlay(alignment: .trailing) { Rectangle().fill(Palette.line).frame(width: 1) }
    }

    private func nav(_ title: String, icon: String, page: Int) -> some View {
        Button { model.page = page } label: {
            HStack(spacing: 11) {
                Image(systemName: icon).font(.system(size: 16)).frame(width: 18)
                Text(title).font(.system(size: 12, weight: model.page == page ? .semibold : .regular))
                Spacer(minLength: 0)
                if page == 1 && !model.transferName.isEmpty {
                    Text("1").font(.system(size: 10)).padding(.horizontal, 5).padding(.vertical, 1)
                        .background(.white, in: RoundedRectangle(cornerRadius: 5))
                }
            }
            .foregroundStyle(model.page == page ? Palette.purple : Palette.muted)
            .padding(.horizontal, 13).padding(.vertical, 11)
            .background(model.page == page ? Color(red: 234 / 255, green: 231 / 255, blue: 250 / 255) : Color.clear,
                        in: RoundedRectangle(cornerRadius: 9))
        }.buttonStyle(.plain)
    }

    private func heading(_ eyebrow: String, title: String, connection: Bool = false) -> some View {
        HStack(alignment: .center) {
            VStack(alignment: .leading, spacing: 4) {
                Text(eyebrow).font(.system(size: 10)).tracking(1.5).foregroundStyle(Palette.muted.opacity(0.8))
                Text(title).font(.system(size: 23, weight: .semibold)).tracking(-0.6)
            }
            Spacer()
            if connection {
                HStack(spacing: 7) {
                    Circle().fill(model.connected ? Palette.green : Palette.muted.opacity(0.5)).frame(width: 6, height: 6)
                    Text(model.connected ? "设备已连接" : "等待连接").font(.system(size: 11))
                }
                .foregroundStyle(Palette.muted).padding(.horizontal, 9).padding(.vertical, 6)
                .background(.white, in: RoundedRectangle(cornerRadius: 7))
                .overlay(RoundedRectangle(cornerRadius: 7).stroke(Palette.line))
            }
        }.padding(.bottom, 22)
    }

    private var dashboard: some View {
        VStack(alignment: .leading, spacing: 0) {
            heading("YOUR CONNECTED DESK", title: "两台电脑，一个工作空间。", connection: true)
            hero
            HStack {
                Text("我的设备").font(.system(size: 12, weight: .semibold))
                Spacer()
                Text("同一工作空间中的两台电脑").font(.system(size: 10)).foregroundStyle(Palette.muted.opacity(0.8))
            }.padding(.top, 22).padding(.bottom, 10)
            HStack(spacing: 12) {
                deviceCard("MacBook", key: "mac", icon: "laptopcomputer", detail: "本机")
                deviceCard("Windows", key: "windows", icon: "display", detail: model.connected ? "已连接" : "等待连接")
            }
            sourcePanel.padding(.top, 16)
            clipboardPanel.padding(.top, 16)
            HStack(alignment: .top) {
                Text(model.connectionDetail).font(.system(size: 10)).foregroundStyle(Palette.muted.opacity(0.85))
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 12)
                Button("连接与设置 ↗") { model.page = 2 }
                    .buttonStyle(.plain).font(.system(size: 10)).foregroundStyle(Palette.purple.opacity(0.7))
            }.padding(.top, 15)
        }
    }

    private var hero: some View {
        HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 0) {
                HStack(spacing: 7) {
                    Circle().fill(Palette.purple.opacity(0.75)).frame(width: 6, height: 6)
                    Text("当前控制").font(.system(size: 11))
                }.foregroundStyle(Palette.purple.opacity(0.75))
                Text(targetName).font(.system(size: 32, weight: .semibold)).tracking(-1)
                    .padding(.top, 4).padding(.bottom, 6)
                Text(model.switching ? "正在切换，请稍候…" : model.stateDetail)
                    .font(.system(size: 11)).foregroundStyle(Palette.muted)
                    .fixedSize(horizontal: false, vertical: true).padding(.bottom, 20)
                Button(action: model.switchTarget) {
                    Label(switchTitle, systemImage: "arrow.left.arrow.right")
                }
                .buttonStyle(.plain).modifier(PrimaryButton())
                .disabled(!canSwitch).opacity(canSwitch ? 1 : 0.45)
                if let observation = model.pendingObservation {
                    observedDisplayConfirmation(observation).padding(.top, 12)
                }
                if !model.switchTimingDetail.isEmpty {
                    Text("\(model.switchTimingDetail) · 软件阶段耗时，非黑屏实测")
                        .font(.system(size: 10)).foregroundStyle(Palette.muted)
                        .fixedSize(horizontal: false, vertical: true).padding(.top, 10)
                }
                if model.connected && model.target == "unknown" && !model.canRecoverWindows {
                    Text(model.recoveryDetail.isEmpty ? "Windows 未提供恢复原因，请在 Windows 检查显示器连接与恢复设置。" : model.recoveryDetail)
                        .font(.system(size: 11)).foregroundStyle(Palette.muted)
                        .fixedSize(horizontal: false, vertical: true).padding(.top, 10)
                }
                HStack(spacing: 4) {
                    Text("或按").font(.system(size: 10)).foregroundStyle(Palette.muted.opacity(0.75))
                    DeskKey(label: "F12")
                }.padding(.top, 12)
                Text("若 F12 控制音量，请按 Fn+F12")
                    .font(.system(size: 10)).foregroundStyle(Palette.muted)
                    .fixedSize(horizontal: false, vertical: true).padding(.top, 5)
                if model.connected && model.canRecoverWindows && model.target != "unknown" {
                    Button("恢复 Windows 画面") { model.restoreWindows() }
                        .buttonStyle(.plain).font(.system(size: 10)).foregroundStyle(Palette.purple)
                        .disabled(model.switching).padding(.top, 9)
                }
                if model.connected && !model.capabilityInput {
                    Button("完成键鼠授权 ↗") { model.page = 2 }
                        .buttonStyle(.plain).font(.system(size: 10)).foregroundStyle(Palette.purple).padding(.top, 9)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.leading, 27).padding(.vertical, 27)
            monitorPreview.frame(maxWidth: .infinity).padding(.trailing, 20).padding(.vertical, 22)
        }
        .frame(minHeight: 246)
        .background(Palette.hero, in: RoundedRectangle(cornerRadius: 15))
        .overlay(RoundedRectangle(cornerRadius: 15).stroke(Palette.heroLine))
    }

    private var canSwitch: Bool {
        model.connected && (model.canSwitch || (model.target == "unknown" && model.canRecoverWindows)) && !model.switching
    }

    private func observedDisplayConfirmation(_ observation: DisplayObservation) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Button(observation.title, action: model.confirmObservedDisplay)
                .buttonStyle(.plain).modifier(SecondaryButton()).foregroundStyle(Palette.purple)
            Text("仅当外接 AOC 确实显示 \(observation.targetName) 桌面时点击。应用内预览不能证明 AOC 已切换；确认后再选择键鼠来源。")
                .font(.system(size: 10)).foregroundStyle(Palette.muted)
                .fixedSize(horizontal: false, vertical: true)
        }
    }
    private var switchTitle: String {
        if model.target == "unknown" { return "恢复 Windows 画面" }
        return model.target == "mac" ? "切换到 Windows" : "切换到 Mac"
    }
    private var targetName: String {
        switch model.target { case "mac": return "MacBook"; case "windows": return "Windows"; default: return "尚未确认" }
    }

    private var monitorPreview: some View {
        ZStack {
            Circle().stroke(Palette.heroLine, lineWidth: 1).frame(width: 230, height: 230)
            Circle().stroke(Palette.heroLine, lineWidth: 1).frame(width: 194, height: 194)
            VStack(spacing: 7) {
                HStack(spacing: 4) {
                    Text(model.previewLabel).font(.system(size: 10, weight: .medium)).lineLimit(1)
                    Spacer(minLength: 4)
                    Circle().fill(model.previewLive ? Palette.green : Palette.muted.opacity(0.5)).frame(width: 4, height: 4)
                    Text(model.previewLive ? "实时预览" : "预览暂停").font(.system(size: 8))
                }.foregroundStyle(Palette.muted)
                VStack(spacing: 0) {
                    previewSurface(large: false)
                        .frame(width: 226, height: 127).clipShape(RoundedRectangle(cornerRadius: 4))
                        .padding(.horizontal, 6).padding(.top, 6).padding(.bottom, 10)
                        .background(Color(red: 46 / 255, green: 48 / 255, blue: 64 / 255), in: RoundedRectangle(cornerRadius: 9))
                        .overlay(alignment: .bottom) {
                            Capsule().fill(.white.opacity(0.2)).frame(width: 14, height: 2).padding(.bottom, 4)
                        }
                        .shadow(color: Palette.purple.opacity(0.13), radius: 13, y: 9)
                    Rectangle().fill(Color(red: 183 / 255, green: 180 / 255, blue: 201 / 255)).frame(width: 31, height: 13)
                    Capsule().fill(Color(red: 187 / 255, green: 184 / 255, blue: 204 / 255)).frame(width: 88, height: 5)
                }
                HStack(spacing: 4) {
                    Text("另一台电脑的桌面").font(.system(size: 8)).foregroundStyle(Palette.muted)
                    Spacer(minLength: 3)
                    previewAction(model.previewEnabled ? "暂停预览" : "开启预览") { model.setPreview(!model.previewEnabled) }
                    previewAction("放大") { previewExpanded = true }
                }
            }.frame(width: 238)
        }
    }

    private func previewAction(_ title: String, action: @escaping () -> Void) -> some View {
        Button(title, action: action).buttonStyle(.plain).font(.system(size: 8))
            .foregroundStyle(Palette.purple.opacity(0.75)).padding(.horizontal, 6).padding(.vertical, 3)
            .background(.white.opacity(0.65), in: RoundedRectangle(cornerRadius: 4))
            .overlay(RoundedRectangle(cornerRadius: 4).stroke(Palette.heroLine))
    }

    @ViewBuilder private func previewSurface(large: Bool) -> some View {
        ZStack {
            Color(red: 233 / 255, green: 231 / 255, blue: 239 / 255)
            if let image = model.previewImage, model.previewLive, model.previewEnabled {
                Color.black
                Image(nsImage: image).resizable().scaledToFit()
            } else {
                VStack(spacing: large ? 12 : 7) {
                    Image(systemName: model.previewEnabled ? "display" : "pause.circle")
                        .font(.system(size: large ? 32 : 21, weight: .light))
                    Text(model.previewEnabled ? (model.connected ? "画面暂不可用" : "等待另一台电脑") : "桌面预览已关闭")
                        .font(.system(size: large ? 17 : 10, weight: .medium))
                    Text(model.previewEnabled ? model.previewDetail : "开启预览后显示另一台电脑的画面")
                        .font(.system(size: large ? 12 : 8)).foregroundStyle(Palette.muted)
                        .multilineTextAlignment(.center).fixedSize(horizontal: false, vertical: true)
                }.foregroundStyle(Palette.muted).padding(large ? 24 : 10)
            }
        }
    }

    private var enlargedPreview: some View {
        VStack(alignment: .leading, spacing: 15) {
            HStack {
                VStack(alignment: .leading, spacing: 5) {
                    Text("\(model.previewLabel) · 桌面预览").font(.system(size: 17, weight: .semibold))
                    Text(model.previewLive ? "实时画面" : model.previewDetail).font(.system(size: 11)).foregroundStyle(Palette.muted)
                }
                Spacer()
                Button { previewExpanded = false } label: { Image(systemName: "xmark") }
                    .buttonStyle(.plain).keyboardShortcut(.cancelAction).foregroundStyle(Palette.muted)
            }
            previewSurface(large: true).frame(width: 736, height: 414).clipShape(RoundedRectangle(cornerRadius: 10))
            Text("用于查看 · 操作电脑请使用主界面的切换按钮").font(.system(size: 10)).foregroundStyle(Palette.muted)
        }.padding(22).frame(width: 780).background(Palette.paper)
    }

    private func deviceCard(_ title: String, key: String, icon: String, detail: String) -> some View {
        let active = model.connected && model.target == key
        let badge = active ? "正在控制" : key == "mac" ? "本机" : model.connected ? "待切换" : "未连接"
        return HStack(spacing: 12) {
            Image(systemName: icon).font(.system(size: 25, weight: .light)).foregroundStyle(Palette.muted)
                .frame(width: 41, height: 41).background(Palette.sidebar, in: RoundedRectangle(cornerRadius: 9))
            VStack(alignment: .leading, spacing: 4) {
                Text(title).font(.system(size: 13, weight: .semibold))
                HStack(spacing: 4) {
                    Image(systemName: key == "mac" ? "laptopcomputer" : "link").font(.system(size: 9))
                    Text(detail).font(.system(size: 10))
                }.foregroundStyle(Palette.muted)
            }
            Spacer(minLength: 5)
            Text(badge).font(.system(size: 9)).foregroundStyle(active ? Palette.purple : Palette.muted)
                .padding(.horizontal, 7).padding(.vertical, 4)
                .background(active ? Palette.pale : Palette.sidebar, in: RoundedRectangle(cornerRadius: 5))
        }
        .padding(.horizontal, 15).padding(.vertical, 14).frame(maxWidth: .infinity, minHeight: 76, alignment: .leading)
        .background(.white, in: RoundedRectangle(cornerRadius: 11))
        .overlay(RoundedRectangle(cornerRadius: 11).stroke(active ? Palette.purple.opacity(0.35) : Palette.line))
    }

    private var sourcePanel: some View {
        DeskCard {
            VStack(alignment: .leading, spacing: 0) {
                HStack {
                    Label("键鼠来源", systemImage: "keyboard").font(.system(size: 12, weight: .semibold))
                    Spacer()
                    Text("切换屏幕时，来源保持不变").font(.system(size: 10)).foregroundStyle(Palette.muted.opacity(0.75))
                }.padding(.bottom, 13)
                HStack(spacing: 10) {
                    sourceButton("MacBook", subtitle: "使用键盘与触控板", value: "mac")
                    sourceButton("Windows", subtitle: "使用连接的键盘与鼠标", value: "windows")
                }
                Text(model.inputReadiness.detail)
                    .font(.system(size: 10)).foregroundStyle(Palette.muted)
                    .fixedSize(horizontal: false, vertical: true).padding(.top, 10)
                if !model.capabilityInput {
                    Button("检查键鼠权限与事件钩子 ↗") { model.page = 2 }
                        .buttonStyle(.plain).font(.system(size: 10)).foregroundStyle(Palette.purple).padding(.top, 6)
                }
                HStack(spacing: 10) {
                    Text(sourceHint).font(.system(size: 10)).foregroundStyle(Palette.muted.opacity(0.8))
                        .fixedSize(horizontal: false, vertical: true)
                    Spacer(minLength: 0)
                    if model.connected {
                        Button("收回键鼠") { model.emergencyRecovery() }
                            .buttonStyle(.plain).font(.system(size: 10)).foregroundStyle(Palette.purple)
                    }
                }.padding(.top, 10)
            }
        }
    }

    private var sourceHint: String {
        if !model.inputDetail.isEmpty { return model.inputDetail }
        if model.pendingObservation != nil { return "先核对外接 AOC 的实际画面并确认，再选择键鼠来源。" }
        if !model.ready { return "连接并完成显示器确认后，可选择键鼠来源" }
        if !model.capabilityInput { return "本机键鼠尚未就绪，请先处理上方缺项并刷新检测。" }
        if !model.remoteControlArmed { return "远控已暂停 · 点击来源设备以手动恢复" }
        return "正在使用 \(model.source == "mac" ? "MacBook 的键盘与触控板" : "Windows 的键鼠") · 来源设备需保持唤醒"
    }

    private func sourceButton(_ title: String, subtitle: String, value: String) -> some View {
        let selected = model.source == value
        return Button { model.chooseSource(value) } label: {
            HStack(spacing: 9) {
                ZStack {
                    Circle().stroke(selected ? Palette.purple : Palette.muted.opacity(0.4), lineWidth: 1).frame(width: 14, height: 14)
                    if selected { Circle().fill(Palette.purple).frame(width: 6, height: 6) }
                }
                VStack(alignment: .leading, spacing: 3) {
                    Text(title).font(.system(size: 11, weight: .medium)).foregroundStyle(Palette.ink)
                    Text(subtitle).font(.system(size: 10)).foregroundStyle(Palette.muted)
                }
                Spacer(minLength: 0)
            }.padding(.horizontal, 12).padding(.vertical, 10).frame(maxWidth: .infinity, alignment: .leading)
                .background(selected ? Color(red: 245 / 255, green: 242 / 255, blue: 255 / 255) : Palette.paper,
                            in: RoundedRectangle(cornerRadius: 8))
                .overlay(RoundedRectangle(cornerRadius: 8).stroke(selected ? Palette.purple.opacity(0.3) : Palette.line))
        }.buttonStyle(.plain).disabled(!model.connected || !model.ready || model.switching)
    }

    private var clipboardPanel: some View {
        DeskCard {
            VStack(alignment: .leading, spacing: 0) {
                settingToggle("共享剪贴板", icon: "doc.on.clipboard", detail: model.clipboardEnabled ? "文字、图片、文件和文件夹 · 双向共享" : "共享已关闭 · 不再同步新内容",
                              value: model.clipboardEnabled, action: model.setClipboard)
                Rectangle().fill(Palette.line.opacity(0.65)).frame(height: 1).padding(.vertical, 13)
                transferRow
            }
        }
    }

    @ViewBuilder private var transferRow: some View {
        if !model.transferName.isEmpty {
            HStack(spacing: 12) {
                Image(systemName: "folder").font(.system(size: 19)).foregroundStyle(Color(red: 0.7, green: 0.57, blue: 0.27))
                    .frame(width: 34, height: 34).background(Color(red: 1, green: 0.97, blue: 0.91), in: RoundedRectangle(cornerRadius: 8))
                VStack(alignment: .leading, spacing: 5) {
                    HStack {
                        Text(model.transferName).lineLimit(1)
                        Spacer()
                        Text("\(Int(model.transferProgress * 100))%")
                    }.font(.system(size: 11))
                    ProgressView(value: model.transferProgress).tint(Palette.purple).controlSize(.mini)
                }
                Button { model.cancelTransfer() } label: { Image(systemName: "xmark").font(.system(size: 11)) }
                    .buttonStyle(.plain).foregroundStyle(Palette.muted).help("取消文件传输")
            }
        } else {
            HStack(spacing: 10) {
                Image(systemName: "folder").font(.system(size: 16)).foregroundStyle(Palette.muted.opacity(0.65))
                Text("当前没有文件传输").font(.system(size: 10)).foregroundStyle(Palette.muted)
                Spacer()
                Text("粘贴文件时按需传输").font(.system(size: 9)).foregroundStyle(Palette.muted.opacity(0.7))
            }.frame(minHeight: 27)
        }
    }

    private var clipboardPage: some View {
        VStack(alignment: .leading, spacing: 0) {
            heading("SHARED BETWEEN DEVICES", title: "复制在这里，粘贴在那里。")
            clipboardPanel
            DeskCard(padding: 21) {
                VStack(alignment: .leading, spacing: 14) {
                    Text("在两台电脑之间共享").font(.system(size: 14, weight: .semibold))
                    Text("连接 Windows 后，在任一电脑复制，再在另一台粘贴。文件和文件夹会在粘贴时传输。")
                        .font(.system(size: 11)).foregroundStyle(Palette.muted).lineSpacing(4)
                    HStack(spacing: 8) {
                        ForEach(["文字", "图片", "文件", "文件夹"], id: \.self) { format in
                            Text(format).font(.system(size: 10)).foregroundStyle(Palette.purple.opacity(0.8))
                                .padding(.horizontal, 9).padding(.vertical, 5)
                                .background(Palette.pale, in: RoundedRectangle(cornerRadius: 5))
                        }
                    }
                    Text(model.connected ? "已连接 Windows，可同步新复制的内容。" : "等待连接 Windows 后开始共享。")
                        .font(.system(size: 11)).foregroundStyle(Palette.muted)
                        .padding(16).frame(maxWidth: .infinity, alignment: .leading)
                        .background(Palette.sidebar, in: RoundedRectangle(cornerRadius: 8))
                }
            }.padding(.top, 16)
            Button("前往连接与设置 ↗") { model.page = 2 }
                .buttonStyle(.plain).font(.system(size: 11)).foregroundStyle(Palette.purple).padding(.top, 16)
        }
    }

    private var settings: some View {
        VStack(alignment: .leading, spacing: 0) {
            heading("MAKE IT YOURS", title: "按照你的习惯切换。")
            DeskCard(padding: 21) {
                VStack(alignment: .leading, spacing: 17) {
                    Text("快捷键").font(.system(size: 14, weight: .semibold))
                    HStack {
                        settingsCopy("切换电脑", detail: "若 F12 控制音量，请按 Fn+F12；也可在系统设置中将 F1、F2 等键设为标准功能键。")
                        Spacer()
                        DeskKey(label: "F12")
                    }
                    Divider()
                    HStack {
                        settingsCopy("紧急收回键鼠", detail: "立即收回键鼠，并尝试恢复本机画面")
                        Spacer()
                        HStack(spacing: 4) { DeskKey(label: "Ctrl"); DeskKey(label: "⌥"); DeskKey(label: "Esc") }
                    }
                }
            }
            connectionSettings.padding(.top, 16)
            DeskCard(padding: 21) {
                VStack(alignment: .leading, spacing: 16) {
                    Text("桌面预览").font(.system(size: 14, weight: .semibold))
                    settingToggle("另一台电脑的桌面", detail: "打开窗口时查看另一台电脑的实时画面。",
                                  value: model.previewEnabled, action: model.setPreview)
                    Divider()
                    HStack {
                        settingsCopy("共享 Mac 画面", detail: model.capabilityCapture ? "录屏权限已就绪。" : "将 Mac 桌面预览分享给 Windows 需要录屏权限。")
                        Spacer()
                        Button("申请预览录屏权限…", action: model.requestPreviewPermission)
                            .buttonStyle(.plain).modifier(SecondaryButton())
                    }
                }
            }.padding(.top, 16)
            DeskCard(padding: 21) {
                VStack(alignment: .leading, spacing: 17) {
                    Text("日常使用").font(.system(size: 14, weight: .semibold))
                    settingToggle("共享剪贴板", detail: "同步新复制的文字、图片、文件和文件夹。",
                                  value: model.clipboardEnabled, action: model.setClipboard)
                    Divider()
                    settingToggle("常用编辑快捷键映射", detail: "复制、粘贴、剪切、撤销、重做与全选适配目标系统。",
                                  value: model.shortcutMapping, action: model.setShortcutMapping)
                }
            }.padding(.top, 16)
        }
    }

    private var localAppVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "未知版本"
    }

    private var windowsVersionLabel: String {
        guard model.connected else { return "Windows 未连接" }
        return model.remoteAppVersion.isEmpty ? "Windows 旧版（未提供版本）" : "Windows \(model.remoteAppVersion)"
    }

    private var connectionSettings: some View {
        DeskCard(padding: 21) {
            VStack(alignment: .leading, spacing: 13) {
                HStack {
                    Text("设备连接").font(.system(size: 14, weight: .semibold))
                    Spacer()
                    Text(model.connected ? "已连接 Windows" : "等待连接")
                        .font(.system(size: 10)).foregroundStyle(model.connected ? Palette.green : Palette.muted)
                }
                Text("Mac \(localAppVersion) · \(windowsVersionLabel)")
                    .font(.system(size: 10)).foregroundStyle(Palette.muted)
                    .textSelection(.enabled)
                Text("在 Windows 上复制配对邀请，或导出 .switchdesk 文件。首次连接需在 Windows 窗口允许。")
                    .font(.system(size: 11)).foregroundStyle(Palette.muted).fixedSize(horizontal: false, vertical: true)
                TextField("粘贴 switchdesk1: 开头的邀请字符串", text: $model.invitationInput)
                    .textFieldStyle(.roundedBorder).font(.system(size: 11))
                HStack(spacing: 12) {
                    Button("保存并连接") { model.saveInvitation() }.buttonStyle(.plain).modifier(PrimaryButton())
                    Button("打开邀请文件…") { model.openInvitationFile() }.buttonStyle(.plain).modifier(SecondaryButton())
                    Spacer()
                    Text(model.pairedHost.isEmpty ? "尚未配对" : "已保存：\(model.pairedHost)")
                        .font(.system(size: 10)).foregroundStyle(Palette.muted)
                }
                if !model.settingsMessage.isEmpty {
                    Text(model.settingsMessage).font(.system(size: 11)).foregroundStyle(Palette.purple)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Divider()
                inputPermissionSettings
                DisclosureGroup("连接详情", isExpanded: $connectionExpanded) {
                    VStack(alignment: .leading, spacing: 10) {
                        Text(model.connectionDetail)
                        Text("显示器接线与切源确认在 Windows 端完成。Mac 和 Windows 需同时运行。")
                        Text(model.nativeStatus)
                        if !model.inputDetail.isEmpty { Text("键鼠诊断：\(model.inputDetail)") }
                        if !model.switchTimingDetail.isEmpty {
                            Text("最近切屏软件阶段耗时：\(model.switchTimingDetail)")
                            Text("这些时间来自切换指令、显示器通信和软件确认流程，不代表外接 AOC 的实际黑屏时长。")
                        }
                        if let observation = model.pendingObservation { observedDisplayConfirmation(observation) }
                        if model.connected && model.canRecoverWindows {
                            Button("恢复 Windows 画面") { model.restoreWindows() }
                                .disabled(model.switching).foregroundStyle(Palette.purple)
                        }
                    }.font(.system(size: 11)).foregroundStyle(Palette.muted)
                        .fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: .infinity, alignment: .leading).padding(.top, 10)
                }.font(.system(size: 11)).foregroundStyle(Palette.muted)
            }
        }
    }

    private var inputPermissionSettings: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("键鼠权限").font(.system(size: 12, weight: .medium))
                Spacer()
                Button("申请键鼠权限…", action: model.requestInputPermissions)
                    .buttonStyle(.plain).modifier(SecondaryButton())
                Button("刷新检测", action: model.refreshPermissions)
                    .buttonStyle(.plain).modifier(SecondaryButton())
            }
            inputPermissionRow("辅助功能", granted: model.inputReadiness.accessibility) {
                model.openInputSettings(accessibility: true)
            }
            inputPermissionRow("输入监听", granted: model.inputReadiness.inputMonitoring) {
                model.openInputSettings(accessibility: false)
            }
            HStack {
                Text("事件钩子").font(.system(size: 11))
                Spacer()
                Text(model.inputReadiness.eventTap.rawValue).font(.system(size: 11))
                    .foregroundStyle(model.inputReadiness.available ? Palette.green : Palette.muted)
            }
            Text(model.inputReadiness.detail).font(.system(size: 10)).foregroundStyle(Palette.muted)
                .fixedSize(horizontal: false, vertical: true)
            Text("系统设置中允许后，返回双栖会自动刷新；若仍显示未授权，请退出并重新打开双栖。")
                .font(.system(size: 10)).foregroundStyle(Palette.muted)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func inputPermissionRow(_ title: String, granted: Bool?, openSettings: @escaping () -> Void) -> some View {
        HStack {
            Text(title).font(.system(size: 11))
            Spacer()
            Text(InputReadiness.permissionLabel(granted)).font(.system(size: 11))
                .foregroundStyle(granted == true ? Palette.green : Palette.muted)
            Button("打开设置", action: openSettings).buttonStyle(.plain)
                .font(.system(size: 10)).foregroundStyle(Palette.purple)
        }
    }

    private func settingsCopy(_ title: String, detail: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.system(size: 12, weight: .medium))
            Text(detail).font(.system(size: 10)).foregroundStyle(Palette.muted)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func settingToggle(_ title: String, icon: String? = nil, detail: String, value: Bool, action: @escaping (Bool) -> Void) -> some View {
        HStack(spacing: 14) {
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 8) {
                    if let icon = icon { Image(systemName: icon).foregroundStyle(Palette.muted) }
                    Text(title)
                }.font(.system(size: 12, weight: .semibold))
                Text(detail).font(.system(size: 10)).foregroundStyle(Palette.muted)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
            Toggle(title, isOn: Binding(get: { value }, set: action))
                .toggleStyle(.switch).labelsHidden().tint(Palette.purple).controlSize(.small)
        }
    }
}

final class DesktopAppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    private var window: NSWindow!
    private var statusItem: NSStatusItem!
    private var model: DesktopModel!
    private var pendingInvitationURL: URL?

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        model = DesktopModel(uiReview: ProcessInfo.processInfo.arguments.contains("--ui-review") ||
                             Bundle.main.bundleIdentifier == "com.switchdesk.desktop.ui-review")
        let root = DesktopView(model: model)
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1120, height: 824),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable],
                          backing: .buffered, defer: false)
        window.title = model.isUIReview ? "双栖 · SwitchDesk · UI 核验" : "双栖 · SwitchDesk"
        window.contentView = NSHostingView(rootView: root)
        window.center()
        window.delegate = self
        model.window = window
        installMainMenu()
        window.makeKeyAndOrderFront(nil)
        if !model.isUIReview { installStatusMenu() }
        NSApp.activate(ignoringOtherApps: true)
        model.updateSubscription()
        if let url = pendingInvitationURL { model.loadInvitationFile(url); pendingInvitationURL = nil }
    }
    func application(_ sender: NSApplication, openFile filename: String) -> Bool {
        let url = URL(fileURLWithPath: filename)
        if let model = model { model.loadInvitationFile(url); showWindow(nil) }
        else { pendingInvitationURL = url }
        return true
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows: Bool) -> Bool {
        showWindow(nil); return true
    }
    func applicationWillTerminate(_ notification: Notification) {
        if !terminating { model?.stop() }
    }
    func windowWillClose(_ notification: Notification) { model?.windowHidden() }
    func windowDidMiniaturize(_ notification: Notification) { model?.windowHidden() }
    func windowDidDeminiaturize(_ notification: Notification) { model?.updateSubscription() }
    func applicationDidBecomeActive(_ notification: Notification) { model?.refreshPermissions(); model?.updateSubscription() }
    func applicationDidResignActive(_ notification: Notification) { model?.windowHidden() }

    private var terminating = false

    private func installMainMenu() {
        let main = NSMenu()
        let appItem = NSMenuItem(title: "双栖", action: nil, keyEquivalent: "")
        let appMenu = NSMenu(title: "双栖")
        appItem.submenu = appMenu
        main.addItem(appItem)

        let about = NSMenuItem(title: "关于双栖", action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        about.target = NSApp
        appMenu.addItem(about)
        appMenu.addItem(.separator())
        let hide = NSMenuItem(title: "隐藏双栖", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        hide.target = NSApp
        appMenu.addItem(hide)
        appMenu.addItem(.separator())
        let quit = NSMenuItem(title: "退出双栖", action: #selector(quitFromMenu(_:)), keyEquivalent: "q")
        quit.target = self
        appMenu.addItem(quit)

        let editItem = NSMenuItem(title: "编辑", action: nil, keyEquivalent: "")
        let editMenu = NSMenu(title: "编辑")
        editItem.submenu = editMenu
        main.addItem(editItem)
        func addEdit(_ title: String, _ action: String, _ key: String, shift: Bool = false) {
            let item = NSMenuItem(title: title, action: Selector(action), keyEquivalent: key)
            if shift { item.keyEquivalentModifierMask = [.command, .shift] }
            editMenu.addItem(item)
        }
        addEdit("撤销", "undo:", "z")
        addEdit("重做", "redo:", "z", shift: true)
        editMenu.addItem(.separator())
        addEdit("剪切", "cut:", "x")
        addEdit("复制", "copy:", "c")
        addEdit("粘贴", "paste:", "v")
        addEdit("全选", "selectAll:", "a")
        NSApp.mainMenu = main
    }

    private func installStatusMenu() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        let mark = NSImage(size: NSSize(width: 22, height: 18), flipped: false) { _ in
            NSColor.black.setStroke()
            for rect in [NSRect(x: 2, y: 7, width: 12, height: 9), NSRect(x: 8, y: 1, width: 12, height: 9)] {
                let path = NSBezierPath(roundedRect: rect, xRadius: 2.5, yRadius: 2.5)
                path.lineWidth = 1.7; path.stroke()
            }
            return true
        }
        mark.isTemplate = true
        mark.accessibilityDescription = "双栖"
        statusItem.button?.image = mark
        let menu = NSMenu()
        let open = NSMenuItem(title: "打开双栖", action: #selector(showWindow(_:)), keyEquivalent: "o")
        let toggle = NSMenuItem(title: "切换 Mac / Windows", action: #selector(switchFromMenu(_:)), keyEquivalent: "")
        let emergency = NSMenuItem(title: "紧急恢复键鼠", action: #selector(emergencyFromMenu(_:)), keyEquivalent: "")
        let quit = NSMenuItem(title: "退出双栖", action: #selector(quitFromMenu(_:)), keyEquivalent: "q")
        for item in [open, toggle, emergency, quit] { item.target = self; menu.addItem(item) }
        statusItem.menu = menu
    }
    @objc private func showWindow(_ sender: Any?) {
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        model.updateSubscription()
    }
    @objc private func switchFromMenu(_ sender: Any?) { model.switchTarget() }
    @objc private func emergencyFromMenu(_ sender: Any?) { model.emergencyRecovery() }
    @objc private func quitFromMenu(_ sender: Any?) {
        if !terminating { terminating = true; model?.stop() }
        NSApp.terminate(nil)
    }
}

@main struct SwitchDeskMac {
    static func main() {
        let app = NSApplication.shared
        let delegate = DesktopAppDelegate()
        app.delegate = delegate
        app.run()
    }
}
