"""Behavior tests for the macOS read-only probe executable."""

import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
BINARY = ROOT / "dist" / "macos" / "switchdesk-probe"
VALIDATOR = ROOT / "tools" / "probe_report.py"


@unittest.skipUnless(sys.platform == "darwin", "原生探针仅在 macOS 上运行")
class ProbeCliTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location("probe_report", VALIDATOR)
        cls.validator = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.validator)

    def setUp(self):
        self.assertTrue(BINARY.is_file(), f"缺少 {BINARY}；请先运行 native/macos/build-probe.sh")

    def run_probe(self, *args, **options):
        return subprocess.run(
            [str(BINARY), *map(str, args)], capture_output=True, check=False, **options
        )

    def assert_valid_report(self, raw):
        report = json.loads(raw.decode("utf-8"))
        self.assertEqual(self.validator.validate_report(report), [])
        self.assertEqual(report["platform"], "macos")
        self.assertTrue(report["read_only"])
        self.assertEqual(set(report["capabilities"].values()), {"not_tested"})
        for display in report["displays"]:
            expected = "unsupported" if display["builtin"] else "not_tested"
            self.assertEqual(display["ddc_input"]["status"], expected)
            self.assertIsNone(display["ddc_input"]["value"])
        return report

    def test_help(self):
        result = self.run_probe("--help")
        self.assertEqual(result.returncode, 0, result.stderr.decode("utf-8", errors="replace"))
        self.assertIn(b"--output", result.stdout)

    def test_unknown_argument_and_missing_output_value(self):
        for args in (("--unknown",), ("--output",)):
            with self.subTest(args=args):
                result = self.run_probe(*args)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(result.stdout, b"")
                self.assertTrue(result.stderr.strip())

    def test_stdout_is_only_valid_report(self):
        result = self.run_probe()
        self.assertEqual(result.returncode, 0, result.stderr.decode("utf-8", errors="replace"))
        self.assertEqual(result.stderr, b"")
        self.assert_valid_report(result.stdout)

    def test_output_path_with_spaces_is_utf8_without_bom(self):
        with tempfile.TemporaryDirectory(prefix="switchdesk probe ") as folder:
            output = Path(folder) / "diagnostic report.json"
            result = self.run_probe("--output", output)
            self.assertEqual(result.returncode, 0, result.stderr.decode("utf-8", errors="replace"))
            self.assertEqual(result.stdout, b"")
            raw = output.read_bytes()
            self.assertFalse(raw.startswith(b"\xef\xbb\xbf"))
            self.assert_valid_report(raw)

    def test_refuses_overwrite_and_preserves_original(self):
        with tempfile.TemporaryDirectory() as folder:
            output = Path(folder) / "already exists.json"
            original = b"keep this exact content\n"
            output.write_bytes(original)
            result = self.run_probe("--output", output)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(result.stdout, b"")
            self.assertTrue(result.stderr.strip())
            self.assertEqual(output.read_bytes(), original)
            self.assertEqual(set(Path(folder).iterdir()), {output})

    def test_failed_write_leaves_no_target_or_temp_and_can_retry(self):
        import resource
        import signal

        def limit_file_size():
            signal.signal(signal.SIGXFSZ, signal.SIG_IGN)
            resource.setrlimit(resource.RLIMIT_FSIZE, (128, 128))

        with tempfile.TemporaryDirectory() as folder:
            output = Path(folder) / "retry.json"
            failed = self.run_probe("--output", output, preexec_fn=limit_file_size)
            self.assertNotEqual(failed.returncode, 0)
            self.assertEqual(failed.stdout, b"")
            self.assertTrue(failed.stderr.strip())
            self.assertEqual(list(Path(folder).iterdir()), [], "写入失败不得留下目标文件或临时文件")

            retried = self.run_probe("--output", output)
            self.assertEqual(retried.returncode, 0, retried.stderr.decode("utf-8", errors="replace"))
            self.assert_valid_report(output.read_bytes())

    def test_missing_parent_is_error_without_creating_directories(self):
        with tempfile.TemporaryDirectory() as folder:
            parent = Path(folder) / "missing parent"
            result = self.run_probe("--output", parent / "report.json")
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(result.stdout, b"")
            self.assertTrue(result.stderr.strip())
            self.assertFalse(parent.exists())


if __name__ == "__main__":
    unittest.main()
