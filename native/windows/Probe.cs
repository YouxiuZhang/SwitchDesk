using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SwitchDesk
{
    public sealed class DdcInfo
    {
        public string Status;
        public int? Value;
        public string Detail;
    }

    public sealed class DisplayInfo
    {
        public string Id;
        public string Name;
        public int Width;
        public int Height;
        public double? RefreshHz;
        public DdcInfo DdcInput;
    }

    public sealed class ProbeResult
    {
        public readonly List<DisplayInfo> Displays = new List<DisplayInfo>();
        public readonly List<string> Warnings = new List<string>();
    }

    public static class Native
    {
        private const int ENUM_CURRENT_SETTINGS = -1;
        private const uint ERROR_VCP_NOT_SUPPORTED = 0xC0262584;
        private const uint ERROR_I2C_NOT_SUPPORTED = 0xC0262580;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEXW
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Explicit, Size = 16)]
        public struct DEVMODE_UNION
        {
            [FieldOffset(0)] public short dmOrientation;
            [FieldOffset(2)] public short dmPaperSize;
            [FieldOffset(4)] public short dmPaperLength;
            [FieldOffset(6)] public short dmPaperWidth;
            [FieldOffset(8)] public short dmScale;
            [FieldOffset(10)] public short dmCopies;
            [FieldOffset(12)] public short dmDefaultSource;
            [FieldOffset(14)] public short dmPrintQuality;
            [FieldOffset(0)] public int dmPositionX;
            [FieldOffset(4)] public int dmPositionY;
            [FieldOffset(8)] public uint dmDisplayOrientation;
            [FieldOffset(12)] public uint dmDisplayFixedOutput;
        }

        // The complete public DEVMODEW ABI: 220 bytes, including its 16-byte union.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODEW
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public DEVMODE_UNION dmUnion;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEXW info);

        [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref DEVMODEW mode);

        [DllImport("Dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

        [DllImport("Dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PHYSICAL_MONITOR[] physical);

        [DllImport("Dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyPhysicalMonitors(uint count, [In] PHYSICAL_MONITOR[] physical);

        [DllImport("Dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, out int codeType,
            out uint current, out uint maximum);

        private static string ErrorCode(int code)
        {
            return "0x" + unchecked((uint)code).ToString("X8");
        }

        private static DdcInfo DdcError(string operation, int code)
        {
            uint unsignedCode = unchecked((uint)code);
            bool unsupported = unsignedCode == ERROR_VCP_NOT_SUPPORTED || unsignedCode == ERROR_I2C_NOT_SUPPORTED;
            return new DdcInfo {
                Status = unsupported ? "unsupported" : "error",
                Value = null,
                Detail = operation + " failed (Win32 " + ErrorCode(code) + ")."
            };
        }

        private static DisplayInfo CopyDisplay(DisplayInfo source, string id, string name, DdcInfo ddc)
        {
            return new DisplayInfo {
                Id = id, Name = name, Width = source.Width, Height = source.Height,
                RefreshHz = source.RefreshHz, DdcInput = ddc
            };
        }

        private static string PhysicalName(string logicalName, string description, int number)
        {
            string fallback = logicalName + " physical " + number;
            if (String.IsNullOrWhiteSpace(description)) return fallback;
            string model = description.Trim();
            if (model.Length > 120 || Regex.IsMatch(model,
                @"(?i)(\bserial|\bs/n\b|\bsn\s*[:#]|\bedid\b|\b(?:\d{1,3}\.){3}\d{1,3}\b|@|\\|[\x00-\x1F])")) {
                return fallback;
            }
            return model + " (" + fallback + ")";
        }

        private static void AddDdcDisplays(IntPtr monitor, DisplayInfo logical, ProbeResult result)
        {
            uint count;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out count)) {
                result.Displays.Add(CopyDisplay(logical, logical.Id, logical.Name,
                    DdcError("GetNumberOfPhysicalMonitorsFromHMONITOR", Marshal.GetLastWin32Error())));
                return;
            }
            if (count == 0 || count > 256) {
                result.Displays.Add(CopyDisplay(logical, logical.Id, logical.Name,
                    new DdcInfo { Status = "error", Value = null, Detail = "Physical monitor count was zero or excessive." }));
                return;
            }
            PHYSICAL_MONITOR[] physical = new PHYSICAL_MONITOR[(int)count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical)) {
                result.Displays.Add(CopyDisplay(logical, logical.Id, logical.Name,
                    DdcError("GetPhysicalMonitorsFromHMONITOR", Marshal.GetLastWin32Error())));
                return;
            }
            try {
                for (int i = 0; i < physical.Length; i++) {
                    string id = logical.Id + "/physical-" + (i + 1);
                    string name = PhysicalName(logical.Name,
                        physical[i].szPhysicalMonitorDescription, i + 1);
                    int codeType;
                    uint current, maximum;
                    DdcInfo ddc;
                    if (!GetVCPFeatureAndVCPFeatureReply(physical[i].hPhysicalMonitor, 0x60,
                        out codeType, out current, out maximum)) {
                        ddc = DdcError("GetVCPFeatureAndVCPFeatureReply(0x60)", Marshal.GetLastWin32Error());
                    } else if (current > ushort.MaxValue) {
                        ddc = new DdcInfo { Status = "error", Value = null,
                            Detail = "VCP 0x60 returned a value outside uint16." };
                    } else {
                        ddc = new DdcInfo { Status = "readable", Value = (int)current,
                            Detail = "Read current VCP 0x60 input value; switching was not tested." };
                    }
                    result.Displays.Add(CopyDisplay(logical, id, name, ddc));
                }
            } finally {
                if (!DestroyPhysicalMonitors(count, physical)) {
                    result.Warnings.Add("DestroyPhysicalMonitors failed (Win32 " +
                        ErrorCode(Marshal.GetLastWin32Error()) + ").");
                }
            }
        }

        public static ProbeResult Collect(bool readDdc)
        {
            ProbeResult result = new ProbeResult();
            int index = 0;
            MonitorEnumProc callback = delegate(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data) {
                index++;
                MONITORINFOEXW info = new MONITORINFOEXW();
                info.cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFOEXW));
                if (!GetMonitorInfo(monitor, ref info)) {
                    result.Warnings.Add("GetMonitorInfoW failed for display " + index + " (Win32 " +
                        ErrorCode(Marshal.GetLastWin32Error()) + ").");
                    return true;
                }
                DEVMODEW mode = new DEVMODEW();
                mode.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODEW));
                if (!EnumDisplaySettings(info.szDevice, ENUM_CURRENT_SETTINGS, ref mode)) {
                    result.Warnings.Add("EnumDisplaySettingsW failed for display " + index + " (Win32 " +
                        ErrorCode(Marshal.GetLastWin32Error()) + "); physical pixel size is unavailable.");
                    return true;
                }
                if (mode.dmPelsWidth == 0 || mode.dmPelsHeight == 0 ||
                    mode.dmPelsWidth > int.MaxValue || mode.dmPelsHeight > int.MaxValue) {
                    result.Warnings.Add("Display " + index + " returned an invalid physical pixel size; skipped.");
                    return true;
                }
                string id = String.IsNullOrWhiteSpace(info.szDevice)
                    ? "display-" + index : info.szDevice + "/logical-" + index;
                DisplayInfo logical = new DisplayInfo {
                    Id = id, Name = "Display " + index, Width = (int)mode.dmPelsWidth,
                    Height = (int)mode.dmPelsHeight,
                    RefreshHz = mode.dmDisplayFrequency > 1 ? (double?)mode.dmDisplayFrequency : null,
                    DdcInput = new DdcInfo { Status = "not_tested", Value = null,
                        Detail = "DDC input was not read." }
                };
                if (readDdc) {
                    try {
                        AddDdcDisplays(monitor, logical, result);
                    } catch (DllNotFoundException) {
                        result.Displays.Add(CopyDisplay(logical, logical.Id, logical.Name,
                            new DdcInfo { Status = "error", Value = null,
                                Detail = "DDC library is unavailable; no Win32 error code was returned." }));
                    } catch (EntryPointNotFoundException) {
                        result.Displays.Add(CopyDisplay(logical, logical.Id, logical.Name,
                            new DdcInfo { Status = "error", Value = null,
                                Detail = "DDC entry point is unavailable; no Win32 error code was returned." }));
                    }
                } else result.Displays.Add(logical);
                return true;
            };
            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)) {
                result.Warnings.Add("EnumDisplayMonitors failed (Win32 " +
                    ErrorCode(Marshal.GetLastWin32Error()) + ").");
            }
            if (result.Displays.Count == 0) {
                result.Warnings.Add("No reportable displays were enumerated in this session; this does not mean none are connected.");
            }
            GC.KeepAlive(callback);
            return result;
        }
    }
}
