using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace SwitchDesk.Desktop {
    internal sealed class InputEngine : IDisposable {
        private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        private const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_MOUSEWHEEL = 0x20A, WM_MOUSEHWHEEL = 0x20E;
        private const uint KEYEVENTF_KEYUP = 2, MOUSEEVENTF_MOVE = 1, MOUSEEVENTF_LEFTDOWN = 2, MOUSEEVENTF_LEFTUP = 4, MOUSEEVENTF_RIGHTDOWN = 8, MOUSEEVENTF_RIGHTUP = 16, MOUSEEVENTF_MIDDLEDOWN = 32, MOUSEEVENTF_MIDDLEUP = 64, MOUSEEVENTF_WHEEL = 0x800, MOUSEEVENTF_HWHEEL = 0x1000;
        [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion u; }
        [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mouse; [FieldOffset(0)] public KEYBDINPUT key; }
        [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, flags, time; public IntPtr extraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extraInfo; }
        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        private readonly object gate = new object();
        private readonly HashSet<int> injectedKeys = new HashSet<int>();
        private readonly HashSet<int> injectedButtons = new HashSet<int>();
        private readonly HashSet<int> forwardedKeys = new HashSet<int>();
        private readonly HashSet<int> physicalKeys = new HashSet<int>();
        private readonly HashSet<int> hotkeyKeys = new HashSet<int>();
        private readonly GlobalHotkeys globalHotkeys = new GlobalHotkeys();
        private readonly HookProc keyboardProc, mouseProc;
        private IntPtr keyboardHook, mouseHook;
        private Timer leaseTimer;
        private DateTime lease = DateTime.MinValue;
        private int epoch; private bool forward, receive, shortcutMapping;
        private bool injectionFailed;

        internal Action<Dictionary<string, object>> Send;
        internal Action Switch, Emergency;
        internal Action<string> Status;
        internal Action InputUnavailable;
        internal InputEngine() { keyboardProc = Keyboard; mouseProc = Mouse; }
        internal bool Installed { get { return keyboardHook != IntPtr.Zero && mouseHook != IntPtr.Zero; } }
        internal void SetShortcutMapping(bool value) { lock (gate) shortcutMapping = value; }
        internal bool Install() {
            IntPtr module = GetModuleHandle(null);
            keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, module, 0);
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, module, 0);
            if (!Installed) { Dispose(); return false; }
            leaseTimer = new Timer(CheckLease, null, 500, 500); return true;
        }
        internal void Configure(int newEpoch, bool shouldForward, bool shouldReceive) {
            lock (gate) {
                if (epoch != newEpoch || !shouldReceive) ReleaseInjected();
                if (!shouldForward && forward) ForwardRelease();
                epoch = newEpoch; forward = shouldForward && Installed && !injectionFailed; receive = shouldReceive && Installed && !injectionFailed;
            }
        }
        internal void Heartbeat() { lock (gate) lease = DateTime.UtcNow.AddSeconds(3); }
        private void CheckLease(object ignored) { lock (gate) if (receive && DateTime.UtcNow > lease) { receive = false; ReleaseInjected(); } }
        internal void Inject(IDictionary<string, object> eventData, long packetEpoch) {
            if (eventData == null) return;
            lock (gate) {
                if (!receive || packetEpoch != epoch || DateTime.UtcNow > lease) return;
                string kind = Wire.S(eventData, "kind");
                if (kind == "release") { ReleaseInjected(); return; }
                if (kind == "key") {
                    long rawVk = Wire.L(eventData, "code", -1); bool down = Wire.B(eventData, "down");
                    if (rawVk < 1 || rawVk > 254 || rawVk == 0x13) return;
                    int vk = (int)rawVk;
                    if (down) { injectedKeys.Add(vk); if (!Key(vk, false)) FailInjection(); }
                    else if (injectedKeys.Remove(vk) && !Key(vk, true)) FailInjection();
                } else if (kind == "move") {
                    long rawDx = Wire.L(eventData, "dx", 99999), rawDy = Wire.L(eventData, "dy", 99999);
                    if (rawDx >= -2048 && rawDx <= 2048 && rawDy >= -2048 && rawDy <= 2048 && !MouseInput(MOUSEEVENTF_MOVE, (int)rawDx, (int)rawDy, 0)) FailInjection();
                } else if (kind == "button") {
                    long rawButton = Wire.L(eventData, "button", -1); bool down = Wire.B(eventData, "down");
                    if (rawButton < 0 || rawButton > 2) return;
                    int button = (int)rawButton;
                    if (down && injectedButtons.Add(button) && !MouseInput(ButtonFlag(button, true), 0, 0, 0)) FailInjection();
                    else if (!down && injectedButtons.Remove(button) && !MouseInput(ButtonFlag(button, false), 0, 0, 0)) FailInjection();
                } else if (kind == "scroll") {
                    double dy = Number(eventData, "dy"), dx = Number(eventData, "dx");
                    if (Math.Abs(dy) <= 20 && Math.Abs(dx) <= 20) {
                        if (dy != 0 && !MouseInput(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)(int)Math.Round(dy * 120)))) FailInjection();
                        if (dx != 0 && !MouseInput(MOUSEEVENTF_HWHEEL, 0, 0, unchecked((uint)(int)Math.Round(dx * 120)))) FailInjection();
                    }
                } else if (kind == "shortcut" && !Shortcut(Wire.S(eventData, "action"))) FailInjection();
            }
        }
        private static double Number(IDictionary<string, object> value, string key) { object x; if (!value.TryGetValue(key, out x)) return 0; return x is int || x is long || x is double || x is decimal ? Convert.ToDouble(x) : Double.NaN; }
        private bool Shortcut(string action) {
            int vk = action == "copy" ? 0x43 : action == "paste" ? 0x56 : action == "cut" ? 0x58 : action == "undo" ? 0x5A : action == "redo" ? 0x59 : action == "selectAll" ? 0x41 : 0;
            if (vk == 0) return true;
            bool ok = Key(0xA2, false); ok = Key(vk, false) && ok; ok = Key(vk, true) && ok; ok = Key(0xA2, true) && ok; return ok;
        }
        private IntPtr Keyboard(int code, IntPtr w, IntPtr l) {
            if (code < 0) return CallNextHookEx(keyboardHook, code, w, l);
            var data = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(KBDLLHOOKSTRUCT));
            if ((data.flags & 0x10) != 0 || (data.flags & 0x02) != 0) return CallNextHookEx(keyboardHook, code, w, l);
            int msg = w.ToInt32(); bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            int vk = PhysicalVk((int)data.vkCode, data.scanCode, data.flags);
            lock (gate) {
                if (down) physicalKeys.Add(vk); else physicalKeys.Remove(vk);
                bool ctrl = physicalKeys.Contains(0xA2) || physicalKeys.Contains(0xA3);
                bool alt = physicalKeys.Contains(0xA4) || physicalKeys.Contains(0xA5);
                GlobalHotkeyAction globalAction = globalHotkeys.Handle(vk, down, ctrl, alt);
                if (globalAction != GlobalHotkeyAction.None) {
                    if (globalAction == GlobalHotkeyAction.Emergency) {
                        forward = false; receive = false; ForwardRelease(); ReleaseInjected();
                        Action emergency = Emergency;
                        if (emergency != null) ThreadPool.QueueUserWorkItem(delegate { emergency(); });
                    } else if (globalAction == GlobalHotkeyAction.Switch) {
                        Action switchAction = Switch;
                        if (switchAction != null) ThreadPool.QueueUserWorkItem(delegate { switchAction(); });
                    }
                    return (IntPtr)1;
                }
                if (!down && hotkeyKeys.Remove(vk)) return (IntPtr)1;
                if (!forward) return CallNextHookEx(keyboardHook, code, w, l);
                string action = ctrl && !alt && down && shortcutMapping ? ShortcutAction(vk) : null;
                if (action != null) {
                    if (hotkeyKeys.Add(vk)) { ForwardRelease(); SendEvent(Wire.Obj("kind", "shortcut", "action", action)); }
                    return (IntPtr)1;
                }
                if (down) ResyncModifiers(vk);
                if (down) forwardedKeys.Add(vk); else forwardedKeys.Remove(vk);
                SendEvent(Wire.Obj("kind", "key", "code", vk, "down", down));
                return (IntPtr)1;
            }
        }
        private static int PhysicalVk(int vk, uint scan, uint flags) {
            if (vk == 0x10) return scan == 0x36 ? 0xA1 : 0xA0;
            if (vk == 0x11) return (flags & 1) != 0 ? 0xA3 : 0xA2;
            if (vk == 0x12) return (flags & 1) != 0 ? 0xA5 : 0xA4;
            return vk;
        }
        private static string ShortcutAction(int vk) {
            return vk == 0x43 ? "copy" : vk == 0x56 ? "paste" : vk == 0x58 ? "cut" :
                vk == 0x5A ? "undo" : vk == 0x59 ? "redo" : vk == 0x41 ? "selectAll" : null;
        }
        private void ResyncModifiers(int current) {
            int[] modifiers = { 0xA2, 0xA3, 0xA0, 0xA1, 0xA4, 0xA5, 0x5B, 0x5C };
            foreach (int modifier in modifiers) if (modifier != current && physicalKeys.Contains(modifier) && !forwardedKeys.Contains(modifier)) {
                forwardedKeys.Add(modifier); SendEvent(Wire.Obj("kind", "key", "code", modifier, "down", true));
            }
        }
        private IntPtr Mouse(int code, IntPtr w, IntPtr l) {
            if (code < 0) return CallNextHookEx(mouseHook, code, w, l);
            var data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(MSLLHOOKSTRUCT));
            if ((data.flags & 1) != 0) return CallNextHookEx(mouseHook, code, w, l);
            lock (gate) {
                int msg = w.ToInt32();
                if (!forward) return CallNextHookEx(mouseHook, code, w, l);
                if (msg == WM_MOUSEMOVE) {
                    POINT anchor;
                    if (GetCursorPos(out anchor)) { int dx = data.pt.x - anchor.x, dy = data.pt.y - anchor.y; if (Math.Abs(dx) <= 2048 && Math.Abs(dy) <= 2048 && (dx != 0 || dy != 0)) SendEvent(Wire.Obj("kind", "move", "dx", dx, "dy", dy)); }
                } else if (msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL) {
                    short delta = unchecked((short)(data.mouseData >> 16)); SendEvent(Wire.Obj("kind", "scroll", "dx", msg == WM_MOUSEHWHEEL ? delta / 120.0 : 0.0, "dy", msg == WM_MOUSEWHEEL ? delta / 120.0 : 0.0));
                } else {
                    int button = msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP ? 0 : msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP ? 1 : msg == WM_MBUTTONDOWN || msg == WM_MBUTTONUP ? 2 : -1;
                    if (button >= 0) SendEvent(Wire.Obj("kind", "button", "button", button, "down", msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN));
                }
                return (IntPtr)1;
            }
        }
        private void SendEvent(Dictionary<string, object> e) { if (Send != null) Send(Wire.Obj("type", "input", "epoch", epoch, "event", e)); }
        private void ForwardRelease() { forwardedKeys.Clear(); SendEvent(Wire.Obj("kind", "release")); }
        private static uint ButtonFlag(int button, bool down) { return button == 0 ? (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP) : button == 1 ? (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP) : (down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP); }
        private static bool Key(int vk, bool up) { var i = new INPUT { type = 1, u = new InputUnion { key = new KEYBDINPUT { vk = (ushort)vk, flags = up ? KEYEVENTF_KEYUP : 0 } } }; return SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT))) == 1; }
        private static bool MouseInput(uint flags, int dx, int dy, uint data) { var i = new INPUT { type = 0, u = new InputUnion { mouse = new MOUSEINPUT { flags = flags, dx = dx, dy = dy, mouseData = data } } }; return SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT))) == 1; }
        private void FailInjection() {
            if (injectionFailed) return;
            injectionFailed = true; receive = false; forward = false; ReleaseInjected();
            Action<string> status = Status; Action unavailable = InputUnavailable;
            ThreadPool.QueueUserWorkItem(delegate { if (status != null) status("Windows 输入注入失败；远控已停用。请检查 UAC 或安全桌面状态"); if (unavailable != null) unavailable(); });
        }
        private void ReleaseInjected() { foreach (int vk in injectedKeys) Key(vk, true); foreach (int button in injectedButtons) MouseInput(ButtonFlag(button, false), 0, 0, 0); injectedKeys.Clear(); injectedButtons.Clear(); }
        public void Dispose() { lock (gate) { forward = false; receive = false; ReleaseInjected(); if (leaseTimer != null) leaseTimer.Dispose(); if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook); if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook); keyboardHook = mouseHook = IntPtr.Zero; globalHotkeys.Reset(); physicalKeys.Clear(); hotkeyKeys.Clear(); } }
    }
}
