using System;

namespace SwitchDesk
{
    // This file intentionally depends only on System so its safety rules run on macOS too.
    public static class SwitchPolicy
    {
        public static bool IsTargetPort(int value)
        {
            return value == 17 || value == 18 || value == 15;
        }
    }

    public sealed class SwitchState
    {
        public string DisplayId { get; private set; }
        public string DisplayName { get; private set; }
        public int OriginalInput { get; private set; }
        public int TargetInput { get; private set; }
        public bool TrialReturned { get; private set; }
        public bool UserConfirmed { get; private set; }
        public bool Busy { get; private set; }

        public void Configure(string id, string name, int original, int target)
        {
            Invalidate();
            if (String.IsNullOrEmpty(id) || String.IsNullOrEmpty(name) ||
                original < 0 || original > ushort.MaxValue || !SwitchPolicy.IsTargetPort(target) ||
                original == target) return;
            DisplayId = id;
            DisplayName = name;
            OriginalInput = original;
            TargetInput = target;
        }

        public void Invalidate()
        {
            DisplayId = null;
            DisplayName = null;
            OriginalInput = -1;
            TargetInput = -1;
            TrialReturned = false;
            UserConfirmed = false;
            Busy = false;
        }

        public bool CanTrial()
        {
            return !Busy && DisplayId != null && SwitchPolicy.IsTargetPort(TargetInput) &&
                OriginalInput >= 0 && OriginalInput != TargetInput;
        }

        public bool Begin()
        {
            if (Busy) return false;
            Busy = true;
            return true;
        }

        public void End() { Busy = false; }

        public void RecordTrial(int original, int restored, bool targetWriteReturned, bool returnReadConfirmed)
        {
            TrialReturned = DisplayId != null && targetWriteReturned && returnReadConfirmed &&
                original == OriginalInput && restored == OriginalInput;
            UserConfirmed = false;
        }

        public void ConfirmSeen()
        {
            if (TrialReturned) UserConfirmed = true;
        }

        public void RevokeConfirmation()
        {
            UserConfirmed = false;
        }

        public bool CanNormalSwitch()
        {
            return !Busy && TrialReturned && UserConfirmed && DisplayId != null &&
                SwitchPolicy.IsTargetPort(TargetInput);
        }
    }
}
