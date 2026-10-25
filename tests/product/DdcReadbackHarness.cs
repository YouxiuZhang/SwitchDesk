// Compile the real write/verification policy on macOS without loading Win32 DLLs.
#pragma warning disable 0649 // Native structures are populated by Win32 only, never by this harness.
using System;
using System.Collections.Generic;

namespace SwitchDesk {
    internal static class Native {
        public struct MONITORINFOEXW { public uint cbSize; public string szDevice; }
        public struct DEVMODEW { public ushort dmSize; public uint dmPelsWidth, dmPelsHeight; }
        public struct PHYSICAL_MONITOR { public IntPtr hPhysicalMonitor; public string szPhysicalMonitorDescription; }
    }

    public static class DdcReadbackHarness {
        public static Dictionary<string, object> Run(string scenario) {
            var row = new Dictionary<string, object>();

            int?[] values = new int?[] { null, 18 };
            int[] readCosts = new int[] { 0, 0, 0 };
            bool writeReturns = true;
            int writeCost = 0, refreshCost = 0, delayOverrun = 0;
            if (scenario == "old_then_target") values = new int?[] { 17, 18 };
            if (scenario == "persistent_error") values = new int?[] { null, null, null };
            if (scenario == "persistent_old") values = new int?[] { 17, 17, 17 };
            if (scenario == "immediate" || scenario == "write_failed") values = new int?[] { 18 };
            if (scenario == "write_failed") writeReturns = false;
            if (scenario == "slow_call") readCosts[0] = 950;
            if (scenario == "budget") { values = new int?[] { null, null, 18 }; readCosts[0] = 400; readCosts[1] = 400; }
            if (scenario == "refresh_budget") refreshCost = 900;
            if (scenario == "sleep_overrun") delayOverrun = 1000;
            if (scenario == "timing") { writeCost = 37; refreshCost = 13; readCosts[0] = 11; readCosts[1] = 17; }
            if (scenario == "old_then_error") values = new int?[] { 17, null, null };

            long now = 0;
            int writes = 0, reads = 0, refreshes = 0;
            var delays = new List<int>();
            Func<bool> write = delegate { writes++; now += writeCost; return writeReturns; };
            Func<int?> read = delegate {
                int position = reads++;
                if (position >= values.Length) throw new InvalidOperationException("Read beyond the scenario's allowed attempts");
                now += readCosts[position];
                return values[position];
            };
            Func<long> elapsed = delegate { return now; };
            Action<int> delay = delegate(int milliseconds) { delays.Add(milliseconds); now += milliseconds + delayOverrun; };
            Action refresh = delegate { refreshes++; now += refreshCost; };
            var result = new ControlResult();
            DdcWritePolicy.Execute(result, 18, write, read, elapsed, delay, refresh);
            row["success"] = result.Success;
            row["write_returned"] = result.WriteReturned;
            row["readback_ok"] = result.ReadbackOk;
            row["readback"] = result.Readback;
            row["writes"] = writes;
            row["reads"] = reads;
            row["refreshes"] = refreshes;
            row["delays"] = delays;
            row["elapsed"] = now;
            row["TotalMs"] = result.TotalMs;
            row["DiscoveryMs"] = result.DiscoveryMs;
            row["ReadBeforeMs"] = result.ReadBeforeMs;
            row["WriteMs"] = result.WriteMs;
            row["ReadbackMs"] = result.ReadbackMs;
            row["ReadbackAttempts"] = result.ReadbackAttempts;
            return row;
        }
    }
}
