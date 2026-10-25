using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SwitchDesk.Desktop {
    internal sealed class FileItem { internal string Path; internal bool Dir; internal long Size; internal string Source; internal DateTime Mtime; }
    internal sealed class FileOffer { internal string Id; internal List<FileItem> Entries = new List<FileItem>(); internal List<string> Roots = new List<string>(); internal long Total; }
    internal sealed class FileTransfers {
        private readonly Session session;
        private readonly object gate = new object();
        private readonly Dictionary<string, FileOffer> local = new Dictionary<string, FileOffer>();
        private readonly Dictionary<string, FileOffer> remote = new Dictionary<string, FileOffer>();
        private readonly Dictionary<string, Transfer> pending = new Dictionary<string, Transfer>();
        private readonly Dictionary<string, string> outgoing = new Dictionary<string, string>();
        private readonly string cache;
        internal Action<string> Status;
        private sealed class Transfer {
            internal string Id; internal FileOffer Offer; internal string Partial; internal string Completed; internal ManualResetEventSlim Done = new ManualResetEventSlim(false);
            internal string Error; internal int Index; internal FileStream Stream; internal SHA256 Hash; internal long Offset; internal bool Active;
            internal void Close() { if (Stream != null) { Stream.Dispose(); Stream = null; } if (Hash != null) { Hash.Dispose(); Hash = null; } }
        }
        internal FileTransfers(Session session) {
            this.session = session;
            cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwitchDesk", "Cache");
            Directory.CreateDirectory(cache);
        }
        internal static FileOffer Scan(string[] paths) {
            if (paths == null || paths.Length == 0 || paths.Length > 128) throw new InvalidDataException("文件选择数量无效");
            var offer = new FileOffer { Id = Guid.NewGuid().ToString() };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in paths) {
                string full = System.IO.Path.GetFullPath(root);
                string name = System.IO.Path.GetFileName(full.TrimEnd(System.IO.Path.DirectorySeparatorChar));
                if (!Wire.ValidPath(name) || !seen.Add(name)) throw new InvalidDataException("文件名无效或重复");
                offer.Roots.Add(name);
                ScanOne(offer, full, name, seen);
            }
            return offer;
        }
        private static void ScanOne(FileOffer offer, string full, string relative, HashSet<string> seen) {
            FileAttributes attrs = File.GetAttributes(full);
            if ((attrs & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("不支持符号链接或重解析点");
            bool dir = (attrs & FileAttributes.Directory) != 0;
            long size = dir ? 0 : new FileInfo(full).Length;
            if (!Wire.ValidPath(relative) || offer.Entries.Count >= 4096 || size < 0 || offer.Total > 16L * 1024 * 1024 * 1024 - size) throw new InvalidDataException("文件清单超限");
            offer.Total += size;
            offer.Entries.Add(new FileItem { Path = relative, Dir = dir, Size = size, Source = full, Mtime = File.GetLastWriteTimeUtc(full) });
            if (dir) foreach (string child in Directory.GetFileSystemEntries(full)) {
                string path = relative + "/" + System.IO.Path.GetFileName(child);
                if (!seen.Add(path)) throw new InvalidDataException("大小写重复路径");
                ScanOne(offer, child, path, seen);
            }
        }
        internal static FileOffer Parse(string id, IDictionary<string, object> msg) {
            IList entries = Wire.A(msg, "entries"), roots = Wire.A(msg, "roots");
            if (String.IsNullOrEmpty(id) || entries == null || roots == null || entries.Count > 4096 || roots.Count > 128) throw new InvalidDataException("文件清单非法");
            var offer = new FileOffer { Id = id };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object r in roots) { string root = r as string; if (root == null || !Wire.ValidPath(root) || root.IndexOf('/') >= 0 || !seen.Add(root)) throw new InvalidDataException("顶层文件名非法"); offer.Roots.Add(root); }
            seen.Clear();
            foreach (object raw in entries) {
                var item = raw as IDictionary<string, object>;
                string path = Wire.S(item, "path"); bool dir = Wire.B(item, "dir"); long size = Wire.L(item, "size", -1);
                if (!Wire.ValidPath(path) || !seen.Add(path) || size < 0 || (dir && size != 0) || offer.Total > 16L * 1024 * 1024 * 1024 - size) throw new InvalidDataException("文件项非法或超限");
                string top = path.Split('/')[0]; if (!offer.Roots.Contains(top)) throw new InvalidDataException("文件不属于顶层清单");
                offer.Total += size; offer.Entries.Add(new FileItem { Path = path, Dir = dir, Size = size });
            }
            if (offer.Entries.Count == 0 || offer.Roots.Count == 0) throw new InvalidDataException("空文件清单");
            foreach (string root in offer.Roots) if (!seen.Contains(root)) throw new InvalidDataException("顶层目录缺失");
            return offer;
        }
        internal static object[] EntryWire(FileOffer offer) {
            var a = new object[offer.Entries.Count]; for (int i = 0; i < a.Length; i++) { FileItem f = offer.Entries[i]; a[i] = Wire.Obj("path", f.Path, "dir", f.Dir, "size", f.Size); } return a;
        }
        internal void KeepLocal(FileOffer offer) { lock (gate) { local[offer.Id] = offer; if (local.Count > 8) { string oldest = null; foreach (string id in local.Keys) { if (!outgoing.ContainsValue(id) && id != offer.Id) { oldest = id; break; } } if (oldest != null) local.Remove(oldest); } } }
        internal void KeepRemote(FileOffer offer) { lock (gate) { remote[offer.Id] = offer; if (remote.Count > 8) { string oldest = null; foreach (string id in remote.Keys) { oldest = id; break; } if (oldest != null) remote.Remove(oldest); } } }
        internal string[] Materialize(string offerId) {
            if (!session.MediaConnected) throw new IOException("文件通道未连接");
            FileOffer offer; lock (gate) { if (!remote.TryGetValue(offerId, out offer)) throw new InvalidDataException("文件清单已失效"); }
            string transferId = Guid.NewGuid().ToString();
            string partial = Path.Combine(cache, transferId + ".partial"), complete = Path.Combine(cache, transferId);
            var transfer = new Transfer { Id = transferId, Offer = offer, Partial = partial, Completed = complete };
            Directory.CreateDirectory(partial);
            lock (gate) pending.Add(transferId, transfer);
            try {
                StatusMessage("正在获取文件：" + offer.Roots.Count + " 项");
                session.SendFile(Wire.Obj("type", "file_request", "id", offerId, "transfer", transferId));
                if (!transfer.Done.Wait(TimeSpan.FromMinutes(30))) { Cancel(transferId); throw new TimeoutException("文件传输超时"); }
                if (transfer.Error != null) throw new IOException(transfer.Error);
                string[] roots = new string[offer.Roots.Count]; for (int i = 0; i < roots.Length; i++) roots[i] = Path.Combine(complete, offer.Roots[i]);
                return roots;
            } finally { lock (gate) pending.Remove(transferId); }
        }
        internal void Receive(IDictionary<string, object> msg) {
            string type = Wire.S(msg, "type"), transferId = Wire.S(msg, "transfer");
            if (type == "file_request") {
                FileOffer offer; lock (gate) local.TryGetValue(Wire.S(msg, "id") ?? "", out offer);
                if (offer == null || !GuidValid(transferId)) { Error(transferId, "文件清单已失效"); return; }
                bool duplicate;
                lock (gate) { duplicate = outgoing.ContainsKey(transferId) || pending.ContainsKey(transferId); if (!duplicate) outgoing[transferId] = offer.Id; }
                if (duplicate) { Error(transferId, "传输编号重复"); return; }
                Task.Run(delegate { SendOffer(offer, transferId); }); return;
            }
            if (!GuidValid(transferId)) return;
            if (type == "file_cancel" || type == "file_error") { lock (gate) { if (outgoing.ContainsKey(transferId)) { outgoing.Remove(transferId); return; } } }
            Transfer transfer; lock (gate) pending.TryGetValue(transferId, out transfer);
            if (transfer == null) return;
            try {
                if (type == "file_error" || type == "file_cancel") throw new IOException(Wire.S(msg, "detail") ?? "传输取消");
                if (type == "file_begin") {
                    if (transfer.Active) throw new InvalidDataException("文件传输重复开始");
                    FileOffer offered = Parse(Wire.S(msg, "id"), msg);
                    if (offered.Id != transfer.Offer.Id || !Same(offered, transfer.Offer)) throw new InvalidDataException("文件清单与复制内容不一致");
                    DriveInfo disk = new DriveInfo(Path.GetPathRoot(cache)); if (disk.AvailableFreeSpace < offered.Total + 16 * 1024 * 1024) throw new IOException("缓存磁盘空间不足");
                    transfer.Active = true;
                    foreach (FileItem f in offered.Entries) if (f.Dir) Directory.CreateDirectory(SafePath(transfer.Partial, f.Path));
                } else if (type == "file_chunk") {
                    if (!transfer.Active || transfer.Index >= transfer.Offer.Entries.Count) throw new InvalidDataException("文件传输顺序错误");
                    FileItem f = NextFile(transfer);
                    if (f == null || Wire.S(msg, "path") != f.Path || Wire.L(msg, "offset", -1) != transfer.Offset) throw new InvalidDataException("文件块位置错误");
                    byte[] bytes = Convert.FromBase64String(Wire.S(msg, "data") ?? "");
                    if (bytes.Length == 0 || bytes.Length > 32768 || transfer.Offset + bytes.Length > f.Size) throw new InvalidDataException("文件块长度错误");
                    OpenFile(transfer, f); transfer.Stream.Write(bytes, 0, bytes.Length); transfer.Hash.TransformBlock(bytes, 0, bytes.Length, bytes, 0); transfer.Offset += bytes.Length;
                    if (transfer.Offset == f.Size || (transfer.Offset % (1024 * 1024)) < bytes.Length) StatusMessage("正在接收 " + f.Path + "：" + transfer.Offset + "/" + f.Size + " 字节");
                } else if (type == "file_end") {
                    FileItem f = NextFile(transfer);
                    if (f == null || Wire.S(msg, "path") != f.Path) throw new InvalidDataException("文件结束顺序错误");
                    OpenFile(transfer, f); transfer.Hash.TransformFinalBlock(new byte[0], 0, 0);
                    if (transfer.Offset != f.Size || !String.Equals(Wire.S(msg, "sha256"), Wire.Hex(transfer.Hash.Hash), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("文件校验失败");
                    transfer.Close(); transfer.Index++; transfer.Offset = 0;
                } else if (type == "file_done") {
                    while (transfer.Index < transfer.Offer.Entries.Count && transfer.Offer.Entries[transfer.Index].Dir) transfer.Index++;
                    if (!transfer.Active || transfer.Index != transfer.Offer.Entries.Count) throw new InvalidDataException("文件未全部校验");
                    Directory.Move(transfer.Partial, transfer.Completed); transfer.Done.Set(); StatusMessage("文件已就绪");
                }
            } catch (Exception ex) { Fail(transfer, ex.Message); Error(transferId, "文件传输校验失败"); }
        }
        private static FileItem NextFile(Transfer t) { while (t.Index < t.Offer.Entries.Count && t.Offer.Entries[t.Index].Dir) t.Index++; return t.Index < t.Offer.Entries.Count ? t.Offer.Entries[t.Index] : null; }
        private static void OpenFile(Transfer t, FileItem f) {
            if (t.Stream != null) return;
            string path = SafePath(t.Partial, f.Path); Directory.CreateDirectory(Path.GetDirectoryName(path));
            t.Stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); t.Hash = SHA256.Create();
        }
        private static string SafePath(string root, string relative) { if (!Wire.ValidPath(relative)) throw new InvalidDataException("路径非法"); string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))); if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径越界"); return full; }
        private static bool Same(FileOffer a, FileOffer b) { if (a.Roots.Count != b.Roots.Count || a.Entries.Count != b.Entries.Count) return false; for (int i = 0; i < a.Roots.Count; i++) if (a.Roots[i] != b.Roots[i]) return false; for (int i = 0; i < a.Entries.Count; i++) if (a.Entries[i].Path != b.Entries[i].Path || a.Entries[i].Dir != b.Entries[i].Dir || a.Entries[i].Size != b.Entries[i].Size) return false; return true; }
        private static bool GuidValid(string value) { Guid id; return Guid.TryParse(value, out id); }
        private void SendOffer(FileOffer offer, string transferId) {
            try {
                session.SendFile(Wire.Obj("type", "file_begin", "id", offer.Id, "transfer", transferId, "entries", EntryWire(offer), "roots", offer.Roots.ToArray()));
                foreach (FileItem f in offer.Entries) {
                    CheckOutgoing(transferId);
                    if (f.Dir) continue;
                    FileInfo info = new FileInfo(f.Source); if (!info.Exists || info.Length != f.Size || info.LastWriteTimeUtc != f.Mtime || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("源文件已变化");
                    using (var sha = SHA256.Create()) using (var stream = new FileStream(f.Source, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                        byte[] buffer = new byte[32768]; int n; long offset = 0;
                        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0) {
                            CheckOutgoing(transferId);
                            sha.TransformBlock(buffer, 0, n, buffer, 0);
                            byte[] payload = new byte[n]; Buffer.BlockCopy(buffer, 0, payload, 0, n);
                            session.SendFile(Wire.Obj("type", "file_chunk", "transfer", transferId, "path", f.Path, "offset", offset, "data", Convert.ToBase64String(payload)));
                            offset += n;
                        }
                        sha.TransformFinalBlock(new byte[0], 0, 0);
                        info.Refresh(); if (info.Length != f.Size || info.LastWriteTimeUtc != f.Mtime || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("源文件传输期间变化");
                        session.SendFile(Wire.Obj("type", "file_end", "transfer", transferId, "path", f.Path, "sha256", Wire.Hex(sha.Hash)));
                    }
                }
                CheckOutgoing(transferId); session.SendFile(Wire.Obj("type", "file_done", "transfer", transferId));
            } catch (Exception ex) { if (ex is OperationCanceledException) return; Error(transferId, "文件发送失败"); StatusMessage("文件发送失败：" + ex.Message); }
            finally { lock (gate) outgoing.Remove(transferId); }
        }
        private void CheckOutgoing(string id) { lock (gate) if (!outgoing.ContainsKey(id) || !session.MediaConnected) throw new OperationCanceledException(); }
        private void Error(string id, string detail) { if (GuidValid(id)) session.SendFile(Wire.Obj("type", "file_error", "transfer", id, "detail", detail.Length > 120 ? detail.Substring(0, 120) : detail)); }
        private void Fail(Transfer t, string detail) { t.Close(); t.Error = detail; t.Done.Set(); try { if (Directory.Exists(t.Partial)) Directory.Delete(t.Partial, true); } catch { } StatusMessage("文件传输失败：" + detail); }
        internal void Cancel(string id) { Transfer t; lock (gate) pending.TryGetValue(id, out t); if (t == null) return; Fail(t, "传输已取消"); session.SendFile(Wire.Obj("type", "file_cancel", "transfer", id)); }
        internal void CancelAll() {
            List<string> inbound, outbound;
            lock (gate) { inbound = new List<string>(pending.Keys); outbound = new List<string>(outgoing.Keys); outgoing.Clear(); }
            foreach (string id in inbound) Cancel(id);
            foreach (string id in outbound) session.SendFile(Wire.Obj("type", "file_cancel", "transfer", id));
            StatusMessage("文件传输已取消");
        }
        internal void Disconnect() { List<Transfer> list; lock (gate) { list = new List<Transfer>(pending.Values); remote.Clear(); outgoing.Clear(); } foreach (Transfer t in list) Fail(t, "连接已断开"); }
        private void StatusMessage(string s) { if (Status != null) Status(s); }
    }
}
