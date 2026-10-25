"""Contract tests for the read-only display probe report."""

import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "tools" / "probe_report.py"


def valid_report():
    return {
        "schema_version": 1,
        "platform": "macos",
        "os_version": "15.0",
        "architecture": "arm64",
        "collected_at": "2026-10-17T08:30:00Z",
        "read_only": True,
        "displays": [{
            "id": "display-1",
            "name": "Built-in Display",
            "builtin": True,
            "width": 2560,
            "height": 1600,
            "refresh_hz": 60.0,
            "ddc_input": {"status": "readable", "value": 17, "detail": ""},
        }],
        "permissions": {
            "screen_capture": "not_checked",
            "input_capture": "denied",
            "input_injection": "not_applicable",
        },
        "capabilities": {
            "automatic_switch": "not_tested",
            "native_file_paste": "not_tested",
            "inactive_display_capture": "not_tested",
        },
        "warnings": [],
    }


def load_module(test_case):
    test_case.assertTrue(SCRIPT.exists(), "缺少 tools/probe_report.py")
    spec = importlib.util.spec_from_file_location("probe_report", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ReportContractTests(unittest.TestCase):
    def setUp(self):
        self.module = load_module(self)

    def assert_field_error(self, report, path):
        errors = self.module.validate_report(report)
        self.assertTrue(any(path in error for error in errors), errors)

    def test_valid_report_and_empty_display_list(self):
        self.assertEqual(self.module.validate_report(valid_report()), [])
        report = valid_report()
        report["displays"] = []
        self.assertEqual(self.module.validate_report(report), [])

    def test_required_fields_report_paths(self):
        for field in valid_report():
            with self.subTest(field=field):
                report = valid_report()
                del report[field]
                self.assert_field_error(report, field)

    def test_rejects_unknown_top_level_and_display_fields(self):
        report = valid_report()
        report["hostname"] = "secret-host"
        report["displays"][0]["serial_number"] = "secret-serial"
        self.assert_field_error(report, "hostname")
        self.assert_field_error(report, "displays[0].serial_number")

    def test_top_level_types_and_utc_timestamp(self):
        invalid = [
            ("schema_version", True), ("schema_version", 2),
            ("platform", "linux"), ("os_version", " "),
            ("architecture", None), ("collected_at", "2026-10-17"),
            ("collected_at", "2026-10-17T08:30:00+08:00"),
            ("collected_at", "2026-10-17T08:30:00"),
            ("collected_at", "not-a-date"), ("read_only", 1),
            ("displays", {}), ("warnings", ["fine", 1]),
        ]
        for field, value in invalid:
            with self.subTest(field=field, value=value):
                report = valid_report()
                report[field] = value
                self.assert_field_error(report, field)

    def test_accepts_valid_iso8601_utc_time_forms(self):
        for timestamp in (
            "2026-10-17T08:30:00+0000",
            "20261017T083000Z",
            "2026-10-17T08:30Z",
        ):
            with self.subTest(timestamp=timestamp):
                report = valid_report()
                report["collected_at"] = timestamp
                self.assertEqual(self.module.validate_report(report), [])

    def test_display_types_boundaries_and_missing_fields(self):
        display = valid_report()["displays"][0]
        for field in display:
            with self.subTest(missing=field):
                report = valid_report()
                del report["displays"][0][field]
                self.assert_field_error(report, f"displays[0].{field}")
        invalid = [
            ("id", ""), ("name", " "), ("builtin", "true"),
            ("width", True), ("width", 0), ("height", -1),
            ("height", 2.5), ("refresh_hz", True),
            ("refresh_hz", -0.1), ("refresh_hz", float("nan")),
            ("refresh_hz", float("inf")),
        ]
        for field, value in invalid:
            with self.subTest(field=field, value=value):
                report = valid_report()
                report["displays"][0][field] = value
                self.assert_field_error(report, f"displays[0].{field}")
        report = valid_report()
        report["displays"][0]["builtin"] = None
        report["displays"][0]["refresh_hz"] = None
        report["platform"] = "windows"
        self.assertEqual(self.module.validate_report(report), [])

    def test_large_nonnegative_integer_refresh_rate_is_finite(self):
        report = valid_report()
        report["displays"][0]["refresh_hz"] = 10**1000
        try:
            errors = self.module.validate_report(report)
        except Exception as exc:
            self.fail(f"极大整数应得到验证结果，实际抛出 {type(exc).__name__}: {exc}")
        self.assertEqual(errors, [])

    def test_ddc_status_and_uint16_value(self):
        for status in ("not_tested", "unsupported", "error"):
            with self.subTest(status=status):
                report = valid_report()
                report["displays"][0]["ddc_input"] = {
                    "status": status, "value": None, "detail": "unavailable"
                }
                self.assertEqual(self.module.validate_report(report), [])
                report["displays"][0]["ddc_input"]["value"] = 1
                self.assert_field_error(report, "displays[0].ddc_input.value")
        for value in (True, -1, 65536, 1.5, None):
            with self.subTest(value=value):
                report = valid_report()
                report["displays"][0]["ddc_input"]["value"] = value
                self.assert_field_error(report, "displays[0].ddc_input.value")
        report = valid_report()
        report["displays"][0]["ddc_input"]["value"] = 65535
        self.assertEqual(self.module.validate_report(report), [])
        report["displays"][0]["ddc_input"]["status"] = "switched"
        self.assert_field_error(report, "displays[0].ddc_input.status")

    def test_fixed_permission_and_capability_maps(self):
        for key in ("screen_capture", "input_capture", "input_injection"):
            report = valid_report()
            report["permissions"].pop(key)
            self.assert_field_error(report, f"permissions.{key}")
        report = valid_report()
        report["permissions"]["screen_capture"] = "unknown"
        report["permissions"]["microphone"] = "granted"
        self.assert_field_error(report, "permissions.screen_capture")
        self.assert_field_error(report, "permissions.microphone")
        for key in ("automatic_switch", "native_file_paste", "inactive_display_capture"):
            report = valid_report()
            report["capabilities"][key] = "supported"
            self.assert_field_error(report, f"capabilities.{key}")
        report = valid_report()
        report["capabilities"].pop("automatic_switch")
        self.assert_field_error(report, "capabilities.automatic_switch")

    def test_nested_objects_in_state_fields_return_errors(self):
        report = valid_report()
        report["displays"][0]["ddc_input"]["status"] = []
        report["permissions"]["screen_capture"] = {}
        report["capabilities"]["automatic_switch"] = []
        try:
            errors = self.module.validate_report(report)
        except Exception as exc:
            self.fail(f"应返回字段路径错误，实际抛出 {type(exc).__name__}: {exc}")
        self.assertTrue(any("displays[0].ddc_input.status" in error for error in errors), errors)
        self.assertTrue(any("permissions.screen_capture" in error for error in errors), errors)
        self.assertTrue(any("capabilities.automatic_switch" in error for error in errors), errors)

    def test_summary_preserves_uncertainty(self):
        report = valid_report()
        summary = self.module.summarize_report(report)
        self.assertIn("自动切屏尚未验证", summary)
        self.assertNotIn("切屏成功", summary)
        report["displays"] = []
        summary = self.module.summarize_report(report)
        self.assertIn("当前进程未枚举到显示器", summary)
        self.assertNotIn("未插显示器", summary)

    def test_rejects_isolated_surrogates_in_string_values_and_unknown_keys(self):
        cases = (
            ("os_version", lambda report: report.__setitem__("os_version", "\ud800")),
            ("displays[0].name", lambda report: report["displays"][0].__setitem__("name", "X\udfff")),
            ("displays[0].ddc_input.detail", lambda report: report["displays"][0]["ddc_input"].__setitem__("detail", "\ud800")),
            ("warnings[0]", lambda report: report["warnings"].append("\ud800")),
        )
        for path, change in cases:
            with self.subTest(path=path):
                report = valid_report()
                change(report)
                self.assert_field_error(report, path)
        report = valid_report()
        report["\ud800"] = "secret"
        report["displays"][0]["\udfff"] = "secret"
        errors = self.module.validate_report(report)
        self.assertTrue(errors)
        self.assertTrue(all("\ud800" not in error for error in errors), errors)
        self.assertTrue(all("\udfff" not in error for error in errors), errors)
        report = valid_report()
        report["os_version"] = "macOS 日本語 😀"
        self.assertEqual(self.module.validate_report(report), [])


class CliTests(unittest.TestCase):
    def run_cli(self, file, *args):
        return subprocess.run(
            [sys.executable, str(SCRIPT), str(file), *args],
            capture_output=True, text=True, check=False,
        )

    def test_valid_path_with_spaces_and_json_output_does_not_overwrite(self):
        with tempfile.TemporaryDirectory(prefix="probe report ") as folder:
            file = Path(folder) / "report with spaces.json"
            original = json.dumps(valid_report(), ensure_ascii=False, indent=2)
            file.write_text(original, encoding="utf-8")
            result = self.run_cli(file, "--json")
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(json.loads(result.stdout), valid_report())
            self.assertEqual(file.read_text(encoding="utf-8"), original)

    def test_cli_rejects_invalid_json_and_contract(self):
        with tempfile.TemporaryDirectory() as folder:
            file = Path(folder) / "report.json"
            file.write_text("{ invalid", encoding="utf-8")
            result = self.run_cli(file)
            self.assertEqual(result.returncode, 2)
            report = valid_report()
            report["schema_version"] = 2
            file.write_text(json.dumps(report), encoding="utf-8")
            result = self.run_cli(file, "--json")
            self.assertEqual(result.returncode, 2)
            self.assertEqual(result.stdout, "")

    def test_cli_rejects_nonfinite_json_number(self):
        with tempfile.TemporaryDirectory() as folder:
            file = Path(folder) / "report.json"
            report = valid_report()
            report["displays"][0]["refresh_hz"] = float("nan")
            file.write_text(json.dumps(report), encoding="utf-8")
            result = self.run_cli(file, "--json")
            self.assertEqual(result.returncode, 2)
            self.assertEqual(result.stdout, "")

    def test_cli_handles_large_integer_and_deep_json(self):
        with tempfile.TemporaryDirectory() as folder:
            file = Path(folder) / "report.json"
            huge_integer = json.dumps(valid_report()).replace(
                '"schema_version": 1', '"schema_version": ' + "9" * 5000
            )
            for content in (huge_integer, "[" * 1500 + "0" + "]" * 1500):
                with self.subTest(length=len(content)):
                    file.write_text(content, encoding="utf-8")
                    result = self.run_cli(file)
                    self.assertEqual(result.returncode, 2, result.stderr)
                    self.assertNotIn("Traceback", result.stderr)

    def test_cli_handles_isolated_surrogate_in_value_and_unknown_key(self):
        with tempfile.TemporaryDirectory() as folder:
            file = Path(folder) / "report.json"
            for report in (
                {**valid_report(), "os_version": "\ud800"},
                {**valid_report(), "\ud800": "secret"},
            ):
                with self.subTest(keys=list(report)):
                    file.write_text(json.dumps(report), encoding="utf-8")
                    for args in ((), ("--json",)):
                        result = self.run_cli(file, *args)
                        self.assertEqual(result.returncode, 2, result.stderr)
                        self.assertEqual(result.stdout, "")
                        self.assertNotIn("Traceback", result.stderr)

    def test_cli_prints_chinese_summary(self):
        with tempfile.TemporaryDirectory() as folder:
            file = Path(folder) / "report.json"
            file.write_text(json.dumps(valid_report()), encoding="utf-8")
            result = self.run_cli(file)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("自动切屏尚未验证", result.stdout)


if __name__ == "__main__":
    unittest.main()
