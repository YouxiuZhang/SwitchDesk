using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Threading;

namespace SwitchDesk.Desktop {
    // Relative movement may be coalesced only while it is still queued. Every
    // other message (and every epoch change) remains an ordering barrier.
    internal sealed class ControlMessageQueue {
        private readonly object gate = new object();
        private readonly LinkedList<Dictionary<string, object>> messages = new LinkedList<Dictionary<string, object>>();
        private readonly int capacity;
        private bool completed;
        internal ControlMessageQueue(int capacity) { if (capacity <= 0) throw new ArgumentOutOfRangeException("capacity"); this.capacity = capacity; }
        internal int Count { get { lock (gate) return messages.Count; } }
        internal bool IsAddingCompleted { get { lock (gate) return completed; } }
        internal bool TryAdd(Dictionary<string, object> message) {
            if (message == null) throw new ArgumentNullException("message");
            lock (gate) {
                if (completed) throw new InvalidOperationException("队列已关闭");
                if (messages.Last != null && MergeMove(messages.Last, message)) return true;
                if (messages.Count >= capacity) return false;
                messages.AddLast(message); Monitor.Pulse(gate); return true;
            }
        }
        private static bool Move(Dictionary<string, object> message, out IDictionary<string, object> data, out long epoch, out long dx, out long dy) {
            data = Wire.D(message, "event");
            epoch = Wire.L(message, "epoch", long.MinValue);
            dx = Wire.L(data, "dx", long.MaxValue); dy = Wire.L(data, "dy", long.MaxValue);
            return Wire.S(message, "type") == "input" && Wire.S(data, "kind") == "move" && epoch != long.MinValue &&
                dx >= -2048 && dx <= 2048 && dy >= -2048 && dy <= 2048;
        }
        private static bool MergeMove(LinkedListNode<Dictionary<string, object>> tail, Dictionary<string, object> message) {
            IDictionary<string, object> previous, next; long beforeEpoch, afterEpoch, beforeX, beforeY, afterX, afterY;
            if (!Move(tail.Value, out previous, out beforeEpoch, out beforeX, out beforeY) ||
                !Move(message, out next, out afterEpoch, out afterX, out afterY) || beforeEpoch != afterEpoch) return false;
            long dx = beforeX + afterX, dy = beforeY + afterY;
            // Keep another frame when the receiver's per-axis bound is reached.
            if (dx < -2048 || dx > 2048 || dy < -2048 || dy > 2048) return false;
            var data = new Dictionary<string, object>(previous); data["dx"] = dx; data["dy"] = dy;
            var merged = new Dictionary<string, object>(tail.Value); merged["event"] = data;
            tail.Value = merged; return true;
        }
        internal Dictionary<string, object> Take() {
            lock (gate) {
                while (messages.Count == 0) {
                    if (completed) throw new InvalidOperationException("队列已关闭");
                    Monitor.Wait(gate);
                }
                var message = messages.First.Value; messages.RemoveFirst(); return message;
            }
        }
        internal bool TryTake(out Dictionary<string, object> message) {
            lock (gate) {
                if (messages.Count == 0) { message = null; return false; }
                message = messages.First.Value; messages.RemoveFirst(); return true;
            }
        }
        internal void CompleteAdding() { lock (gate) { completed = true; Monitor.PulseAll(gate); } }
    }

    internal sealed class Session : IDisposable {
        private readonly Identity identity;
        private readonly object gate = new object();
        private readonly ControlMessageQueue controlOut = new ControlMessageQueue(512);
        private readonly BlockingCollection<Dictionary<string, object>> mediaOut = new BlockingCollection<Dictionary<string, object>>(new ConcurrentQueue<Dictionary<string, object>>(), 16);
        private TcpListener listener;
        private TcpClient controlClient, mediaClient;
        private SslStream control, media;
        private bool controlPending, mediaPending;
        private int admissionGeneration;
        private readonly object controlWrite = new object(), mediaWrite = new object();
        private string activeDevice;
        private volatile bool running;
        private DateTime lastPong = DateTime.MinValue;
        private Timer heartbeat;
        internal Func<string, string, bool> AskApproval;
        internal Action<Dictionary<string, object>> OnControl, OnMedia;
        internal Action<string> OnStatus;
        internal Action OnConnected, OnDisconnected, OnHeartbeat;
        internal Session(Identity identity) { this.identity = identity; }
        internal bool Connected { get { lock (gate) return control != null; } }
        internal bool MediaConnected { get { lock (gate) return media != null; } }
        internal void Start() {
            listener = new TcpListener(IPAddress.Any, 45923); listener.Start(); running = true;
            new Thread(Accept) { IsBackground = true, Name = "SwitchDesk TLS accept" }.Start();
            new Thread(new ThreadStart(delegate { SendLoop(controlOut.Take, false); })) { IsBackground = true, Name = "SwitchDesk control send" }.Start();
            new Thread(new ThreadStart(delegate { SendLoop(mediaOut.Take, true); })) { IsBackground = true, Name = "SwitchDesk media send" }.Start();
            heartbeat = new Timer(Heartbeat, null, 1000, 1000);
        }
        private void Accept() {
            while (running) {
                try { TcpClient client = listener.AcceptTcpClient(); client.NoDelay = true; new Thread(new ThreadStart(delegate { Authenticate(client); })) { IsBackground = true }.Start(); }
                catch (SocketException) { if (running) Status("监听中断"); }
                catch (ObjectDisposedException) { break; }
            }
        }
        private void Authenticate(TcpClient client) {
            SslStream ssl = null;
            bool reservedControl = false, reservedMedia = false;
            int reservationGeneration = -1;
            try {
                ssl = new SslStream(client.GetStream(), false);
                ssl.ReadTimeout = 4000; ssl.WriteTimeout = 4000;
                ssl.AuthenticateAsServer(identity.Certificate, false, SslProtocols.Tls12, false);
                var hello = Wire.Read(ssl, 65536);
                string channel = Wire.S(hello, "channel"), device = Wire.S(hello, "device");
                if (Wire.S(hello, "type") != "hello" || Wire.L(hello, "version", 0) != 1 ||
                    (channel != "control" && channel != "media") || !GuidLike(device) || !identity.TokenMatches(Wire.S(hello, "token")))
                    throw new InvalidDataException("认证失败");
                if (channel == "control") {
                    lock (gate) {
                        if (control != null || controlPending || (identity.Device != null && identity.Device != device)) throw new InvalidDataException("设备不匹配或已连接");
                        controlPending = true; reservedControl = true; reservationGeneration = admissionGeneration;
                    }
                    if (identity.Device == null) {
                        if (AskApproval == null || !AskApproval(device, Wire.S(hello, "name") ?? "Mac")) throw new InvalidDataException("用户拒绝配对");
                        lock (gate) { if (reservationGeneration != admissionGeneration || (identity.Device != null && identity.Device != device)) throw new InvalidDataException("配对已变化"); identity.Approve(device); }
                    }
                    Wire.Write(ssl, Wire.Obj("type", "welcome", "version", 1, "channel", channel), controlWrite, 65536);
                    lock (gate) {
                        if (!running || reservationGeneration != admissionGeneration || control != null) throw new InvalidDataException("连接已关闭");
                        control = ssl; controlClient = client; activeDevice = device; lastPong = DateTime.UtcNow;
                        controlPending = false; reservedControl = false;
                    }
                    ssl.ReadTimeout = System.Threading.Timeout.Infinite; ssl.WriteTimeout = System.Threading.Timeout.Infinite;
                    if (OnConnected != null) OnConnected();
                    ReadLoop(ssl, false);
                } else {
                    SslStream parentControl;
                    lock (gate) {
                        if (control == null || media != null || mediaPending || activeDevice != device) throw new InvalidDataException("控制通道未认证");
                        parentControl = control; mediaPending = true; reservedMedia = true; reservationGeneration = admissionGeneration;
                    }
                    Wire.Write(ssl, Wire.Obj("type", "welcome", "version", 1, "channel", channel), mediaWrite, 65536);
                    lock (gate) {
                        if (!running || reservationGeneration != admissionGeneration || control != parentControl || media != null || activeDevice != device) throw new InvalidDataException("控制通道已变化");
                        media = ssl; mediaClient = client; mediaPending = false; reservedMedia = false;
                    }
                    ssl.ReadTimeout = System.Threading.Timeout.Infinite; ssl.WriteTimeout = System.Threading.Timeout.Infinite;
                    ReadLoop(ssl, true);
                }
            } catch (Exception ex) {
                if (ex is SocketException || ex is IOException || ex is AuthenticationException || ex is InvalidDataException) Status(ex is InvalidDataException ? ex.Message : "连接关闭");
                else Status("连接异常：" + ex.GetType().Name);
            } finally {
                lock (gate) {
                    if (reservedControl) controlPending = false;
                    if (reservedMedia) mediaPending = false;
                    if (ssl != null && (ssl == control || ssl == media)) DisconnectLocked();
                }
                try { if (ssl != null) ssl.Dispose(); } catch { }
                try { client.Close(); } catch { }
            }
        }
        private static bool GuidLike(string value) { Guid g; return value != null && Guid.TryParse(value, out g); }
        private void ReadLoop(SslStream stream, bool isMedia) {
            while (running) {
                var msg = Wire.Read(stream, isMedia ? 12 * 1024 * 1024 : 65536);
                if (!isMedia && Wire.S(msg, "type") == "pong") { lastPong = DateTime.UtcNow; if (OnHeartbeat != null) OnHeartbeat(); continue; }
                if (!isMedia && Wire.S(msg, "type") == "ping") { lastPong = DateTime.UtcNow; if (OnHeartbeat != null) OnHeartbeat(); SendControl(Wire.Obj("type", "pong")); continue; }
                if (isMedia) { if (OnMedia != null) OnMedia(msg); }
                else if (OnControl != null) OnControl(msg);
            }
        }
        private void SendLoop(Func<Dictionary<string, object>> take, bool isMedia) {
            while (running) {
                Dictionary<string, object> msg;
                try { msg = take(); } catch (InvalidOperationException) { break; }
                SslStream stream; lock (gate) stream = isMedia ? media : control;
                if (stream == null) continue;
                try { Wire.Write(stream, msg, isMedia ? mediaWrite : controlWrite, isMedia ? 12 * 1024 * 1024 : 65536); }
                catch { Disconnect(); }
            }
        }
        internal void SendControl(Dictionary<string, object> msg) {
            if (!Connected || controlOut.IsAddingCompleted) return;
            if (!controlOut.TryAdd(msg)) { Status("控制通道拥塞，已断开以恢复本地输入"); Disconnect(); }
        }
        internal void SendMedia(Dictionary<string, object> msg) { if (Wire.S(msg, "type") != "preview") throw new InvalidOperationException("仅预览帧允许丢弃"); if (MediaConnected && !mediaOut.IsAddingCompleted) mediaOut.TryAdd(msg); }
        internal void SendFile(Dictionary<string, object> msg) { while (MediaConnected && !mediaOut.IsAddingCompleted && !mediaOut.TryAdd(msg, 500)) { } }
        private void Heartbeat(object state) {
            if (!Connected) return;
            if ((DateTime.UtcNow - lastPong).TotalSeconds > 4) { Status("心跳超时，已恢复本地输入"); Disconnect(); return; }
            SendControl(Wire.Obj("type", "ping"));
        }
        internal void Disconnect() { lock (gate) { admissionGeneration++; DisconnectLocked(); } }
        private void DisconnectLocked() {
            if (control == null && media == null) return;
            admissionGeneration++;
            try { if (controlClient != null) controlClient.Close(); } catch { }
            try { if (mediaClient != null) mediaClient.Close(); } catch { }
            control = null; media = null; controlClient = null; mediaClient = null; activeDevice = null;
            while (controlOut.Count > 0) { Dictionary<string, object> ignored; controlOut.TryTake(out ignored); }
            while (mediaOut.Count > 0) { Dictionary<string, object> ignored; mediaOut.TryTake(out ignored); }
            ThreadPool.QueueUserWorkItem(delegate { if (OnDisconnected != null) OnDisconnected(); });
        }
        internal void Revoke() { lock (gate) { admissionGeneration++; DisconnectLocked(); identity.Revoke(); } Status("配对已撤销"); }
        private void Status(string s) { if (OnStatus != null) OnStatus(s); }
        public void Dispose() { running = false; if (heartbeat != null) heartbeat.Dispose(); if (listener != null) listener.Stop(); Disconnect(); controlOut.CompleteAdding(); mediaOut.CompleteAdding(); }
    }
}
