"""Contract and CLI tests for a read-only sequence of Windows input reads."""

from copy import deepcopy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from tools import input_observation


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "tools" / "input_observation.py"


def probe(value=17, *, status="readable", display_id="display-1"):
    return {
        "schema_version": 1, "platform": "windows", "os_version": "Windows 11",
        "architecture": "x86_64", "collected_at": "2026-10-17T08:30:00Z",
        "read_only": True,
        "displays": [{
            "id": display_id, "name": display_id, "builtin": False,
            "width": 1920, "height": 1080, "refresh_hz": 60,
            "ddc_input": {"status": status,
                          "value": value if status == "readable" else None,
                          "detail": ""},
        }],
        "permissions": {"screen_capture": "not_checked",
                        "input_capture": "not_checked",
                        "input_injection": "not_checked"},
        "capabilities": {"automatic_switch": "not_tested",
                         "native_file_paste": "not_tested",
                         "inactive_display_capture": "not_tested"},
        "warnings": [],
    }


def sample(start, finish, outcome="ok", report=None):
    return {"started_ms": start, "finished_ms": finish, "outcome": outcome,
            "exit_code": 0 if outcome in ("ok", "invalid_report") else
            3 if outcome == "worker_error" else None,
            "report": probe() if outcome == "ok" and report is None else report}


def observation(samples=None):
    return {"schema_version": 1, "kind": "input_observation",
            "platform": "windows", "read_only": True,
            "started_at": "2026-10-17T08:30:00Z",
            "requested_duration_seconds": 30, "elapsed_ms": 30000,
            "samples": samples if samples is not None else [sample(0, 100)]}


class ObservationContractTests(unittest.TestCase):
    def assert_error(self, report, path):
        errors = input_observation.validate_observation(report)
        self.assertTrue(any(path in error for error in errors), errors)

    def test_valid_sequence_and_no_mutation(self):
        report = observation([sample(0, 100, report=probe(17)),
                              sample(100, 200, report=probe(18)),
                              sample(200, 300, report=probe(17))])
        original = deepcopy(report)
        self.assertEqual(input_observation.validate_observation(report), [])
        summary = input_observation.summarize_observation(report)
        self.assertEqual(report, original)
        for fragment in ("3", "17", "18", "自动切屏", "尚未验证"):
            self.assertIn(fragment, summary)
        self.assertLess(summary.find("17"), summary.find("18"))
        self.assertNotIn("Windows 输入", summary)
        self.assertNotIn("Mac 输入", summary)

    def test_timeout_interrupts_readability(self):
        report = observation([sample(0, 100, report=probe(17)),
                              sample(100, 110, "timeout"),
                              sample(110, 200, report=probe(17))])
        self.assertEqual(input_observation.validate_observation(report), [])
        summary = input_observation.summarize_observation(report)
        self.assertIn("超时", summary)
        self.assertIn("1", summary)
        self.assertNotIn("持续可读", summary)

    def test_ddc_failure_and_displays_appearing_or_disappearing(self):
        first = probe(17)
        second = probe(status="error")
        second["displays"].append(probe(18, display_id="display-2")["displays"][0])
        report = observation([sample(0, 100, report=first),
                              sample(100, 200, report=second),
                              sample(200, 300, report={**probe(), "displays": []})])
        self.assertEqual(input_observation.validate_observation(report), [])
        summary = input_observation.summarize_observation(report)
        for fragment in ("display-1", "display-2", "17", "18", "DDC", "错误", "未枚举"):
            self.assertIn(fragment, summary)

    def test_zero_samples_and_worker_failure_count(self):
        self.assertIn("未取得观测样本", input_observation.summarize_observation(observation([])))
        report = observation([sample(0, 1, "worker_error"), sample(1, 2, "invalid_report")])
        summary = input_observation.summarize_observation(report)
        self.assertIn("工作进程失败", summary)
        self.assertIn("1", summary)
        self.assertIn("报告无效", summary)

    def test_exact_top_level_fields_and_types(self):
        base = observation()
        for field in base:
            with self.subTest(missing=field):
                changed = deepcopy(base)
                del changed[field]
                self.assert_error(changed, field)
        for field, value in (("schema_version", True), ("schema_version", 2),
                             ("kind", "other"), ("platform", "macos"),
                             ("read_only", 1), ("started_at", "2026-10-17T08:30:00+08:00"),
                             ("started_at", "2026-10-17"),
                             ("started_at", "\ud800"),
                             ("requested_duration_seconds", True),
                             ("requested_duration_seconds", 14),
                             ("requested_duration_seconds", 121),
                             ("elapsed_ms", False), ("elapsed_ms", -1),
                             ("samples", {}), ("samples", [sample(0, 0)] * 257)):
            with self.subTest(field=field, value=str(value)[:40]):
                changed = deepcopy(base)
                changed[field] = value
                self.assert_error(changed, field)
        changed = deepcopy(base)
        changed["unknown"] = 1
        self.assert_error(changed, "unknown")
        changed = deepcopy(base)
        changed["\ud800"] = 1
        errors = input_observation.validate_observation(changed)
        self.assertTrue(errors)
        self.assertTrue(all("\ud800" not in error for error in errors))
        for timestamp in ("2026-10-17T08:30:00+0000", "20261017T083000Z", "2026-10-17T08:30Z"):
            with self.subTest(timestamp=timestamp):
                changed = deepcopy(base)
                changed["started_at"] = timestamp
                self.assertEqual(input_observation.validate_observation(changed), [])

    def test_sample_time_order_and_exact_fields(self):
        for change, path in (
            (lambda r: r["samples"][0].__setitem__("started_ms", True), "samples[0].started_ms"),
            (lambda r: r["samples"][0].__setitem__("finished_ms", -1), "samples[0].finished_ms"),
            (lambda r: r["samples"][0].__setitem__("finished_ms", 30001), "samples[0].finished_ms"),
            (lambda r: r["samples"][0].__setitem__("started_ms", 101), "samples[0].started_ms"),
            (lambda r: r["samples"].append(sample(99, 200)), "samples[1].started_ms"),
            (lambda r: r["samples"][0].__setitem__("extra", 1), "samples[0].extra"),
            (lambda r: r["samples"][0].pop("outcome"), "samples[0].outcome"),
        ):
            with self.subTest(path=path):
                changed = observation()
                change(changed)
                self.assert_error(changed, path)

    def test_outcome_exit_code_and_nested_report(self):
        cases = (("ok", None, "exit_code"),
                 ("ok", probe(17), "exit_code"),
                 ("timeout", probe(17), "report"),
                 ("timeout", None, "exit_code"),
                 ("worker_error", None, "exit_code"),
                 ("invalid_report", None, "exit_code"),
                 ("cleanup_failed", probe(17), "report"),
                 ("unknown", None, "outcome"))
        for outcome, nested, field in cases:
            with self.subTest(outcome=outcome, field=field):
                item = sample(0, 1, outcome, nested)
                if field == "exit_code":
                    item["exit_code"] = 0 if outcome in ("timeout", "worker_error") else None
                    if outcome == "invalid_report":
                        item["exit_code"] = True
                if outcome == "ok" and nested is None:
                    item["report"] = None
                self.assert_error(observation([item]), f"samples[0].{field}")
        item = sample(0, 1)
        item["report"]["platform"] = "macos"
        self.assert_error(observation([item]), "samples[0].report.platform")
        item = sample(0, 1)
        item["report"]["displays"][0]["ddc_input"]["value"] = -1
        self.assert_error(observation([item]), "samples[0].report.displays[0].ddc_input.value")
        item = sample(0, 1)
        item["report"]["unknown"] = 1
        self.assert_error(observation([item]), "samples[0].report.unknown")

    def test_unhashable_outcome_and_nonobject_report_return_precise_errors(self):
        item = sample(0, 1)
        item["outcome"] = []
        self.assert_error(observation([item]), "samples[0].outcome")
        item = sample(0, 1)
        item["report"] = None
        errors = input_observation.validate_observation(observation([item]))
        self.assertTrue(any(error.startswith("samples[0].report:") for error in errors), errors)


class CliTests(unittest.TestCase):
    def run_cli(self, path):
        return subprocess.run([sys.executable, str(SCRIPT), str(path)],
                              capture_output=True, text=True, check=False)

    def test_valid_file_with_spaces_is_not_overwritten(self):
        with tempfile.TemporaryDirectory(prefix="input observation ") as folder:
            path = Path(folder) / "observation with spaces.json"
            original = json.dumps(observation(), ensure_ascii=False, indent=2)
            path.write_text(original, encoding="utf-8")
            result = self.run_cli(path)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("17", result.stdout)
            self.assertEqual(path.read_text(encoding="utf-8"), original)

    def test_invalid_json_encoding_contract_and_depth_fail_without_traceback(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "report.json"
            cases = (b"{ invalid", b"\xff", json.dumps({**observation(), "kind": "wrong"}).encode(),
                     ("[" * 1500 + "0" + "]" * 1500).encode(),
                     json.dumps(observation()).replace('"schema_version": 1', '"schema_version": ' + '9' * 5000).encode())
            for content in cases:
                with self.subTest(length=len(content)):
                    path.write_bytes(content)
                    result = self.run_cli(path)
                    self.assertEqual(result.returncode, 2, result.stderr)
                    self.assertEqual(result.stdout, "")
                    self.assertNotIn("Traceback", result.stderr)
                    self.assertEqual(path.read_bytes(), content)


if __name__ == "__main__":
    unittest.main()
