using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SwitchDesk.Desktop {
    internal static class Wire {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 24 * 1024 * 1024, RecursionLimit = 48 };
        internal static Dictionary<string, object> Obj(params object[] pairs) {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i < pairs.Length; i += 2) result[(string)pairs[i]] = pairs[i + 1];
            return result;
        }
        internal static string S(IDictionary<string, object> o, string key) { object value; return o != null && o.TryGetValue(key, out value) ? value as string : null; }
        internal static bool B(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v is bool && (bool)v; }
        internal static long L(IDictionary<string, object> o, string key, long fallback) {
            object v; if (o == null || !o.TryGetValue(key, out v)) return fallback;
            if (v is int) return (int)v;
            if (v is long) return (long)v;
            return fallback;
        }
        internal static IDictionary<string, object> D(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as IDictionary<string, object> : null; }
        internal static IList A(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as IList : null; }
        internal static void Write(Stream stream, object message, object gate, int limit) {
            string json = Json.Serialize(message); int length = Encoding.UTF8.GetByteCount(json);
            if (length == 0 || length > limit) throw new InvalidDataException("消息超出协议大小限制");
            byte[] frame = new byte[length + 4];
            frame[0] = (byte)(length >> 24); frame[1] = (byte)(length >> 16); frame[2] = (byte)(length >> 8); frame[3] = (byte)length;
            Encoding.UTF8.GetBytes(json, 0, json.Length, frame, 4);
            lock (gate) { stream.Write(frame, 0, frame.Length); stream.Flush(); }
        }
        internal static Dictionary<string, object> Read(Stream stream, int limit) {
            byte[] header = ReadExact(stream, 4);
            uint n = ((uint)header[0] << 24) | ((uint)header[1] << 16) | ((uint)header[2] << 8) | header[3];
            if (n == 0 || n > limit) throw new InvalidDataException("消息长度非法");
            string json = new UTF8Encoding(false, true).GetString(ReadExact(stream, (int)n));
            var obj = Json.DeserializeObject(json) as Dictionary<string, object>;
            if (obj == null || S(obj, "type") == null) throw new InvalidDataException("消息对象非法");
            return obj;
        }
        private static byte[] ReadExact(Stream s, int count) {
            byte[] b = new byte[count]; int offset = 0;
            while (offset < count) { int n = s.Read(b, offset, count - offset); if (n <= 0) throw new EndOfStreamException(); offset += n; }
            return b;
        }
        internal static bool ValidPath(string path) { return PathRules.Valid(path); }
        internal static string Hex(byte[] bytes) { var b = new StringBuilder(bytes.Length * 2); foreach (byte value in bytes) b.Append(value.ToString("x2")); return b.ToString(); }
    }
}
