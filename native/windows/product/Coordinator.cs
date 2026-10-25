using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SwitchDesk;

namespace SwitchDesk.Desktop {
    internal sealed class Coordinator {
        private readonly object gate = new object();
        private readonly SemaphoreSlim switchLock = new SemaphoreSlim(1, 1);
        private readonly Session session;
        private readonly InputEngine input;
        internal readonly SwitchState Mapping = new SwitchState();
        private int epoch = 1, sessionGeneration, commandGeneration, mappingGeneration, displayOperationGeneration;
        private sealed class RecoveryMapping {
            internal readonly string Id, Name;
            internal readonly int WindowsPort, MacPort;
            internal readonly bool MacConfirmed;
            internal RecoveryMapping(SwitchState state) { Id = state.DisplayId; Name = state.DisplayName; WindowsPort = state.OriginalInput; MacPort = state.TargetInput; MacConfirmed = state.UserConfirmed; }
        }
        private RecoveryMapping recoveryMapping;
        private sealed class InputContinuation {
            internal readonly int Epoch, Session;
            internal readonly string Source;
            internal InputContinuation(int epoch, int session, string source) { Epoch = epoch; Session = session; Source = source; }
        }
        private string confirmationDetail = "选择显示器和 Mac 输入口后，确实看到画面即可勾选确认。";
        private string target = "windows", source = "windows", detail = "等待配对与显示器校准";
        private bool ready, switching, macInput, windowsInput, captureAllowed, armed;
        private DateTime expectedDisplayChangeUntil = DateTime.MinValue;
        private string pendingVisualTarget = "", observedTarget = "", switchTimingDetail = "";
        private int pendingVisualOperation, pendingVisualMapping, pendingVisualSession;
        private DateTime pendingVisualUntil = DateTime.MinValue;
        private string trialDirectory, trialRunId;
        internal Action Changed;
        internal Coordinator(Session session, InputEngine input) {
            this.session = session; this.input = input;
            input.Send = session.SendControl;
            input.Switch = delegate { RequestSwitch(null); };
            input.Emergency = Emergency;
            session.OnConnected += Connected;
            session.OnDisconnected += Disconnected;
            session.OnHeartbeat += Heartbeat;
            session.OnControl += Receive;
        }
        internal int Epoch { get { lock (gate) return epoch; } }
        internal string Target { get { lock (gate) return target; } }
        internal string Source { get { lock (gate) return source; } }
        internal bool Ready { get { lock (gate) return ready; } }
        internal bool Armed { get { lock (gate) return armed; } }
        internal bool DisplayOperationBusy { get { return switchLock.CurrentCount == 0; } }
        internal bool CanConfirmObserved { get { lock (gate) return Mapping.CanTrial() && !switching && !DisplayOperationBusy && !ProcessRunner.WriteBlocked; } }
        internal string ConfirmationDetail { get { lock (gate) return ProcessRunner.WriteBlocked ? "前次显示器操作未确认结束，请完全退出双栖后重新打开，再确认画面。" : confirmationDetail; } }
        internal bool CanRecoverWindows { get { lock (gate) return recoveryMapping != null && !switching && !Mapping.Busy && !ProcessRunner.WriteBlocked; } }
        internal string RecoveryDetail { get { lock (gate) {
            if (ProcessRunner.WriteBlocked) return "上次显示器进程尚未确认退出，已暂时停止切屏；请完全退出 Windows 双栖后重开。";
            if (switching || Mapping.Busy) return "显示器操作进行中，结束后可以恢复 Windows。";
            if (recoveryMapping == null) return "尚未记录 Windows 返回接口；请在 Windows 设置中选择显示器并开始一次安全试切。";
            return trialDirectory != null ? "可立即结束试切并请求返回 Windows。" : "Windows 返回接口已记录，可独立恢复画面。";
        } } }
        internal bool CanRequestSwitch { get { lock (gate) return !switching && !ProcessRunner.WriteBlocked && (Mapping.CanNormalSwitch() || (target == "unknown" && CanRecoverWindows)); } }
        internal bool DisplayTransitionExpected { get { lock (gate) return switching || trialDirectory != null || DateTime.UtcNow < expectedDisplayChangeUntil; } }
        internal string Detail { get { lock (gate) return ReadinessDetail(); } }
        internal string PendingVisualTarget { get { lock (gate) return HasPendingVisual() ? pendingVisualTarget : ""; } }
        internal int VisualConfirmationId { get { lock (gate) return HasPendingVisual() ? pendingVisualOperation : 0; } }
        internal string SwitchTimingDetail { get { lock (gate) return switchTimingDetail; } }
        internal string InputDetail { get { lock (gate) {
            var missing = new List<string>();
            if (!session.Connected) missing.Add("Mac 尚未连接");
            if (switching) missing.Add("正在切屏，键鼠暂留本机");
            else if (target == "unknown" || !Mapping.CanNormalSwitch()) missing.Add(HasPendingVisual() ? "请先确认 AOC 上实际显示的桌面" : "显示器画面尚未确认");
            if (!macInput) missing.Add("Mac 键鼠权限未就绪，请在 Mac 设置查看辅助功能和输入监听");
            if (!windowsInput) missing.Add("Windows 键鼠服务未就绪");
            if (ProcessRunner.WriteBlocked) missing.Add("显示器操作未结束，请重开 Windows 双栖");
            if (missing.Count != 0) return String.Join("；", missing);
            return armed ? "已启用 " + (source == "mac" ? "Mac 的键盘与触控板" : "Windows 的键鼠") + "，随当前画面控制电脑" : "权限与画面已就绪，点击键鼠来源即可启用";
        } } }
        private bool HasPendingVisual() {
            return pendingVisualOperation > 0 && (pendingVisualTarget == "mac" || pendingVisualTarget == "windows") &&
                pendingVisualOperation == displayOperationGeneration && pendingVisualMapping == mappingGeneration &&
                pendingVisualSession == sessionGeneration && session.Connected && target == "unknown" &&
                !switching && !Mapping.Busy && !DisplayOperationBusy && !ProcessRunner.WriteBlocked && DateTime.UtcNow < pendingVisualUntil;
        }
        private void ClearVisualEvidence() {
            pendingVisualTarget = observedTarget = ""; pendingVisualOperation = 0; pendingVisualUntil = DateTime.MinValue;
        }
        internal bool ConfirmDisplayedTarget(string value, long operation) {
            lock (gate) {
                if (!HasPendingVisual() || value != pendingVisualTarget || operation != pendingVisualOperation ||
                    recoveryMapping == null || !recoveryMapping.MacConfirmed ||
                    Mapping.DisplayId != recoveryMapping.Id || Mapping.DisplayName != recoveryMapping.Name ||
                    Mapping.OriginalInput != recoveryMapping.WindowsPort || Mapping.TargetInput != recoveryMapping.MacPort) return false;
                // This is explicit human observation, never a claimed DDC readback.
                if (!Mapping.ReconfirmObservedTarget(value == "mac" ? recoveryMapping.MacPort : recoveryMapping.WindowsPort)) return false;
                ClearVisualEvidence(); observedTarget = target = value; armed = false; epoch++; commandGeneration++;
                detail = "你已确认 AOC 显示 " + (value == "mac" ? "Mac" : "Windows") + " 桌面；显示器未提供回读，选择键鼠来源后启用共享";
                Recompute(); return true;
            }
        }
        private string ReadinessDetail() {
            if (switching) return detail;
            if (ProcessRunner.WriteBlocked) return "前次显示器操作未确认结束，请重启双栖后重试";
            if (target == "unknown" && recoveryMapping != null) return detail + "；可点击「恢复 Windows 画面」，键鼠保持本地。";
            if (!Mapping.CanNormalSwitch()) return "请在 Windows「设置」选择显示器并确认看到 Mac 画面；未看到时可先安全试切";
            if (!session.Connected) return "可以独立切换画面；Mac 未连接，键鼠共享未启用";
            var missing = new List<string>();
            if (!macInput) missing.Add("请在 Mac「连接与设置」授权辅助功能与输入监听");
            if (!windowsInput) missing.Add("Windows 键鼠服务未就绪，请检查安全桌面或重启双栖");
            if (target == "unknown") return "显示器输入待确认，可点击恢复 Windows 画面";
            if (missing.Count != 0) return "可以切换画面；键鼠共享待授权：" + String.Join("；", missing);
            return armed ? detail : "已连接，选择键鼠来源以启用控制";
        }
        internal void SetCapture(bool value) { lock (gate) { captureAllowed = value; session.SendControl(Wire.Obj("type", "capabilities", "input", windowsInput, "capture", captureAllowed)); } }
        internal void SetWindowsInput(bool value) { lock (gate) { if (!value && windowsInput) { armed = false; epoch++; commandGeneration++; detail = "Windows 输入注入不可用，请检查 UAC 或安全桌面"; } windowsInput = value; session.SendControl(Wire.Obj("type", "capabilities", "input", windowsInput, "capture", captureAllowed)); Recompute(); } }
        private void Connected() { lock (gate) { ClearVisualEvidence(); sessionGeneration++; commandGeneration++; epoch++; armed = false; macInput = false; detail = "已连接 Mac"; Recompute(); session.SendControl(Wire.Obj("type", "capabilities", "input", windowsInput, "capture", captureAllowed)); Publish(); } }
        private void Disconnected() {
            lock (gate) { if (session.Connected) return; ClearVisualEvidence(); sessionGeneration++; commandGeneration++; epoch++; macInput = false; armed = false; ready = false; source = "windows"; if (!switching && target == "mac") target = "unknown"; detail = "Mac 已断开，本地输入已恢复"; input.Configure(epoch, false, false); SignalRestore(); Notify(); }
        }
        private void Receive(Dictionary<string, object> message) {
            string type = Wire.S(message, "type");
            if (type == "capabilities") { lock (gate) { bool next = Wire.B(message, "input"); if (!next && macInput) { armed = false; epoch++; commandGeneration++; detail = "Mac 输入权限已失效"; } macInput = next; Recompute(); } }
            else if (type == "switch_request") RequestSwitch(Wire.S(message, "target"));
            else if (type == "restore_windows_request") RestoreWindows();
            else if (type == "source_request") SetSource(Wire.S(message, "source"));
            else if (type == "display_observed") ConfirmDisplayedTarget(Wire.S(message, "target"), Wire.L(message, "operation", 0));
            else if (type == "input") input.Inject(Wire.D(message, "event"), Wire.L(message, "epoch", -1));
            else if (type == "emergency" && Wire.S(message, "source") == "mac") EmergencyFromMac();
            else if (type == "ping" || type == "pong") input.Heartbeat();
        }
        internal void Heartbeat() { input.Heartbeat(); }
        private void Recompute() {
            bool newReady = !switching && !ProcessRunner.WriteBlocked && session.Connected && Mapping.CanNormalSwitch() && windowsInput && macInput && target != "unknown";
            ready = newReady;
            input.Configure(epoch, ready && armed && source == "windows" && target == "mac", ready && armed && source == "mac" && target == "windows");
            Publish();
        }
        private void Publish() {
            session.SendControl(Wire.Obj("type", "state", "epoch", epoch, "target", target, "source", source, "ready", ready, "can_switch", CanRequestSwitch, "can_recover_windows", CanRecoverWindows, "recovery_detail", RecoveryDetail, "app_version", typeof(Coordinator).Assembly.GetName().Version.ToString(), "armed", armed, "switching", switching, "detail", ReadinessDetail(), "pending_visual_target", PendingVisualTarget, "visual_confirmation_id", VisualConfirmationId, "switch_timing_detail", switchTimingDetail, "input_detail", InputDetail));
            Notify();
        }
        private void Notify() { if (Changed != null) Changed(); }
        internal void SetSource(string value) {
            if (value != "windows" && value != "mac") return;
            int command; lock (gate) command = commandGeneration;
            StartCommand(ArmSourceAsync(value, command));
        }
        private async Task ArmSourceAsync(string value, int command) {
            await switchLock.WaitAsync();
            try {
                int checkingEpoch, generation, originalPort, targetPort;
                string displayId, displayName;
                lock (gate) {
                    if (command != commandGeneration) return;
                    if (!session.Connected || !Mapping.CanNormalSwitch() || !windowsInput || !macInput || ProcessRunner.WriteBlocked) return;
                    source = value; armed = false; epoch++; checkingEpoch = epoch; generation = sessionGeneration;
                    if (observedTarget == target && target != "unknown" && !String.IsNullOrEmpty(observedTarget)) {
                        armed = true; detail = "按你确认的 AOC 画面启用键鼠来源：" + (value == "windows" ? "Windows" : "Mac"); Recompute(); return;
                    }
                    displayId = Mapping.DisplayId; displayName = Mapping.DisplayName;
                    originalPort = Mapping.OriginalInput; targetPort = Mapping.TargetInput;
                    detail = "正在回读显示器，确认键鼠来源…"; Recompute();
                }
                RunReply read = await Task.Run(() => ProcessRunner.Run(15000, false, "--worker", "read", displayId, displayName));
                lock (gate) {
                    if (epoch != checkingEpoch || sessionGeneration != generation || ProcessRunner.WriteBlocked) return;
                    int? actual = read.Worker == null || read.Worker.Control == null || !read.Worker.Ok ? null : read.Worker.Control.Readback;
                    target = actual == originalPort ? "windows" : actual == targetPort ? "mac" : "unknown";
                    armed = target != "unknown";
                    detail = armed ? "键鼠来源已确认：" + (value == "windows" ? "Windows" : "Mac") : "显示器输入回读不匹配，请重新校准";
                    Recompute();
                }
            } finally { switchLock.Release(); }
        }
        internal void RequestSwitch(string request) {
            if (request != null && request != "windows" && request != "mac") return;
            int command; string currentSource; bool sameTarget;
            lock (gate) {
                if (trialDirectory != null) { if (request == null || request == "windows") RestoreWindows(); return; }
                command = commandGeneration; currentSource = source; sameTarget = request != null && request == target;
            }
            StartCommand(sameTarget ? ArmSourceAsync(currentSource, command) : SwitchAsync(request, command));
        }
        internal void RestoreWindows() {
            int command;
            lock (gate) {
                if (trialDirectory != null) { SignalRestore(); detail = "已请求结束试切并恢复 Windows 画面"; Publish(); return; }
                command = commandGeneration;
            }
            StartCommand(SwitchAsync("windows", command, true));
        }
        private void StartCommand(Task task) {
            task.ContinueWith(delegate(Task failed) {
                if (failed.Exception == null) return;
                lock (gate) { ClearVisualEvidence(); armed = false; ready = false; switching = false; epoch++; detail = "操作失败，请检查显示器与连接"; Recompute(); }
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
        private async Task SwitchAsync(string request, int command, bool recoveryOnly = false) {
            await switchLock.WaitAsync();
            int operation = 0, reconciliationMapping = -1;
            string reconciliationTarget = null;
            InputContinuation inputContinuation = null;
            try {
                string destination;
                int switchingEpoch, startingGeneration, mappingVersion, originalPort, targetPort;
                string displayId, displayName;
                bool recovering;
                lock (gate) {
                    if (command != commandGeneration) return;
                    destination = request ?? (target == "mac" || target == "unknown" ? "windows" : "mac");
                    recovering = destination == "windows" && CanRecoverWindows && (recoveryOnly || target == "unknown" || !Mapping.CanNormalSwitch());
                    if (ProcessRunner.WriteBlocked || Mapping.Busy || switching || (recoveryOnly && !recovering)) return;
                    if (!recovering && (!Mapping.CanNormalSwitch() || destination == target)) return;
                    ClearVisualEvidence();
                    armed = !recovering && session.Connected && windowsInput && macInput; switching = true; ready = false; epoch++; switchingEpoch = epoch; startingGeneration = sessionGeneration;
                    operation = ++displayOperationGeneration; mappingVersion = mappingGeneration;
                    expectedDisplayChangeUntil = DateTime.UtcNow.AddSeconds(3);
                    displayId = recovering ? recoveryMapping.Id : Mapping.DisplayId; displayName = recovering ? recoveryMapping.Name : Mapping.DisplayName;
                    originalPort = recovering ? recoveryMapping.WindowsPort : Mapping.OriginalInput; targetPort = recovering ? recoveryMapping.MacPort : Mapping.TargetInput;
                    detail = "正在切换显示器…"; input.Configure(epoch, false, false); Publish();
                }
                int value = destination == "mac" ? targetPort : originalPort;
                int expected = destination == "mac" ? originalPort : -1;
                var commandTimer = Stopwatch.StartNew();
                RunReply run = await Task.Run(() => ProcessRunner.Run(20000, true, "--worker", "set", displayId, displayName,
                    expected.ToString(), value.ToString(), originalPort.ToString(), targetPort.ToString()));
                commandTimer.Stop();
                lock (gate) {
                    // Input/session changes disarm control but do not erase a completed physical display operation.
                    if (mappingGeneration != mappingVersion || displayOperationGeneration != operation) return;
                    if (epoch != switchingEpoch || sessionGeneration != startingGeneration) armed = false;
                    switching = false;
                    ControlResult control = run.Worker == null ? null : run.Worker.Control;
                    switchTimingDetail = "本次命令 " + commandTimer.ElapsedMilliseconds + "ms" + (control == null ? "，未收到阶段结果" :
                        " · 查找 " + control.DiscoveryMs + "ms · 预读 " + control.ReadBeforeMs + "ms · 写入 " + control.WriteMs + "ms · 回读 " + control.ReadbackMs + "ms（" + control.ReadbackAttempts + " 次）");
                    expectedDisplayChangeUntil = DateTime.UtcNow.AddSeconds(3);
                    bool ok = run.Worker != null && run.Worker.Ok && run.Worker.Control != null && run.Worker.Control.Success && run.Worker.Control.Readback == value;
                    target = ok ? destination : "unknown";
                    detail = ok ? "显示器回读确认：" + value : "切换后未确认当前输入：" + (run.Worker != null && run.Worker.Control != null ? run.Worker.Control.Detail : "未收到显示器回读");
                    if (!ok) {
                        bool resumeInput = armed && !recovering;
                        armed = false; Mapping.RevokeConfirmation();
                        bool mayReconcile = recoveryMapping != null &&
                            run.Started && !run.TimedOut && !run.CleanupUncertain && !ProcessRunner.WriteBlocked &&
                            run.Worker != null && !run.Worker.RejectedBeforeWrite && control != null && control.WriteReturned && control.ReadbackUnconfirmed &&
                            (control.Before == originalPort || control.Before == targetPort);
                        if (mayReconcile) {
                            reconciliationTarget = destination; reconciliationMapping = mappingVersion;
                            if (resumeInput) inputContinuation = new InputContinuation(epoch, sessionGeneration, source);
                        }
                        if (mayReconcile && session.Connected && sessionGeneration == startingGeneration && recoveryMapping.MacConfirmed) {
                            pendingVisualTarget = destination; pendingVisualOperation = operation; pendingVisualMapping = mappingVersion;
                            pendingVisualSession = startingGeneration; pendingVisualUntil = DateTime.UtcNow.AddMinutes(2);
                        }
                    }
                    else if (recovering) {
                        Mapping.Configure(displayId, displayName, originalPort, targetPort);
                        if (recoveryMapping.MacConfirmed) Mapping.ConfirmObservedReturn(originalPort);
                        armed = false;
                        confirmationDetail = recoveryMapping.MacConfirmed ? "已核对保存的显示器并恢复 Windows，无需重复确认。" : "已恢复 Windows；确实看到过 Mac 画面后，可勾选确认。";
                    }
                    Recompute();
                }
            } catch {
                lock (gate) if (operation != 0 && operation == displayOperationGeneration) { target = "unknown"; armed = false; Mapping.RevokeConfirmation(); detail = "显示器操作异常，当前输入未确认"; }
            } finally {
                switchLock.Release();
                lock (gate) if (operation != 0 && operation == displayOperationGeneration) { switching = false; Recompute(); }
            }
            if (reconciliationTarget != null) StartCommand(ReconcileDisplayAsync(operation, reconciliationMapping, reconciliationTarget, inputContinuation));
        }
        private async Task ReconcileDisplayAsync(int operation, int mappingVersion, string destination, InputContinuation continuation) {
            // A panel may become readable only after its video input settles. Recheck
            // independently of input permissions, without ever issuing another write.
            int[] delays = { 500, 1000, 2000 };
            for (int attempt = 0; attempt < delays.Length; attempt++) {
                await Task.Delay(delays[attempt]);
                if (!await switchLock.WaitAsync(0)) return;
                try {
                    RecoveryMapping snapshot;
                    lock (gate) {
                        if (operation != displayOperationGeneration || mappingVersion != mappingGeneration || target != "unknown" ||
                            switching || Mapping.Busy || trialDirectory != null || ProcessRunner.WriteBlocked || recoveryMapping == null) return;
                        snapshot = recoveryMapping;
                    }
                    RunReply read = await Task.Run(() => ProcessRunner.Run(3000, false, "--worker", "read", snapshot.Id, snapshot.Name));
                    lock (gate) {
                        if (operation != displayOperationGeneration || mappingVersion != mappingGeneration || target != "unknown" ||
                            switching || Mapping.Busy || ProcessRunner.WriteBlocked || recoveryMapping != snapshot) return;
                        int expected = destination == "mac" ? snapshot.MacPort : snapshot.WindowsPort;
                        if (!read.Started || read.TimedOut || read.CleanupUncertain || read.Worker == null || !read.Worker.Ok ||
                            read.Worker.Control == null || !read.Worker.Control.Success || read.Worker.Control.Readback != expected) continue;
                        if (snapshot.MacConfirmed) {
                            if (!Mapping.RestoreValidatedMapping(snapshot.Id, snapshot.Name, snapshot.WindowsPort, snapshot.MacPort, expected)) return;
                        } else Mapping.Configure(snapshot.Id, snapshot.Name, snapshot.WindowsPort, snapshot.MacPort);
                        // Resume only the input authorization carried by this normal
                        // switch. Recovery, reconnect, permission loss and any later
                        // input action invalidate it, even if permission comes back.
                        bool resumeInput = continuation != null && continuation.Epoch == epoch &&
                            continuation.Session == sessionGeneration && continuation.Source == source &&
                            session.Connected && windowsInput && macInput && snapshot.MacConfirmed;
                        ClearVisualEvidence(); target = destination; armed = resumeInput; epoch++; commandGeneration++;
                        detail = "已重新读取显示器，确认 " + (destination == "mac" ? "Mac" : "Windows") + " 画面";
                        confirmationDetail = snapshot.MacConfirmed ? "显示器状态已自动同步，无需重复确认。" : "已恢复 Windows；确实看到过 Mac 画面后，可勾选确认。";
                        Recompute(); return;
                    }
                } catch {
                    // Preserve recovery and explicit visual confirmation if a read fails.
                    return;
                } finally {
                    switchLock.Release();
                    lock (gate) if (operation == displayOperationGeneration && mappingVersion == mappingGeneration) Recompute();
                }
            }
        }
        internal void Emergency() { EmergencyFor("windows", true); }
        private void EmergencyFromMac() { EmergencyFor("mac", false); }
        private void EmergencyFor(string origin, bool announce) {
            int emergencyEpoch, emergencyGeneration;
            lock (gate) { ClearVisualEvidence(); bool wasSwitching = switching; commandGeneration++; displayOperationGeneration++; source = origin; armed = false; ready = false; switching = false; if (wasSwitching) target = "unknown"; epoch++; emergencyEpoch = epoch; emergencyGeneration = sessionGeneration; input.Configure(epoch, false, false); detail = "紧急恢复：输入已释放"; Publish(); if (announce) session.SendControl(Wire.Obj("type", "emergency", "source", "windows")); }
            Task.Run(async delegate {
                await switchLock.WaitAsync();
                try {
                    int recoveryEpoch, recoveryGeneration, value, originalPort, targetPort;
                    string displayId, displayName;
                    lock (gate) {
                        if (epoch != emergencyEpoch || sessionGeneration != emergencyGeneration) return;
                        if (recoveryMapping == null || target == origin || ProcessRunner.WriteBlocked) return;
                        expectedDisplayChangeUntil = DateTime.UtcNow.AddSeconds(3);
                        recoveryEpoch = epoch; recoveryGeneration = sessionGeneration;
                        displayId = recoveryMapping.Id; displayName = recoveryMapping.Name;
                        originalPort = recoveryMapping.WindowsPort; targetPort = recoveryMapping.MacPort;
                        value = origin == "mac" ? targetPort : originalPort;
                    }
                    RunReply run = ProcessRunner.Run(20000, true, "--worker", "set", displayId, displayName, "-1", value.ToString(), originalPort.ToString(), targetPort.ToString());
                    lock (gate) {
                        if (epoch != recoveryEpoch || sessionGeneration != recoveryGeneration) return;
                        expectedDisplayChangeUntil = DateTime.UtcNow.AddSeconds(3);
                        target = run.Worker != null && run.Worker.Ok && run.Worker.Control != null && run.Worker.Control.Readback == value ? origin : "unknown";
                        detail = target == origin ? "已恢复 " + (origin == "mac" ? "Mac" : "Windows") + " 画面" : "回切未确认，请用显示器信号源按键恢复";
                        Recompute();
                    }
                } finally { switchLock.Release(); }
            });
        }
        internal void ConfigureDisplay(DisplayInfo info, int targetPort) {
            lock (gate) {
                ClearVisualEvidence(); SignalRestore(); switching = false; commandGeneration++; mappingGeneration++; displayOperationGeneration++; recoveryMapping = null;
                Mapping.Configure(info.Id, info.Name, info.DdcInput.Value.Value, targetPort);
                confirmationDetail = Mapping.CanTrial() ? "已看到 Mac 桌面并回到 Windows？可直接勾选，程序将读取当前输入确认。" : "Mac 输入口不能与当前 Windows 输入相同，请选择 Mac 实际接入的另一个接口。";
                target = "windows"; armed = false; epoch++; detail = "请先试切并确认 Mac 画面"; Recompute();
            }
        }
        internal bool RestoreSavedDisplay(DisplayInfo info, int windowsPort, int macPort) {
            lock (gate) {
                if (info == null || info.DdcInput == null || !info.DdcInput.Value.HasValue || DisplayOperationBusy) return false;
                InvalidateMapping();
                int current = info.DdcInput.Value.Value;
                if (!Mapping.RestoreValidatedMapping(info.Id, info.Name, windowsPort, macPort, current)) return false;
                recoveryMapping = new RecoveryMapping(Mapping); target = current == windowsPort ? "windows" : "mac";
                detail = "已核对保存的显示器与当前输入"; confirmationDetail = "已恢复保存的画面确认，无需重复勾选。";
                Recompute(); return true;
            }
        }
        internal void InvalidateMapping() { lock (gate) { ClearVisualEvidence(); SignalRestore(); Mapping.Invalidate(); recoveryMapping = null; armed = false; switching = false; epoch++; commandGeneration++; mappingGeneration++; displayOperationGeneration++; target = "unknown"; detail = "显示器已变化，请刷新并重新校准"; confirmationDetail = "请先选择显示器和 Mac 所接的输入口。"; Recompute(); } }
        internal void DisplayConfigurationChanged() {
            lock (gate) {
                if (recoveryMapping == null) { InvalidateMapping(); return; }
                ClearVisualEvidence(); Mapping.End(); Mapping.RevokeConfirmation(); armed = false; switching = false;
                epoch++; commandGeneration++; mappingGeneration++; displayOperationGeneration++; target = "unknown";
                detail = "收到显示器变化通知，保留已确认的 Windows 接口";
                confirmationDetail = "接口记录已保留，可先恢复 Windows 画面。"; Recompute();
            }
        }
        internal void RevokeConfirmation() { lock (gate) { ClearVisualEvidence(); SignalRestore(); Mapping.RevokeConfirmation(); recoveryMapping = null; armed = false; switching = false; epoch++; commandGeneration++; displayOperationGeneration++; detail = "显示器确认已撤销"; Recompute(); } }
        internal void ConfirmTrial() { lock (gate) { Mapping.ConfirmSeen(); if (Mapping.UserConfirmed) { recoveryMapping = new RecoveryMapping(Mapping); detail = "显示器已校准"; confirmationDetail = "画面已确认，可以到控制台切换。"; } Recompute(); } }
        internal async Task<bool> ConfirmObservedAsync() {
            if (!await switchLock.WaitAsync(0)) return false;
            int generation = -1;
            try {
                string id, name;
                lock (gate) {
                    if (!Mapping.CanTrial() || switching || ProcessRunner.WriteBlocked) return false;
                    generation = mappingGeneration; id = Mapping.DisplayId; name = Mapping.DisplayName;
                    ClearVisualEvidence(); Mapping.Begin(); armed = false; epoch++; commandGeneration++;
                    confirmationDetail = "正在读取当前输入，不会切换画面…"; Recompute();
                }
                RunReply read = await Task.Run(() => ProcessRunner.Run(15000, false, "--worker", "read", id, name));
                lock (gate) {
                    if (generation != mappingGeneration) { confirmationDetail = "显示器选择已变化，请重新确认。"; return false; }
                    Mapping.End();
                    if (ProcessRunner.WriteBlocked || read.Worker == null || !read.Worker.Ok || read.Worker.Control == null || !read.Worker.Control.Readback.HasValue) {
                        confirmationDetail = "当前输入读取失败，确认未保存；请稍后重新勾选。"; return false;
                    }
                    if (!Mapping.ConfirmObservedReturn(read.Worker.Control.Readback.Value)) {
                        confirmationDetail = "当前输入与选择的 Windows 输入不一致；回到 Windows 后重新选择显示器。"; return false;
                    }
                    target = "windows"; detail = "显示器已校准";
                    recoveryMapping = new RecoveryMapping(Mapping);
                    confirmationDetail = "画面已确认，可以到控制台切换。";
                    return true;
                }
            } catch {
                lock (gate) confirmationDetail = "读取当前输入失败，确认未保存；请稍后重新勾选。";
                return false;
            } finally {
                switchLock.Release();
                lock (gate) { if (generation == mappingGeneration) Mapping.End(); Recompute(); }
            }
        }
        // A fresh read establishes a return address even before Mac video is confirmed.
        // This grants Windows-only recovery, never normal switching or input forwarding.
        private bool RememberTrialRecovery(RunReply before, int generation, string id, string name, int original, int port) {
            lock (gate) {
                if (generation != mappingGeneration || !Mapping.CanTrial() || ProcessRunner.WriteBlocked ||
                    Mapping.DisplayId != id || Mapping.DisplayName != name || Mapping.OriginalInput != original || Mapping.TargetInput != port ||
                    before == null || before.Worker == null || !before.Worker.Ok || before.Worker.Control == null || before.Worker.Control.Readback != original) return false;
                recoveryMapping = new RecoveryMapping(Mapping);
                return true;
            }
        }
        internal async Task<string> TrialAsync() {
            if (!await switchLock.WaitAsync(0)) return "已有切换操作";
            try {
                int trialGeneration;
                string id, name; int original, port;
                lock (gate) {
                    if (!Mapping.CanTrial() || ProcessRunner.WriteBlocked) return "请选择可读取输入的显示器与实际 Mac 接口";
                    trialGeneration = mappingGeneration; id = Mapping.DisplayId; name = Mapping.DisplayName;
                    original = Mapping.OriginalInput; port = Mapping.TargetInput;
                }
                RunReply before = await Task.Run(() => ProcessRunner.Run(15000, false, "--worker", "read", id, name));
                if (!RememberTrialRecovery(before, trialGeneration, id, name, original, port)) return "当前输入或显示器选择已变化，试切已取消，请刷新显示器";
                string runId = Guid.NewGuid().ToString("N");
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwitchDesk", "Trial", runId);
                Directory.CreateDirectory(directory);
                lock (gate) {
                    if (mappingGeneration != trialGeneration || Mapping.DisplayId != id || Mapping.DisplayName != name || Mapping.OriginalInput != original || Mapping.TargetInput != port) return "显示器已变化，试切已取消";
                    ClearVisualEvidence(); trialDirectory = directory; trialRunId = runId;
                    target = "unknown"; armed = false; epoch++; commandGeneration++;
                    detail = "正在安全试切，可随时请求恢复 Windows 画面"; Recompute();
                    expectedDisplayChangeUntil = DateTime.UtcNow.AddSeconds(3);
                }
                if (!await Task.Run(() => StartGuardian(directory, id, name, original, port, runId))) { ProcessRunner.WriteBlocked = true; SignalRestore(); return "恢复守护进程未就绪，已停止写入"; }
                TrialReply result = await Task.Run(() => WaitTrial(directory, runId));
                trialDirectory = null; trialRunId = null;
                if (result == null || result.CleanupUncertain) { ProcessRunner.WriteBlocked = true; return "试切结果未确认，请用显示器按键恢复"; }
                bool targetReturned = result.Target != null && result.Target.Control != null && result.Target.Control.WriteReturned;
                bool restored = result.Return != null && result.Return.Control != null && result.Return.Control.Success && result.Return.Control.Readback == original;
                lock (gate) {
                    if (mappingGeneration != trialGeneration || Mapping.DisplayId != id || Mapping.DisplayName != name || Mapping.OriginalInput != original || Mapping.TargetInput != port) return "显示器已变化，旧试切结果已忽略";
                    Mapping.RecordTrial(original, restored ? original : -1, targetReturned, restored);
                    target = restored ? "windows" : "unknown";
                    detail = Mapping.TrialReturned ? "已切回原输入；请确认看到 Mac 画面" : "试切未确认，请检查显示器"; Recompute();
                }
                return detail;
            } finally { SignalRestore(); trialDirectory = null; trialRunId = null; switchLock.Release(); lock (gate) { expectedDisplayChangeUntil = DateTime.UtcNow.AddSeconds(3); Recompute(); } }
        }
        private static bool StartGuardian(string directory, string id, string name, int original, int port, string runId) {
            try {
                Process process = Process.Start(new ProcessStartInfo { FileName = Application.ExecutablePath,
                    Arguments = ProcessRunner.Arguments("--guardian", directory, id, name, original.ToString(), port.ToString(), runId), UseShellExecute = false, CreateNoWindow = true });
                if (process == null) return false;
                for (int elapsed = 0; elapsed < 5000; elapsed += 100) {
                    if (File.Exists(Path.Combine(directory, "ready")) && File.ReadAllText(Path.Combine(directory, "ready")) == runId) return true;
                    if (process.HasExited) return false; Thread.Sleep(100);
                }
                File.WriteAllText(Path.Combine(directory, "restore"), runId); return false;
            } catch { return false; }
        }
        private static TrialReply WaitTrial(string directory, string runId) {
            for (int elapsed = 0; elapsed < 65000; elapsed += 200) {
                string path = Path.Combine(directory, "result.json");
                if (File.Exists(path)) { try { TrialReply answer = Program.Json.Deserialize<TrialReply>(File.ReadAllText(path)); if (answer != null && answer.RunId == runId) return answer; } catch { } }
                Thread.Sleep(200);
            }
            return null;
        }
        internal void SignalRestore() { if (trialDirectory != null) { try { File.WriteAllText(Path.Combine(trialDirectory, "restore"), trialRunId ?? ""); } catch { } } }
    }
}
