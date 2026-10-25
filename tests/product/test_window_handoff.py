"""Exercise real window geometry/state policy without moving any user windows."""
from pathlib import Path
import shutil
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]


class WindowHandoffTests(unittest.TestCase):
    @unittest.skipUnless(shutil.which('swiftc'), 'Swift compiler unavailable')
    def test_geometry_restoration_and_state_cancellation(self):
        output = ROOT / '.build/tests/window-handoff-tests'
        output.parent.mkdir(parents=True, exist_ok=True)
        result = subprocess.run([
            'swiftc', '-module-cache-path', str(ROOT / '.build/macos-app/module-cache'),
            str(ROOT / 'native/macos/app/WindowHandoffPolicy.swift'),
            str(ROOT / 'tests/product/window_handoff_tests.swift'), '-o', str(output),
        ], capture_output=True, text=True, timeout=90)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        result = subprocess.run([str(output)], capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('Window handoff policy tests passed', result.stdout)

    @unittest.skipUnless(shutil.which('swiftc'), 'Swift compiler unavailable')
    def test_ui_review_does_not_start_window_or_network_services(self):
        build = ROOT / '.build/tests'
        build.mkdir(parents=True, exist_ok=True)
        # Exercise the real model with service constructors that fail if instantiated.
        source = (ROOT / 'native/macos/app/Main.swift').read_text()
        model = build / 'UIReviewDesktopModel.swift'
        model.write_text(source.split('private struct DeskCard<')[0])
        result = subprocess.run([
            'swiftc', '-module-cache-path', str(ROOT / '.build/macos-app/module-cache'),
            str(model), str(ROOT / 'native/macos/app/Protocol.swift'),
            str(ROOT / 'native/macos/app/InputReadiness.swift'),
            str(ROOT / 'tests/product/ui_review_model_tests.swift'), '-o', str(build / 'ui-review-model-tests'),
        ], capture_output=True, text=True, timeout=90)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        result = subprocess.run([str(build / 'ui-review-model-tests')], capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('UI review action isolation tests passed', result.stdout)


if __name__ == '__main__':
    unittest.main()
