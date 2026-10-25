import json
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]


class TrialRecoveryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        paths = ['tests/product/CoordinatorStubs.cs', 'native/windows/product/SwitchState.cs', 'native/windows/product/Coordinator.cs']
        source = '$ErrorActionPreference="Stop"; Add-Type -WarningAction SilentlyContinue -IgnoreWarnings -Path @(' + ','.join("'"+str(ROOT/p)+"'" for p in paths) + ')\n'
        modes = ['unconfirmed', 'display_event', 'wrong_input', 'read_failed', 'stale_selection', 'new_selection', 'active_trial']
        source += '$answers = @(' + ';'.join("[SwitchDesk.Desktop.CoordinatorHarness]::TrialRecovery('"+m+"')" for m in modes) + '); $answers | ConvertTo-Json -Depth 8 -Compress'
        run = subprocess.run([str(ROOT/'.build/tools/powershell-7.6.6/pwsh'), '-NoProfile', '-NonInteractive', '-Command', source], capture_output=True, text=True, timeout=30)
        if run.returncode:
            raise AssertionError(run.stdout + run.stderr)
        cls.rows = dict(zip(modes, json.loads(run.stdout)))

    def test_verified_trial_origin_offers_return_before_visual_confirmation(self):
        row = self.rows['unconfirmed']
        self.assertTrue(row['accepted'])
        self.assertTrue(row['offered'])
        self.assertEqual(row['writes'], [17])
        self.assertEqual(row['target'], 'windows')

    def test_return_does_not_claim_the_unseen_mac_port_is_confirmed(self):
        row = self.rows['unconfirmed']
        self.assertTrue(row['offered'])
        self.assertFalse(row['confirmed'] or row['armed'] or row['input_active'])
        self.assertEqual(row['writes'], [17])

    def test_display_notification_keeps_the_trial_return_address(self):
        row = self.rows['display_event']
        self.assertTrue(row['offered'])
        self.assertEqual(row['writes'], [17])

    def test_failed_or_stale_read_never_creates_a_return_address(self):
        for mode in ['wrong_input', 'read_failed', 'stale_selection', 'new_selection']:
            row = self.rows[mode]
            self.assertFalse(row['offered'], mode)
            self.assertEqual(row['writes'], [], mode)

    def test_restore_during_trial_signals_its_guardian_without_a_second_write(self):
        row = self.rows['active_trial']
        self.assertTrue(row['signaled'])
        self.assertEqual(row['writes'], [])


if __name__ == '__main__':
    unittest.main()
