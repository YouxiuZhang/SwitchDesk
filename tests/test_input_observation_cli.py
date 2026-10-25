"""Portable process and file checks for the bounded Windows observation runner."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import unittest

from tools.input_observation import validate_observation


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "native" / "windows" / "observe-input.ps1"
_PWSH = os.environ.get("SWITCHDESK_PWSH") or shutil.which("pwsh") or shutil.which("powershell")
PWSH = str(Path(_PWSH).resolve()) if _PWSH else None


@unittest.skipUnless(PWSH, "缺少 PowerShell；设置 SWITCHDESK_PWSH")
class ObservationCliTests(unittest.TestCase):
    def ps(self, command, *, env=None, cwd=ROOT, timeout=15):
        variables = os.environ.copy()
        variables["POWERSHELL_TELEMETRY_OPTOUT"] = "1"
        variables["SWITCHDESK_SCRIPT"] = str(SCRIPT)
        variables.update(env or {})
        return subprocess.run(
            [PWSH, "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
            cwd=cwd, env=variables, capture_output=True, timeout=timeout, check=False,
        )

    def worker(self, folder, body):
        path = Path(folder) / "worker with spaces.ps1"
        path.write_text(body, encoding="utf-8-sig")
        return path

    def sample(self, worker, timeout_ms=2000):
        command = (
            ". $env:SWITCHDESK_SCRIPT;"
            "$exe=[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName;"
            "Invoke-SwitchDeskProbeSample $exe $env:SWITCHDESK_WORKER "
            "$env:SWITCHDESK_TIMEOUT | ConvertTo-Json -Depth 12 -Compress"
        )
        result = self.ps(command, env={"SWITCHDESK_WORKER": str(worker),
                                       "SWITCHDESK_TIMEOUT": str(timeout_ms)})
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
        return json.loads(result.stdout.decode("utf-8"))

    def test_dot_source_has_no_output_file_or_global_setting_side_effect(self):
        with tempfile.TemporaryDirectory(prefix="switchdesk observation ") as folder:
            command = (
                "$ErrorActionPreference='Continue';"
                ". $env:SWITCHDESK_SCRIPT;"
                "if($ErrorActionPreference -ne 'Continue'){throw 'changed preference'};"
                "if(-not (Get-Command Invoke-SwitchDeskProbeSample -ErrorAction SilentlyContinue)){throw 'missing function'}"
            )
            result = self.ps(command, cwd=folder)
            self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
            self.assertEqual(result.stdout, b"")
            self.assertEqual(list(Path(folder).iterdir()), [])

    def test_valid_nonascii_report_and_ddc_error_are_ok(self):
        report = {
            "schema_version": 1, "platform": "windows", "os_version": "10.0.1",
            "architecture": "x64", "collected_at": "2026-10-17T00:00:00Z",
            "read_only": True,
            "displays": [{"id": "DISPLAY1", "name": "显示器", "builtin": None,
                          "width": 1920, "height": 1080, "refresh_hz": 60.0,
                          "ddc_input": {"status": "error", "value": None, "detail": "读取失败"}}],
            "permissions": {"screen_capture": "not_checked", "input_capture": "not_checked",
                            "input_injection": "not_checked"},
            "capabilities": {"automatic_switch": "not_tested", "native_file_paste": "not_tested",
                             "inactive_display_capture": "not_tested"},
            "warnings": [],
        }
        with tempfile.TemporaryDirectory(prefix="switchdesk observation ") as folder:
            worker = self.worker(folder, "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)\n"
                                 + "[Console]::WriteLine('" + json.dumps(report, ensure_ascii=False) + "')\n")
            sample = self.sample(worker)
            self.assertEqual(sample, {"outcome": "ok", "exit_code": 0, "report": report})

    def test_worker_error_and_invalid_reports(self):
        with tempfile.TemporaryDirectory(prefix="switchdesk observation ") as folder:
            worker = self.worker(folder, "[Console]::Error.WriteLine('private failure'); exit 7\n")
            self.assertEqual(self.sample(worker), {"outcome": "worker_error", "exit_code": 7,
                                                    "report": None})
            worker.write_text("[Console]::WriteLine('not json')\n", encoding="utf-8")
            self.assertEqual(self.sample(worker), {"outcome": "invalid_report", "exit_code": 0,
                                                    "report": None})

    def test_array_typed_enum_fields_are_invalid_reports(self):
        report = {
            "schema_version": 1, "platform": "windows", "os_version": "10.0.1",
            "architecture": "x64", "collected_at": "2026-10-17T00:00:00Z",
            "read_only": True,
            "displays": [{"id": "DISPLAY1", "name": "Display", "builtin": None,
                          "width": 1920, "height": 1080, "refresh_hz": 60,
                          "ddc_input": {"status": "error", "value": None, "detail": ""}}],
            "permissions": {"screen_capture": "not_checked", "input_capture": "not_checked",
                            "input_injection": "not_checked"},
            "capabilities": {"automatic_switch": "not_tested", "native_file_paste": "not_tested",
                             "inactive_display_capture": "not_tested"},
            "warnings": [],
        }
        with tempfile.TemporaryDirectory(prefix="switchdesk observation ") as folder:
            worker = self.worker(folder, "")
            for field_path, value in (
                (("platform",), ["windows"]),
                (("capabilities", "automatic_switch"), ["not_tested"]),
                (("permissions", "screen_capture"), ["not_checked"]),
                (("displays", 0, "ddc_input", "status"), ["error"]),
            ):
                candidate = json.loads(json.dumps(report))
                target = candidate
                for part in field_path[:-1]:
                    target = target[part]
                target[field_path[-1]] = value
                worker.write_text("[Console]::WriteLine('" + json.dumps(candidate) + "')\n",
                                  encoding="utf-8")
                with self.subTest(field=field_path):
                    self.assertEqual(self.sample(worker), {
                        "outcome": "invalid_report", "exit_code": 0, "report": None,
                    })

    def test_non_iso_utc_timestamp_is_invalid_report(self):
        report = {
            "schema_version": 1, "platform": "windows", "os_version": "10.0.1",
            "architecture": "x64", "collected_at": "2026-1-7T00:00:00Z",
            "read_only": True, "displays": [],
            "permissions": {"screen_capture": "not_checked", "input_capture": "not_checked",
                            "input_injection": "not_checked"},
            "capabilities": {"automatic_switch": "not_tested", "native_file_paste": "not_tested",
                             "inactive_display_capture": "not_tested"},
            "warnings": [],
        }
        with tempfile.TemporaryDirectory(prefix="switchdesk observation ") as folder:
            worker = self.worker(folder, "[Console]::WriteLine('" + json.dumps(report) + "')\n")
            self.assertEqual(self.sample(worker), {
                "outcome": "invalid_report", "exit_code": 0, "report": None,
            })

    def test_top_level_singleton_array_is_invalid_report(self):
        report = {
            "schema_version": 1, "platform": "windows", "os_version": "10.0.1",
            "architecture": "x64", "collected_at": "2026-10-17T00:00:00Z",
            "read_only": True, "displays": [],
            "permissions": {"screen_capture": "not_checked", "input_capture": "not_checked",
                            "input_injection": "not_checked"},
            "capabilities": {"automatic_switch": "not_tested", "native_file_paste": "not_tested",
                             "inactive_display_capture": "not_tested"},
            "warnings": [],
        }
        with tempfile.TemporaryDirectory(prefix="switchdesk observation ") as folder:
            worker = self.worker(folder, "[Console]::WriteLine('" + json.dumps([report]) + "')\n")
            self.assertEqual(self.sample(worker), {
                "outcome": "invalid_report", "exit_code": 0, "report": None,
            })
            worker.write_text("[Console]::WriteLine('{\"platform\":\"windows\"}')\n", encoding="utf-8")
            self.assertEqual(self.sample(worker), {"outcome": "invalid_report", "exit_code": 0,
                                                    "report": None})

    def test_timeout_kills_worker(self):
        with tempfile.TemporaryDirectory(prefix="switchdesk observation ") as folder:
            marker = Path(folder) / "late marker"
            worker = self.worker(folder, "Start-Sleep -Seconds 3\n"
                                 "[IO.File]::WriteAllText($env:SWITCHDESK_MARKER, 'late')\n")
            command = (
                ". $env:SWITCHDESK_SCRIPT;"
                "$exe=[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName;"
                "Invoke-SwitchDeskProbeSample $exe $env:SWITCHDESK_WORKER 150 "
                "| ConvertTo-Json -Compress"
            )
            result = self.ps(command, env={"SWITCHDESK_WORKER": str(worker),
                                           "SWITCHDESK_MARKER": str(marker)}, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
            self.assertEqual(json.loads(result.stdout), {"outcome": "timeout", "exit_code": None,
                                                         "report": None})
            time.sleep(3.2)
            self.assertFalse(marker.exists())

    def test_large_stdout_and_stderr_do_not_deadlock(self):
        with tempfile.TemporaryDirectory(prefix="switchdesk observation ") as folder:
            worker = self.worker(
                folder,
                "[Console]::Out.Write('x' * 131072)\n"
                "[Console]::Error.Write('y' * 131072)\n",
            )
            self.assertEqual(self.sample(worker, 3000), {
                "outcome": "invalid_report", "exit_code": 0, "report": None,
            })

    def test_bounded_fake_observation_matches_validator(self):
        report = {
            "schema_version": 1, "platform": "windows", "os_version": "10.0.1",
            "architecture": "x64", "collected_at": "2026-10-17T00:00:00Z",
            "read_only": True, "displays": [],
            "permissions": {"screen_capture": "not_checked", "input_capture": "not_checked",
                            "input_injection": "not_checked"},
            "capabilities": {"automatic_switch": "not_tested", "native_file_paste": "not_tested",
                             "inactive_display_capture": "not_tested"},
            "warnings": [],
        }
        with tempfile.TemporaryDirectory(prefix="switchdesk observation ") as folder:
            worker = self.worker(folder, "[Console]::WriteLine('" + json.dumps(report) + "')\n")
            command = (
                ". $env:SWITCHDESK_SCRIPT;"
                "$exe=[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName;"
                "$r=Invoke-SwitchDeskObservation $exe $env:SWITCHDESK_WORKER 15 5 4;"
                "ConvertTo-Json -InputObject $r -Depth 12 -Compress"
            )
            result = self.ps(command, env={"SWITCHDESK_WORKER": str(worker)}, timeout=25)
            self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
            observation = json.loads(result.stdout)
            self.assertEqual(validate_observation(observation), [])
            self.assertGreaterEqual(observation["elapsed_ms"], 15000)
            self.assertTrue(observation["samples"])
            self.assertTrue(all(item["outcome"] == "ok" for item in observation["samples"]))

    def test_save_is_utf8_no_bom_relative_and_never_overwrites(self):
        with tempfile.TemporaryDirectory(prefix="switchdesk observation ") as folder:
            command = (
                ". $env:SWITCHDESK_SCRIPT;"
                "Set-Location -LiteralPath $env:SWITCHDESK_FOLDER;"
                "Save-SwitchDeskObservation @{name='显示器'} 'report with spaces.json';"
                "try { Save-SwitchDeskObservation @{name='overwrite'} 'report with spaces.json';"
                "throw 'overwrite allowed' } catch { if($_.Exception.Message -eq 'overwrite allowed'){throw} };"
                "try { Save-SwitchDeskObservation @{} 'missing/report.json';"
                "throw 'missing parent allowed' } catch { if($_.Exception.Message -eq 'missing parent allowed'){throw} };"
                "exit 0"
            )
            result = self.ps(command, env={"SWITCHDESK_FOLDER": folder})
            self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
            output = Path(folder) / "report with spaces.json"
            raw = output.read_bytes()
            self.assertFalse(raw.startswith(b"\xef\xbb\xbf"))
            self.assertEqual(json.loads(raw.decode("utf-8")), {"name": "显示器"})
            self.assertEqual(set(Path(folder).iterdir()), {output})

    @unittest.skipIf(sys.platform == "win32", "仅检查非 Windows 保护")
    def test_entry_rejects_non_windows_before_output(self):
        with tempfile.TemporaryDirectory(prefix="switchdesk observation ") as folder:
            output = Path(folder) / "output.json"
            result = subprocess.run(
                [PWSH, "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(SCRIPT),
                 "-OutputPath", str(output), "-DurationSeconds", "15"],
                cwd=folder, capture_output=True, timeout=10, check=False,
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("Windows", result.stderr.decode(errors="replace"))
            self.assertFalse(output.exists())

    @unittest.skipUnless(sys.platform == "win32", "Windows 硬件集成测试需要 Windows")
    def test_windows_entry_writes_contract(self):
        with tempfile.TemporaryDirectory(prefix="switchdesk observation ") as folder:
            output = Path(folder) / "observation.json"
            result = subprocess.run(
                [PWSH, "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(SCRIPT),
                 "-OutputPath", str(output), "-DurationSeconds", "15"],
                cwd=folder, capture_output=True, timeout=30, check=False,
            )
            self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
            self.assertEqual(validate_observation(json.loads(output.read_text(encoding="utf-8"))), [])


if __name__ == "__main__":
    unittest.main()
