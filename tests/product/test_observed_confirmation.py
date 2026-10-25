import json
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]


class ObservedConfirmationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        paths = ['tests/product/CoordinatorStubs.cs', 'native/windows/product/SwitchState.cs', 'native/windows/product/Coordinator.cs']
        source = '$ErrorActionPreference="Stop"; Add-Type -WarningAction SilentlyContinue -IgnoreWarnings -Path @(' + ','.join("'"+str(ROOT/p)+"'" for p in paths) + ')\n'
        cls.modes = ['seen', 'wrong_input', 'read_failed', 'blocked', 'selection_changed', 'reconnected', 'read_exception', 'no_selection']
        source += '$answers = @(' + ';'.join("[SwitchDesk.Desktop.CoordinatorHarness]::ConfirmObservation('"+mode+"')" for mode in cls.modes) + '); $answers | ConvertTo-Json -Depth 8 -Compress'
        run = subprocess.run([str(ROOT/'.build/tools/powershell-7.6.6/pwsh'), '-NoProfile', '-NonInteractive', '-Command', source], capture_output=True, text=True, timeout=30)
        if run.returncode:
            raise AssertionError(run.stdout + run.stderr)
        cls.rows = dict(zip(cls.modes, json.loads(run.stdout)))

    def test_seen_mac_can_be_confirmed_without_automatic_trial_or_network(self):
        row = self.rows['seen']
        self.assertTrue(row['confirmed'] and row['can_switch'])
        self.assertEqual(row['calls'], 1)
        self.assertEqual(row['writes'], 0)
        self.assertFalse(row['input_active'] or row['busy'])

    def test_wrong_input_and_failed_read_do_not_confirm(self):
        for mode in ['wrong_input', 'read_failed', 'read_exception']:
            with self.subTest(mode=mode):
                row = self.rows[mode]
                self.assertFalse(row['confirmed'] or row['can_switch'] or row['busy'])
                self.assertEqual(row['writes'], 0)

    def test_changed_selection_does_not_accept_old_readback(self):
        self.assertFalse(self.rows['selection_changed']['confirmed'])

    def test_network_reconnection_does_not_discard_video_confirmation(self):
        self.assertTrue(self.rows['reconnected']['confirmed'])
        self.assertFalse(self.rows['reconnected']['input_active'])

    def test_blocked_worker_or_missing_selection_does_not_issue_commands(self):
        for mode in ['blocked', 'no_selection']:
            with self.subTest(mode=mode):
                row = self.rows[mode]
                self.assertFalse(row['confirmed'] or row['can_switch'])
                self.assertEqual(row['calls'], 0)
