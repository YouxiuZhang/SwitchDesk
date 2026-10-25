import json
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]


class DdcReadbackTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        paths = ['native/windows/app/MonitorControl.cs', 'tests/product/DdcReadbackHarness.cs']
        source = '$ErrorActionPreference="Stop"; Add-Type -CompilerOptions "/langversion:5" -Path @(' + ','.join("'" + str(ROOT / path) + "'" for path in paths) + ')\n'
        cls.scenarios = ['error_then_target', 'old_then_target', 'persistent_error', 'persistent_old',
                         'immediate', 'write_failed', 'slow_call', 'budget', 'refresh_budget',
                         'sleep_overrun', 'timing', 'old_then_error']
        source += '$answers = @(' + ';'.join("[SwitchDesk.DdcReadbackHarness]::Run('" + scenario + "')" for scenario in cls.scenarios) + '); $answers | ConvertTo-Json -Depth 8 -Compress'
        run = subprocess.run([str(ROOT / '.build/tools/powershell-7.6.6/pwsh'), '-NoLogo', '-NoProfile', '-NonInteractive', '-Command', source],
                             capture_output=True, text=True, timeout=30)
        if run.returncode:
            raise AssertionError(run.stdout + run.stderr)
        cls.rows = dict(zip(cls.scenarios, json.loads(run.stdout)))

    def scenario(self, name):
        return self.rows[name]

    def test_failed_first_read_recovers_with_one_read_only_handle_refresh(self):
        row = self.scenario('error_then_target')
        self.assertTrue(row['success'] and row['readback_ok'])
        self.assertEqual((row['readback'], row['reads'], row['refreshes']), (18, 2, 1))

    def test_old_value_can_settle_to_target_without_refreshing_a_working_handle(self):
        row = self.scenario('old_then_target')
        self.assertTrue(row['success'])
        self.assertEqual((row['readback'], row['reads'], row['refreshes']), (18, 2, 0))

    def test_persistent_failure_or_old_value_stops_after_three_reads(self):
        for name in ['persistent_error', 'persistent_old']:
            with self.subTest(name=name):
                row = self.scenario(name)
                self.assertFalse(row['success'])
                self.assertEqual((row['reads'], row['ReadbackAttempts']), (3, 3))
                self.assertLessEqual(row['elapsed'], 900)
                self.assertLessEqual(row['refreshes'], 1)
                self.assertEqual(row['readback_ok'], name == 'persistent_old')

    def test_first_success_has_no_delay_or_refresh(self):
        row = self.scenario('immediate')
        self.assertTrue(row['success'])
        self.assertEqual((row['reads'], row['delays'], row['refreshes'], row['elapsed']), (1, [], 0, 0))

    def test_write_false_is_never_declared_success_even_if_readback_matches(self):
        row = self.scenario('write_failed')
        self.assertFalse(row['success'] or row['write_returned'])
        self.assertTrue(row['readback_ok'])
        self.assertEqual((row['reads'], row['delays'], row['refreshes']), (1, [], 0))

    def test_every_scenario_writes_exactly_once(self):
        for name in self.scenarios:
            with self.subTest(name=name):
                self.assertEqual(self.scenario(name)['writes'], 1)

    def test_blocking_read_can_exceed_budget_but_never_schedules_another_read(self):
        row = self.scenario('slow_call')
        self.assertFalse(row['success'])
        self.assertEqual((row['reads'], row['delays'], row['refreshes'], row['ReadbackMs']), (1, [], 0, 950))

    def test_elapsed_read_costs_are_counted_in_retry_budget(self):
        row = self.scenario('budget')
        self.assertFalse(row['success'])
        self.assertEqual(row['reads'], 2)
        self.assertEqual(len(row['delays']), 1)

    def test_refresh_and_scheduler_overrun_cannot_start_a_read_after_budget(self):
        for name in ['refresh_budget', 'sleep_overrun']:
            with self.subTest(name=name):
                row = self.scenario(name)
                self.assertFalse(row['success'])
                self.assertEqual((row['reads'], row['ReadbackAttempts']), (1, 1))

    def test_command_timing_accounts_for_reads_waits_and_refresh_separately_from_write(self):
        row = self.scenario('timing')
        self.assertTrue(row['success'])
        self.assertEqual(row['WriteMs'], 37)
        self.assertEqual(row['ReadbackMs'], 11 + sum(row['delays']) + 13 + 17)
        self.assertEqual(row['elapsed'], row['WriteMs'] + row['ReadbackMs'])
        for name in ['TotalMs', 'DiscoveryMs', 'ReadBeforeMs']:
            self.assertEqual(row[name], 0, 'Unexecuted command phases must remain measurable zeroes')

    def test_latest_failed_read_does_not_reuse_a_previous_read_value(self):
        row = self.scenario('old_then_error')
        self.assertFalse(row['success'] or row['readback_ok'])
        self.assertIsNone(row['readback'])


if __name__ == '__main__':
    unittest.main()
