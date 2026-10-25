using System;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SwitchDesk.Desktop {
    internal sealed class Identity {
        internal X509Certificate2 Certificate;
        internal byte[] Token;
        internal string Device;
        private readonly string folder;
        internal Identity() {
            folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwitchDesk");
            Directory.CreateDirectory(folder);
            string certPath = Path.Combine(folder, "identity.dat");
            if (File.Exists(certPath)) Certificate = new X509Certificate2(ProtectedData.Unprotect(File.ReadAllBytes(certPath), null, DataProtectionScope.CurrentUser), "", X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
            else {
                using (var rsa = new RSACryptoServiceProvider(2048)) {
                    var request = new CertificateRequest("CN=SwitchDesk Local", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    using (var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10)))
                        Certificate = new X509Certificate2(cert.Export(X509ContentType.Pfx), "", X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
                }
                File.WriteAllBytes(certPath, ProtectedData.Protect(Certificate.Export(X509ContentType.Pfx), null, DataProtectionScope.CurrentUser));
            }
            string tokenPath = Path.Combine(folder, "token.dat");
            Token = File.Exists(tokenPath) ? ProtectedData.Unprotect(File.ReadAllBytes(tokenPath), null, DataProtectionScope.CurrentUser) : NewToken();
            if (Token.Length != 32) Token = NewToken();
            SaveToken();
            string devicePath = Path.Combine(folder, "paired.dat");
            if (File.Exists(devicePath)) Device = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(devicePath), null, DataProtectionScope.CurrentUser));
        }
        private static byte[] NewToken() { byte[] result = new byte[32]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(result); return result; }
        private void SaveToken() { File.WriteAllBytes(Path.Combine(folder, "token.dat"), ProtectedData.Protect(Token, null, DataProtectionScope.CurrentUser)); }
        internal void Approve(string device) { Device = device; File.WriteAllBytes(Path.Combine(folder, "paired.dat"), ProtectedData.Protect(Encoding.UTF8.GetBytes(device), null, DataProtectionScope.CurrentUser)); }
        internal void Revoke() { Device = null; Token = NewToken(); SaveToken(); string file = Path.Combine(folder, "paired.dat"); if (File.Exists(file)) File.Delete(file); }
        internal bool TokenMatches(string encoded) {
            byte[] candidate; try { candidate = Convert.FromBase64String(encoded ?? ""); } catch { return false; }
            if (candidate.Length != 32) return false;
            int diff = 0; for (int i = 0; i < 32; i++) diff |= candidate[i] ^ Token[i]; return diff == 0;
        }
        internal string Invitation() {
            string host = null; int best = Int32.MinValue;
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces()) {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties properties = nic.GetIPProperties();
                bool gateway = false;
                foreach (GatewayIPAddressInformation route in properties.GatewayAddresses)
                    if (route.Address.AddressFamily == AddressFamily.InterNetwork && !route.Address.Equals(IPAddress.Any)) gateway = true;
                bool physical = nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet || nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address) && !address.Address.ToString().StartsWith("169.254.")) {
                        byte[] bytes = address.Address.GetAddressBytes();
                        bool privateIp = bytes[0] == 10 || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) || (bytes[0] == 192 && bytes[1] == 168);
                        int score = (gateway ? 100 : 0) + (physical ? 30 : 0) + (privateIp ? 10 : 0);
                        if (score > best) { best = score; host = address.Address.ToString(); }
                    }
            }
            if (host == null) throw new InvalidOperationException("没有可用的局域网 IPv4 地址");
            byte[] digest; using (var sha = SHA256.Create()) digest = sha.ComputeHash(Certificate.RawData);
            string json = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(Wire.Obj("host", host, "port", 45923, "pin", Wire.Hex(digest), "token", Convert.ToBase64String(Token)));
            return "switchdesk1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }
    }
}
