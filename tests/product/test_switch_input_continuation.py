import json
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]


class SwitchInputContinuationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        paths = ['tests/product/CoordinatorStubs.cs', 'native/windows/product/SwitchState.cs', 'native/windows/product/Coordinator.cs']
        source = '$ErrorActionPreference="Stop"; Add-Type -WarningAction SilentlyContinue -IgnoreWarnings -Path @(' + ','.join("'" + str(ROOT / p) + "'" for p in paths) + ')\n'
        modes = ['round_trip', 'mac_source', 'no_permission', 'permission_during_write', 'permission_after_write', 'windows_permission', 'reconnect', 'disconnect']
        source += '$answers = @(' + ';'.join('[SwitchDesk.Desktop.CoordinatorHarness]::ReconcileInput("' + m + '")' for m in modes) + '); $answers | ConvertTo-Json -Depth 8 -Compress'
        run = subprocess.run([str(ROOT / '.build/tools/powershell-7.6.6/pwsh'), '-NoProfile', '-NonInteractive', '-Command', source], capture_output=True, text=True, timeout=35)
        if run.returncode:
            raise AssertionError(run.stdout + run.stderr)
        cls.rows = dict(zip(modes, json.loads(run.stdout)))

    def test_pending_display_still_releases_input(self):
        for row in self.rows.values():
            self.assertEqual(row['pending_target'], 'unknown')
            self.assertFalse(row['pending_armed'])

    def test_windows_input_follows_normal_round_trip_after_delayed_readback(self):
        row = self.rows['round_trip']
        self.assertTrue(row['armed'] and row['forward'])
        self.assertTrue(row['state']['armed'] and row['state']['ready'])
        self.assertEqual(row['state']['target'], 'mac')
        self.assertEqual((row['final_target'], row['source']), ('windows', 'windows'))
        self.assertTrue(row['final_armed'])
        self.assertFalse(row['final_forward'] or row['final_receive'])
        self.assertEqual(row['writes'], [18, 17])

    def test_mac_source_stays_selected_and_receives_after_return_to_windows(self):
        row = self.rows['mac_source']
        self.assertTrue(row['armed'])
        self.assertFalse(row['forward'] or row['receive'])
        self.assertEqual(row['source'], 'mac')
        self.assertTrue(row['final_armed'] and row['final_receive'])
        self.assertFalse(row['final_forward'])

    def test_permission_or_session_change_cannot_reenable_input(self):
        for mode in ['no_permission', 'permission_during_write', 'permission_after_write', 'windows_permission', 'reconnect', 'disconnect']:
            with self.subTest(mode=mode):
                row = self.rows[mode]
                self.assertFalse(row['armed'] or row['forward'] or row['receive'])
                self.assertEqual(row['writes'], [18])


if __name__ == '__main__':
    unittest.main()
