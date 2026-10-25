using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

// PowerShell runs on .NET, which has no System.Web serializer. The framing and
// queue under test remain the production Wire.cs and Session.cs sources.
namespace System.Web.Script.Serialization {
    public sealed class JavaScriptSerializer {
        public int MaxJsonLength { get; set; }
        public int RecursionLimit { get; set; }
        public string Serialize(object value) { return System.Text.Json.JsonSerializer.Serialize(value); }
        public object DeserializeObject(string value) { throw new NotSupportedException("Read is outside this harness"); }
    }
}

namespace SwitchDesk.Desktop {
    internal sealed class Identity {
        internal X509Certificate2 Certificate { get { return null; } }
        internal string Device { get { return null; } }
        internal bool TokenMatches(string token) { return false; }
        internal void Approve(string device) { }
        internal void Revoke() { }
    }

    public static class TransportHarness {
        private sealed class QueueAccess {
            private readonly object queue;
            internal QueueAccess(Session session) { queue = typeof(Session).GetField("controlOut", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(session); }
            private object Call(string name, Type[] types, object[] args) {
                try { return queue.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, types, null).Invoke(queue, args); }
                catch (TargetInvocationException ex) { throw ex.InnerException; }
            }
            internal bool Add(Dictionary<string, object> value) { return (bool)Call("TryAdd", new[] { typeof(Dictionary<string, object>) }, new object[] { value }); }
            internal Dictionary<string, object> Take() { return (Dictionary<string, object>)Call("Take", Type.EmptyTypes, new object[0]); }
            internal bool TryTake(out Dictionary<string, object> value) {
                object[] args = { null };
                bool result = (bool)Call("TryTake", new[] { typeof(Dictionary<string, object>).MakeByRefType() }, args);
                value = (Dictionary<string, object>)args[0]; return result;
            }
            internal int Count { get { return (int)queue.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(queue, null); } }
            internal bool Completed { get { return (bool)queue.GetType().GetProperty("IsAddingCompleted", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(queue, null); } }
            internal void Complete() { Call("CompleteAdding", Type.EmptyTypes, new object[0]); }
            internal List<Dictionary<string, object>> Drain() {
                var values = new List<Dictionary<string, object>>(); Dictionary<string, object> value;
                while (TryTake(out value)) values.Add(value);
                return values;
            }
        }
        private sealed class CountingStream : MemoryStream {
            internal int Writes, Flushes;
            public override void Write(byte[] buffer, int offset, int count) { Writes++; base.Write(buffer, offset, count); }
            public override void Flush() { Flushes++; base.Flush(); }
        }
        private static Dictionary<string, object> Move(int x, int y, int epoch) {
            return Wire.Obj("type", "input", "epoch", epoch, "event", Wire.Obj("kind", "move", "dx", x, "dy", y));
        }
        private static Session ConnectedSession() {
            var session = new Session(null);
            // An in-memory, unauthenticated stream marks Connected without any
            // listener, network connection, native hook, or background sender.
            typeof(Session).GetField("control", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(session, new SslStream(new MemoryStream()));
            return session;
        }
        public static object Burst() {
            var session = ConnectedSession(); var queue = new QueueAccess(session);
            for (int i = 0; i < 10000; i++) session.SendControl(Move(3, -2, 7));
            var messages = queue.Drain(); long x = 0, y = 0; bool bounded = true;
            foreach (var message in messages) {
                var data = Wire.D(message, "event"); long dx = Wire.L(data, "dx", 0), dy = Wire.L(data, "dy", 0);
                x += dx; y += dy; bounded &= Math.Abs(dx) <= 2048 && Math.Abs(dy) <= 2048;
            }
            return new { x = x, y = y, frames = messages.Count, bounded = bounded, connected = session.Connected };
        }
        public static object Barriers() {
            var session = new Session(null); var queue = new QueueAccess(session);
            string[] kinds = { "button", "key", "release", "scroll", "shortcut", "ping" };
            foreach (string kind in kinds) {
                queue.Add(Move(1, 2, 7)); queue.Add(Move(3, 4, 7));
                queue.Add(kind == "ping" ? Wire.Obj("type", "ping") : Wire.Obj("type", "input", "epoch", 7, "event", Wire.Obj("kind", kind)));
            }
            queue.Add(Move(5, 6, 7)); queue.Add(Move(7, 8, 8)); queue.Add(Move(9, 10, 7));
            return queue.Drain();
        }
        public static object SegmentsAndOwnership() {
            var queue = new QueueAccess(new Session(null)); var first = Move(2040, -2040, 1);
            queue.Add(first); queue.Add(Move(8, -8, 1)); queue.Add(Move(1, -1, 1));
            var inflight = queue.Take(); queue.Add(Move(11, -11, 1));
            var rows = queue.Drain();
            queue.Add(Move(-2048, 2048, 1)); queue.Add(Move(-1, 1, 1));
            return new { original = first, inflight = inflight, pending = rows, negative = queue.Drain() };
        }
        public static object CapacityAndClear() {
            var session = ConnectedSession(); var queue = new QueueAccess(session);
            bool filled = true;
            for (int i = 0; i < 511; i++) filled &= queue.Add(Wire.Obj("type", "ping", "sequence", i));
            filled &= queue.Add(Move(1, 1, 1));
            bool mergedAtCapacity = queue.Add(Move(2, 2, 1));
            bool rejected = !queue.Add(Wire.Obj("type", "ping")); int count = queue.Count;
            session.Disconnect(); int cleared = queue.Count;
            queue.Add(Move(4, 5, 2)); var fresh = queue.Take();
            return new { filled = filled, merged = mergedAtCapacity, rejected = rejected, count = count, cleared = cleared, fresh = fresh };
        }
        public static object InvalidMovesAndOverflow() {
            var queue = new QueueAccess(new Session(null));
            var missingEpoch = Move(1, 2, 1); missingEpoch.Remove("epoch");
            var missingDelta = Move(1, 2, 1); Wire.D(missingDelta, "event").Remove("dy");
            var fractional = Move(1, 2, 1); Wire.D(fractional, "event")["dx"] = 1.5;
            var tooLarge = Move(2049, 2, 1);
            var malformed = new[] { missingEpoch, missingDelta, fractional, tooLarge };
            foreach (var message in malformed) { queue.Add(Move(1, 1, 1)); queue.Add(message); queue.Add(Move(2, 2, 1)); queue.Add(Wire.Obj("type", "ping")); }
            int count = queue.Count;
            var session = ConnectedSession(); var pending = new QueueAccess(session); string status = null;
            session.OnStatus = delegate(string value) { status = value; };
            for (int i = 0; i < 512; i++) session.SendControl(Wire.Obj("type", "ping", "sequence", i));
            session.SendControl(Wire.Obj("type", "input", "epoch", 1, "event", Wire.Obj("kind", "key", "code", 65, "down", true)));
            var closed = ConnectedSession(); var closedQueue = new QueueAccess(closed); closedQueue.Complete(); closed.SendControl(Move(1, 1, 1));
            return new { count = count, connected = session.Connected, pending = pending.Count, status = status, closedPending = closedQueue.Count };
        }
        public static object Completion() {
            var queue = new QueueAccess(new Session(null)); var started = new ManualResetEvent(false);
            bool wokeCompleted = false; Exception failure = null;
            var thread = new Thread(new ThreadStart(delegate {
                started.Set();
                try { queue.Take(); }
                catch (InvalidOperationException) { wokeCompleted = true; }
                catch (Exception ex) { failure = ex; }
            }));
            thread.IsBackground = true; thread.Start(); started.WaitOne(1000); queue.Complete();
            bool joined = thread.Join(2000); bool rejected = false;
            try { queue.Add(Move(1, 1, 1)); } catch (InvalidOperationException) { rejected = true; }
            var draining = new QueueAccess(new Session(null)); draining.Add(Move(1, 2, 1)); draining.Complete();
            var last = draining.Take(); bool ended = false;
            try { draining.Take(); } catch (InvalidOperationException) { ended = true; }
            return new { completed = queue.Completed, woke = wokeCompleted, joined = joined, rejected = rejected, ended = ended, last = last, failure = failure == null ? null : failure.ToString() };
        }
        public static object ProducerConsumer() {
            var queue = new QueueAccess(new Session(null)); var started = new ManualResetEvent(false);
            long x = 0, y = 0; int frames = 0; bool bounded = true; Exception failure = null;
            var thread = new Thread(new ThreadStart(delegate {
                started.Set();
                try {
                    while (true) {
                        var data = Wire.D(queue.Take(), "event"); long dx = Wire.L(data, "dx", 0), dy = Wire.L(data, "dy", 0);
                        x += dx; y += dy; frames++; bounded &= Math.Abs(dx) <= 2048 && Math.Abs(dy) <= 2048;
                    }
                } catch (InvalidOperationException) { }
                catch (Exception ex) { failure = ex; }
            }));
            thread.IsBackground = true; thread.Start(); started.WaitOne(1000);
            for (int i = 0; i < 30000; i++) { var message = Move(2, -3, 1); while (!queue.Add(message)) Thread.Yield(); }
            queue.Complete(); bool joined = thread.Join(4000);
            return new { x = x, y = y, frames = frames, bounded = bounded, joined = joined, failure = failure == null ? null : failure.ToString() };
        }
        public static object Frame() {
            var stream = new CountingStream(); Wire.Write(stream, Wire.Obj("type", "ping", "name", "中文"), new object(), 65536);
            byte[] bytes = stream.ToArray(); int length = (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
            bool rejected = false; var overLimit = new CountingStream();
            try { Wire.Write(overLimit, Wire.Obj("type", "ping"), new object(), 1); } catch (InvalidDataException) { rejected = true; }
            return new { writes = stream.Writes, flushes = stream.Flushes, length = length, payload = Encoding.UTF8.GetString(bytes, 4, bytes.Length - 4), size = bytes.Length, rejected = rejected, rejectedWrites = overLimit.Writes };
        }
    }
}
