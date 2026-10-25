"""Compile the production C#5 queue/session/framing without network or input hooks."""
import json
from pathlib import Path
import shutil
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]


class ControlTransportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        bundled = ROOT / '.build/tools/powershell-7.6.6/pwsh'
        pwsh = str(bundled) if bundled.exists() else shutil.which('pwsh')
        if not pwsh:
            raise unittest.SkipTest('PowerShell 7 unavailable')
        paths = ['tests/product/TransportHarness.cs', 'native/windows/product/PathRules.cs',
                 'native/windows/product/Wire.cs', 'native/windows/product/Session.cs']
        source = '$ErrorActionPreference="Stop"; Add-Type -WarningAction SilentlyContinue -IgnoreWarnings -CompilerOptions /langversion:5 -Path @(' + ','.join("'" + str(ROOT / p) + "'" for p in paths) + ')\n'
        names = ['Burst', 'Barriers', 'SegmentsAndOwnership', 'CapacityAndClear', 'InvalidMovesAndOverflow', 'Completion', 'ProducerConsumer', 'Frame']
        source += '$answers = @{' + ';'.join(n + '=[SwitchDesk.Desktop.TransportHarness]::' + n + '()' for n in names) + '}; $answers | ConvertTo-Json -Depth 12 -Compress'
        run = subprocess.run([pwsh, '-NoProfile', '-NonInteractive', '-Command', source], capture_output=True, text=True, timeout=35, cwd=ROOT)
        if run.returncode:
            raise AssertionError(run.stdout + run.stderr)
        cls.rows = json.loads(run.stdout)

    def test_high_frequency_moves_preserve_displacement_and_reduce_pending_frames(self):
        row = self.rows['Burst']
        self.assertEqual((row['x'], row['y']), (30000, -20000))
        self.assertLessEqual(row['frames'], 16)
        self.assertTrue(row['bounded'] and row['connected'])

    def test_move_coalescing_respects_every_non_move_and_epoch_barrier(self):
        rows = self.rows['Barriers']
        self.assertEqual(len(rows), 15)
        for index, kind in enumerate(['button', 'key', 'release', 'scroll', 'shortcut', 'ping']):
            self.assertEqual(rows[index * 2]['event'], {'kind': 'move', 'dx': 4, 'dy': 6})
            barrier = rows[index * 2 + 1]
            self.assertEqual(barrier['type'] if kind == 'ping' else barrier['event']['kind'], kind)
        self.assertEqual([m['epoch'] for m in rows[-3:]], [7, 8, 7])
        self.assertEqual([m['event']['dx'] for m in rows[-3:]], [5, 7, 9])

    def test_delta_segments_remain_bounded_without_mutating_original_or_inflight(self):
        row = self.rows['SegmentsAndOwnership']
        self.assertEqual(row['original']['event']['dx'], 2040)
        self.assertEqual(row['inflight']['event'], {'kind': 'move', 'dx': 2048, 'dy': -2048})
        self.assertEqual([m['event'] for m in row['pending']], [{'kind': 'move', 'dx': 12, 'dy': -12}])
        self.assertEqual([m['event']['dx'] for m in row['negative']], [-2048, -1])

    def test_bounded_capacity_allows_tail_merge_and_disconnect_discards_pending_input(self):
        row = self.rows['CapacityAndClear']
        self.assertTrue(row['filled'] and row['merged'] and row['rejected'])
        self.assertEqual((row['count'], row['cleared']), (512, 0))
        self.assertEqual(row['fresh']['epoch'], 2)
        self.assertEqual(row['fresh']['event']['dx'], 4)

    def test_completion_wakes_take_rejects_add_and_drains_existing_items(self):
        row = self.rows['Completion']
        self.assertTrue(all(row[key] for key in ['completed', 'woke', 'joined', 'rejected', 'ended']))
        self.assertIsNone(row['failure'])
        self.assertEqual(row['last']['event']['dx'], 1)

    def test_invalid_moves_remain_barriers_and_saturation_still_disconnects(self):
        row = self.rows['InvalidMovesAndOverflow']
        self.assertEqual(row['count'], 16)
        self.assertFalse(row['connected'])
        self.assertEqual((row['pending'], row['closedPending']), (0, 0))
        self.assertIn('控制通道拥塞', row['status'])

    def test_concurrent_producer_and_consumer_preserve_all_displacement(self):
        row = self.rows['ProducerConsumer']
        self.assertEqual((row['x'], row['y']), (60000, -90000))
        self.assertTrue(row['bounded'] and row['joined'])
        self.assertIsNone(row['failure'])

    def test_wire_writes_one_complete_length_prefixed_utf8_frame(self):
        row = self.rows['Frame']
        self.assertEqual(row['writes'], 1)
        self.assertEqual(row['flushes'], 1)
        self.assertEqual(row['length'] + 4, row['size'])
        self.assertEqual(json.loads(row['payload']), {'type': 'ping', 'name': '中文'})
        self.assertTrue(row['rejected'])
        self.assertEqual(row['rejectedWrites'], 0)


if __name__ == '__main__':
    unittest.main()
