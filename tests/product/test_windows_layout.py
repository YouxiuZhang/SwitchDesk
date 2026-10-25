"""Run the Windows UI's actual geometry expressions and layout methods on macOS.

GDI and WinForms cannot run here. The viewport stub only models the space consumed
by native scrollbars; LayoutPages, FinishPage and stroke geometry are product code.
"""
import json
from pathlib import Path
import re
import shutil
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]
PRODUCT = ROOT / 'native/windows/product'
PWSH = ROOT / '.build/tools/powershell-7.6.6/pwsh'


def method(source, name):
    start = source.index('        private void ' + name + '(')
    opening = source.index('{', start)
    depth = 1
    end = opening + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]


def constructor(source, name):
    start = source.index('        internal ' + name + '()')
    opening = source.index('{', start)
    return source[start:source.index('}', opening) + 1]


def first_argument(source, call):
    start = source.index(call) + len(call)
    depth = 0
    end = start
    while source[end] != ',' or depth:
        depth += (source[end] == '(') - (source[end] == ')')
        end += 1
    return source[start:end]


def run_geometry():
    executable = str(PWSH) if PWSH.exists() else shutil.which('pwsh')
    if not executable:
        raise unittest.SkipTest('PowerShell 7 unavailable')
    design = (PRODUCT / 'DesignControls.cs').read_text()
    main = (PRODUCT / 'MainForm.cs').read_text()
    expressions = []
    for name in ('DeskPanel', 'DeskButton', 'DeskBadge', 'DeskDevice'):
        section = re.split(r'\bclass ' + name + r'\b', design)[1]
        expressions.append(first_argument(section, 'DeskTheme.Round('))
    helper = PRODUCT / 'LayoutGeometry.cs'
    source = (helper.read_text() if helper.exists() else '') + r'''
namespace SwitchDesk.Desktop {
    // ButtonBase starts Opaque in .NET Framework. Test the exact product
    // constructors against that inherited style, without invoking native UI.
    [System.Flags]
    internal enum ControlStyles {
        UserPaint=1,AllPaintingInWmPaint=2,OptimizedDoubleBuffer=4,
        ResizeRedraw=8,SupportsTransparentBackColor=16,Opaque=32
    }
    internal enum FlatStyle { Flat }
    internal enum AccessibleRole { CheckButton }
    internal static class Cursors { internal static readonly object Hand=new object(); }
    internal static class DeskTheme { internal static object Font(float n,bool bold) { return null; } }
    internal sealed class FlatAppearance { internal int BorderSize; }
    internal class Button {
        private ControlStyles styles=ControlStyles.Opaque;
        internal Color BackColor;
        internal FlatStyle FlatStyle;
        internal FlatAppearance FlatAppearance=new FlatAppearance();
        internal object Font,Cursor;
        internal bool UseVisualStyleBackColor,AutoSize;
        internal AccessibleRole AccessibleRole;
        protected void SetStyle(ControlStyles flags,bool enabled) {
            styles=enabled?styles|flags:styles&~flags;
        }
        internal bool DrawsBackground { get { return (styles&ControlStyles.Opaque)==0; } }
    }
    internal sealed class DeskButton : Button { BUTTON_CONSTRUCTOR }
    internal sealed class DeskToggle : Button { TOGGLE_CONSTRUCTOR }
    public static class LayoutHarness {
        public static bool[] Backgrounds() {
            return new[] { new DeskButton().DrawsBackground,new DeskToggle().DrawsBackground };
        }
        public static object[] Borders() {
            var rows = new System.Collections.Generic.List<object>();
            foreach (float s in new float[] { 1, 1.25F, 1.5F, 1.75F, 2, 3 }) {
                float scale = s;
                int Width = (int)(134 * s), Height = (int)(30 * s);
                var bounds = new System.Drawing.RectangleF[] { EXPRESSIONS };
                for (int i = 0; i < bounds.Length; i++) {
                    var r = bounds[i];
                    rows.Add(new { kind=i, scale=s, left=r.Left-s/2,
                        top=r.Top-s/2, right=r.Right+s/2, bottom=r.Bottom+s/2,
                        width=Width, height=Height });
                }
            }
            return rows.ToArray();
        }
        public static object[] Pages() {
            var rows = new System.Collections.Generic.List<object>();
            foreach (int width in new int[] { 430, 625, 932 }) {
                foreach (float scale in new float[] { 1, 1.25F, 1.5F, 2 }) {
                    var form = new LayoutForm(width, scale);
                    form.Run(1200); rows.Add(form.Snapshot("overflow"));
                    form.Run(400); rows.Add(form.Snapshot("fits"));
                    form.Run(1500); rows.Add(form.Snapshot("overflow-again"));
                }
            }
            return rows.ToArray();
        }
    }
    internal sealed class FakePage {
        internal readonly int FullWidth, FullHeight, Bar;
        internal System.Drawing.Size ClientSize;
        internal bool Horizontal, Vertical;
        internal FakePage(int width, int height, int bar) {
            FullWidth=width; FullHeight=height; Bar=bar;
            ClientSize = new System.Drawing.Size(width,height);
        }
        internal void Update(System.Drawing.Size content) {
            // WinForms' scrollbars reduce the client viewport in both axes.
            bool horizontal=false,vertical=false;
            for(int i=0;i<3;i++) {
                vertical=content.Height > FullHeight-(horizontal?Bar:0);
                horizontal=content.Width > FullWidth-(vertical?Bar:0);
            }
            Horizontal=horizontal; Vertical=vertical;
            ClientSize=new System.Drawing.Size(FullWidth-(vertical?Bar:0),FullHeight-(horizontal?Bar:0));
        }
    }
    internal sealed class FakeContent {
        private System.Drawing.Size size;
        private readonly FakePage page;
        internal FakeContent(FakePage owner) { page=owner; }
        internal System.Drawing.Size Size {
            get { return size; } set { size=value; page.Update(value); }
        }
    }
    internal sealed class FakeSidebar {
        internal int Width;
        internal System.Drawing.Size ClientSize = new System.Drawing.Size(188,850);
    }
    internal sealed class LayoutForm {
        private bool layingOut;
        private object hero=new object(), dailyCard=new object(), sidebarNote=new object();
        private object[] eyebrows=new object[3], headings=new object[3];
        private FakePage[] pages=new FakePage[3];
        private FakeContent[] contents=new FakeContent[3];
        private FakeSidebar sidebar=new FakeSidebar();
        private float UiScale, contentHeight;
        internal LayoutForm(int width,float scale) {
            UiScale=scale;
            for(int i=0;i<3;i++) {
                pages[i]=new FakePage(D(width),D(850),D(17));
                contents[i]=new FakeContent(pages[i]);
            }
        }
        private int D(float value) { return (int)System.Math.Round(value*UiScale); }
        private void Put(object control,float x,float y,float width,float height) { }
        private void LayoutConsole(float w,float x,float y) { FinishPage(0,contentHeight); }
        private void LayoutClipboard(float w,float x,float y) { FinishPage(1,contentHeight); }
        private void LayoutSettings(float w,float x,float y) { FinishPage(2,contentHeight); }
        internal void Run(float height) { contentHeight=height; LayoutPages(); }
        internal object Snapshot(string state) {
            var rows=new System.Collections.Generic.List<object>();
            for(int i=0;i<3;i++)rows.Add(new {
                contentWidth=contents[i].Size.Width,clientWidth=pages[i].ClientSize.Width,
                horizontal=pages[i].Horizontal,vertical=pages[i].Vertical });
            return new { state=state,scale=UiScale,pages=rows.ToArray() };
        }
LAYOUT_METHODS
    }
}
'''
    source = ('using System;\nusing System.Drawing;\n' + source.replace('EXPRESSIONS', ','.join(expressions))
              .replace('BUTTON_CONSTRUCTOR', constructor(design, 'DeskButton'))
              .replace('TOGGLE_CONSTRUCTOR', constructor(design, 'DeskToggle'))
              .replace('LAYOUT_METHODS', method(main, 'LayoutPages') + '\n' + method(main, 'FinishPage')))
    script = "$ErrorActionPreference='Stop'; Add-Type -TypeDefinition @'\n" + source + "\n'@\n"
    script += "@{ backgrounds=[SwitchDesk.Desktop.LayoutHarness]::Backgrounds(); borders=[SwitchDesk.Desktop.LayoutHarness]::Borders(); pages=[SwitchDesk.Desktop.LayoutHarness]::Pages() } | ConvertTo-Json -Depth 8 -Compress"
    result = subprocess.run([executable, '-NoProfile', '-NonInteractive', '-Command', script],
                            capture_output=True, text=True, timeout=30, cwd=ROOT)
    if result.returncode:
        raise AssertionError(result.stdout + result.stderr)
    return json.loads(result.stdout)


class WindowsLayoutTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.geometry = run_geometry()

    def test_scaled_border_strokes_stay_inside_all_four_control_types(self):
        for row in self.geometry['borders']:
            with self.subTest(kind=row['kind'], scale=row['scale']):
                self.assertGreaterEqual(row['left'], 0)
                self.assertGreaterEqual(row['top'], 0)
                self.assertLessEqual(row['right'], row['width'])
                self.assertLessEqual(row['bottom'], row['height'])

    def test_round_buttons_and_toggles_allow_background_painting(self):
        self.assertEqual(self.geometry['backgrounds'], [True, True],
                         'Inherited Opaque skips background painting and exposes stale buffer pixels')

    def test_pages_follow_client_width_when_scrollbars_appear_and_disappear(self):
        for scenario in self.geometry['pages']:
            for page in scenario['pages']:
                with self.subTest(scenario=scenario):
                    self.assertEqual(page['contentWidth'], page['clientWidth'])
                    self.assertFalse(page['horizontal'])
                    self.assertEqual(page['vertical'], scenario['state'] != 'fits')


if __name__ == '__main__':
    unittest.main()
