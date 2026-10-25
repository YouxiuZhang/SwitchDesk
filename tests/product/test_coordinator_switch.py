import json
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]

class DisplaySwitchRegressionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        paths = ['tests/product/CoordinatorStubs.cs', 'native/windows/product/SwitchState.cs', 'native/windows/product/Coordinator.cs']
        source = '$ErrorActionPreference="Stop"; Add-Type -WarningAction SilentlyContinue -IgnoreWarnings -Path @(' + ','.join("'"+str(ROOT/p)+"'" for p in paths) + ')\n'
        cases = [(True,False,False,True,False),(False,False,False,True,False),(True,True,True,False,False),(True,True,True,True,False),(True,False,False,True,True)]
        source += '$answers = @(' + ';'.join('[SwitchDesk.Desktop.CoordinatorHarness]::Run('+','.join('$true' if v else '$false' for v in case)+')' for case in cases) + '); $answers | ConvertTo-Json -Depth 8 -Compress'
        run = subprocess.run([str(ROOT/'.build/tools/powershell-7.6.6/pwsh'), '-NoLogo','-NoProfile','-NonInteractive','-Command',source],capture_output=True,text=True,timeout=30)
        if run.returncode: raise AssertionError(run.stdout+run.stderr)
        cls.rows = json.loads(run.stdout)

    def test_missing_input_permission_does_not_block_video_or_enable_input(self):
        r=self.rows[0]
        self.assertTrue(r['can_switch'])
        self.assertEqual((r['calls'],r['target']),(1,'mac'))
        self.assertFalse(r['ready'] or r['armed'] or r['input_active'])
        self.assertTrue(r['state']['can_switch'])

    def test_windows_keeps_standalone_display_switching_without_network(self):
        r=self.rows[1]
        self.assertTrue(r['can_switch'])
        self.assertEqual((r['calls'],r['target']),(1,'mac'))
        self.assertFalse(r['ready'] or r['armed'] or r['input_active'])

    def test_unconfirmed_mapping_still_blocks_display_writes(self):
        r=self.rows[2]
        self.assertFalse(r['can_switch'])
        self.assertEqual(r['calls'],0)

    def test_authorized_input_remains_available(self):
        r=self.rows[3]
        self.assertEqual((r['calls'],r['target']),(1,'mac'))
        self.assertTrue(r['ready'] and r['armed'] and r['input_active'])

    def test_failed_readback_disarms_but_preserves_windows_recovery(self):
        r=self.rows[4]
        self.assertEqual((r['calls'],r['target']),(1,'unknown'))
        self.assertFalse(r['ready'] or r['armed'] or r['input_active'])
        self.assertTrue(r['state']['can_switch'])
        self.assertTrue(r['state']['can_recover_windows'])
