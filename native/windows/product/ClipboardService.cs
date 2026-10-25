using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SwitchDesk.Desktop {
    internal sealed class LazyFileData : IDataObject {
        private readonly DataObject other = new DataObject();
        private readonly FileTransfers transfers; private readonly string id;
        private readonly object gate = new object(); private string[] materialized;
        internal LazyFileData(FileTransfers transfers, string id) { this.transfers = transfers; this.id = id; other.SetData("SwitchDeskOfferId", id); }
        private string[] Files() { lock (gate) { if (materialized == null) materialized = transfers.Materialize(id); return materialized; } }
        public object GetData(string format, bool autoConvert) { return format == DataFormats.FileDrop ? Files() : other.GetData(format, autoConvert); }
        public object GetData(string format) { return GetData(format, true); }
        public object GetData(Type format) { return format == typeof(string[]) ? Files() : other.GetData(format); }
        public bool GetDataPresent(string format, bool autoConvert) { return format == DataFormats.FileDrop || other.GetDataPresent(format, autoConvert); }
        public bool GetDataPresent(string format) { return GetDataPresent(format, true); }
        public bool GetDataPresent(Type format) { return format == typeof(string[]) || other.GetDataPresent(format); }
        public string[] GetFormats(bool autoConvert) { return new[] { DataFormats.FileDrop, "SwitchDeskOfferId" }; }
        public string[] GetFormats() { return GetFormats(true); }
        public void SetData(string format, bool autoConvert, object data) { other.SetData(format, autoConvert, data); }
        public void SetData(string format, object data) { other.SetData(format, data); }
        public void SetData(Type format, object data) { other.SetData(format, data); }
        public void SetData(object data) { other.SetData(data); }
    }
    internal sealed class ClipboardService : IDisposable {
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
        private readonly Session session; private readonly FileTransfers files;
        private readonly ConcurrentQueue<Action> actions = new ConcurrentQueue<Action>();
        private readonly Thread thread; private volatile bool running = true, enabled;
        private readonly object revisionGate = new object(); private long revision;
        private int pendingRemote;
        private string lastFingerprint, ownedOffer;
        private uint lastWrittenSequence;
        internal Action<string> Status;
        internal ClipboardService(Session session, FileTransfers files) {
            this.session = session; this.files = files;
            thread = new Thread(Loop) { IsBackground = true, Name = "SwitchDesk clipboard STA" }; thread.SetApartmentState(ApartmentState.STA); thread.Start();
        }
        internal void SetEnabled(bool value) { enabled = value; lastFingerprint = null; if (!value) { ThreadPool.QueueUserWorkItem(delegate { files.Disconnect(); }); actions.Enqueue(ClearOwnedIfCurrent); } }
        internal void Disconnect() { lastFingerprint = null; files.Disconnect(); actions.Enqueue(ClearOwnedIfCurrent); }
        private void Loop() {
            DateTime poll = DateTime.MinValue;
            while (running) {
                Application.DoEvents(); Action action;
                while (actions.TryDequeue(out action)) { try { action(); } catch (Exception ex) { Report("剪贴板操作失败：" + ex.Message); } }
                if (enabled && session.Connected && session.MediaConnected && (DateTime.UtcNow - poll).TotalMilliseconds >= 400) {
                    poll = DateTime.UtcNow; try { CaptureLocal(); } catch (Exception ex) { Report("剪贴板读取失败：" + ex.Message); }
                }
                Thread.Sleep(30);
            }
        }
        private void CaptureLocal() {
            uint currentSequence = GetClipboardSequenceNumber();
            if (lastWrittenSequence != 0 && currentSequence == lastWrittenSequence) return;
            IDataObject data = Clipboard.GetDataObject(); if (data == null) return;
            if (data.GetDataPresent("SwitchDeskOfferId")) return;
            if (data.GetDataPresent(DataFormats.FileDrop)) {
                string[] paths = data.GetData(DataFormats.FileDrop) as string[];
                if (paths == null || paths.Length == 0) return;
                string fingerprint = "files:" + String.Join("|", paths);
                if (fingerprint == lastFingerprint) return;
                FileOffer offer = FileTransfers.Scan(paths); files.KeepLocal(offer);
                if (Publish(Wire.Obj("type", "clipboard", "id", offer.Id, "origin", "windows", "kind", "files", "entries", FileTransfers.EntryWire(offer), "roots", offer.Roots.ToArray())) < 0) return;
                lastFingerprint = fingerprint;
                Report("文件已共享：" + offer.Roots.Count + " 项"); return;
            }
            if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text)) {
                string value = data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string;
                if (value == null || value.StartsWith("switchdesk1:", StringComparison.Ordinal)) return;
                if (System.Text.Encoding.UTF8.GetByteCount(value) > 1024 * 1024) { Report("文字超过 1 MiB，未共享"); return; }
                string fp = "text:" + value; if (fp == lastFingerprint) return;
                if (Publish(Wire.Obj("type", "clipboard", "id", Guid.NewGuid().ToString(), "origin", "windows", "kind", "text", "text", value)) < 0) return;
                lastFingerprint = fp; return;
            }
            if (data.GetDataPresent(DataFormats.Bitmap)) {
                Image image = data.GetData(DataFormats.Bitmap) as Image; if (image == null) return;
                using (var ms = new MemoryStream()) {
                    image.Save(ms, ImageFormat.Png); if (ms.Length > 8 * 1024 * 1024) { Report("图片超过 8 MiB，未共享"); return; }
                    byte[] bytes = ms.ToArray(); string fp = "image:" + Wire.Hex(Hash(bytes)); if (fp == lastFingerprint) return;
                    if (Publish(Wire.Obj("type", "clipboard", "id", Guid.NewGuid().ToString(), "origin", "windows", "kind", "image", "data", Convert.ToBase64String(bytes))) < 0) return;
                    lastFingerprint = fp;
                }
            }
        }
        private static byte[] Hash(byte[] b) { using (var sha = System.Security.Cryptography.SHA256.Create()) return sha.ComputeHash(b); }
        private long Publish(Dictionary<string, object> message) {
            lock (revisionGate) {
                if (!session.MediaConnected) return -1;
                if (Wire.S(message, "origin") == "windows" && Interlocked.CompareExchange(ref pendingRemote, 0, 0) > 0) return -1;
                long ordered = ++revision;
                message["revision"] = ordered;
                session.SendFile(message);
                return ordered;
            }
        }
        internal void Receive(IDictionary<string, object> message) {
            string type = Wire.S(message, "type");
            if (type != "clipboard") { if (enabled && type != null && type.StartsWith("file_", StringComparison.Ordinal)) files.Receive(message); return; }
            if (!enabled || !session.Connected || Wire.S(message, "origin") != "mac") return;
            string id = Wire.S(message, "id"); Guid parsed; if (!Guid.TryParse(id, out parsed)) return;
            string kind = Wire.S(message, "kind");
            string value = null; byte[] png = null; FileOffer offer = null;
            try {
                if (kind == "text") { value = Wire.S(message, "text"); if (value == null || value.StartsWith("switchdesk1:", StringComparison.Ordinal) || System.Text.Encoding.UTF8.GetByteCount(value) > 1024 * 1024) return; }
                else if (kind == "image") { png = Convert.FromBase64String(Wire.S(message, "data") ?? ""); if (png.Length == 0 || png.Length > 8 * 1024 * 1024) return; using (var ms = new MemoryStream(png)) using (var image = Image.FromStream(ms)) { if (image.RawFormat.Guid != ImageFormat.Png.Guid || (long)image.Width * image.Height > 10000000) return; } }
                else if (kind == "files") offer = FileTransfers.Parse(id, message);
                else return;
            } catch (Exception ex) { Report("收到的剪贴板无效：" + ex.Message); return; }
            Interlocked.Increment(ref pendingRemote);
            Dictionary<string, object> ordered = new Dictionary<string, object>(message);
            long r = Publish(ordered);
            if (r < 0) { Interlocked.Decrement(ref pendingRemote); return; }
            string capturedValue = value; byte[] capturedPng = png; FileOffer capturedOffer = offer;
            actions.Enqueue(delegate {
                try {
                    if (!enabled || !session.Connected) return;
                    lock (revisionGate) if (r < revision) return;
                    if (kind == "text") { Clipboard.SetText(capturedValue); lastFingerprint = "text:" + capturedValue; ownedOffer = null; }
                    else if (kind == "image") { using (var ms = new MemoryStream(capturedPng)) using (var image = Image.FromStream(ms)) using (var bitmap = new Bitmap(image)) Clipboard.SetImage(bitmap); lastFingerprint = "image:" + Wire.Hex(Hash(capturedPng)); ownedOffer = null; }
                    else { files.KeepRemote(capturedOffer); Clipboard.SetDataObject(new LazyFileData(files, id), false); ownedOffer = id; lastFingerprint = "remote:" + id; }
                    lastWrittenSequence = GetClipboardSequenceNumber();
                } finally { Interlocked.Decrement(ref pendingRemote); }
            });
        }
        private void ClearOwnedIfCurrent() {
            if (ownedOffer == null) return;
            try { IDataObject data = Clipboard.GetDataObject(); if (data != null && data.GetDataPresent("SwitchDeskOfferId") && (data.GetData("SwitchDeskOfferId") as string) == ownedOffer) Clipboard.Clear(); } catch { }
            ownedOffer = null;
        }
        private void Report(string s) { if (Status != null) Status(s); }
        public void Dispose() { running = false; thread.Join(1000); files.Disconnect(); }
    }
}
