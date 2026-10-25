using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SwitchDesk
{
    public sealed class WorkerReply
    {
        public bool Ok;
        public bool RejectedBeforeWrite;
        public string Error;
        public ProbePayload Probe;
        public ControlResult Control;
    }

    public sealed class ProbePayload
    {
        public List<DisplayInfo> Displays;
        public List<string> Warnings;
    }

    public sealed class TrialReply
    {
        public string RunId;
        public WorkerReply Target;
        public WorkerReply Return;
        public bool CleanupUncertain;
    }

    public sealed class RunReply
    {
        public bool Started;
        public bool TimedOut;
        public bool CleanupUncertain;
        public string Output;
        public string Error;
        public WorkerReply Worker;
    }

    public static class ProcessRunner
    {
        public static volatile bool WriteBlocked;

        private static string Quote(string value)
        {
            StringBuilder result = new StringBuilder("\"");
            int slashCount = 0;
            foreach (char c in value) {
                if (c == '\\') { slashCount++; continue; }
                if (c == '"') {
                    result.Append('\\', slashCount * 2 + 1);
                    result.Append('"');
                    slashCount = 0;
                    continue;
                }
                result.Append('\\', slashCount);
                slashCount = 0;
                result.Append(c);
            }
            result.Append('\\', slashCount * 2);
            result.Append('"');
            return result.ToString();
        }

        public static string Arguments(params string[] parts)
        {
            StringBuilder b = new StringBuilder();
            foreach (string part in parts) {
                if (b.Length != 0) b.Append(' ');
                b.Append(Quote(part));
            }
            return b.ToString();
        }

        public static RunReply Run(int timeoutMs, bool mayWrite, params string[] arguments)
        {
            RunReply result = new RunReply();
            if (mayWrite && WriteBlocked) {
                result.Error = "上次进程未确认退出，禁止继续写入。";
                return result;
            }
            StringBuilder output = new StringBuilder(), error = new StringBuilder();
            object gate = new object();
            using (ManualResetEvent outputDone = new ManualResetEvent(false))
            using (ManualResetEvent errorDone = new ManualResetEvent(false))
            using (Process process = new Process()) {
                process.StartInfo = new ProcessStartInfo {
                    FileName = Application.ExecutablePath,
                    Arguments = Arguments(arguments),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                    if (e.Data == null) { outputDone.Set(); return; }
                    lock (gate) { if (output.Length < 65536) output.AppendLine(e.Data); }
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                    if (e.Data == null) { errorDone.Set(); return; }
                    lock (gate) { if (error.Length < 65536) error.AppendLine(e.Data); }
                };
                try {
                    result.Started = process.Start();
                    if (!result.Started) { result.Error = "子进程未启动。"; return result; }
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(timeoutMs)) {
                        result.TimedOut = true;
                        try { process.Kill(); } catch (Exception ex) { result.Error = ex.Message; }
                        if (!process.WaitForExit(3000)) {
                            result.CleanupUncertain = true;
                            WriteBlocked = true;
                        }
                    }
                    // Both redirected streams have bounded completion; never use infinite WaitForExit().
                    outputDone.WaitOne(1000);
                    errorDone.WaitOne(1000);
                    lock (gate) {
                        result.Output = output.ToString().Trim();
                        result.Error = (result.Error ?? "") + error.ToString().Trim();
                    }
                    if (!result.TimedOut && !String.IsNullOrWhiteSpace(result.Output)) {
                        result.Worker = new JavaScriptSerializer().Deserialize<WorkerReply>(result.Output);
                    }
                } catch (Exception ex) {
                    result.Error = ex.GetType().Name + ": " + ex.Message;
                    try {
                        if (result.Started && !process.HasExited) {
                            process.Kill();
                            if (!process.WaitForExit(3000)) result.CleanupUncertain = true;
                        }
                    } catch { result.CleanupUncertain = true; }
                    if (result.CleanupUncertain) WriteBlocked = true;
                }
            }
            return result;
        }
    }

    internal static class Program
    {
        internal static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        private static void Output(WorkerReply reply)
        {
            Console.WriteLine(Json.Serialize(reply));
        }

        private static int Worker(string[] args)
        {
            WorkerReply reply = new WorkerReply();
            try {
                if (args.Length == 2 && args[1] == "list") {
                    ProbeResult probe = Native.Collect(true);
                    reply.Probe = new ProbePayload { Displays = probe.Displays, Warnings = probe.Warnings };
                    reply.Ok = true;
                } else if (args.Length == 4 && args[1] == "read") {
                    reply.Control = MonitorControl.Execute(args[2], args[3], -1, null);
                    reply.Ok = reply.Control.Success;
                } else if (args.Length == 8 && args[1] == "set") {
                    int expected, value, original, target;
                    if (!Int32.TryParse(args[4], out expected) || !Int32.TryParse(args[5], out value) ||
                        !Int32.TryParse(args[6], out original) || !Int32.TryParse(args[7], out target) ||
                        !SwitchPolicy.IsTargetPort(target) || original < 0 || original > ushort.MaxValue ||
                        (value != target && value != original)) {
                        reply.Error = "输入值不在允许范围。";
                    } else {
                        reply.Control = MonitorControl.Execute(args[2], args[3], expected, value);
                        reply.Ok = reply.Control.Success;
                    }
                } else reply.Error = "未知 worker 命令。";
            } catch (Exception ex) { reply.Error = ex.GetType().Name + ": " + ex.Message; }
            Output(reply);
            return reply.Ok ? 0 : 1;
        }

        private static int Guardian(string[] args)
        {
            if (args.Length != 7) return 2;
            string directory = args[1], id = args[2], name = args[3], runId = args[6];
            int original, target;
            if (!Int32.TryParse(args[4], out original) || !Int32.TryParse(args[5], out target) ||
                original < 0 || original > ushort.MaxValue || !SwitchPolicy.IsTargetPort(target)) return 2;
            TrialReply trial = new TrialReply { RunId = runId };
            try {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "ready"), runId);
                Stopwatch elapsed = Stopwatch.StartNew();
                RunReply targetRun = ProcessRunner.Run(10000, true, "--worker", "set", id, name,
                    original.ToString(), target.ToString(), original.ToString(), target.ToString());
                trial.Target = targetRun.Worker ?? new WorkerReply { Error = targetRun.Error ??
                    (targetRun.TimedOut ? "目标切换超时。" : "目标切换失败。") };
                trial.CleanupUncertain = targetRun.CleanupUncertain;
                bool rejected = targetRun.Worker != null && targetRun.Worker.RejectedBeforeWrite;
                while (!rejected && elapsed.ElapsedMilliseconds < 10000 &&
                    !File.Exists(Path.Combine(directory, "restore"))) {
                    Thread.Sleep(200);
                }
                // A rejected worker never wrote a target input. A timed-out worker may have written;
                // recover only once its exit was confirmed, with the same global worker lock.
                bool targetCouldHaveWritten = targetRun.Started && !rejected;
                if (!trial.CleanupUncertain && targetCouldHaveWritten) {
                    // Recovery permits a changed current value, but still requires a fresh readable input.
                    RunReply returnRun = ProcessRunner.Run(20000, true, "--worker", "set", id, name,
                        "-1", original.ToString(), original.ToString(), target.ToString());
                    trial.Return = returnRun.Worker ?? new WorkerReply { Error = returnRun.Error ??
                        (returnRun.TimedOut ? "回切超时。" : "回切失败。") };
                    trial.CleanupUncertain = returnRun.CleanupUncertain;
                }
            } catch (Exception ex) {
                trial.Return = new WorkerReply { Error = ex.GetType().Name + ": " + ex.Message };
            }
            try {
                string temp = Path.Combine(directory, "result.tmp");
                File.WriteAllText(temp, Json.Serialize(trial), Encoding.UTF8);
                File.Move(temp, Path.Combine(directory, "result.json"));
            } catch { return 3; }
            return 0;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--worker") {
                bool freshWorker;
                using (Mutex marker = new Mutex(true, @"Local\SwitchDeskDdcWorker", out freshWorker)) {
                    if (!freshWorker) {
                        Output(new WorkerReply { Error = "DDC worker 正在运行。", RejectedBeforeWrite = true });
                        return 2;
                    }
                    try { return Worker(args); }
                    finally { marker.ReleaseMutex(); }
                }
            }
            if (args.Length > 0 && args[0] == "--guardian") {
                bool freshTrial;
                using (Mutex marker = new Mutex(true, @"Local\SwitchDeskTestTrial", out freshTrial)) {
                    if (!freshTrial) return 2;
                    try { return Guardian(args); }
                    finally { marker.ReleaseMutex(); }
                }
            }
            bool fresh;
            using (Mutex mutex = new Mutex(true, @"Local\SwitchDeskGui", out fresh)) {
                if (!fresh) {
                    MessageBox.Show("双栖已打开，请使用现有窗口。", "双栖");
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            return 0;
        }
    }
}
