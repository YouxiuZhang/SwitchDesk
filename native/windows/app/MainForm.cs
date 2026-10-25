using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SwitchDesk
{
    public sealed class MainForm : Form
    {
        private const int WM_HOTKEY = 0x0312;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private readonly SwitchState state = new SwitchState();
        private readonly ComboBox display = new ComboBox();
        private readonly ComboBox port = new ComboBox();
        private readonly Label status = new Label();
        private readonly Label current = new Label();
        private readonly Button trial = new Button();
        private readonly Button toMac = new Button();
        private readonly Button toWindows = new Button();
        private readonly Button refresh = new Button();
        private readonly Button export = new Button();
        private readonly CheckBox confirmed = new CheckBox();
        private readonly TextBox log = new TextBox();
        private readonly Panel logPanel = new Panel();
        private readonly Button logToggle = new Button();
        private bool selecting;
        private bool busy;
        private bool onMac;
        private string trialDirectory;
        private string trialRunId;
        private int generation;
        private bool externalGuardian;
        private bool externalWorker;
        private bool ownGuardianPending;
        private readonly System.Windows.Forms.Timer guardianTimer = new System.Windows.Forms.Timer();

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        private sealed class PortChoice
        {
            public int Value;
            public string Label;
            public override string ToString() { return Label; }
        }

        private sealed class DisplayChoice
        {
            public DisplayInfo Info;
            public override string ToString()
            {
                return Info.Name + " · " + Info.Width + "×" + Info.Height +
                    (Info.DdcInput != null && Info.DdcInput.Value.HasValue
                        ? " · 当前输入 " + Info.DdcInput.Value.Value : " · 输入不可读");
            }
        }

        public MainForm()
        {
            externalGuardian = GuardianActive();
            externalWorker = WorkerActive();
            Text = "双栖 · 显示器切屏测试版";
            ClientSize = new Size(680, 520);
            MinimumSize = new Size(680, 520);
            BackColor = Color.FromArgb(248, 247, 251);
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;

            Label title = LabelAt("双栖", 28, 22, 610, 37, 20F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(65, 40, 130);
            LabelAt("选择一台显示器和 Mac 所在输入口，先安全试切一次。", 30, 67, 620, 25, 9F);

            LabelAt("显示器", 30, 112, 95, 23, 9F);
            display.SetBounds(128, 108, 415, 29);
            display.DropDownStyle = ComboBoxStyle.DropDownList;
            display.SelectedIndexChanged += delegate { SelectionChanged(); };
            Controls.Add(display);
            refresh.Text = "刷新";
            refresh.SetBounds(555, 108, 90, 29);
            refresh.Click += async delegate { await RefreshDisplays(); };
            Controls.Add(refresh);

            LabelAt("Mac 输入口", 30, 154, 95, 23, 9F);
            port.SetBounds(128, 150, 215, 29);
            port.DropDownStyle = ComboBoxStyle.DropDownList;
            port.Items.Add(new PortChoice { Value = 17, Label = "HDMI 1 · 标准值 17" });
            port.Items.Add(new PortChoice { Value = 18, Label = "HDMI 2 · 标准值 18" });
            port.Items.Add(new PortChoice { Value = 15, Label = "DisplayPort · 标准值 15" });
            port.SelectedIndexChanged += delegate { SelectionChanged(); };
            Controls.Add(port);
            LabelAt("标准候选值；实际接口须通过试切确认。", 357, 153, 290, 25, 8F);

            current.SetBounds(30, 191, 615, 24);
            current.Text = "当前输入：等待发现";
            Controls.Add(current);
            status.SetBounds(30, 222, 615, 36);
            status.ForeColor = Color.FromArgb(70, 45, 140);
            status.Text = "正在发现显示器…";
            Controls.Add(status);

            trial.Text = "试切到 Mac（约 10 秒后尝试切回）";
            trial.SetBounds(30, 269, 365, 42);
            trial.BackColor = Color.FromArgb(112, 70, 204);
            trial.ForeColor = Color.White;
            trial.FlatStyle = FlatStyle.Flat;
            trial.FlatAppearance.BorderSize = 0;
            trial.Click += async delegate { await Trial(); };
            Controls.Add(trial);

            confirmed.Text = "我看到了 Mac 桌面并已切回";
            confirmed.SetBounds(30, 321, 335, 28);
            confirmed.CheckedChanged += delegate {
                if (confirmed.Checked) state.ConfirmSeen();
                else state.RevokeConfirmation();
                UpdateActions();
            };
            Controls.Add(confirmed);

            toMac.Text = "切到 Mac";
            toMac.SetBounds(30, 359, 170, 36);
            toMac.Click += async delegate { await NormalSwitch(true); };
            Controls.Add(toMac);
            toWindows.Text = "切回 Windows";
            toWindows.SetBounds(210, 359, 170, 36);
            toWindows.Click += async delegate { await NormalSwitch(false); };
            Controls.Add(toWindows);

            LabelAt("仅切显示器画面。键鼠共享、剪贴板共享、桌面预览尚未接入。", 30, 408, 620, 24, 8F);
            LabelAt("热键用 Windows 侧 USB 键盘：Ctrl+Alt+Space 双向切换；Ctrl+Alt+Backspace 请求返回。Mac 键盘无法触发。", 30, 431, 625, 43, 8F);

            logToggle.Text = "查看日志 ▾";
            logToggle.SetBounds(30, 475, 120, 28);
            logToggle.Click += delegate {
                logPanel.Visible = !logPanel.Visible;
                ClientSize = new Size(680, logPanel.Visible ? 690 : 520);
                logToggle.Text = logPanel.Visible ? "收起日志 ▴" : "查看日志 ▾";
            };
            Controls.Add(logToggle);
            logPanel.SetBounds(30, 510, 615, 150);
            logPanel.Visible = false;
            log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Color.White, Font = new Font("Consolas", 8F) };
            log.SetBounds(0, 0, 510, 145);
            logPanel.Controls.Add(log);
            export.Text = "导出日志";
            export.SetBounds(520, 0, 95, 29);
            export.Click += delegate { ExportLog(); };
            logPanel.Controls.Add(export);
            Controls.Add(logPanel);

            Shown += async delegate {
                bool spaceHotkey = RegisterHotKey(Handle, 1, MOD_CONTROL | MOD_ALT, 0x20);
                bool returnHotkey = RegisterHotKey(Handle, 2, MOD_CONTROL | MOD_ALT, 0x08);
                if (!spaceHotkey) AddLog("Ctrl+Alt+Space 热键注册失败。");
                if (!returnHotkey) AddLog("Ctrl+Alt+Backspace 热键注册失败。");
                if (!spaceHotkey || !returnHotkey)
                    MessageBox.Show(this, "快捷键注册失败，可能已被其他程序占用。请使用窗口按钮操作。",
                        "双栖 · 快捷键不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                await RefreshDisplays();
                if (externalGuardian || externalWorker)
                    SetStatus("上次显示器操作仍在运行；本窗口暂不执行写入。" );
            };
            FormClosing += delegate {
                SignalRestore();
                guardianTimer.Stop();
                UnregisterHotKey(Handle, 1);
                UnregisterHotKey(Handle, 2);
            };
            guardianTimer.Interval = 1000;
            guardianTimer.Tick += delegate { CheckGuardian(); };
            guardianTimer.Start();
            UpdateActions();
        }

        private static bool GuardianActive()
        {
            return MarkerActive(@"Local\SwitchDeskTestTrial");
        }

        private static bool WorkerActive()
        {
            return MarkerActive(@"Local\SwitchDeskDdcWorker");
        }

        private static bool MarkerActive(string name)
        {
            try {
                Mutex existing;
                if (!Mutex.TryOpenExisting(name, out existing)) return false;
                existing.Dispose();
                return true;
            } catch { return true; }
        }

        private void CheckGuardian()
        {
            bool active = GuardianActive();
            if (ownGuardianPending) {
                if (!active && !busy) ownGuardianPending = false;
                UpdateActions();
                return;
            }
            if (active && !externalGuardian) {
                externalGuardian = true;
                InvalidateSelection(true);
                SetStatus("检测到独立恢复进程，暂停所有写入。" );
            } else if (!active && externalGuardian) {
                externalGuardian = false;
                InvalidateSelection(true);
                SetStatus("恢复进程已结束，请刷新并重新选择显示器。" );
            }
            if (!busy) {
                bool worker = WorkerActive();
                if (worker && !externalWorker) {
                    externalWorker = true;
                    InvalidateSelection(true);
                    SetStatus("检测到独立 DDC 操作，暂停所有写入。" );
                } else if (!worker && externalWorker) {
                    externalWorker = false;
                    InvalidateSelection(true);
                    SetStatus("上次 DDC 操作已结束，请刷新并重新选择显示器。" );
                }
            }
            UpdateActions();
        }

        private Label LabelAt(string text, int x, int y, int w, int h, float size, FontStyle style = FontStyle.Regular)
        {
            Label label = new Label { Text = text, Font = new Font("Microsoft YaHei UI", size, style),
                ForeColor = Color.FromArgb(72, 68, 82) };
            label.SetBounds(x, y, w, h);
            Controls.Add(label);
            return label;
        }

        private void AddLog(string text)
        {
            log.AppendText(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + Environment.NewLine);
        }

        private void SetStatus(string text)
        {
            status.Text = text;
            AddLog(text);
        }

        private void UpdateActions()
        {
            bool writesAllowed = !externalGuardian && !externalWorker && !GuardianActive() &&
                !WorkerActive() && !ProcessRunner.WriteBlocked;
            bool ready = state.CanTrial() && !busy && writesAllowed;
            trial.Enabled = ready && !state.TrialReturned;
            confirmed.Enabled = !busy && state.TrialReturned;
            bool normal = state.CanNormalSwitch() && !busy && writesAllowed;
            toMac.Enabled = normal && !onMac;
            toWindows.Enabled = normal;
            display.Enabled = !busy;
            port.Enabled = !busy;
            refresh.Enabled = !busy;
        }

        private void InvalidateSelection(bool clearChoices)
        {
            generation++;
            state.Invalidate();
            onMac = false;
            confirmed.Checked = false;
            confirmed.Enabled = false;
            if (clearChoices) {
                selecting = true;
                display.SelectedIndex = -1;
                port.SelectedIndex = -1;
                selecting = false;
                current.Text = "当前输入：等待发现";
            }
            UpdateActions();
        }

        private void SelectionChanged()
        {
            if (selecting || busy) return;
            InvalidateSelection(false);
            DisplayChoice selected = display.SelectedItem as DisplayChoice;
            DisplayInfo item = selected == null ? null : selected.Info;
            PortChoice choice = port.SelectedItem as PortChoice;
            if (item == null) {
                current.Text = "当前输入：请选择显示器";
                SetStatus("请选择显示器和 Mac 输入口。");
                return;
            }
            if (item.DdcInput == null || item.DdcInput.Status != "readable" || !item.DdcInput.Value.HasValue) {
                current.Text = "当前输入：无法读取";
                SetStatus("该显示器不能读取 VCP 0x60，不能试切。" );
                return;
            }
            current.Text = "当前输入：" + item.DdcInput.Value.Value + "（VCP 0x60）";
            if (choice == null) {
                SetStatus("请选择 Mac 实际连接的输入口。");
                return;
            }
            state.Configure(item.Id, item.Name, item.DdcInput.Value.Value, choice.Value);
            SetStatus(state.CanTrial() ? "就绪：试切将约 10 秒后尝试恢复原输入。" :
                "目标口与当前输入相同；请选择另一个实际连接的输入口。" );
            UpdateActions();
        }

        private async Task RefreshDisplays()
        {
            if (busy) return;
            InvalidateSelection(true);
            busy = true;
            UpdateActions();
            SetStatus("正在读取显示器和当前输入…");
            RunReply run = await Task.Run(() => ProcessRunner.Run(20000, false, "--worker", "list"));
            busy = false;
            selecting = true;
            display.Items.Clear();
            if (run.Worker != null && run.Worker.Ok && run.Worker.Probe != null) {
                foreach (DisplayInfo item in run.Worker.Probe.Displays) {
                    if (item.Id != null && item.Id.Contains("/physical-"))
                        display.Items.Add(new DisplayChoice { Info = item });
                }
                SetStatus("发现 " + display.Items.Count + " 台物理显示器；请选择一台。" );
                foreach (string warning in run.Worker.Probe.Warnings) AddLog("发现警告：" + warning);
            } else SetStatus("读取失败或超时：" + (run.Error ?? "无返回") + "。可检查显示器 DDC/CI 开关后刷新。" );
            selecting = false;
            UpdateActions();
        }

        private static string Describe(RunReply run)
        {
            if (run == null) return "无结果";
            if (run.TimedOut) return "超时" + (run.CleanupUncertain ? "，进程退出未确认" : "，已停止子进程");
            if (run.Worker == null) return run.Error ?? "无结构化返回";
            if (run.Worker.Control == null) return run.Worker.Error ?? "无读数";
            ControlResult c = run.Worker.Control;
            return "操作前=" + (c.Before.HasValue ? c.Before.Value.ToString() : "未知") +
                "，写入返回=" + c.WriteReturned + "，回读=" +
                (c.Readback.HasValue ? c.Readback.Value.ToString() : "失败") + "，" + c.Detail;
        }

        private static string Describe(WorkerReply worker)
        {
            if (worker == null) return "无结果";
            if (worker.Control == null) return worker.Error ?? "无读数";
            ControlResult c = worker.Control;
            return "操作前=" + (c.Before.HasValue ? c.Before.Value.ToString() : "未知") +
                "，写入返回=" + c.WriteReturned + "，回读=" +
                (c.Readback.HasValue ? c.Readback.Value.ToString() : "失败") + "，" + c.Detail;
        }

        private async Task Trial()
        {
            if (busy || !state.CanTrial() || ProcessRunner.WriteBlocked || externalGuardian ||
                externalWorker || GuardianActive() || WorkerActive()) return;
            busy = true;
            state.Begin();
            UpdateActions();
            int thisGeneration = generation;
            string id = state.DisplayId, name = state.DisplayName;
            int original = state.OriginalInput, target = state.TargetInput;
            SetStatus("试切前正在确认显示器身份和当前输入…");
            RunReply before = await Task.Run(() => ProcessRunner.Run(15000, false, "--worker", "read", id, name));
            AddLog("试切前：" + Describe(before));
            if (thisGeneration != generation || before.Worker == null || !before.Worker.Ok ||
                before.Worker.Control == null || before.Worker.Control.Readback != original) {
                busy = false;
                state.End();
                SetStatus("当前输入与发现时不同，试切已取消。请刷新并重新选择。" );
                InvalidateSelection(true);
                return;
            }
            trialRunId = Guid.NewGuid().ToString("N");
            trialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SwitchDesk", "Trial", trialRunId);
            Directory.CreateDirectory(trialDirectory);
            string directory = trialDirectory;
            string runId = trialRunId;
            ownGuardianPending = true;
            SetStatus("启动独立恢复守护进程…" );
            bool started = await Task.Run(() => StartGuardian(directory, id, name, original, target, runId));
            if (!started) {
                ProcessRunner.WriteBlocked = true;
                trialDirectory = null;
                busy = false;
                state.End();
                SetStatus("恢复守护进程就绪未确认，已请求恢复并停止后续写入。请检查显示器。" );
                UpdateActions();
                return;
            }
            SetStatus("正在试切；约 10 秒后尝试切回。若无画面，请用显示器信号源按键恢复。" );
            TrialReply result = await Task.Run(() => WaitTrial(directory, runId));
            trialDirectory = null;
            busy = false;
            state.End();
            if (result == null || result.CleanupUncertain) {
                ProcessRunner.WriteBlocked = true;
                SetStatus("试切结果未确认；已停止后续写入。请用显示器信号源按键恢复。" );
                UpdateActions();
                return;
            }
            AddLog("目标写入：" + Describe(result.Target));
            AddLog("恢复写入：" + Describe(result.Return));
            bool targetReturned = result.Target != null && result.Target.Control != null &&
                result.Target.Control.WriteReturned;
            bool restoreConfirmed = result.Return != null && result.Return.Control != null &&
                result.Return.Control.Success && result.Return.Control.Readback == original;
            if (thisGeneration == generation) state.RecordTrial(original,
                restoreConfirmed ? original : -1, targetReturned, restoreConfirmed);
            if (state.TrialReturned) SetStatus("已回读到原输入。若确实看到 Mac 桌面，请勾选确认。" );
            else SetStatus("切换未确认或未能回读原输入；请用显示器信号源按键检查，再刷新重试。" );
            UpdateActions();
        }

        private static bool StartGuardian(string directory, string id, string name, int original, int target, string runId)
        {
            try {
                ProcessStartInfo info = new ProcessStartInfo {
                    FileName = Application.ExecutablePath,
                    Arguments = ProcessRunner.Arguments("--guardian", directory, id, name,
                        original.ToString(), target.ToString(), runId),
                    UseShellExecute = false, CreateNoWindow = true
                };
                Process process = Process.Start(info);
                if (process == null) return false;
                for (int elapsed = 0; elapsed < 5000; elapsed += 100) {
                    if (File.Exists(Path.Combine(directory, "ready")) &&
                        File.ReadAllText(Path.Combine(directory, "ready")) == runId) return true;
                    if (process.HasExited) return false;
                    Thread.Sleep(100);
                }
                // The guardian may still be running; request recovery before returning failure.
                File.WriteAllText(Path.Combine(directory, "restore"), runId);
                return false;
            } catch { return false; }
        }

        private static TrialReply WaitTrial(string directory, string runId)
        {
            for (int elapsed = 0; elapsed < 65000; elapsed += 200) {
                string path = Path.Combine(directory, "result.json");
                if (File.Exists(path)) {
                    try {
                        TrialReply answer = Program.Json.Deserialize<TrialReply>(File.ReadAllText(path));
                        if (answer != null && answer.RunId == runId) return answer;
                    } catch { }
                }
                Thread.Sleep(200);
            }
            return null;
        }

        private async Task NormalSwitch(bool mac)
        {
            if (busy || !state.CanNormalSwitch() || ProcessRunner.WriteBlocked ||
                externalGuardian || externalWorker || GuardianActive() || WorkerActive()) return;
            busy = true;
            state.Begin();
            UpdateActions();
            int value = mac ? state.TargetInput : state.OriginalInput;
            int expected = mac ? state.OriginalInput : -1;
            SetStatus(mac ? "正在请求切到 Mac…" : "正在请求切回 Windows…");
            RunReply run = await Task.Run(() => ProcessRunner.Run(20000, true, "--worker", "set",
                state.DisplayId, state.DisplayName, expected.ToString(), value.ToString(),
                state.OriginalInput.ToString(), state.TargetInput.ToString()));
            AddLog((mac ? "切到 Mac：" : "切回 Windows：") + Describe(run));
            busy = false;
            state.End();
            if (run.Worker != null && run.Worker.Ok && run.Worker.Control != null && run.Worker.Control.Success) {
                onMac = mac;
                current.Text = "当前输入：" + value + "（已回读）";
                SetStatus(mac ? "已回读到 Mac 输入口。" : "已回读到原 Windows 输入口。" );
            } else SetStatus("切换未确认。可用显示器信号源按键恢复；再次尝试前请检查当前画面。" );
            UpdateActions();
        }

        private void SignalRestore()
        {
            if (trialDirectory == null) return;
            try { File.WriteAllText(Path.Combine(trialDirectory, "restore"), trialRunId ?? ""); }
            catch { }
        }

        private void ExportLog()
        {
            using (SaveFileDialog dialog = new SaveFileDialog()) {
                dialog.Filter = "文本文件 (*.txt)|*.txt";
                dialog.FileName = "双栖测试日志.txt";
                if (dialog.ShowDialog(this) == DialogResult.OK) {
                    try { File.WriteAllText(dialog.FileName, log.Text, Encoding.UTF8); }
                    catch (Exception ex) { MessageBox.Show(this, "导出失败：" + ex.Message, "双栖"); }
                }
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DISPLAYCHANGE) {
                InvalidateSelection(true);
                SetStatus("显示器拓扑已变化，请刷新并重新选择。" );
            } else if (m.Msg == WM_HOTKEY) {
                if (m.WParam.ToInt32() == 1) { if (state.CanNormalSwitch() && !busy) NormalSwitch(!onMac); }
                if (m.WParam.ToInt32() == 2) { if (state.CanNormalSwitch() && !busy) NormalSwitch(false); }
            }
            base.WndProc(ref m);
        }
    }
}
