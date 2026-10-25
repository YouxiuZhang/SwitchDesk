from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]


class GlobalHotkeysTests(unittest.TestCase):
    def test_production_key_lifecycle(self):
        paths = ['native/windows/product/GlobalHotkeys.cs', 'tests/product/GlobalHotkeysHarness.cs']
        source = '$ErrorActionPreference="Stop"; Add-Type -CompilerOptions "/langversion:5" -Path @(' + ','.join("'" + str(ROOT / p) + "'" for p in paths) + ')\n'
        source += '\n'.join('[SwitchDesk.Desktop.GlobalHotkeysHarness]::Run("' + s + '")' for s in ['f12', 'modifiers', 'emergency', 'ordinary', 'reset'])
        run = subprocess.run([str(ROOT / '.build/tools/powershell-7.6.6/pwsh'), '-NoProfile', '-NonInteractive', '-Command', source], capture_output=True, text=True, timeout=30)
        self.assertEqual(run.returncode, 0, run.stdout + run.stderr)


if __name__ == '__main__':
    unittest.main()
