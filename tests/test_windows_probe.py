"""Portable checks and Windows integration tests for the read-only probe."""

import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "native" / "windows" / "diagnose.ps1"
SOURCE = ROOT / "native" / "windows" / "Probe.cs"
VALIDATOR = ROOT / "tools" / "probe_report.py"
PWSH = os.environ.get("SWITCHDESK_PWSH") or shutil.which("pwsh") or shutil.which("powershell")


@unittest.skipUnless(PWSH, "缺少 PowerShell；设置 SWITCHDESK_PWSH 或安装 pwsh")
class WindowsProbeTests(unittest.TestCase):
    def run_ps(self, *args):
        env = os.environ.copy()
        env["POWERSHELL_TELEMETRY_OPTOUT"] = "1"
        return subprocess.run(
            [PWSH, "-NoLogo", "-NoProfile", "-NonInteractive", *map(str, args)],
            cwd=ROOT, env=env, capture_output=True, check=False,
        )

    def test_powershell_parser_accepts_script(self):
        command = (
            "$tokens=$null;$errors=$null;"
            "$path=Join-Path (Get-Location) 'native/windows/diagnose.ps1';"
            "[System.Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)|Out-Null;"
            "if($errors.Count){$errors|ForEach-Object{[Console]::Error.WriteLine($_.Message)};exit 1}"
        )
        result = self.run_ps("-Command", command)
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))

    def test_csharp5_compiles_and_win32_structs_have_expected_sizes(self):
        command = (
            "$path=Join-Path (Get-Location) 'native/windows/Probe.cs';"
            "Add-Type -Path $path -CompilerOptions '/langversion:5' -ErrorAction Stop;"
            "$types=@('SwitchDesk.Native+MONITORINFOEXW','SwitchDesk.Native+DEVMODEW',"
            "'SwitchDesk.Native+PHYSICAL_MONITOR');"
            "$expected=@(104,220,(256+[IntPtr]::Size));"
            "for($i=0;$i -lt $types.Count;$i++){"
            "$t=[SwitchDesk.Native].GetNestedType($types[$i].Split('+')[1]);"
            "if($null -eq $t){throw ('Missing type '+$types[$i])};"
            "$size=[Runtime.InteropServices.Marshal]::SizeOf([Activator]::CreateInstance($t));"
            "if($size -ne $expected[$i]){throw ($types[$i]+': '+$size+' != '+$expected[$i])}"
            "};"
            "$dm=[SwitchDesk.Native].GetNestedType('DEVMODEW');"
            "$offset=[Runtime.InteropServices.Marshal]::OffsetOf($dm,'dmPelsWidth').ToInt32();"
            "if($offset -ne 172){throw ('dmPelsWidth offset '+$offset+' != 172')};"
            "$offset=[Runtime.InteropServices.Marshal]::OffsetOf($dm,'dmPanningHeight').ToInt32();"
            "if($offset -ne 216){throw ('dmPanningHeight offset '+$offset+' != 216')}"
        )
        result = self.run_ps("-Command", command)
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))

    def test_physical_description_uses_model_but_not_serial_marker(self):
        command = (
            "Add-Type -Path (Join-Path (Get-Location) 'native/windows/Probe.cs') "
            "-CompilerOptions '/langversion:5' -ErrorAction Stop;"
            "$flags=[Reflection.BindingFlags]'NonPublic,Static';"
            "$method=[SwitchDesk.Native].GetMethod('PhysicalName',$flags);"
            "if($null -eq $method){throw 'Missing PhysicalName'};"
            "$model=$method.Invoke($null,@('Display 1','AOC 27G2',1));"
            "$private=$method.Invoke($null,@('Display 1','AOC 27G2 Serial: ABC123',1));"
            "if($model -ne 'AOC 27G2 (Display 1 physical 1)'){throw ('Model was '+$model)};"
            "if($private -ne 'Display 1 physical 1'){throw ('Private name was '+$private)}"
        )
        result = self.run_ps("-Command", command)
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))

    @unittest.skipIf(sys.platform == "win32", "仅检查非 Windows 平台保护")
    def test_non_windows_exits_before_native_load_or_file_creation(self):
        with tempfile.TemporaryDirectory(prefix="switchdesk windows ") as folder:
            output = Path(folder) / "report with spaces.json"
            result = self.run_ps("-File", str(SCRIPT), "-OutputPath", str(output), "-ReadDdc")
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(result.stdout, b"")
            self.assertIn("Windows", result.stderr.decode("utf-8", errors="replace"))
            self.assertFalse(output.exists())

    @unittest.skipUnless(sys.platform == "win32", "Win32 集成测试只能在 Windows 运行")
    def test_default_stdout_is_valid_and_ddc_not_tested(self):
        result = self.run_ps("-File", str(SCRIPT))
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
        self.assertFalse(result.stdout.startswith(b"\xef\xbb\xbf"))
        report = json.loads(result.stdout.decode("utf-8"))
        spec = importlib.util.spec_from_file_location("probe_report", VALIDATOR)
        validator = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(validator)
        self.assertEqual(validator.validate_report(report), [])
        self.assertEqual(report["platform"], "windows")
        self.assertTrue(report["read_only"])
        self.assertTrue(all(d["ddc_input"]["status"] == "not_tested" for d in report["displays"]))

    @unittest.skipUnless(sys.platform == "win32", "Win32 集成测试只能在 Windows 运行")
    def test_file_is_utf8_without_bom_and_overwrite_is_rejected(self):
        with tempfile.TemporaryDirectory(prefix="switchdesk windows ") as folder:
            output = Path(folder) / "report with spaces.json"
            first = self.run_ps("-File", str(SCRIPT), "-OutputPath", str(output))
            self.assertEqual(first.returncode, 0, first.stderr.decode(errors="replace"))
            self.assertEqual(first.stdout, b"")
            original = output.read_bytes()
            self.assertFalse(original.startswith(b"\xef\xbb\xbf"))
            json.loads(original.decode("utf-8"))
            second = self.run_ps("-File", str(SCRIPT), "-OutputPath", str(output))
            self.assertNotEqual(second.returncode, 0)
            self.assertEqual(second.stdout, b"")
            self.assertEqual(output.read_bytes(), original)
            self.assertEqual(set(Path(folder).iterdir()), {output})

    @unittest.skipUnless(sys.platform == "win32", "Win32 集成测试只能在 Windows 运行")
    def test_explicit_ddc_read_keeps_distinct_physical_results(self):
        result = self.run_ps("-File", str(SCRIPT), "-ReadDdc")
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
        report = json.loads(result.stdout.decode("utf-8"))
        spec = importlib.util.spec_from_file_location("probe_report", VALIDATOR)
        validator = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(validator)
        self.assertEqual(validator.validate_report(report), [])
        ids = [display["id"] for display in report["displays"]]
        self.assertEqual(len(ids), len(set(ids)))
        self.assertTrue(all(display["ddc_input"]["status"] != "not_tested" for display in report["displays"]))

    @unittest.skipUnless(sys.platform == "win32", "Win32 集成测试只能在 Windows 运行")
    def test_two_invocations_in_one_powershell_process(self):
        command = (
            "$script=Join-Path (Get-Location) 'native/windows/diagnose.ps1';"
            "& $script; & $script"
        )
        result = self.run_ps("-Command", command)
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
        reports = [json.loads(line.decode("utf-8")) for line in result.stdout.splitlines()]
        self.assertEqual(len(reports), 2)
        self.assertTrue(all(report["platform"] == "windows" for report in reports))

    @unittest.skipUnless(sys.platform == "win32", "Win32 集成测试只能在 Windows 运行")
    def test_relative_output_uses_powershell_location(self):
        with tempfile.TemporaryDirectory(prefix="switchdesk location ") as folder:
            filename = "report " + Path(folder).name + ".json"
            output = Path(folder) / filename
            command = (
                "$ErrorActionPreference='Stop';"
                "$folder=$env:SWITCHDESK_TEST_LOCATION;"
                "Set-Location -LiteralPath $folder;"
                "$script=Join-Path $env:SWITCHDESK_TEST_ROOT 'native/windows/diagnose.ps1';"
                "& $script -OutputPath $env:SWITCHDESK_TEST_FILENAME"
            )
            env = os.environ.copy()
            env["POWERSHELL_TELEMETRY_OPTOUT"] = "1"
            env["SWITCHDESK_TEST_LOCATION"] = folder
            env["SWITCHDESK_TEST_ROOT"] = str(ROOT)
            env["SWITCHDESK_TEST_FILENAME"] = filename
            result = subprocess.run(
                [PWSH, "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
                cwd=ROOT, env=env, capture_output=True, check=False,
            )
            self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
            self.assertEqual(result.stdout, b"")
            self.assertTrue(output.is_file())
            self.assertEqual(set(Path(folder).iterdir()), {output})
            self.assertFalse((ROOT / filename).exists())

    @unittest.skipUnless(sys.platform == "win32", "Win32 集成测试只能在 Windows 运行")
    def test_missing_parent_does_not_create_directories(self):
        with tempfile.TemporaryDirectory() as folder:
            parent = Path(folder) / "missing parent"
            result = self.run_ps("-File", str(SCRIPT), "-OutputPath", str(parent / "report.json"))
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(result.stdout, b"")
            self.assertFalse(parent.exists())


if __name__ == "__main__":
    unittest.main()
