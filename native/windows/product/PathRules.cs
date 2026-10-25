using System;
namespace SwitchDesk.Desktop {
    public static class PathRules {
        public static bool Valid(string path) {
            if (String.IsNullOrEmpty(path) || path.Length > 1024 || path.IndexOf('\\') >= 0 || path.IndexOf('\0') >= 0 || path.StartsWith("/")) return false;
            string[] parts = path.Split('/');
            foreach (string p in parts) {
                if (p.Length == 0 || p == "." || p == ".." || p.EndsWith(".") || p.EndsWith(" ") || p.IndexOfAny(new[] {'<','>',':','"','|','?','*'}) >= 0) return false;
                foreach (char c in p) if (c < 32) return false;
                string stem = p.Split('.')[0].ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                    (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] >= '1' && stem[3] <= '9')) return false;
            }
            return true;
        }
    }
}
