using System;

namespace SwitchDesk.Desktop {
    public static class GlobalHotkeysHarness {
        private static void Expect(GlobalHotkeyAction got, GlobalHotkeyAction expected, string reason) {
            if (got != expected) throw new Exception(reason + ": expected " + expected + ", got " + got);
        }
        public static void Run(string scenario) {
            var keys = new GlobalHotkeys();
            if (scenario == "f12") {
                Expect(keys.Handle(0x7B, true, false, false), GlobalHotkeyAction.Switch, "F12 first press");
                for (int i = 0; i < 20; i++) Expect(keys.Handle(0x7B, true, false, false), GlobalHotkeyAction.Consume, "held F12");
                Expect(keys.Handle(0x7B, false, false, false), GlobalHotkeyAction.Consume, "F12 release");
                Expect(keys.Handle(0x7B, true, false, false), GlobalHotkeyAction.Switch, "next press");
            } else if (scenario == "modifiers") {
                Expect(keys.Handle(0x7B, true, true, true), GlobalHotkeyAction.Switch, "F12 with modifiers");
                Expect(keys.Handle(0x7B, true, false, false), GlobalHotkeyAction.Consume, "modifiers released while F12 held");
                Expect(keys.Handle(0x7B, false, false, false), GlobalHotkeyAction.Consume, "release swallowed");
            } else if (scenario == "emergency") {
                Expect(keys.Handle(0x1B, true, true, true), GlobalHotkeyAction.Emergency, "emergency preserved");
                Expect(keys.Handle(0x1B, true, false, false), GlobalHotkeyAction.Consume, "emergency repeat swallowed");
                Expect(keys.Handle(0x1B, false, false, false), GlobalHotkeyAction.Consume, "emergency release");
                Expect(keys.Handle(0x1B, true, false, false), GlobalHotkeyAction.None, "ordinary Esc");
            } else if (scenario == "ordinary") {
                Expect(keys.Handle(0x20, true, true, true), GlobalHotkeyAction.None, "old shortcut no longer switches");
                Expect(keys.Handle(0x20, false, true, true), GlobalHotkeyAction.None, "ordinary space release");
                Expect(keys.Handle(0x43, true, true, false), GlobalHotkeyAction.None, "copy handled by ordinary routing");
                Expect(keys.Handle(0x7A, true, false, false), GlobalHotkeyAction.None, "F11");
            } else if (scenario == "reset") {
                keys.Handle(0x7B, true, false, false); keys.Reset();
                Expect(keys.Handle(0x7B, true, false, false), GlobalHotkeyAction.Switch, "new hook session");
            } else throw new Exception("Unknown scenario");
        }
    }
}
