using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SwitchDesk.Desktop {
    internal sealed class PreviewService : IDisposable {
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetUserObjectInformation(IntPtr desktop, int index, StringBuilder buffer, int length, out int required);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        private readonly Session session;
        private readonly System.Threading.Timer timer;
        private volatile bool subscribed, allowed;
        private Screen screen;
        private volatile bool sentUnavailable; private int capturing;
        internal PreviewService(Session session) { this.session = session; timer = new System.Threading.Timer(Capture, null, 200, 200); }
        internal void SetAllowed(bool value) { allowed = value; sentUnavailable = false; if (!value) Unavailable("请在 Windows 设置中开启「允许 Mac 查看本机屏幕预览」"); }
        internal void SetSubscribed(bool value) { subscribed = value; if (!value) sentUnavailable = false; }
        internal void SetScreen(Screen value) { screen = value; }
        private static bool Unlocked() {
            IntPtr desktop = OpenInputDesktop(0, false, 0x00020001);
            if (desktop == IntPtr.Zero) return false;
            try {
                var name = new StringBuilder(128); int required;
                return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out required) &&
                    String.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
            } finally { CloseDesktop(desktop); }
        }
        private void Capture(object ignored) {
            if (!subscribed || !session.MediaConnected || Interlocked.Exchange(ref capturing, 1) != 0) return;
            try { CaptureOne(); } finally { Interlocked.Exchange(ref capturing, 0); }
        }
        private void CaptureOne() {
            if (!allowed) { Unavailable("请在 Windows 设置中开启「允许 Mac 查看本机屏幕预览」"); return; }
            if (!Unlocked()) { Unavailable("Windows 已锁屏或正在安全桌面，返回桌面后恢复预览"); return; }
            try {
                Screen selected = screen ?? Screen.PrimaryScreen;
                if (selected == null) { Unavailable("没有可用屏幕"); return; }
                var bounds = selected.Bounds;
                if (bounds.Width < 1 || bounds.Height < 1) { Unavailable("屏幕尺寸无效"); return; }
                int width = Math.Min(640, bounds.Width), height = Math.Max(1, (int)((double)bounds.Height * width / bounds.Width));
                using (var source = new Bitmap(bounds.Width, bounds.Height)) using (var graphics = Graphics.FromImage(source)) {
                    graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
                    using (var scaled = new Bitmap(width, height)) using (var g = Graphics.FromImage(scaled)) {
                        g.DrawImage(source, 0, 0, width, height);
                        ImageCodecInfo codec = null; foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders()) if (c.MimeType == "image/jpeg") { codec = c; break; }
                        if (codec == null) return;
                        using (var stream = new MemoryStream()) using (var parameters = new EncoderParameters(1)) {
                            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 65L);
                            scaled.Save(stream, codec, parameters);
                            if (stream.Length > 512 * 1024) { Unavailable("预览帧过大"); return; }
                            session.SendMedia(Wire.Obj("type", "preview", "data", Convert.ToBase64String(stream.ToArray()), "label", "Windows共享屏幕", "width", width, "height", height));
                            sentUnavailable = false;
                        }
                    }
                }
            } catch { Unavailable("屏幕捕获暂不可用"); }
        }
        private void Unavailable(string detail) {
            if (!subscribed || sentUnavailable) return;
            sentUnavailable = true;
            ThreadPool.QueueUserWorkItem(delegate {
                if (subscribed && sentUnavailable) session.SendFile(Wire.Obj("type", "preview_unavailable", "detail", detail));
            });
        }
        public void Dispose() { timer.Dispose(); }
    }
}
