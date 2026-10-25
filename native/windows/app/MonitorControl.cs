using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace SwitchDesk
{
    public sealed class ControlResult
    {
        public bool Success;
        public bool WriteReturned;
        public bool ReadbackOk;
        public bool ReadbackUnconfirmed;
        public int? Before;
        public int? Readback;
        public string Detail;
        // Command timings, not the display panel's blackout or video-settling time.
        public long TotalMs;
        public long DiscoveryMs;
        public long ReadBeforeMs;
        public long WriteMs;
        public long ReadbackMs;
        public int ReadbackAttempts;
    }

    internal static class DdcWritePolicy
    {
        private const int ReadbackBudgetMs = 900, MaximumAttempts = 3, RetryDelayMs = 250;

        // Native calls are synchronous and cannot be interrupted here. The budget bounds
        // scheduling of retries, not the duration of an in-flight driver call.
        internal static void Execute(ControlResult answer, int requested, Func<bool> write,
            Func<int?> read, Func<long> elapsedMilliseconds, Action<int> delay, Action refresh)
        {
            long writeStarted = elapsedMilliseconds();
            try { answer.WriteReturned = write(); }
            finally { answer.WriteMs = elapsedMilliseconds() - writeStarted; }

            long readbackStarted = elapsedMilliseconds();
            bool refreshed = false;
            try {
                for (int attempt = 0; attempt < MaximumAttempts; attempt++) {
                    if (attempt > 0) {
                        long remaining = ReadbackBudgetMs - (elapsedMilliseconds() - readbackStarted);
                        if (remaining <= 0) break;
                        delay((int)Math.Min(RetryDelayMs, remaining));
                        if (elapsedMilliseconds() - readbackStarted >= ReadbackBudgetMs) break;
                        if (!answer.ReadbackOk && !refreshed) {
                            refreshed = true;
                            refresh();
                            if (elapsedMilliseconds() - readbackStarted >= ReadbackBudgetMs) break;
                        }
                    }
                    answer.ReadbackAttempts++;
                    answer.Readback = read();
                    answer.ReadbackOk = answer.Readback.HasValue;
                    answer.Success = answer.WriteReturned && answer.ReadbackOk && answer.Readback.Value == requested;
                    if (answer.Success || !answer.WriteReturned) break;
                }
            } finally { answer.ReadbackMs = elapsedMilliseconds() - readbackStarted; }
            answer.ReadbackUnconfirmed = answer.WriteReturned && !answer.Success;
            answer.Detail = answer.Success ? "写入返回成功且回读一致。" :
                (answer.ReadbackOk ? "切换未确认：写入返回或回读值不一致。" :
                    "切换未确认：写入后回读失败。");
        }
    }

    public static class MonitorControl
    {
        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref Native.MONITORINFOEXW info);

        [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref Native.DEVMODEW mode);

        [DllImport("Dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

        [DllImport("Dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] Native.PHYSICAL_MONITOR[] physical);

        [DllImport("Dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyPhysicalMonitors(uint count, [In] Native.PHYSICAL_MONITOR[] physical);

        [DllImport("Dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, out int type,
            out uint current, out uint maximum);

        [DllImport("Dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);

        private static string PhysicalName(string logicalName, string description, int number)
        {
            string fallback = logicalName + " physical " + number;
            if (String.IsNullOrWhiteSpace(description)) return fallback;
            string model = description.Trim();
            if (model.Length > 120 || Regex.IsMatch(model,
                @"(?i)(\bserial|\bs/n\b|\bsn\s*[:#]|\bedid\b|\b(?:\d{1,3}\.){3}\d{1,3}\b|@|\\|[\x00-\x1F])"))
                return fallback;
            return model + " (" + fallback + ")";
        }

        private static bool ReadInput(IntPtr handle, out int value)
        {
            int type;
            uint current, maximum;
            value = -1;
            if (!GetVCPFeatureAndVCPFeatureReply(handle, 0x60, out type, out current, out maximum) ||
                current > ushort.MaxValue) return false;
            value = (int)current;
            return true;
        }

        // Each callback owns every physical handle it obtains. ID and sanitized name must both match A1.
        public static ControlResult Execute(string id, string name, int expected, int? requested)
        {
            Stopwatch total = Stopwatch.StartNew();
            ControlResult answer = new ControlResult { Detail = "未找到所选显示器。" };
            if (String.IsNullOrEmpty(id) || String.IsNullOrEmpty(name) ||
                (requested.HasValue && (requested.Value < 0 || requested.Value > ushort.MaxValue))) {
                answer.Detail = "显示器或输入值无效。";
                answer.TotalMs = total.ElapsedMilliseconds;
                return answer;
            }
            int index = 0;
            int matches = 0;
            // Includes initial enumeration and final handle cleanup. Retry handle discovery
            // is included in ReadbackMs so it consumes the verification budget.
            Stopwatch discovery = Stopwatch.StartNew();
            MonitorEnumProc callback = delegate(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data) {
                index++;
                Native.MONITORINFOEXW info = new Native.MONITORINFOEXW();
                info.cbSize = (uint)Marshal.SizeOf(typeof(Native.MONITORINFOEXW));
                if (!GetMonitorInfo(monitor, ref info)) return true;
                Native.DEVMODEW mode = new Native.DEVMODEW();
                mode.dmSize = (ushort)Marshal.SizeOf(typeof(Native.DEVMODEW));
                if (!EnumDisplaySettings(info.szDevice, -1, ref mode) || mode.dmPelsWidth == 0 ||
                    mode.dmPelsHeight == 0 || mode.dmPelsWidth > int.MaxValue ||
                    mode.dmPelsHeight > int.MaxValue) return true;
                string logicalId = String.IsNullOrWhiteSpace(info.szDevice)
                    ? "display-" + index : info.szDevice + "/logical-" + index;
                uint count;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out count) || count == 0 || count > 256)
                    return true;
                Native.PHYSICAL_MONITOR[] physical = new Native.PHYSICAL_MONITOR[(int)count];
                if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical)) return true;
                Native.PHYSICAL_MONITOR[] refreshedPhysical = null;
                try {
                    for (int i = 0; i < physical.Length; i++) {
                        string actualId = logicalId + "/physical-" + (i + 1);
                        string actualName = PhysicalName("Display " + index,
                            physical[i].szPhysicalMonitorDescription, i + 1);
                        if (!String.Equals(id, actualId, StringComparison.Ordinal) ||
                            !String.Equals(name, actualName, StringComparison.Ordinal)) continue;
                        matches++;
                        if (matches != 1) continue;
                        discovery.Stop();
                        try {
                            int before;
                            bool readBeforeOk;
                            long readStarted = total.ElapsedMilliseconds;
                            try { readBeforeOk = ReadInput(physical[i].hPhysicalMonitor, out before); }
                            finally { answer.ReadBeforeMs = total.ElapsedMilliseconds - readStarted; }
                            if (!readBeforeOk) {
                                answer.Detail = "操作前读取 VCP 0x60 失败，未写入。";
                                continue;
                            }
                            answer.Before = before;
                            if (expected >= 0 && before != expected) {
                                answer.Detail = "当前输入已变化，未写入。";
                                continue;
                            }
                            if (!requested.HasValue) {
                                answer.Success = true;
                                answer.ReadbackOk = true;
                                answer.Readback = before;
                                answer.Detail = "读取完成。";
                                continue;
                            }
                            IntPtr readHandle = physical[i].hPhysicalMonitor;
                            DdcWritePolicy.Execute(answer, requested.Value,
                                delegate { return SetVCPFeature(readHandle, 0x60, (uint)requested.Value); },
                                delegate { int after; return ReadInput(readHandle, out after) ? (int?)after : null; },
                                delegate { return total.ElapsedMilliseconds; }, Thread.Sleep,
                                delegate {
                                    // A successful input switch can invalidate its physical handle.
                                    // Reopen at most once, and only use a handle for the same identity.
                                    Native.MONITORINFOEXW refreshedInfo = new Native.MONITORINFOEXW();
                                    refreshedInfo.cbSize = (uint)Marshal.SizeOf(typeof(Native.MONITORINFOEXW));
                                    uint refreshedCount;
                                    if (!GetMonitorInfo(monitor, ref refreshedInfo) ||
                                        !String.Equals(info.szDevice, refreshedInfo.szDevice, StringComparison.Ordinal) ||
                                        !GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out refreshedCount) ||
                                        refreshedCount != count) return;
                                    Native.PHYSICAL_MONITOR[] candidate = new Native.PHYSICAL_MONITOR[(int)refreshedCount];
                                    if (!GetPhysicalMonitorsFromHMONITOR(monitor, refreshedCount, candidate)) return;
                                    refreshedPhysical = candidate;
                                    if (candidate[i].hPhysicalMonitor != IntPtr.Zero && String.Equals(name,
                                        PhysicalName("Display " + index, candidate[i].szPhysicalMonitorDescription, i + 1),
                                        StringComparison.Ordinal)) readHandle = candidate[i].hPhysicalMonitor;
                                });
                        } finally { discovery.Start(); }
                    }
                } finally {
                    try {
                        if (refreshedPhysical != null && !DestroyPhysicalMonitors((uint)refreshedPhysical.Length, refreshedPhysical)) {
                            answer.Success = false; answer.ReadbackUnconfirmed = false;
                            answer.Detail += " 释放刷新后的物理显示器句柄失败。";
                        }
                    } finally {
                        if (!DestroyPhysicalMonitors(count, physical)) {
                            answer.Success = false; answer.ReadbackUnconfirmed = false;
                            answer.Detail += " 释放物理显示器句柄失败。";
                        }
                    }
                }
                return true;
            };
            try {
                if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)) {
                    answer.Success = false; answer.ReadbackUnconfirmed = false; answer.Detail = "枚举显示器失败。";
                }
            } catch (Exception ex) {
                answer.Success = false; answer.ReadbackUnconfirmed = false;
                answer.Detail = ex.GetType().Name + ": " + ex.Message;
            } finally { discovery.Stop(); answer.DiscoveryMs = discovery.ElapsedMilliseconds; }
            GC.KeepAlive(callback);
            if (matches > 1) {
                answer.Success = false; answer.ReadbackUnconfirmed = false;
                answer.Detail = "显示器身份不唯一，禁止写入。";
            }
            answer.TotalMs = total.ElapsedMilliseconds;
            return answer;
        }
    }
}
