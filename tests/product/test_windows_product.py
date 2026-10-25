"""Cross-platform checks of the Windows product's pure C# safety rules.

PowerShell 7 compiles the exact product sources on macOS. Native hooks, DDC, TLS and
Explorer clipboard behavior still need a Windows integration run.
"""
import json
from pathlib import Path
import shutil
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]
PWSH = ROOT / '.build/tools/powershell-7.6.6/pwsh'


def run_csharp(paths, body):
    executable = str(PWSH) if PWSH.exists() else shutil.which('pwsh')
    if not executable:
        raise unittest.SkipTest('PowerShell 7 unavailable')
    source = '\n'.join(f"Add-Type -Path '{ROOT / path}'" for path in paths)
    result = subprocess.run([executable, '-NoProfile', '-Command', source + '\n' + body],
                            capture_output=True, text=True, timeout=25, cwd=ROOT)
    if result.returncode:
        raise AssertionError(result.stdout + result.stderr)
    return json.loads(result.stdout.strip())


class WindowsProductSafetyTests(unittest.TestCase):
    def test_relative_file_path_rejects_windows_escapes_and_aliases(self):
        paths = ['root/f.txt', '中文/照片.png', 'root/empty', '../x', 'x/../y',
                 '/absolute', 'C:/drive', 'x\\y', 'a//b', 'a/./b', 'a/NUL.txt',
                 'a/com1', 'a/trailing.', 'a/trailing ', 'a/x|y', 'a/\x00b']
        expected = [True, True, True] + [False] * (len(paths) - 3)
        script = '$items = ConvertFrom-Json @\'\n' + json.dumps(paths, ensure_ascii=False) + "\n'@\n"
        script += '$answer = @($items | ForEach-Object { [SwitchDesk.Desktop.PathRules]::Valid($_) }); $answer | ConvertTo-Json -Compress'
        self.assertEqual(run_csharp(['native/windows/product/PathRules.cs'], script), expected)

    def test_switch_requires_trial_and_explicit_confirmation(self):
        script = '''$s = [SwitchDesk.Desktop.SwitchState]::new()
$a = @($s.CanNormalSwitch())
$s.Configure('display-id', 'Monitor', 17, 18)
$a += $s.CanNormalSwitch()
$s.RecordTrial(17, 17, $true, $true)
$a += $s.CanNormalSwitch()
$s.ConfirmSeen()
$a += $s.CanNormalSwitch()
$s.RevokeConfirmation()
$a += $s.CanNormalSwitch()
$s.Configure('display-id', 'Monitor', 17, 17)
$a += $s.CanTrial()
$a | ConvertTo-Json -Compress'''
        self.assertEqual(run_csharp(['native/windows/product/SwitchState.cs'], script),
                         [False, False, False, True, False, False])


if __name__ == '__main__':
    unittest.main()
