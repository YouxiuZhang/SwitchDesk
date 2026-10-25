using System.Collections.Generic;

namespace SwitchDesk.Desktop {
    internal enum GlobalHotkeyAction { None, Consume, Switch, Emergency }

    // Used by the low-level keyboard hook before local/remote input routing.
    internal sealed class GlobalHotkeys {
        private readonly HashSet<int> consumed = new HashSet<int>();

        internal GlobalHotkeyAction Handle(int key, bool down, bool ctrl, bool alt) {
            if (!down) return consumed.Remove(key) ? GlobalHotkeyAction.Consume : GlobalHotkeyAction.None;
            if (consumed.Contains(key)) return GlobalHotkeyAction.Consume;
            GlobalHotkeyAction action = key == 0x7B ? GlobalHotkeyAction.Switch
                : ctrl && alt && key == 0x1B ? GlobalHotkeyAction.Emergency : GlobalHotkeyAction.None;
            if (action != GlobalHotkeyAction.None) consumed.Add(key);
            return action;
        }

        internal void Reset() { consumed.Clear(); }
    }
}
