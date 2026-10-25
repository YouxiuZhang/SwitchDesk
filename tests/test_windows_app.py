import os
import pathlib
import shutil
import subprocess
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[1]
PWSH = os.environ.get("SWITCHDESK_PWSH") or shutil.which("pwsh")


@unittest.skipUnless(PWSH, "需要 PowerShell 运行纯 System 核心测试")
class WindowsAppCoreTests(unittest.TestCase):
    def core(self, expression):
        script = (
            "$ErrorActionPreference='Stop';"
            "Add-Type -Path 'native/windows/app/Core.cs' -CompilerOptions '/langversion:5';"
            + expression
        )
        completed = subprocess.run(
            [PWSH, "-NoProfile", "-NonInteractive", "-Command", script],
            cwd=ROOT,
            text=True,
            capture_output=True,
            timeout=20,
            env={**os.environ, "POWERSHELL_TELEMETRY_OPTOUT": "1"},
        )
        self.assertEqual(completed.returncode, 0, completed.stderr)
        return completed.stdout.strip()

    def test_target_port_whitelist(self):
        self.assertEqual(
            self.core("17,18,15,16,0,65535 | ForEach-Object { [SwitchDesk.SwitchPolicy]::IsTargetPort($_) }"),
            "True\nTrue\nTrue\nFalse\nFalse\nFalse",
        )

    def test_confirmation_requires_completed_round_trip(self):
        self.assertEqual(
            self.core("$s=New-Object SwitchDesk.SwitchState;"
                      "$s.Configure('id','name',17,18);"
                      "$s.CanNormalSwitch();"
                      "$s.RecordTrial(17,17,$true,$true);"
                      "$s.CanNormalSwitch();"
                      "$s.ConfirmSeen();"
                      "$s.CanNormalSwitch();"
                      "$s.Invalidate();"
                      "$s.CanNormalSwitch()"),
            "False\nFalse\nTrue\nFalse",
        )

    def test_unconfirmed_readback_cannot_complete_trial(self):
        self.assertEqual(
            self.core("$s=New-Object SwitchDesk.SwitchState;"
                      "$s.Configure('id','name',17,18);"
                      "$s.RecordTrial(17,17,$true,$false);"
                      "$s.ConfirmSeen();"
                      "$s.CanNormalSwitch()"),
            "False",
        )

    def test_busy_state_blocks_confirmed_hotkey_actions(self):
        self.assertEqual(
            self.core("$s=New-Object SwitchDesk.SwitchState;"
                      "$s.Configure('id','name',17,18);"
                      "$s.RecordTrial(17,17,$true,$true);"
                      "$s.ConfirmSeen();"
                      "$s.Begin() | Out-Null;"
                      "$s.CanNormalSwitch();"
                      "$s.End();"
                      "$s.CanNormalSwitch()"),
            "False\nTrue",
        )

    def test_unchecking_confirmation_disables_actions(self):
        self.assertEqual(
            self.core("$s=New-Object SwitchDesk.SwitchState;"
                      "$s.Configure('id','name',17,18);"
                      "$s.RecordTrial(17,17,$true,$true);"
                      "$s.ConfirmSeen();"
                      "$s.CanNormalSwitch();"
                      "$s.RevokeConfirmation();"
                      "$s.CanNormalSwitch()"),
            "True\nFalse",
        )

    def test_invalid_mapping_cannot_trial(self):
        self.assertEqual(
            self.core("$s=New-Object SwitchDesk.SwitchState;"
                      "$s.Configure('','name',17,18);$s.CanTrial();"
                      "$s.Configure('id','name',-1,18);$s.CanTrial();"
                      "$s.Configure('id','name',18,18);$s.CanTrial();"
                      "$s.Configure('id','name',17,18);$s.CanTrial()"),
            "False\nFalse\nFalse\nTrue",
        )


if __name__ == "__main__":
    unittest.main()
