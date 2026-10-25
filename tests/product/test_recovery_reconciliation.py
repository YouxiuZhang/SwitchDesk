import json
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]


class RecoveryReconciliationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        paths = ['tests/product/CoordinatorStubs.cs', 'native/windows/product/SwitchState.cs', 'native/windows/product/Coordinator.cs']
        source = '$ErrorActionPreference="Stop"; Add-Type -WarningAction SilentlyContinue -IgnoreWarnings -Path @(' + ','.join("'"+str(ROOT/p)+"'" for p in paths) + ')\n'
        modes = ['success', 'missing_permission', 'disconnect', 'wrong_input', 'read_failure', 'selection_changed', 'blocked']
        source += '$answers = @(' + ';'.join("[SwitchDesk.Desktop.CoordinatorHarness]::ReconcileRecovery('"+m+"')" for m in modes) + '); $answers | ConvertTo-Json -Depth 8 -Compress'
        run = subprocess.run([str(ROOT/'.build/tools/powershell-7.6.6/pwsh'), '-NoProfile', '-NonInteractive', '-Command', source], capture_output=True, text=True, timeout=40)
        if run.returncode:
            raise AssertionError(run.stdout + run.stderr)
        cls.rows = dict(zip(modes, json.loads(run.stdout)))

    def test_late_windows_read_updates_both_clients_and_next_switch_targets_mac(self):
        row = self.rows['success']
        self.assertEqual(row['initial_target'], 'unknown')
        self.assertEqual(row['target'], 'windows')
        self.assertEqual(row['state']['target'], 'windows')
        self.assertTrue(row['state']['can_switch'] and row['confirmed'])
        self.assertEqual(row['next_target'], 'mac')
        self.assertEqual(row['writes'], [18, 17, 18])

    def test_reconciliation_is_read_only_and_never_arms_input(self):
        row = self.rows['success']
        self.assertEqual(row['reads'], 1)
        self.assertEqual(row['writes_before_next'], 2)
        self.assertFalse(row['armed'])

    def test_physical_state_reconciles_without_input_permission_or_network(self):
        for mode in ['missing_permission', 'disconnect']:
            row = self.rows[mode]
            self.assertEqual(row['target'], 'windows', mode)
            self.assertFalse(row['ready'] or row['armed'], mode)
            self.assertEqual(row['writes'], [18, 17])

    def test_wrong_or_unreadable_input_is_bounded_and_not_claimed_as_recovery(self):
        for mode in ['wrong_input', 'read_failure']:
            row = self.rows[mode]
            self.assertEqual(row['target'], 'unknown', mode)
            self.assertEqual(row['reads'], 3, mode)
            self.assertFalse(row['confirmed'] or row['armed'], mode)
            self.assertEqual(row['writes'], [18, 17])

    def test_stale_display_or_blocked_worker_cannot_apply_late_result(self):
        for mode in ['selection_changed', 'blocked']:
            row = self.rows[mode]
            self.assertEqual(row['target'], 'unknown', mode)
            self.assertFalse(row['confirmed'] or row['armed'], mode)
            self.assertEqual(row['writes'], [18, 17])
        self.assertEqual(self.rows['blocked']['reads'], 0)


if __name__ == '__main__':
    unittest.main()
