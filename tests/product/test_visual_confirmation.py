import json
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]


class VisualConfirmationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        paths = ['tests/product/CoordinatorStubs.cs', 'native/windows/product/SwitchState.cs', 'native/windows/product/Coordinator.cs']
        source = '$ErrorActionPreference="Stop"; Add-Type -WarningAction SilentlyContinue -IgnoreWarnings -Path @(' + ','.join("'"+str(ROOT/p)+"'" for p in paths) + ')\n'
        modes = ['accepted', 'missing_permission', 'wrong_target', 'wrong_operation', 'disconnect', 'display_change', 'new_selection', 'expired', 'blocked', 'no_write', 'cleanup', 'unconfirmed', 'replayed', 'worker_error', 'timed_out', 'new_operation']
        source += '$answers = @(' + ';'.join("[SwitchDesk.Desktop.CoordinatorHarness]::VisualConfirmation('"+m+"')" for m in modes) + '); $answers | ConvertTo-Json -Depth 8 -Compress'
        run = subprocess.run([str(ROOT/'.build/tools/powershell-7.6.6/pwsh'), '-NoProfile', '-NonInteractive', '-Command', source], capture_output=True, text=True, timeout=30)
        if run.returncode:
            raise AssertionError(run.stdout + run.stderr)
        cls.rows = dict(zip(modes, json.loads(run.stdout)))

    def test_recent_successful_write_offers_user_observation_without_automatic_input(self):
        row = self.rows['accepted']
        self.assertEqual(row['offered'], 'mac')
        self.assertGreater(row['id'], 0)
        self.assertTrue(row['ready_after_confirmation'])
        self.assertFalse(row['armed_after_confirmation'])

    def test_selecting_source_after_visual_confirmation_does_not_repeat_failed_ddc(self):
        row = self.rows['accepted']
        self.assertEqual(row['target'], 'mac')
        self.assertTrue(row['armed'] and row['input_active'])
        self.assertEqual(row['calls_after_confirmation'], 0)
        self.assertEqual(row['writes'], [18])

    def test_visual_confirmation_does_not_bypass_input_permissions(self):
        row = self.rows['missing_permission']
        self.assertEqual(row['target'], 'mac')
        self.assertFalse(row['ready_after_confirmation'] or row['armed'] or row['input_active'])

    def test_stale_or_mismatched_confirmation_cannot_activate_input(self):
        for mode in ['wrong_target', 'wrong_operation', 'disconnect', 'display_change', 'new_selection', 'expired', 'blocked', 'replayed', 'new_operation']:
            row = self.rows[mode]
            self.assertFalse(row['input_active'] or row['armed'], mode)
            self.assertNotEqual(row['target'], 'mac', mode)

    def test_no_visual_override_without_an_issued_command_and_confirmed_mapping(self):
        for mode in ['no_write', 'cleanup', 'unconfirmed', 'worker_error', 'timed_out']:
            row = self.rows[mode]
            self.assertFalse(row['offered'], mode)
            self.assertFalse(row['input_active'] or row['armed'], mode)


if __name__ == '__main__':
    unittest.main()
