import json
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]


class DisplayRecoveryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        paths = ['tests/product/CoordinatorStubs.cs', 'native/windows/product/SwitchState.cs', 'native/windows/product/Coordinator.cs']
        source = '$ErrorActionPreference="Stop"; Add-Type -WarningAction SilentlyContinue -IgnoreWarnings -Path @(' + ','.join("'"+str(ROOT/p)+"'" for p in paths) + ')\n'
        modes = ['normal', 'readback_failure', 'display_event', 'permission_loss', 'disconnect', 'reconnect', 'recovery_failure', 'blocked', 'selection_changed', 'never_confirmed', 'explicit_restore', 'no_mac_retry', 'recovery_message', 'restart_mac', 'restart_wrong_input']
        source += '$answers = @(' + ';'.join("[SwitchDesk.Desktop.CoordinatorHarness]::Recovery('"+m+"')" for m in modes) + '); $answers | ConvertTo-Json -Depth 8 -Compress'
        run = subprocess.run([str(ROOT/'.build/tools/powershell-7.6.6/pwsh'), '-NoProfile', '-NonInteractive', '-Command', source], capture_output=True, text=True, timeout=30)
        if run.returncode:
            raise AssertionError(run.stdout + run.stderr)
        cls.rows = dict(zip(modes, json.loads(run.stdout)))

    def assert_returned(self, mode):
        row = self.rows[mode]
        self.assertTrue(row['offered'], mode)
        self.assertEqual(row['writes'], [18, 17], mode)
        self.assertEqual(row['target'], 'windows', mode)
        self.assertFalse(row['busy'] or row['input_active'] or row['armed'], mode)

    def test_normal_round_trip_without_input_permissions(self):
        self.assert_returned('normal')

    def test_failed_mac_readback_keeps_windows_recovery(self):
        self.assert_returned('readback_failure')

    def test_delayed_display_change_keeps_confirmed_recovery_port(self):
        self.assert_returned('display_event')

    def test_input_permission_loss_does_not_strand_switching(self):
        self.assert_returned('permission_loss')

    def test_disconnect_and_reconnect_during_write_keep_recovery(self):
        self.assert_returned('disconnect')
        self.assert_returned('reconnect')

    def test_failed_recovery_stays_unknown_and_can_be_retried(self):
        row = self.rows['recovery_failure']
        self.assertEqual(row['writes'], [18, 17])
        self.assertEqual(row['target'], 'unknown')
        self.assertTrue(row['state']['can_recover_windows'])
        self.assertFalse(row['input_active'] or row['armed'])

    def test_worker_still_running_never_allows_overlapping_recovery(self):
        row = self.rows['blocked']
        self.assertFalse(row['offered'])
        self.assertEqual(row['writes'], [18])

    def test_new_selection_and_unconfirmed_devices_cannot_use_old_recovery(self):
        self.assertEqual(self.rows['selection_changed']['writes'], [18])
        self.assertEqual(self.rows['never_confirmed']['writes'], [])

    def test_recovery_capability_does_not_authorize_another_mac_switch(self):
        self.assertEqual(self.rows['no_mac_retry']['writes'], [18])

    def test_mac_restore_message_always_targets_windows_without_arming_input(self):
        self.assert_returned('recovery_message')
        row = self.rows['explicit_restore']
        self.assertEqual(row['writes'], [17])
        self.assertEqual(row['target'], 'windows')
        self.assertFalse(row['armed'] or row['input_active'])

    def test_restart_while_on_mac_restores_saved_mapping_and_return_path(self):
        row = self.rows['restart_mac']
        self.assertTrue(row['offered'])
        self.assertEqual(row['writes'], [17])
        self.assertEqual(row['target'], 'windows')
        self.assertEqual(self.rows['restart_wrong_input']['writes'], [])
